using System.Collections.ObjectModel;
using System.IO;
using OICQStickerManager.Models;

namespace OICQStickerManager.Services;

/// <summary>
/// QQ 收藏表情镜像服务：一个绑定账号一个实例。
/// 只读 personal_emoji（表情面板"爱心分页"的本地缓存），监视 Ori 目录的增删，
/// 维护 Mirror 集合供「QQ（账号）」选项卡展示。绝不写入 QQ 目录。
/// </summary>
public class QqEmojiService : IDisposable
{
    public string Uin { get; }

    public string OriDir { get; }
    public string DatabasePath
    {
        get
        {
            var directory = new DirectoryInfo(OriDir);
            for (int i = 0; i < 4 && directory.Parent != null; i++) directory = directory.Parent;
            return string.Equals(directory.Name, "nt_qq", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directory.FullName, "nt_db", "emoji.db")
                : Path.Combine(TencentFilesRoot, Uin, "nt_qq", "nt_db", "emoji.db");
        }
    }

    private string ThumbDir => Path.Combine(Path.GetDirectoryName(OriDir)!, "Thumb");

    /// <summary>镜像集合（按收藏时间倒序），绑定到 QQ 选项卡的格子列表。</summary>
    public ObservableCollection<QqStickerModel> Mirror { get; } = new();

    /// <summary>镜像增删后触发（页脚计数与角标刷新用）。</summary>
    public event EventHandler? MirrorChanged;

    /// <summary>新收藏落盘（Ori 新增文件，即用户在 QQ 里点了「添加到表情」）→ M4 联动入口。</summary>
    public event Action<QqStickerModel>? NewFavoriteDetected;

    private readonly Action<Action> _marshal;
    private FileSystemWatcher? _watcher;
    private readonly object _pendingLock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private System.Timers.Timer? _debounce;
    private System.Timers.Timer? _recovery;
    private volatile bool _watcherFailed;
    private int _recovering;
    private long _lastRecoveryScan;
    private volatile bool _disposed;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public QqEmojiService(string uin, string oriDir, Action<Action> marshal)
    {
        Uin = uin;
        OriDir = oriDir;
        _marshal = marshal;
    }

    // ———— 账号扫描（静态，绑定对话框与启动询问共用） ————

    public static string TencentFilesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tencent Files");

