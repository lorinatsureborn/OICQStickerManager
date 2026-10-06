using System.IO;
using System.Text.Json;

namespace OICQStickerManager.Services;

/// <summary>一条 AI 识别缓存：Key = 内容 Md5（无 Md5 的旧数据退化为文件路径）。</summary>
public sealed class AiTagCacheEntry
{
    public string Key { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// AI 标签建议缓存（Documents\OICQStickerManager\ai-tag-cache.json）。
/// 定案语义：同一张表情（同 Md5）识别一次永久复用，只有用户点「重新识别」才覆盖——
/// 模型不换季、结果不漂移，也绝不重复烧用户的钱。
/// </summary>
public static class AiTagCache
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static Dictionary<string, AiTagCacheEntry>? _entries;
    private static bool _dirty;
    private static string? _loadedPath;
    private static Task _pendingSave = Task.CompletedTask;

    /// <summary>缓存上限：LRU 按时间淘汰，防无限增长。</summary>
    private const int MaxEntries = 2000;

    public static string FilePath => Path.Combine(AppDataDirectory.Root, "ai-tag-cache.json");

    public static string CacheKey(Models.StickerModel sticker) =>
        string.IsNullOrEmpty(sticker.Md5) ? "path:" + sticker.FullPath.ToLowerInvariant() : "md5:" + sticker.Md5;

    private static Dictionary<string, AiTagCacheEntry> Load()
    {
        var path = FilePath;
        if (_entries != null && _loadedPath == path) return _entries;
        _loadedPath = path;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _entries = JsonSerializer.Deserialize<Dictionary<string, AiTagCacheEntry>>(json)
                           ?? new Dictionary<string, AiTagCacheEntry>(StringComparer.Ordinal);
                return _entries;
            }
        }
        catch { /* 损坏缓存视同没有：重新识别即可，无伤数据 */ }
        _entries = new Dictionary<string, AiTagCacheEntry>(StringComparer.Ordinal);
        return _entries;
    }

    /// <summary>命中返回缓存建议；未命中返回 null（调用方发起识别）。</summary>
    public static AiTagCacheEntry? Get(string key)
    {
        lock (Gate)
        {
            return Load().TryGetValue(key, out var e) ? e : null;
        }
    }

    /// <summary>批量确认弹窗的免弹判定：全部图片都有缓存时不会产生新请求。</summary>
    public static bool WouldAllHitCache(IEnumerable<Models.StickerModel> stickers)
    {
        lock (Gate)
        {
            var store = Load();
            return stickers.All(s => store.ContainsKey(CacheKey(s)));
        }
    }

    /// <summary>写入/覆盖一条缓存并异步落盘（fire-and-forget 安全：写锁串行化 + 原子替换）。</summary>
    public static void Put(string key, IReadOnlyList<string> tags, string providerId, string model)
    {
        lock (Gate)
        {
            var store = Load();
            store[key] = new AiTagCacheEntry
            {
                Key = key,
                Tags = tags.ToList(),
                ProviderId = providerId,
                Model = model,
                CreatedAt = DateTime.Now,
            };
            _dirty = true;
            if (store.Count > MaxEntries) EvictOldest(store);
            _ = SaveIfDirtyCore();
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            if (Load().Remove(key)) { _dirty = true; SaveIfDirtyCore(); }
        }
    }

    /// <summary>测试与退出清理用：等落盘完成。</summary>
    public static Task FlushAsync()
    {
        lock (Gate) { return SaveIfDirtyCore(); }
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            _entries = new Dictionary<string, AiTagCacheEntry>(StringComparer.Ordinal);
            _dirty = false;
            _loadedPath = FilePath;
        }
    }

    private static void EvictOldest(Dictionary<string, AiTagCacheEntry> store)
    {
        foreach (var k in store.Values.OrderBy(e => e.CreatedAt)
                     .Take(store.Count - MaxEntries).Select(e => e.Key).ToList())
            store.Remove(k);
    }

    /// <summary>锁内调用。有脏数据则按序启动落盘，返回全部已排队写入的完成任务。</summary>
    private static Task SaveIfDirtyCore()
    {
        if (!_dirty) return _pendingSave;
        _dirty = false;
        var snapshot = new Dictionary<string, AiTagCacheEntry>(Load(), StringComparer.Ordinal);
        var path = FilePath;
        _pendingSave = _pendingSave.ContinueWith(_ => SaveCoreAsync(path, snapshot),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return _pendingSave;
    }

    private static async Task SaveCoreAsync(string path, Dictionary<string, AiTagCacheEntry> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot);
            await WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
                await Task.Run(() =>
                {
                    try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { }
                    File.Move(tmp, path, overwrite: true);
                }).ConfigureAwait(false);
            }
            finally { WriteLock.Release(); }
        }
        catch (Exception ex)
        {
            AiTagService.Log($"cache save failed: {ex.Message}");
        }
    }
}
