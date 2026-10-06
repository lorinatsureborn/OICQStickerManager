using System.IO;
using OICQStickerManager.Models;

namespace OICQStickerManager.Services;

/// <summary>QQ 收藏深度同步策略（设计 §4.8，用户定案三档）。</summary>
public enum QqSyncStrategy
{
    /// <summary>孤儿留在镜像里，打「已移除」淡色角标，悬浮有解释。</summary>
    Mark = 0,

    /// <summary>孤儿直接从镜像移除（QQ 缓存文件永远不动）。</summary>
    Remove = 1,

    /// <summary>孤儿复制进图库（无默认标签）后从镜像移除；已在图库则仅移除。</summary>
    Adopt = 2,
}

public enum QqAdoptionResult { Added, AlreadyExists, Failed }

/// <summary>
/// QQ 收藏深度同步（Phase B）：解密各绑定账号的 emoji.db，读出权威收藏 MD5 集合，
/// 与 Ori 目录对账 → 按策略处理孤儿。失败一律良性：返回 false 让上层降级回目录镜像模式。
/// </summary>
public class QqDeepSyncService
{
    public const int Deferred = -3;
    public const int AdoptionFailed = -4;
    public const int AuthenticationFailed = -5;
    private readonly string _dbKey;
    private readonly Func<string, QqEmojiService?> _serviceOf;
    private readonly Func<QqStickerModel, Task<QqAdoptionResult>> _adoptToLibrary;
    private readonly Func<string, Task<(HashSet<string>? Md5s, bool Deferred)>>? _readIndex;

    public QqDeepSyncService(string dbKey, Func<string, QqEmojiService?> serviceOf,
        Func<QqStickerModel, Task<bool>> adoptToLibrary,
        Func<string, Task<(HashSet<string>? Md5s, bool Deferred)>>? readIndex = null)
        : this(dbKey, serviceOf, async sticker => await adoptToLibrary(sticker)
            ? QqAdoptionResult.Added : QqAdoptionResult.Failed, readIndex) { }

    public QqDeepSyncService(string dbKey, Func<string, QqEmojiService?> serviceOf,
        Func<QqStickerModel, Task<QqAdoptionResult>> adoptToLibrary,
        Func<string, Task<(HashSet<string>? Md5s, bool Deferred)>>? readIndex = null)
    {
        _dbKey = dbKey;
        _serviceOf = serviceOf;
        _adoptToLibrary = adoptToLibrary;
        _readIndex = readIndex;
    }

    /// <summary>返回处理的孤儿数；-1 = 读取失败；Deferred = 快照不稳定，保留密钥和镜像。</summary>
    public async Task<int> ReconcileAllAsync(List<string> uins, QqSyncStrategy strategy)
    {
        try
        {
            var snapshots = new List<(QqEmojiService Service, HashSet<string> Index)>();
            foreach (var uin in uins.Distinct())
            {
                var service = _serviceOf(uin);
                if (service == null) continue;

                var (indexedMd5, deferred) = await (_readIndex?.Invoke(uin) ?? ReadAuthoritativeMd5sAsync(uin));
                if (deferred) return Deferred;
                if (indexedMd5 == null) return -1; // 解密失败 → 整体降级

                snapshots.Add((service, new HashSet<string>(indexedMd5, StringComparer.OrdinalIgnoreCase)));
            }

            int orphanCount = 0;
            bool adoptionFailed = false;
            foreach (var (service, index) in snapshots)
            {
                foreach (var item in service.Mirror.Where(m => m.Md5 != null && index.Contains(m.Md5)))
                    item.IsOrphaned = false;
                // Unknown cache names are not evidence of removed favorites.
                var orphans = service.Mirror.Where(m => IsMd5(m.Md5) && !index.Contains(m.Md5!)).ToList();
                orphanCount += orphans.Count;
                foreach (var orphan in orphans)
                {
                    switch (strategy)
                    {
                        case QqSyncStrategy.Remove:
                            service.Mirror.Remove(orphan);
                            break;
                        case QqSyncStrategy.Adopt:
                            var result = await _adoptToLibrary(orphan);
                            if (result == QqAdoptionResult.Failed)
                            {
                                orphan.IsOrphaned = true;
                                adoptionFailed = true;
                            }
                            else service.Mirror.Remove(orphan);
                            break;
                        default:
                            orphan.IsOrphaned = true;
                            break;
                    }
                }
            }
            return adoptionFailed ? AdoptionFailed : orphanCount;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return AuthenticationFailed;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsMd5(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);

    public async Task<bool> ConfirmOrphanAsync(QqStickerModel sticker)
    {
        if (!IsMd5(sticker.Md5) || _serviceOf(sticker.Uin) == null) return false;
        var (index, deferred) = await (_readIndex?.Invoke(sticker.Uin) ?? ReadAuthoritativeMd5sAsync(sticker.Uin));
        if (deferred || index == null) return false;
        bool orphan = !index.Contains(sticker.Md5!, StringComparer.OrdinalIgnoreCase);
        sticker.IsOrphaned = orphan;
        return orphan;
    }

    /// <summary>读取账号索引；区分读取失败与数据库仍在写入的暂缓状态。</summary>
    private async Task<(HashSet<string>? Md5s, bool Deferred)> ReadAuthoritativeMd5sAsync(string uin)
    {
        string? tempDir = null;
        try
        {
            var dbPath = _serviceOf(uin)?.DatabasePath ?? Path.Combine(QqEmojiService.TencentFilesRoot, uin, "nt_qq", "nt_db", "emoji.db");
            if (!File.Exists(dbPath)) return (null, false);
            var wal = dbPath + "-wal";
            if (File.Exists(wal) && new FileInfo(wal).Length is > 0 and < 32) return (null, true);

            tempDir = Path.Combine(Path.GetTempPath(), "asuka-deepsync", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var snapshot = await QqDatabaseSnapshot.ReadAsync(dbPath);
            byte[] plain;
            try { plain = await Task.Run(() => SqlcipherDecryptor.Decrypt(snapshot.Database, _dbKey, snapshot.Wal)); }
            catch (InvalidDataException) when (snapshot.Wal is { Length: > 0 }) { return (null, true); }
            var plainPath = Path.Combine(tempDir, "plain.db");
            await File.WriteAllBytesAsync(plainPath, plain);
            var result = await Task.Run(() => ReadFavMd5s(plainPath));
            return (result, false);
        }
        catch (QqSnapshotChangedException) { return (null, true); }
        catch (System.Security.Cryptography.CryptographicException) { throw; }
        catch
        {
            return (null, false); // 密钥失效/QQ 更新改结构 → 降级
        }
        finally
        {
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static HashSet<string> ReadFavMd5s(string plainDbPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = plainDbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false, // 默认连接池让 Dispose 只还池不释放句柄，快照临时目录删不掉（实测残留 plain.db/-shm）
            }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        if (!string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("QQ 索引结构完整性检查失败");
        // Brackets reject a missing column; SQLite can interpret a double-quoted
        // unknown identifier as a string literal and incorrectly return an empty index.
        cmd.CommandText = "SELECT [80011] FROM fav_emoji_info_storage_table WHERE [80011] IS NOT NULL AND [80011] != ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var md5 = reader.GetString(0).ToUpperInvariant();
            if (md5.Length != 32 || !md5.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("QQ 收藏索引包含无法识别的 MD5");
            result.Add(md5);
        }
        return result;
    }
}