    /// <summary>
    /// 扫描本机所有 QQ 账号：目录名是纯数字且含 nt_qq 子目录。
    /// LastActive 取热文件（nt_msg.db-wal 等聊天库 WAL + mmkv + log）最新修改时间——
    /// 活跃账号持续写入、休眠账号冻结（实测本机：活跃账号当日、休眠账号停在两年前）。
    /// </summary>
    public static Task<List<QqAccountScanResult>> ScanAccountsAsync(string? dataRoot = null)
    {
        return Task.Run(() =>
        {
            var results = new List<QqAccountScanResult>();
            var root = dataRoot ?? TencentFilesRoot;
            if (!Directory.Exists(root)) return results;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var uin = Path.GetFileName(dir);
                if (uin.Length == 0 || !uin.All(char.IsDigit)) continue;
                var ntQq = Path.Combine(dir, "nt_qq");
                if (!Directory.Exists(ntQq)) continue;

                var ori = Path.Combine(ntQq, "nt_data", "Emoji", "personal_emoji", "Ori");
                var result = new QqAccountScanResult
                {
                    Uin = uin,
                    AccountDirectory = dir,
                    OriDir = ori,
                    StickerCount = Directory.Exists(ori)
                        ? Directory.EnumerateFiles(ori).Count()
                        : 0,
                    LastActive = LatestHotMtime(ntQq),
                };
                results.Add(result);
            }
            return results;
        });
    }

    private static DateTime LatestHotMtime(string ntQqDir)
    {
        var newest = DateTime.MinValue;
        string[][] probes =
        {
            new[] { Path.Combine(ntQqDir, "nt_db"), "*.db-wal" },
            new[] { Path.Combine(ntQqDir, "nt_data", "mmkv"), "*" },
            new[] { Path.Combine(ntQqDir, "nt_data", "log"), "*" },
        };
        foreach (var probe in probes)
        {
            var dir = probe[0];
            var pattern = probe[1];
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var m = File.GetLastWriteTime(f);
                        if (m > newest) newest = m;
                    }
                    catch { /* 文件恰好被清理 */ }
                }
            }
            catch { /* 目录被清空 */ }
        }
        return newest;
    }

    /// <summary>各账号中"最像当前在玩的"那个：最活跃且收藏目录有货。猜错无后果，只用于启动询问。</summary>
    public static QqAccountScanResult? GuessCurrentAccount(List<QqAccountScanResult> accounts) => accounts
        .Where(a => a.HasEmoji)
        .OrderByDescending(a => a.LastActive)
        .FirstOrDefault();

    // ———— 镜像 ————

    /// <summary>全量扫描 + 启动目录监视。</summary>
    public async Task StartAsync()
    {
        lock (_pendingLock)
        {
            if (_disposed) return;
            StartWatcher();
            if (_recovery == null)
            {
                _recovery = new System.Timers.Timer(1000);
                _recovery.Elapsed += OnRecoveryTick;
                _recovery.Start();
            }
        }
        await RescanAsync();
    }

    private async void OnRecoveryTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _recovering, 1) != 0) return;
        try
        {
            bool rescan;
            lock (_pendingLock)
            {
                if (_disposed) return;
                bool missing = !Directory.Exists(OriDir);
                if (_watcherFailed || missing)
                {
                    _watcher?.Dispose();
                    _watcher = null;
                    _watcherFailed = false;
                }
                bool restart = _watcher == null && !missing;
                if (restart) StartWatcher();
                rescan = restart || Environment.TickCount64 - _lastRecoveryScan >= 60_000;
                if (rescan) _lastRecoveryScan = Environment.TickCount64;
            }
            if (rescan) await RescanAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { QqPanelWatcher.Log("QQ watcher recovery failed: " + ex.GetType().Name); }
        finally { Interlocked.Exchange(ref _recovering, 0); }
    }

    public async Task RescanAsync()
    {
        if (_disposed) return;
        await _refreshLock.WaitAsync();
        try
        {
            if (_disposed) return;
            var items = await Task.Run(() =>
            {
                var list = new List<QqStickerModel>();
                if (!Directory.Exists(OriDir)) return list;
                foreach (var f in Directory.EnumerateFiles(OriDir))
                {
                    var m = BuildModelOrNull(f);
                    if (m != null) list.Add(m);
                }
                list.Sort((a, b) => b.FileMtime.CompareTo(a.FileMtime));
                return list;
            });

            _marshal(() =>
            {
                if (_disposed) return;
                Mirror.Clear();
                foreach (var m in items) Mirror.Add(m);
                MirrorChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QqPanelWatcher.Log("QQ mirror rescan failed: " + ex.Message);
        }
        finally { _refreshLock.Release(); }
    }

    private QqStickerModel? BuildModelOrNull(string path)
    {
        try
        {
            var kind = ImageSniffer.Detect(path);
            if (kind == ImageKind.Unknown) return null;

            var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var thumb = Path.Combine(ThumbDir, name + ".png");
            var info = new FileInfo(path);
            var model = new QqStickerModel
            {
                Uin = Uin,
                Md5 = name,
                FullPath = path,
                FileMtime = info.LastWriteTime,
                ThumbPath = File.Exists(thumb) ? thumb : null,
            };
            model.SetRealKind(kind);
            return model;
        }
        catch
        {
            return null;
        }
    }

    // ———— 目录监视（防半写：QQ 下载中不入场） ————

    private void StartWatcher()
    {
        if (_disposed || _watcher != null || !Directory.Exists(OriDir)) return;

        try
        {
        _watcher = new FileSystemWatcher(OriDir, "*.*")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Enqueue(e.FullPath);
        _watcher.Changed += (_, e) => Enqueue(e.FullPath);
        _watcher.Deleted += (_, e) => Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
        _watcher.Error += (_, _) => _watcherFailed = true;
        _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _watcher?.Dispose();
            _watcher = null;
            _watcherFailed = true;
        }
    }

    private void Enqueue(string path)
    {
        if (_disposed) return;
        lock (_pendingLock)
        {
            if (_disposed) return;
            _pending.Add(path);
            _debounce ??= NewDebounce();
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private System.Timers.Timer NewDebounce()
    {
        var timer = new System.Timers.Timer(400) { AutoReset = false };
        timer.Elapsed += OnDebounceTick;
        return timer;
    }

    private async void OnDebounceTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (_disposed) return;
            string[] paths;
            lock (_pendingLock)
            {
                paths = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var path in paths)
            {
                if (_disposed) return;
                await ApplyOneAsync(path);
            }

            _marshal(() => { if (!_disposed) MirrorChanged?.Invoke(this, EventArgs.Empty); });
        }
        catch
        {
            // 定时器线程上的兜底：任何异常都不能带崩进程，下次事件/全量重扫自愈
        }
        finally { _refreshLock.Release(); }
    }

    private async Task ApplyOneAsync(string path)
    {
        if (!File.Exists(path))
        {
            _marshal(() =>
            {
                if (_disposed) return;
                var stale = Mirror.FirstOrDefault(m => string.Equals(m.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (stale != null) Mirror.Remove(stale);
            });
            return;
        }

        // QQ 正在下载的文件先等写完：独占打开失败就重试，彻底失败留给下次事件或全量重扫（后台线程等待）
        bool ready = false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    ready = true;
                    break;
                }
            }
            catch (IOException)
            {
                await Task.Delay(300);
            }
            catch
            {
                return;
            }
        }
        if (!ready) return;

        var model = BuildModelOrNull(path);
        if (model == null) return;

        // 集合变更必须回 UI 线程（Mirror 绑定在列表上）
        _marshal(() =>
        {
            if (_disposed) return;
            var existing = Mirror.FirstOrDefault(m => string.Equals(m.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                Mirror[Mirror.IndexOf(existing)] = model; // 同名重写入：内容不变（名字即 MD5），仅刷新元数据
            else
            {
                Mirror.Insert(0, model); // 新收藏置顶
                NewFavoriteDetected?.Invoke(model); // M4：用户在 QQ 里点了「添加到表情」
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_pendingLock)
        {
            _debounce?.Stop();
            _debounce?.Dispose();
            _debounce = null;
            _recovery?.Stop();
            _recovery?.Dispose();
            _recovery = null;
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
