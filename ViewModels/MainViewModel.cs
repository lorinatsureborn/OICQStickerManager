using OICQStickerManager.Models;
using OICQStickerManager.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json; // 引用 Json 命名空间
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace OICQStickerManager.ViewModels;

/// <summary>快捷面板选项卡条目：TabItemModel 的面板版，多一个选中态驱动胶囊高亮。</summary>
public class PanelTabItem : ViewModelBase
{
    public PanelTabItem(string value, string label, bool isSelected = false)
    {
        Value = value;
        Label = label;
        _isSelected = isSelected;
    }

    /// <summary>标签名；空串 = 「全部」。</summary>
    public string Value { get; }

    public string Label { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }
}

/// <summary>设置面板里的配色选项（ThemeDef 的可绑定包装）。</summary>
public class ThemeOption : ViewModelBase
{
    public ThemeOption(ThemeDef def)
    {
        Id = def.Id;
        Name = def.Name;
        SwatchBrush = def.SwatchBrush;
    }

    public string Id { get; }
    public string Name { get; }
    public System.Windows.Media.Brush SwatchBrush { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }
}


public class MainViewModel : ViewModelBase
{

    private readonly string _configPath = GetConfigPath();

    /// <summary>config.json 的位置；App 在创建主窗口前要抢先读一次静默启动标志。</summary>
    public static string GetConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "OICQStickerManager", "config.json");

    private readonly ImageService _imageService = new();

    // UI 会绑定这个集合来显示图片列表
    public ObservableCollection<StickerModel> Stickers { get; } = new();

    private string _statusText = "准备就绪";
    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            OnPropertyChanged(); // 通知 UI：状态文字变了！
        }
    }

    private int _filteredCount;
    /// <summary>当前筛选条件下可见的表情数（页脚实时统计，随搜索/标签页即时刷新）</summary>
    public int FilteredCount
    {
        get => _filteredCount;
        private set
        {
            if (_filteredCount == value) return;
            _filteredCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>页脚右侧文案：未在筛选时显示全量，筛选中显示“当前 / 总数”</summary>
    public string StickerCountText
    {
        get
        {
            if (IsQqTabActive)
            {
                var service = CurrentQqService;
                return service == null ? "该账号尚未加载" : $"共 {service.Mirror.Count} 个QQ收藏表情";
            }
            return FilteredCount == Stickers.Count
                ? $"共 {Stickers.Count} 个表情"
                : $"{FilteredCount} / {Stickers.Count} 个表情";
        }
    }

    /// <summary>视图过滤结果或集合本身变化时，刷新页脚计数（防抖合并：启动装载的 Clear+N×Add
    /// 只落一次全视图遍历，而不是每次集合变更两遍 O(n)）。</summary>
    private void UpdateCounts()
    {
        // QQ 页展示的是独立镜像集合，不走图库视图
        FilteredCount = IsQqTabActive
            ? CurrentQqService?.Mirror.Count ?? 0
            : _stickersView.OfType<object>().Count();
        OnPropertyChanged(nameof(StickerCountText));
    }

    private int _countsRefreshPending; // 0/1：Background 优先级的合并刷新在途

    private void ScheduleUpdateCounts()
    {
        if (Interlocked.Exchange(ref _countsRefreshPending, 1) == 1) return;
        _uiDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _countsRefreshPending, 0);
            UpdateCounts();
        });
    }

    private readonly WindowService _windowService = new();

    public ObservableCollection<TabItemModel> TabTags { get; } = new();

    private string _selectedTab = "最近";
    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value) return; // 值未变时不触发全列表刷新

            // 选项卡就是纯页签：搜索中切换也不改搜索栏（2026-10-01 用户定案删除"点标签=搜索该词"
            // 转译——搜索词被莫名替换很蠢）。过滤语义见构造函数：搜索只在「最近」视图生效，
            // 切到标签页=看该标签全量（高亮与结果永远诚实），切回「最近」=搜索结果还在。
            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentQqUin));
            OnPropertyChanged(nameof(CurrentQqService));
            OnPropertyChanged(nameof(IsQqTabActive));
            // 💡 切换标签时，重置搜索框并刷新视图（QQ 页是独立镜像集合，不走此视图）
            _stickersView.Refresh();
            UpdateCounts();
        }
    }

    // 当前选中的 QQ 绑定页：值为 "qq:<uin>"；非 QQ 页时 CurrentQqService 为 null，
    // 主窗口据此把网格数据源在图库视图与镜像集合之间切换
    public string? CurrentQqUin => _selectedTab.StartsWith("qq:", StringComparison.Ordinal) ? _selectedTab[3..] : null;
    public bool IsQqTabActive => CurrentQqUin != null;
    public QqEmojiService? CurrentQqService => CurrentQqUin is string uin && _qqServices.TryGetValue(uin, out var s) ? s : null;

    // UI 线程调度器：VM 在主线程构造，QQ 镜像的集合变更必须经它回到 UI 线程
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;

    public MainViewModel()
    {
        // 初始化命令（get-only 属性，避免每次绑定求值都创建新实例）
        OpenLibraryCommand = new RelayCommand(OpenLibraryAsync);
        HandleStickerClickCommand = new RelayCommand<StickerModel>(HandleStickerClickAsync);
        HandleStickerDoubleClickCommand = new RelayCommand<StickerModel>(HandleStickerDoubleClickAsync);
        PanelStickerClickCommand = new RelayCommand<StickerModel>(PanelStickerClickAsync);
        PanelStickerDoubleClickCommand = new RelayCommand<StickerModel>(PanelStickerDoubleClickAsync);
        ImportQqStickerCommand = new RelayCommand<QqStickerModel>(ImportQqStickerAsync);
        ImportAllQqCommand = new RelayCommand(ImportAllQqAsync);

        // 初始化视图
        _stickersView = (ListCollectionView)CollectionViewSource.GetDefaultView(Stickers);
        _stickersView.Filter = (obj) =>
        {
            var sticker = (StickerModel)obj;

            // 搜索对图库全页签生效（2026-10-01 用户定案）：命中 = 标签名含搜索词；
            // 「最近」= 纯搜索，标签页 = 搜索词 AND 标签交集（侧栏只显示匹配标签，交集=该标签
            // 下与搜索相关的表情）。QQ 页不使用图库视图（ItemsSource 已切镜像分桶视图）
            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !sticker.Tags.Any(t => t.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (SelectedTab == "最近" || SelectedTab.StartsWith("qq:", StringComparison.Ordinal))
                return true;

            // 选项卡过滤
            return sticker.Tags.Contains(SelectedTab);
        };

        // 💡 核心排序：CustomSort 统一走 CompareWithMode（排序模式用户可调，见排序属性区），
        // 默认"前 N 张最近使用置顶 + 其余热度"（最近发送的图常被高频连发，置顶便于快速再找）。
        // CustomSort 在筛选后的集合上排序，SortDescriptions 与之互斥故不再使用
        _stickersView.SortDescriptions.Clear();
        _stickersView.CustomSort = Comparer<StickerModel>.Create(CompareGallery);

        // 快捷面板专用视图：与图库默认视图（带标签页/搜索过滤）解耦，永远全量、同一把热度尺子；
        // 不开实时排序——面板打开期间顺序冻结，发送不会让格子从光标下跳走，每次呼出时再重排。
        // 数据源是 PanelItems（图库 ∪ QQ 未入库镜像，呼出时重建），不是 Stickers 本身
        QuickPanelView = new ListCollectionView(PanelItems);
        QuickPanelView.SortDescriptions.Clear();
        QuickPanelView.CustomSort = Comparer<StickerModel>.Create(ComparePanel);
        // 面板过滤器：视角空 = 全部；"qq" = 只看 QQ 收藏；其他 = 该标签下的图库表情
        // （QQ 镜像条目没有标签，标签视角天然只出图库表情）
        QuickPanelView.Filter = o =>
        {
            if (_panelSelectedTab.Length == 0) return true;
            if (_panelSelectedTab == QqPanelTabValue) return o is QqStickerModel;
            return o is not QqStickerModel && ((StickerModel)o).Tags.Contains(_panelSelectedTab);
        };

        // 页脚计数：过滤结果变化（搜索/切标签/增删表情）都走这里实时刷新；
        // 视图与源集合的变更会各发一次通知，经 ScheduleUpdateCounts 合并成一次
        ((INotifyCollectionChanged)_stickersView).CollectionChanged += (_, _) => ScheduleUpdateCounts();
        Stickers.CollectionChanged += (_, _) => ScheduleUpdateCounts();

        // 💡 启动时自动加载
        _ = LoadConfigAsync();
        _ = LoadStickersAsync();
        _ = LoadQqStatsAsync();
        _ = LoadTagStatsAsync();
    }

    public void UpdateTabTags()
    {
        var current = SelectedTab;
        // 搜索中侧栏只显示匹配的标签（"小猫/小狗/小马"）：用户逐个点过去浏览，
        // 这是"搜索穿透页签"语义的一半（2026-10-01 用户定案）；最近与 QQ 页恒在
        var allTags = Stickers.SelectMany(s => s.Tags).Distinct();
        if (!string.IsNullOrEmpty(SearchText))
        {
            allTags = allTags.Where(t => t.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // 标签按内容热度排：标签热度 = 该标签下最热表情的 RankScore（用大而冷的标签堆分数会失真，故取峰值不取总和），
        // 最近活跃/高频使用的标签随之浮到顶部；同分按名称定序保持稳定
        var heat = ComputeTagHeat();

        TabTags.Clear();
        TabTags.Add(new TabItemModel("最近", "最近"));
        // QQ 绑定页紧跟「最近」：每个绑定一张卡，显示名 = QQ（别名），身份 = qq:<uin>
        foreach (var binding in _qqBindings)
            TabTags.Add(new TabItemModel("qq:" + binding.Uin, QqTabLabel(binding), isQq: true));
        foreach (var tag in allTags.OrderByDescending(t => heat.GetValueOrDefault(t)).ThenBy(t => t))
            TabTags.Add(new TabItemModel(tag, tag));

        SelectedTab = TabTags.Any(t => t.Value == current) ? current : "最近";
        RefreshPanelTabs(heat);
    }

    /// <summary>标签热度表：标签 → 其下最热表情的 RankScore（图库侧栏与快捷面板选项卡共用）。</summary>
    private Dictionary<string, double> ComputeTagHeat()
    {
        var heat = new Dictionary<string, double>();
        foreach (var sticker in Stickers)
        {
            foreach (var tag in sticker.Tags)
            {
                var score = sticker.RankScore;
                if (!heat.TryGetValue(tag, out var existing) || score > existing) heat[tag] = score;
            }
        }
        return heat;
    }

    /// <summary>刷新快捷面板选项卡：全部标签参与（不随主界面搜索过滤，面板视角与主界面解耦）。</summary>
    private void RefreshPanelTabs(Dictionary<string, double> heat)
    {
        var selected = _panelSelectedTab;
        PanelTabs.Clear();
        PanelTabs.Add(new PanelTabItem("", "全部", isSelected: selected.Length == 0));
        if (_qqBindings.Count > 0)
            PanelTabs.Add(new PanelTabItem(QqPanelTabValue, "QQ", isSelected: selected == QqPanelTabValue));
        foreach (var tag in heat.Keys.Where(t => t != QqPanelTabValue).OrderByDescending(t => heat[t]).ThenBy(t => t))
            PanelTabs.Add(new PanelTabItem(tag, tag, isSelected: tag == selected));
        OnPropertyChanged(nameof(HasPanelTabs));
    }

    // ———— 快捷面板数据源（图库 ∪ QQ 未入库镜像）————

    /// <summary>
    /// 面板网格的数据源：图库全部 + 各账号 QQ 收藏中未入库的镜像，按 Md5 去重——
    /// 同一张表情已在图库就以图库身份出现（统计本就记在图库），不重复展示。
    /// 只在呼出时整体重建（QuickPanelView 冻结排序语义），打开期间 QQ 收藏变化不打扰面板。
    /// </summary>
    public ObservableCollection<StickerModel> PanelItems { get; } = new();

    /// <summary>呼出前面板数据源重建：合并 + 去重 + 热度排序（排序由 QuickPanelView 的 SortDescriptions 承担）。</summary>
    public void RebuildPanelItems()
    {
        var items = new List<StickerModel>(Stickers);
        var seenMd5 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Stickers)
        {
            if (!string.IsNullOrEmpty(s.Md5)) seenMd5.Add(s.Md5);
        }
        foreach (var service in _qqServices.Values)
        {
            foreach (var mirror in service.Mirror)
            {
                if (!string.IsNullOrEmpty(mirror.Md5) && seenMd5.Add(mirror.Md5))
                    items.Add(mirror);
            }
        }
        items.Sort((a, b) =>
        {
            var byScore = b.RankScore.CompareTo(a.RankScore);
            return byScore != 0 ? byScore : b.LastUsedTime.CompareTo(a.LastUsedTime);
        });
        // 面板"最近置顶"集 = 混排范围内 LastUsedTime 最新的 N 张（含 QQ 镜像，发送统计已借入）
        _panelPinned = items
            .OrderByDescending(i => i.LastUsedTime)
            .Take(_recentPinnedCount)
            .Select(i => i.Id)
            .ToHashSet();
        PanelItems.Clear();
        foreach (var item in items) PanelItems.Add(item);
    }

    // ———— QQ 账号绑定（一对多、显式绑定，设计见 docs §4.5/§4.6） ————

    private readonly Dictionary<string, QqEmojiService> _qqServices = new();
    private readonly Dictionary<string, ListCollectionView> _qqMirrorViews = new();
    private readonly List<QqBindingInfo> _qqBindings = new();
    private List<string> _qqPromptDismissedUins = new();

    /// <summary>已绑定的账号（只读视图，绑定对话框/设置用）。</summary>
    public IReadOnlyList<QqBindingInfo> QqBindings => _qqBindings;

    /// <summary>QQ 导入完成后触发：新入库的表情 + 跳过数（主窗口据此开标签编辑器或提示）。</summary>
    public event Action<List<StickerModel>, int, int>? QqImportCompleted;

    public static string DefaultQqAlias(string uin) => uin.Length <= 5 ? uin : uin[^5..];

    public string QqTabLabel(QqBindingInfo binding) =>
        $"QQ（{(string.IsNullOrWhiteSpace(binding.Alias) ? DefaultQqAlias(binding.Uin) : binding.Alias)}）";

    private QqEmojiService CreateQqService(QqBindingInfo binding)
    {
        var oriDir = Path.Combine(QqEmojiService.TencentFilesRoot, binding.Uin,
            "nt_qq", "nt_data", "Emoji", "personal_emoji", "Ori");
        var service = new QqEmojiService(binding.Uin, oriDir, action => _uiDispatcher.Invoke(action));
        service.MirrorChanged += (_, _) =>
        {
            ApplyQqSearchBuckets(); // 新收藏条目也参与搜索分桶（搜索中新增的置顶条目需判命中）
            ApplyQqStats(service);
            UpdateImportedFlags(service);
            UpdateCounts();
        };
        // M4 联动：用户在 QQ 里点「添加到表情」→ 新文件落盘 → 按开关自动复制进图库
        // （静默导入：不开标签编辑器、不弹提示；图库里自动带「QQ」标签，可后续整理）
        service.NewFavoriteDetected += model =>
        {
            if (_qqFavoriteAutoImport) _ = ImportQqCoreAsync(new[] { model }, raiseCompleted: false);
        };
        _ = service.StartAsync();
        return service;
    }

    /// <summary>按 config 建立全部绑定（启动时调用；重绑前先 Dispose 旧服务）。</summary>
    private void InitializeQqBindings(List<QqBindingInfo> bindings, List<string> dismissed)
    {
        foreach (var service in _qqServices.Values) service.Dispose();
        _qqServices.Clear();
        _qqMirrorViews.Clear();
        _qqBindings.Clear();
        _qqBindings.AddRange(bindings);
        _qqPromptDismissedUins = dismissed;
        foreach (var binding in _qqBindings) _qqServices[binding.Uin] = CreateQqService(binding);
        UpdateTabTags();
    }

    public async Task BindQqAccountsAsync(IEnumerable<string> uins)
    {
        bool changed = false;
        foreach (var uin in uins)
        {
            if (_qqServices.ContainsKey(uin)) continue;
            var binding = new QqBindingInfo { Uin = uin, Alias = DefaultQqAlias(uin), BoundAt = DateTime.Now };
            _qqBindings.Add(binding);
            _qqServices[uin] = CreateQqService(binding);
            changed = true;
        }
        if (!changed) return;
        UpdateTabTags();
        await SaveConfigAsync();
    }

    /// <summary>解绑：只摘镜像与选项卡，Library 已导入的副本不动（导入即出库原则）。</summary>
    public async Task UnbindQqAccountAsync(string uin)
    {
        var binding = _qqBindings.FirstOrDefault(b => b.Uin == uin);
        if (binding == null) return;
        _qqBindings.Remove(binding);
        if (_qqServices.Remove(uin, out var service)) service.Dispose();
        _qqMirrorViews.Remove(uin);
        if (CurrentQqUin == uin) SelectedTab = "最近";
        UpdateTabTags();
        await SaveConfigAsync();
    }

    public async Task RenameQqAccountAsync(string uin, string alias)
    {
        var binding = _qqBindings.FirstOrDefault(b => b.Uin == uin);
        if (binding == null) return;
        binding.Alias = alias.Trim();
        UpdateTabTags();
        await SaveConfigAsync();
    }

    public async Task DismissQqPromptAsync(string uin)
    {
        if (!_qqPromptDismissedUins.Contains(uin)) _qqPromptDismissedUins.Add(uin);
        await SaveConfigAsync();
    }

    // WebP 环境警告：用户处理过（知道了/打开安装页）就永久记住；环境改善后本就不该再弹
    private bool _webpNoticeDismissed;
    public bool WebpNoticeDismissed => _webpNoticeDismissed;

    public async Task DismissWebpNoticeAsync()
    {
        if (_webpNoticeDismissed) return;
        _webpNoticeDismissed = true;
        await SaveConfigAsync();
    }

    // WebP/WIC 解码支持（设置页「资源库」状态行）：真实解码探测的结果 + 手动重新检测
    private bool _webpSupported = WebpSupportProbe.IsSupported;
    public bool WebpSupported => _webpSupported;

    public string WebpStatusText => _webpSupported
        ? "已支持：系统可正常解码 WebP，拖入的 WebP 会自动转成 PNG 入库"
        : "未安装「WebP 图像扩展」（免费）：WebP 图片暂无法入库；点右侧安装，装好后点「重新检测」";

    public void RecheckWebpSupport()
    {
        _webpSupported = WebpSupportProbe.Recheck();
        OnPropertyChanged(nameof(WebpSupported));
        OnPropertyChanged(nameof(WebpStatusText));
    }

    // ———— QQ 收藏深度同步（Phase B，设计 §4.7/§4.8） ————

    // 总开关：读取 QQ 数据库密钥、解密 emoji.db、对账孤儿表情（默认关）
    private bool _qqDeepSyncEnabled;
    public bool QqDeepSyncEnabled
    {
        get => _qqDeepSyncEnabled;
        set
        {
            if (_qqDeepSyncEnabled == value) return;
            _qqDeepSyncEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QqSyncStrategyVisible));
            OnPropertyChanged(nameof(AdoptBatchButtonVisible));
            NotifyQqDeepSyncStatus();
            _ = SaveConfigAsync();
            if (value)
            {
                _ = InitializeDeepSyncAsync();
            }
            else
            {
                StopKeyWatcher();
            }
        }
    }

    // 同步策略：0=仅标记 1=同步删除 2=挪入图库（发现孤儿那一刻生效，切档不追溯）
    private int _qqSyncStrategy;
    public int QqSyncStrategy
    {
        get => _qqSyncStrategy;
        set
        {
            if (_qqSyncStrategy == value) return;
            _qqSyncStrategy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AdoptBatchButtonVisible)); // 切到挪图库档解锁批量按钮
            _ = SaveConfigAsync();
        }
    }

    public bool QqSyncStrategyVisible => _qqDeepSyncEnabled;

    // 批量收编入口：仅在「挪入图库」档下解锁（用户主动点击，不点则无事发生）
    public bool AdoptBatchButtonVisible => _qqDeepSyncEnabled && _qqSyncStrategy == 2;

    /// <summary>设置页的状态行：深度同步开关在不同阶段的一句话（含密钥获取结果）。
    /// 密钥获取进行中的瞬态提示优先显示（脚本分析 QQ 模块到弹登录窗要 1-2 分钟，必须给等待反馈）。</summary>
    public string QqDeepSyncStatusText
    {
        get
        {
            if (!_qqDeepSyncEnabled) return "推荐想自动识别「已在 QQ 取消收藏的表情」时开启；密钥只保存在本机";
            if (_keyFlowStatus != null) return _keyFlowStatus;
            if (_deepSyncSummary != null) return "已开启 · " + _deepSyncSummary;
            if (!string.IsNullOrEmpty(_qqDbKey)) return "已开启 · 密钥获取成功，收藏索引读取就绪";
            return _keyWatcher != null
                ? "已开启 · 正在等待下一次 QQ 登录以自动读取密钥（保持飞鸟在托盘运行）"
                : "已开启 · 待读取密钥：在设置里打开后会引导选择读取方式";
        }
    }

    private void NotifyQqDeepSyncStatus() => OnPropertyChanged(nameof(QqDeepSyncStatusText));

    // 密钥获取进行中的瞬态状态（null=无进行中流程）；CompleteKeyAcquisition 收尾时清除
    private string? _keyFlowStatus;

    // 最近一次深度同步对账的结论（null=还没跑过），随状态行展示
    private string? _deepSyncSummary;

    /// <summary>主窗口在密钥流各阶段调用，把进度写进设置页状态行。</summary>
    public void SetKeyFlowStatus(string? text)
    {
        _keyFlowStatus = text;
        NotifyQqDeepSyncStatus();
    }

    // 三档策略的分段控件绑定（数值属性的双向包装）
    public bool StrategyIsMark
    {
        get => _qqSyncStrategy == 0;
        set { if (value) QqSyncStrategy = 0; }
    }
    public bool StrategyIsRemove
    {
        get => _qqSyncStrategy == 1;
        set { if (value) QqSyncStrategy = 1; }
    }
    public bool StrategyIsAdopt
    {
        get => _qqSyncStrategy == 2;
        set { if (value) QqSyncStrategy = 2; }
    }

    /// <summary>批量收编：把当前所有孤儿一次性收进图库（收编成功或已重复的都出镜像）。</summary>
    /// <returns>(实际新增数, 已在图库被跳过数)</returns>
    public async Task<(int Adopted, int AlreadyInLibrary)> AdoptAllOrphansAsync()
    {
        int adopted = 0, skipped = 0;
        foreach (var service in _qqServices.Values.ToList())
        {
            // 快照：收编会修改 Mirror
            foreach (var orphan in service.Mirror.Where(m => m.IsOrphaned).ToList())
            {
                if (await CopyQqStickerToLibraryAsync(orphan)) adopted++;
                else skipped++;
                service.Mirror.Remove(orphan); // 无论新增还是重复，孤儿都完成使命出镜像
            }
        }
        return (adopted, skipped);
    }

    /// <summary>删除 QQ 缓存残留（孤儿右键，2026-09-30 用户定案）：QQ 已取消收藏后 Ori 里的文件是纯残留
    /// （P1 探针证实 QQ 不清理），删除文件让它出镜像；QQ 仍收藏的条目绝不提供删除（watcher 会拉回来）。</summary>
    public async Task<bool> DeleteQqOrphanAsync(QqStickerModel sticker)
    {
        if (sticker == null || !sticker.IsOrphaned) return false;
        try
        {
            if (File.Exists(sticker.FullPath)) await Task.Run(() => File.Delete(sticker.FullPath));
            if (_qqServices.TryGetValue(sticker.Uin, out var service)) service.Mirror.Remove(sticker);
            return true;
        }
        catch { return false; }
    }

    /// <summary>通用复制到图库（QQ 页右键）：与 QQ 导入同链路但不带「QQ」标签；MD5 命中即静默跳过。</summary>
    public async Task<bool> CopyQqStickerToLibraryAsync(QqStickerModel sticker)
    {
        if (sticker == null || string.IsNullOrEmpty(sticker.Md5) || !File.Exists(sticker.FullPath)) return false;
        var knownMd5 = Stickers.Where(s => !string.IsNullOrEmpty(s.Md5)).Select(s => s.Md5!).ToHashSet(StringComparer.Ordinal);
        if (knownMd5.Contains(sticker.Md5)) return false; // 已在图库：静默跳过（语义定案）

        string library = EnsureLibraryFolder();
        if (!ImageSniffer.TryGetImportExtension(sticker.FullPath, out var ext, out var kind)) return false;
        var destination = Path.Combine(library, sticker.Md5 + ext);
        if (File.Exists(destination)) { CancelPendingDelete(destination); return false; } // 命中待删旧文件=用户重新入库，销单放行

        destination = await ImageSniffer.MaterializeAsync(sticker.FullPath, library, sticker.Md5, kind);
        var copy = new StickerModel
        {
            FullPath = destination,
            Md5 = sticker.Md5,
            Tags = new List<string>(), // 通用接口：不带「QQ」默认标签（用户定案）
            LastUsedTime = sticker.FileMtime == DateTime.MinValue ? DateTime.Now : sticker.FileMtime,
        };
        Stickers.Add(copy);
        await SaveDatabaseAsync();
        sticker.IsImported = true;
        return true;
    }

    private async Task InitializeDeepSyncAsync()
    {
        try
        {
            var count = await RunDeepSyncAsync();
            if (count == -2) return; // 已有对账在跑（watcher 回填并发触发），它会自己更新状态行
            if (count >= 0)
            {
                StatusText = count > 0
                    ? $"QQ 收藏对账完成：发现 {count} 个缓存残留"
                    : "QQ 收藏对账完成：没有缓存残留";
                return;
            }

            // 对账失败 → 旧密钥作废，走一次完整的读取引导（用户可取消=自动关深度同步）。
            // 2026-09-30 用户定案：有密钥但对不上账时，不能"已有密钥"一句话堵死重读的路。
            _deepSync = null;
            _qqDbKey = "";
            _keyTcs = null;
            NotifyQqDeepSyncStatus();
            StatusText = "QQ 收藏对账失败：密钥可能已失效，请重新读取";
            var key = await AcquireDbKeyAsync(); // 弹三选引导并等待结果
            if (!string.IsNullOrEmpty(key))
            {
                count = await RunDeepSyncAsync();
                StatusText = count > 0
                    ? $"QQ 收藏对账完成：发现 {count} 个缓存残留"
                    : count == 0 ? "QQ 收藏对账完成：没有缓存残留" : "QQ 收藏对账仍失败：QQ 版本结构可能已变化";
            }
        }
        catch { /* 降级已在 DeepSync 内处理 */ }
    }

    private QqDeepSyncService? _deepSync;
    private TaskCompletionSource<string?>? _keyTcs;
    private QqKeyWatchService? _keyWatcher;

    /// <summary>取密钥：已有就用；没有则触发 UI 引导（三选：立即读 / 下次自动 / 取消）。</summary>
    private async Task<string?> AcquireDbKeyAsync()
    {
        if (!string.IsNullOrEmpty(_qqDbKey)) return _qqDbKey;
        _keyTcs ??= new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyAcquisitionRequested?.Invoke(this, EventArgs.Empty);
        return await _keyTcs.Task;
    }

    /// <summary>主窗口完成获取流程后回填密钥（watcher 抓到也走这里）。</summary>
    public void CompleteKeyAcquisition(string? key)
    {
        _keyFlowStatus = null; // 清瞬态状态（下面的 Notify 会刷新状态行到终态）
        _qqDbKey = key ?? "";
        if (key != null) _keyTcs?.TrySetResult(key);
        else _keyTcs?.TrySetResult(null);
        _keyTcs = null;
        NotifyQqDeepSyncStatus(); // 设置页状态行即时反映"密钥获取成功"
        _ = SaveConfigAsync();
        // watcher 静默抓到密钥（「下次自动」路径）→ 立刻对账出角标，不等下次重启；
        // 打开开关的引导流唤醒后自己会跑一次，由 _reconcileBusy 护栏去重
        if (key != null && _qqDeepSyncEnabled) _ = RunStartupReconcileIfDueAsync();
    }

    public event EventHandler? KeyAcquisitionRequested;

    /// <summary>静默抓取：监视 QQ 进程启动，附加调试器在登录瞬间抓密钥（QQ 无感）。</summary>
    public void StartKeyWatcher()
    {
        if (string.IsNullOrEmpty(_qqDbKey) == false) return;
        _keyWatcher ??= new QqKeyWatchService(
            key => _uiDispatcher.Invoke(() =>
            {
                CompleteKeyAcquisition(key);
                StatusText = "QQ 收藏索引密钥已自动获取，深度同步已就绪";
            }),
            msg => QqPanelWatcher.Log("keywatch: " + msg));
        _keyWatcher.Start();
        NotifyQqDeepSyncStatus(); // "正在等待下一次 QQ 登录"
    }

    public void StopKeyWatcher()
    {
        _keyWatcher?.Dispose();
        _keyWatcher = null;
        NotifyQqDeepSyncStatus();
    }

    /// <summary>启动时恢复：深度同步开着但密钥为空 → 静默续抓（跨开关机等待下次登录）。</summary>
    public void ResumeKeyWatcherIfPending()
    {
        if (_qqDeepSyncEnabled && string.IsNullOrEmpty(_qqDbKey)) StartKeyWatcher();
    }

    // 持久化的密钥（QQ 大版本更新后可能失效，失效时重新引导）
    private string _qqDbKey = "";
    public string QqDbKey => _qqDbKey;

    private async Task<QqDeepSyncService?> GetOrCreateDeepSyncAsync()
    {
        if (_deepSync != null) return _deepSync;
        var key = await AcquireDbKeyAsync();
        if (string.IsNullOrEmpty(key)) return null;
        _deepSync = new QqDeepSyncService(key,
            uin => _qqServices.TryGetValue(uin, out var s) ? s : null,
            async sticker => await CopyQqStickerToLibraryAsync(sticker));
        return _deepSync;
    }

    // ———— AI 视觉标签建议（用户自备 API Key；配置入口在设置 → AI 识别）————

    /// <summary>共享识别客户端（用户手动触发的低频调用，无需连接池调优）。</summary>
    public AiTagService AiTag { get; } = new();

    private string _aiTagApiKey = "";
    /// <summary>服务商 API Key（明文本机存储，与 QqDbKey 同口径；不上传任何服务器）。
    /// 修改 Key 意味着"正在录入另一份配置"：脱离激活档案进入草稿态，测试通过后另存为新档案。</summary>
    public string AiTagApiKey
    {
        get => _aiTagApiKey;
        set
        {
            value = value?.Trim() ?? "";
            if (_aiTagApiKey == value) return;
            _aiTagApiKey = value;
            OnPropertyChanged();
            if (!_aiProfileSwitching) DetachActiveProfile();
            NotifyAiConfigState();
            _ = SaveConfigAsync();
        }
    }

    private string _aiTagProvider = "auto";
    /// <summary>服务商 id：auto=按 Key 前缀自动识别；其余见 AiTagService.Providers。
    /// 修改服务商同样脱离激活档案（与 Key 一起构成一份新配置）。</summary>
    public string AiTagProvider
    {
        get => _aiTagProvider;
        set
        {
            value = string.IsNullOrEmpty(value) ? "auto" : value!;
            if (_aiTagProvider == value) return;
            _aiTagProvider = value;
            OnPropertyChanged();
            if (!_aiProfileSwitching) DetachActiveProfile();
            NotifyAiConfigState();
            _ = SaveConfigAsync();
        }
    }

    /// <summary>改 Key/服务商 = 录入新配置：脱离激活档案（草稿态，档案原值不动）。</summary>
    private void DetachActiveProfile()
    {
        if (_aiActiveProfileId.Length == 0) return;
        _aiActiveProfileId = "";
        OnPropertyChanged(nameof(AiActiveProfileId));
    }

    private string _aiTagModel = "";
    /// <summary>模型名：空 = 用服务商默认视觉模型。</summary>
    public string AiTagModel
    {
        get => _aiTagModel;
        set
        {
            value = value?.Trim() ?? "";
            if (_aiTagModel == value) return;
            _aiTagModel = value;
            OnPropertyChanged();
            _ = SaveConfigAsync();
        }
    }

    private string _aiTagBaseUrl = "";
    /// <summary>自定义服务商的接口地址（OpenAI 兼容 /chat/completions 根，如 https://xxx/v1）。</summary>
    public string AiTagBaseUrl
    {
        get => _aiTagBaseUrl;
        set
        {
            value = value?.Trim() ?? "";
            if (_aiTagBaseUrl == value) return;
            _aiTagBaseUrl = value;
            OnPropertyChanged();
            _ = SaveConfigAsync();
        }
    }

    private string _aiTagEffort = "";
    /// <summary>思考强度档位（空=服务商默认）。档位集合随服务商（AiProviderDef.EffortLevels），非法档位由 400 去参重试兜底。</summary>
    public string AiTagEffort
    {
        get => _aiTagEffort;
        set
        {
            value = value?.Trim() ?? "";
            if (_aiTagEffort == value) return;
            _aiTagEffort = value;
            OnPropertyChanged();
            if (_aiProfileSwitching) return;
            _activeProfile?.Effort = value; // 模型/强度是使用偏好：即时写回激活档案
            _ = SaveConfigAsync();
        }
    }

    // ———— AI Key 档案（多 Key 管理：测试通过 → 命名保存 → 随时切换）————

    /// <summary>档案列表为持久事实源；工作配置（上面的 AiTag* 属性）是编辑区当前值。</summary>
    public List<AiKeyProfile> AiKeyProfiles { get; } = new();

    private string _aiActiveProfileId = "";
    public string AiActiveProfileId => _aiActiveProfileId;

    private AiKeyProfile? _activeProfile => AiKeyProfiles.FirstOrDefault(p => p.Id == _aiActiveProfileId);

    /// <summary>档案切换/迁移期间置 true：工作配置 setter 不做脱钩、不重复写档案。</summary>
    private bool _aiProfileSwitching;

    /// <summary>把工作配置四字段 + 思考强度写入指定档案。</summary>
    private void CopyWorkConfigToProfile(AiKeyProfile p)
    {
        p.ProviderId = _aiTagProvider;
        p.ApiKey = _aiTagApiKey;
        p.BaseUrl = _aiTagBaseUrl;
        p.Model = _aiTagModel;
        p.Effort = _aiTagEffort;
    }

    /// <summary>激活某档案：档案值写回工作配置（触发 UI 刷新）。</summary>
    public void ActivateAiProfile(string id)
    {
        var p = AiKeyProfiles.FirstOrDefault(x => x.Id == id);
        if (p == null || _aiActiveProfileId == id) return;
        _aiProfileSwitching = true;
        try
        {
            _aiActiveProfileId = id;
            AiTagProvider = p.ProviderId;
            AiTagApiKey = p.ApiKey;
            AiTagBaseUrl = p.BaseUrl;
            AiTagModel = p.Model;
            AiTagEffort = p.Effort;
        }
        finally { _aiProfileSwitching = false; }
        OnPropertyChanged(nameof(AiActiveProfileId));
        NotifyAiConfigState();
        _ = SaveConfigAsync();
    }

    /// <summary>删除档案；删的是激活档案则回到草稿态（工作配置保持原值，便于改完重存）。</summary>
    public void DeleteAiProfile(string id)
    {
        var p = AiKeyProfiles.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        AiKeyProfiles.Remove(p);
        if (_aiActiveProfileId == id)
        {
            _aiActiveProfileId = "";
            OnPropertyChanged(nameof(AiActiveProfileId));
        }
        NotifyAiConfigState();
        _ = SaveConfigAsync();
    }

    /// <summary>把当前工作配置以给定别名存为新档案并激活（「测试并保存」通过后调用；Key 已验证）。</summary>
    public AiKeyProfile SaveCurrentAsProfile(string name, IReadOnlyList<string> detectedModels)
    {
        var p = new AiKeyProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名 Key" : name.Trim(),
            VerifiedAt = DateTime.Now,
            DetectedModels = detectedModels.ToList(),
        };
        CopyWorkConfigToProfile(p);
        AiKeyProfiles.Add(p);
        _aiProfileSwitching = true;
        try { _aiActiveProfileId = p.Id; }
        finally { _aiProfileSwitching = false; }
        OnPropertyChanged(nameof(AiActiveProfileId));
        NotifyAiConfigState();
        _ = SaveConfigAsync();
        return p;
    }

    /// <summary>更新激活档案的检测结果（「检测可用视觉模型」重测后）。</summary>
    public void UpdateActiveProfileDetectedModels(IReadOnlyList<string> models)
    {
        var p = _activeProfile;
        if (p == null) return;
        p.DetectedModels = models.ToList();
        p.VerifiedAt = DateTime.Now;
        _ = SaveConfigAsync();
    }

    /// <summary>草稿态提示：有 Key 但不属于任何档案（改过 Key/服务商，或删掉了激活档案）。</summary>
    public bool AiHasUnsavedDraft => _activeProfile == null && _aiTagApiKey.Length > 0;

    /// <summary>配置变更后需要联动刷新的派生显示（状态行/引导/预设），统一补通知。</summary>
    private void NotifyAiConfigState()
    {
        OnPropertyChanged(nameof(AiSelectedProvider));
        OnPropertyChanged(nameof(AiStatusText));
        OnPropertyChanged(nameof(AiProviderGuide));
        OnPropertyChanged(nameof(AiProviderGuideUrl));
        OnPropertyChanged(nameof(AiProviderGuideVisible));
        OnPropertyChanged(nameof(AiModelPresets));
        OnPropertyChanged(nameof(AiCustomUrlVisible));
        OnPropertyChanged(nameof(AiAutoDetectHint));
        OnPropertyChanged(nameof(AiTagConfigured));
        OnPropertyChanged(nameof(AiHasUnsavedDraft));
        OnPropertyChanged(nameof(AiKeyProfilesView));
        OnPropertyChanged(nameof(AiEffortLevels));
        OnPropertyChanged(nameof(AiEffortVisible));
    }

    /// <summary>档案行的只读视图（列表绑定）。</summary>
    public IReadOnlyList<AiKeyProfile> AiKeyProfilesView => AiKeyProfiles;

    /// <summary>当前服务商的思考强度档位；未声明=不显示控件。</summary>
    public string[] AiEffortLevels => AiSelectedProvider?.EffortLevels ?? Array.Empty<string>();
    public bool AiEffortVisible => AiEffortLevels.Length > 0;

    /// <summary>思考强度档位的显示名（默认=不发送参数）。</summary>
    public static string EffortLabel(string effort) => effort switch
    {
        "" => "默认",
        "low" => "低（快）",
        "medium" => "中",
        "high" => "高",
        "max" => "最大（慢）",
        _ => effort,
    };

    /// <summary>当前生效的服务商定义：auto 时按 Key 识别，识别不出为 null（需手动选）。</summary>
    public AiProviderDef? AiSelectedProvider
    {
        get
        {
            var id = _aiTagProvider == "auto"
                ? AiTagService.DetectProviderId(_aiTagApiKey) ?? ""
                : _aiTagProvider;
            return AiTagService.FindProvider(id);
        }
    }

    public string AiStatusText
    {
        get
        {
            if (_aiTagApiKey.Trim().Length == 0 && _aiTagProvider != "ollama")
                return "未配置：填入 API Key 即可启用";
            var p = AiSelectedProvider;
            if (p == null) return "已填 Key：无法识别服务商，请在下方手动选择";
            var model = _aiTagModel.Length > 0 ? _aiTagModel : p.DefaultModel;
            return $"已就绪：{p.Name} · {model}";
        }
    }

    /// <summary>具体服务商的引导文案（auto/未配置时隐藏，避免噪音）。</summary>
    public string AiProviderGuide => _aiTagProvider == "auto" ? "" : AiSelectedProvider?.Guide ?? "";
    public string AiProviderGuideUrl => _aiTagProvider == "auto" ? "" : AiSelectedProvider?.GuideUrl ?? "";
    public bool AiProviderGuideVisible => _aiTagProvider != "auto" && AiSelectedProvider != null;
    public bool AiCustomUrlVisible => _aiTagProvider == "custom";
    public string[] AiModelPresets => _aiTagProvider == "auto" ? Array.Empty<string>() : AiSelectedProvider?.Models ?? Array.Empty<string>();

    /// <summary>AI 识别是否已可使用（有 Key，或选择了免 Key 的本地服务商）。</summary>
    public bool AiTagConfigured => _aiTagApiKey.Trim().Length > 0 || _aiTagProvider == "ollama";

    /// <summary>自动识别一行说明：识别成功报哪家；sk- 开头各家通用识别不出时请用户手动挑。</summary>
    public string AiAutoDetectHint
    {
        get
        {
            if (_aiTagProvider != "auto") return "";
            if (_aiTagApiKey.Trim().Length == 0) return "填入 Key 后自动识别所属服务商";
            var p = AiSelectedProvider;
            return p != null ? $"已根据 Key 自动识别：{p.Name}" : "常见 sk- 开头的 Key 各服务商通用，无法自动识别——请在下方选择";
        }
    }

    /// <summary>把当前设置解析成一次识别请求的完整参数；未配置/不完整时抛 AiTagException（Message 面向用户）。</summary>
    public AiTagOptions BuildAiTagOptions()
    {
        var id = _aiTagProvider;
        if (id == "auto")
        {
            if (_aiTagApiKey.Trim().Length == 0)
                throw new AiTagException(
                    "还没有配置 AI 识别：请到 设置 → AI 识别 填入 API Key。",
                    "auto+empty key");
            id = AiTagService.DetectProviderId(_aiTagApiKey) ?? throw new AiTagException(
                "无法从 API Key 识别服务商：请到 设置 → AI 识别 手动选择 Key 所属的服务商。",
                $"auto detect failed keylen={_aiTagApiKey.Length}");
        }
        return AiTagService.BuildOptions(id, _aiTagApiKey, _aiTagModel, _aiTagBaseUrl, _aiTagEffort);
    }

    /// <summary>确保表情 Md5 就绪（旧数据惰性补算的即时版；AI 缓存键靠它区分同图副本）。</summary>
    public async Task EnsureMd5Async(StickerModel sticker)
    {
        if (!string.IsNullOrEmpty(sticker.Md5) || !File.Exists(sticker.FullPath)) return;
        try
        {
            sticker.Md5 = await ComputeMd5Async(sticker.FullPath);
            _ = SaveDatabaseAsync();
        }
        catch { /* 文件恰好被占用：缓存退化用路径键，不影响识别 */ }
    }

    /// <summary>
    /// 取 AI 标签建议：缓存优先（同 Md5 只识别一次，除非 forceRefresh）；
    /// 未命中才发起网络请求并写缓存。识别失败抛 AiTagException（Message 面向用户）。
    /// </summary>
    public async Task<(AiTagResult Result, bool FromCache)> GetAiSuggestionsAsync(
        StickerModel sticker, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            var hit = AiTagCache.Get(AiTagCache.CacheKey(sticker));
            if (hit != null)
                return (new AiTagResult(hit.Tags, hit.ProviderId, hit.Model, hit.CreatedAt), true);
        }
        await EnsureMd5Async(sticker);
        var key = AiTagCache.CacheKey(sticker); // Md5 补算后键可能从路径键升级为内容键
        if (!forceRefresh)
        {
            var hit = AiTagCache.Get(key);
            if (hit != null)
                return (new AiTagResult(hit.Tags, hit.ProviderId, hit.Model, hit.CreatedAt), true);
        }
        var opt = BuildAiTagOptions();
        var result = await AiTag.SuggestTagsAsync(sticker.FullPath, opt, AllExistingTags, ct);
        AiTagCache.Put(key, result.Tags, result.ProviderId, result.Model);
        return (result, false);
    }

    /// <summary>按当前配置拉取并探测服务商的全部可用视觉模型（设置页「检测可用视觉模型」按钮）。
    /// 探测会对清单内每个候选发一次 8×8 小图请求（极小开销）；失败抛 AiTagException（Message 面向用户）。</summary>
    public async Task<List<string>> FetchAiVisionModelsAsync(CancellationToken ct = default)
    {
        var opt = BuildAiTagOptions();
        return await AiTag.ListVisionModelsAsync(opt, ct);
    }


    /// <summary>
    /// 执行一次对账：返回发现的孤儿数；-1 = 失败（密钥失效/QQ 结构变化）；-2 = 已有对账在跑（本次跳过）。
    /// 结果写入 _deepSyncSummary 反映到设置页状态行（2026-09-30 用户反馈"开了却没标记也没说法"）。
    /// </summary>
    private int _reconcileBusy; // 0/1 护栏：引导流唤醒与 watcher 回填可能并发触发对账
    public async Task<int> RunDeepSyncAsync()
    {
        if (Interlocked.CompareExchange(ref _reconcileBusy, 1, 0) != 0) return -2;
        try
        {
            var deep = await GetOrCreateDeepSyncAsync();
            if (deep == null)
            {
                _deepSyncSummary = "上次对账未执行：未获取到数据库密钥";
                NotifyQqDeepSyncStatus();
                return -1;
            }
            var count = await deep.ReconcileAllAsync(
                _qqBindings.Select(b => b.Uin).ToList(),
                (QqSyncStrategy)QqSyncStrategy);
            if (count < 0)
            {
                _deepSync = null; // 失败降级：下次重试（可能要重取密钥）
                _deepSyncSummary = "上次对账失败：密钥可能已失效，重新读取后可再对账";
            }
            else
            {
                _deepSyncSummary = count > 0
                    ? $"上次对账完成：发现 {count} 个缓存残留（QQ 页已打角标）"
                    : "上次对账完成：没有发现缓存残留";
            }
            NotifyQqDeepSyncStatus();
            return count;
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileBusy, 0);
        }
    }

    /// <summary>启动/watcher 拿到密钥后的静默对账：绝不弹密钥引导（不打扰），
    /// 失败只落状态行，用户可用「立即对账」走完整引导。2026-10-02 用户实测：
    /// 对账此前只在打开开关那一刻执行，重启后 38 个真实残留零角标。</summary>
    public async Task RunStartupReconcileIfDueAsync()
    {
        if (!_qqDeepSyncEnabled || string.IsNullOrEmpty(_qqDbKey)) return;
        if (_qqBindings.Count > 0 && _qqServices.Count == 0) return; // 加载竞态：绑定服务还没建，等下次触发
        try { await RunDeepSyncAsync(); } catch { /* 良性：状态行如实记录 */ }
    }

    /// <summary>设置页「立即对账」入口：与打开开关同一条链路（失败会引导重读密钥）。</summary>
    public Task ReconcileNowAsync() => InitializeDeepSyncAsync();

    /// <summary>
    /// 启动询问候选：猜出的当前账号若未绑定且没被"不再询问"过，返回它（弹一次询问）。
    /// 猜错无后果——用户拒绝即可，设置里的绑定对话框是正门。
    /// </summary>
    public async Task<QqAccountScanResult?> GetStartupPromptCandidateAsync()
    {
        var accounts = await QqEmojiService.ScanAccountsAsync();
        var guess = QqEmojiService.GuessCurrentAccount(accounts);
        if (guess == null) return null;
        if (_qqBindings.Any(b => b.Uin == guess.Uin)) return null;
        if (_qqPromptDismissedUins.Contains(guess.Uin)) return null;
        return guess;
    }

    private void UpdateImportedFlags(QqEmojiService service)
    {
        // 图库按 Md5 建一次索引：镜像逐条 FirstOrDefault 是 O(镜像×图库)，在 UI 线程上放大明显
        var twinsByMd5 = new Dictionary<string, StickerModel>(StringComparer.Ordinal);
        foreach (var s in Stickers)
        {
            if (!string.IsNullOrEmpty(s.Md5) && !twinsByMd5.ContainsKey(s.Md5))
                twinsByMd5[s.Md5] = s; // 首条优先，与 FirstOrDefault 一致
        }
        foreach (var item in service.Mirror)
        {
            twinsByMd5.TryGetValue(item.Md5 ?? "", out var twin);
            item.IsImported = twin != null;
            // 借入标签：图库同款副本的标签供 QQ 页悬浮页脚显示（自身标签优先，见 EffectiveTags）
            item.BorrowedTags = twin?.Tags;
        }
    }

    /// <summary>图库标签编辑/表情删除后调用：刷新 QQ 页镜像的已入库角标与借入标签。</summary>
    public void RefreshQqMirrorFlags()
    {
        foreach (var service in _qqServices.Values) UpdateImportedFlags(service);
    }

    // ———— QQ 页搜索分桶（2026-10-01 用户定案：QQ 页搜索不过滤、按命中优先排序）————
    // QQ 页可能有用户标注的标签（镜像自身暂无标签时，按 Md5 对账"借"图库同款副本的标签判命中）：
    // 搜索词生效时命中桶永远在前、未命中桶永远在后，两桶内部都按时序+频率（RankScore）排序；
    // 搜索词为空时整体就是热度序，与无搜索一致。

    /// <summary>QQ 页网格的数据源：镜像的分桶排序视图（每账号一个，缓存复用）。</summary>
    public ListCollectionView GetQqMirrorView(string uin)
    {
        if (!_qqMirrorViews.TryGetValue(uin, out var view))
        {
            view = new ListCollectionView(_qqServices[uin].Mirror)
            {
                CustomSort = Comparer<QqStickerModel>.Create(CompareQqMirror),
            };
            _qqMirrorViews[uin] = view;
        }
        return view;
    }

    private int CompareQqMirror(QqStickerModel a, QqStickerModel b)
    {
        if (!string.IsNullOrWhiteSpace(_searchText) && a.SearchHit != b.SearchHit)
            return a.SearchHit ? -1 : 1; // 命中桶恒在前
        var byScore = b.RankScore.CompareTo(a.RankScore);
        return byScore != 0 ? byScore : b.LastUsedTime.CompareTo(a.LastUsedTime);
    }

    /// <summary>搜索词变化时重算各账号镜像的命中标记并重排（比较器只读标记，命中计算集中在此一次）。</summary>
    private void ApplyQqSearchBuckets()
    {
        if (_qqServices.Count == 0) return;

        // 图库标签按 Md5 建索引：镜像条目借用图库同款副本的标签判命中
        var libTagsByMd5 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Stickers)
        {
            if (!string.IsNullOrEmpty(s.Md5)) libTagsByMd5[s.Md5] = s.Tags;
        }

        foreach (var kv in _qqServices)
        {
            foreach (var m in kv.Value.Mirror)
            {
                bool hit = false;
                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    hit = m.Tags.Any(t => t.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
                    if (!hit && m.Md5 != null && libTagsByMd5.TryGetValue(m.Md5, out var tags))
                        hit = tags.Any(t => t.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
                }
                m.SearchHit = hit;
            }
            GetQqMirrorView(kv.Key).Refresh();
        }
    }

    private void UpdateImportedFlagsAll()
    {
        foreach (var service in _qqServices.Values) UpdateImportedFlags(service);
    }

    // ———— QQ 表情使用统计（快捷面板 frecency 的镜像侧输入）————
    // QQ 收藏镜像不落 stickers.json（导入即出库原则），但快捷面板要与图库同一把热度尺子，
    // 未入库镜像的发送次数/最近活跃单独存 qq-stats.json，键 = 内容 MD5。

    private sealed class QqUsageStat
    {
        public int N { get; set; }          // 累计发送次数
        public DateTime T { get; set; }     // 最近一次发送时间
    }

    private Dictionary<string, QqUsageStat> _qqStats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _qqStatsWriteLock = new(1, 1);

    private static string QqStatsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "OICQStickerManager", "qq-stats.json");

    private async Task LoadQqStatsAsync()
    {
        try
        {
            if (!File.Exists(QqStatsPath)) return;
            var json = await File.ReadAllTextAsync(QqStatsPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, QqUsageStat>>(json);
            if (loaded == null) return;
            _qqStats = new Dictionary<string, QqUsageStat>(loaded, StringComparer.OrdinalIgnoreCase);
            // 统计晚于镜像到位的情况（QQ 扫描先完成）：补应用到已就绪的镜像
            foreach (var service in _qqServices.Values) ApplyQqStats(service);
        }
        catch { /* 统计损坏按无统计处理，发送后会被新数据覆盖 */ }
    }

    /// <summary>把持久化的使用统计套到镜像条目上（RankScore 随之生效，与图库副本同尺）。</summary>
    private void ApplyQqStats(QqEmojiService service)
    {
        foreach (var item in service.Mirror)
        {
            if (!string.IsNullOrEmpty(item.Md5) && _qqStats.TryGetValue(item.Md5, out var stat))
            {
                item.UseCount = stat.N;
                if (stat.T > DateTime.MinValue) item.LastUsedTime = stat.T;
            }
        }
    }

    private async Task SaveQqStatsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(QqStatsPath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_qqStats);
            await WriteJsonWithBackupAsync(QqStatsPath, json, _qqStatsWriteLock);
        }
        catch { /* 统计落盘失败不拖累发送链路，下次发送再写 */ }
    }

    // ———— QQ 表情导入（复制转正：详见设计文档 §4.6） ————

    public RelayCommand<QqStickerModel> ImportQqStickerCommand { get; }
    public RelayCommand ImportAllQqCommand { get; }

    private async Task ImportQqStickerAsync(QqStickerModel? sticker)
    {
        if (sticker == null) return;
        var report = await ImportQqCoreAsync(new[] { sticker });
        QqImportCompleted?.Invoke(report.Added, report.Duplicates, report.Unsupported);
    }

    private async Task ImportAllQqAsync()
    {
        var service = CurrentQqService;
        if (service == null) return;
        var report = await ImportQqCoreAsync(service.Mirror.ToList());
        QqImportCompleted?.Invoke(report.Added, report.Duplicates, report.Unsupported);
    }

    private async Task<ImportReport> ImportQqCoreAsync(IEnumerable<QqStickerModel> stickers, bool raiseCompleted = true)
    {
        var report = new ImportReport();
        string library = EnsureLibraryFolder();
        var knownMd5 = Stickers.Where(s => !string.IsNullOrEmpty(s.Md5)).Select(s => s.Md5!).ToHashSet(StringComparer.Ordinal);

        foreach (var item in stickers)
        {
            try
            {
                if (string.IsNullOrEmpty(item.Md5)) { report.Unsupported++; continue; } // 无 MD5 无法去重命名，按不支持处理
                if (knownMd5.Contains(item.Md5)) { item.IsImported = true; report.Duplicates++; continue; }
                if (!File.Exists(item.FullPath)) continue; // QQ 可能已清理缓存
                // 魔数定落盘扩展名；WebP 解得动就转 PNG 落库，解不动才跳过计数
                if (!ImageSniffer.TryGetImportExtension(item.FullPath, out var ext, out var kind)) { report.Unsupported++; continue; }

                var destination = Path.Combine(library, item.Md5 + ext);
                if (File.Exists(destination)) { CancelPendingDelete(destination); item.IsImported = true; report.Duplicates++; continue; } // 命中待删旧文件=用户重新入库，销单放行

                destination = await ImageSniffer.MaterializeAsync(item.FullPath, library, item.Md5, kind);

                knownMd5.Add(item.Md5);
                item.IsImported = true;
                // 导入即转正：自动带「QQ」来源标签（可自由增删）；LastUsedTime 取收藏时间（文件 mtime）
                var sticker = new StickerModel
                {
                    FullPath = destination,
                    Md5 = item.Md5,
                    Tags = new List<string> { "QQ" },
                    LastUsedTime = item.FileMtime == DateTime.MinValue ? DateTime.Now : item.FileMtime,
                };
                report.Added.Add(sticker);
                Stickers.Add(sticker);
            }
            catch { /* 单张失败不拖累批次 */ }
        }

        if (report.Added.Count > 0)
        {
            await SaveDatabaseAsync();
            UpdateTabTags();
            RecalcRecentPinned();      // 新入库 = 最新使用，应进入"最近置顶"集
            _stickersView.Refresh();
        }
        // M4 静默导入（自动入库）不触发完成事件，避免弹出标签编辑器打扰
        if (raiseCompleted) QqImportCompleted?.Invoke(report.Added, report.Duplicates, report.Unsupported);
        return report;
    }

    // ———— 待删除队列（半删除兜底；2026-10-03 用户定案）————
    // 删除时物理文件被占用（GIF 预览/杀毒扫描等）：记录已从图库移除、UI 即时消失，
    // 文件记入待删队列，30 秒后与下次启动各重试一轮；期间物理收编跳过这些文件，
    // 防止“半删除”文件被重新收编成无标签表情。删除成功即销单——不是永久删除意图，
    // 文件被重新入库占用时也销单放行，用户加回同一张图不受任何干扰。

    private sealed record PendingDelete(string Path, string? Md5, DateTime RequestedAt);

    private List<PendingDelete> _pendingDeletes = new();
    private static readonly SemaphoreSlim _pendingDeleteWriteLock = new(1, 1);
    private DispatcherTimer? _pendingRetryTimer;

    private static string PendingDeletesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "OICQStickerManager", "pending-deletes.json");

    private void LoadPendingDeletes()
    {
        try
        {
            if (!File.Exists(PendingDeletesPath)) return;
            var json = File.ReadAllText(PendingDeletesPath);
            _pendingDeletes = JsonSerializer.Deserialize<List<PendingDelete>>(json) ?? new();
        }
        catch { _pendingDeletes = new(); /* 损坏按空处理，最坏=残留文件被收编（历史行为） */ }
    }

    private async Task SavePendingDeletesAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(PendingDeletesPath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_pendingDeletes);
            await WriteJsonWithBackupAsync(PendingDeletesPath, json, _pendingDeleteWriteLock);
        }
        catch { /* 落盘失败不拖累删除链路，下次入队再写 */ }
    }

    /// <summary>删除时文件被占用：记入待删队列并安排一轮延迟重试（启动时还有一轮）。</summary>
    public void QueuePendingDelete(string path, string? md5)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_pendingDeletes.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        _pendingDeletes.Add(new PendingDelete(path, md5, DateTime.Now));
        _ = SavePendingDeletesAsync();

        if (_pendingRetryTimer == null)
        {
            _pendingRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _pendingRetryTimer.Tick += async (_, _) =>
            {
                _pendingRetryTimer.Stop();
                await RetryPendingDeletesAsync();
            };
        }
        _pendingRetryTimer.Stop();
        _pendingRetryTimer.Start(); // 无论入队几张，只安排一轮延迟重试
    }

    /// <summary>用户重新入库占用了待删路径（导入落盘命中旧文件）：销单放行。</summary>
    public void CancelPendingDelete(string path)
    {
        var before = _pendingDeletes.Count;
        _pendingDeletes.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (_pendingDeletes.Count != before) _ = SavePendingDeletesAsync();
    }

    /// <summary>
    /// 重试待删队列：删除成功销单；路径已被新记录占用（用户重新入库）销单放行；
    /// 仍被占用则保留，留给下次启动。返回是否仍有残留。
    /// </summary>
    private async Task<bool> RetryPendingDeletesAsync()
    {
        if (_pendingDeletes.Count == 0) return false;

        // 在库路径：待删文件只可能是“已不在图库”的孤儿，被重新入库占用的路径直接销单
        var alivePaths = new HashSet<string>(
            Stickers.Select(s => s.FullPath), StringComparer.OrdinalIgnoreCase);

        var remaining = new List<PendingDelete>();
        foreach (var item in _pendingDeletes.ToList())
        {
            if (alivePaths.Contains(item.Path)) continue; // 销单：文件已归新记录
            var deleted = await Task.Run(() =>
            {
                try { if (File.Exists(item.Path)) File.Delete(item.Path); return true; }
                catch { return false; } // 仍被占用：保留，下次启动再试
            });
            if (!deleted) remaining.Add(item);
        }

        _pendingDeletes = remaining;
        await SavePendingDeletesAsync();
        return _pendingDeletes.Count > 0;
    }

    private async Task LoadStickersAsync()
    {
        StatusText = "正在自动同步图库文件...";

        string libraryPath = EnsureLibraryFolder();

        // 0. 待删队列：启动先重试一轮（此时 Stickers 尚未载入，在库保护集为空=全删）；
        //    仍删不掉的文件在下面收编时跳过，防止“半删除复活成无标签表情”
        LoadPendingDeletes();
        await RetryPendingDeletesAsync();

        // 1. 获取物理文件列表（待删队列中的被占用文件不参与收编）
        var pendingPaths = new HashSet<string>(
            _pendingDeletes.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        var physicalFiles = (await _imageService.GetStickersAsync(libraryPath))
            .Where(f => !pendingPaths.Contains(f.FullPath))
            .ToList();

        // 2. 获取 JSON 记录（损坏时自愈：.bak 恢复 / .bad 留证——这里有全部标签，丢不起）
        List<StickerModel> savedRecords = new();
        if (File.Exists(DbPath))
        {
            try
            {
                var json = await ReadJsonRecoveringAsync(DbPath,
                    s => { try { return JsonSerializer.Deserialize<List<StickerModel>>(s) != null; } catch { return false; } });
                if (json != null)
                    savedRecords = JsonSerializer.Deserialize<List<StickerModel>>(json) ?? new();
            }
            catch { /* 极端情况下仍以物理文件为准 */ }
        }

        // 3. 构建最终集合 (以物理文件为 ID)；JSON 记录按路径建索引，避免文件循环里逐条 FirstOrDefault（O(n²)）
        var recordsByPath = new Dictionary<string, StickerModel>(StringComparer.Ordinal);
        foreach (var r in savedRecords)
            if (!recordsByPath.ContainsKey(r.FullPath)) recordsByPath[r.FullPath] = r; // 重复路径取首条，与 FirstOrDefault 一致
        var finalStickers = new List<StickerModel>();
        foreach (var file in physicalFiles)
        {
            // 查找 JSON 中是否有对应路径的标签记录
            if (recordsByPath.TryGetValue(file.FullPath, out var record))
            if (record != null)
            {
                file.Tags = record.Tags ?? new List<string>();
                file.UseCount = record.UseCount;
                // 记录里有真实的"最近使用"才覆盖；从未使用的旧记录保留 mtime 兜底（见 ImageService），
                // 否则 MinValue 会把老表情压到冷却兜底档
                if (record.LastUsedTime > DateTime.MinValue) file.LastUsedTime = record.LastUsedTime;
                file.Md5 = string.IsNullOrEmpty(record.Md5) ? file.Md5 : record.Md5;
            }

            // MD5 命名规则（<md5>.<ext>）时代之前入库的文件：JSON 没有就从文件名恢复
            if (string.IsNullOrEmpty(file.Md5))
            {
                var stem = Path.GetFileNameWithoutExtension(file.FullPath);
                if (stem.Length == 32 && stem.All(char.IsAsciiHexDigit)) file.Md5 = stem.ToUpperInvariant();
            }

            finalStickers.Add(file); // 物理存在但 JSON 没记录的新表情会以默认形式加载

        }

        // 4. 更新 UI
        Stickers.Clear();
        foreach (var s in finalStickers) Stickers.Add(s);

        StatusText = _pendingDeletes.Count > 0
            ? $"同步完成，共有 {Stickers.Count} 个表情包；另有 {_pendingDeletes.Count} 个被占用文件将在下次启动继续删除"
            : $"同步完成，共有 {Stickers.Count} 个表情包";

        // 5. 自动反向保存一次 JSON，修复差异
        await SaveDatabaseAsync();
        UpdateTabTags();

        // 6. 旧数据（Guid 命名时代）后台补算 MD5，让查重与 QQ 页"已导入"角标全局生效
        _ = BackfillMd5Async();
        UpdateImportedFlagsAll();

        // 7. 数据就绪：算"最近置顶"集并让自定义排序生效
        RecalcRecentPinned();
        _stickersView.Refresh();
        UpdateTabTags();
    }

    private async Task BackfillMd5Async()
    {
        var missing = Stickers.Where(s => string.IsNullOrEmpty(s.Md5) && File.Exists(s.FullPath)).ToList();
        if (missing.Count == 0) return;
        foreach (var sticker in missing)
        {
            try { sticker.Md5 = await ComputeMd5Async(sticker.FullPath); }
            catch { /* 文件恰好被占用，下次启动再补 */ }
        }
        await SaveDatabaseAsync();
        UpdateImportedFlagsAll();
    }

    private static string EnsureLibraryFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "OICQStickerManager", "Library");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ComputeMd5Async(string path)
    {
        return await Task.Run(() =>
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(fs));
        });
    }

    // 💡 拖放入库：魔数定真实格式 + MD5 内容去重 + 以 <md5>.<ext> 命名（设计 §5.1）。
    // 不自动打标签：文件夹名/文件名由窗口作为"第一备选建议"给到标签编辑器，用户点选才加
    public async Task<ImportReport> AddStickersFromPathAsync(string sourcePath)
    {
        var report = new ImportReport();
        string appDataFolder = EnsureLibraryFolder();

        // 1. 获取所有待处理的文件路径
        List<string> filesToProcess = new();
        if (Directory.Exists(sourcePath))
        {
            // 如果是文件夹，获取其中所有支持的图片（与 ImageService 支持的格式保持一致）
            var exts = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
            filesToProcess = Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                                      .Where(f => exts.Contains(Path.GetExtension(f).ToLower()))
                                      .ToList();
        }
        else if (File.Exists(sourcePath))
        {
            filesToProcess.Add(sourcePath);
        }

        var knownMd5 = Stickers.Where(s => !string.IsNullOrEmpty(s.Md5)).Select(s => s.Md5!).ToHashSet(StringComparer.Ordinal);

        // 2. 批量拷贝并创建模型
        foreach (var file in filesToProcess)
        {
            try
            {
                // 魔数嗅探：QQ/网络来源约 1/3 文件后缀错误，落盘以真实格式为准；
                // WebP 解得动就转 PNG 落库，解不动才跳过计数
                if (!ImageSniffer.TryGetImportExtension(file, out var ext, out var kind)) { report.Unsupported++; continue; }

                var md5 = await ComputeMd5Async(file);
                if (!knownMd5.Add(md5)) { report.Duplicates++; continue; }

                string destinationPath = Path.Combine(appDataFolder, md5 + ext);
                if (File.Exists(destinationPath)) { CancelPendingDelete(destinationPath); report.Duplicates++; continue; } // 命中待删旧文件=用户重新入库，销单放行

                destinationPath = await ImageSniffer.MaterializeAsync(file, appDataFolder, md5, kind);

                var sticker = new StickerModel
                {
                    FullPath = destinationPath,
                    Md5 = md5,
                    Tags = new List<string>(),
                    // 入库即活跃基线：新表情按"最近添加"高位起步，之后随时间自然下沉
                    LastUsedTime = DateTime.Now
                };
                report.Added.Add(sticker);
                Stickers.Add(sticker);
            }
            catch { /* 忽略单个文件拷贝失败 */ }
        }

        if (report.Added.Count > 0)
        {
            await SaveDatabaseAsync();
            RecalcRecentPinned();      // 新入库 = 最新使用，应进入"最近置顶"集
            _stickersView.Refresh();   // 置顶集变了，重新定位已插入的条目
        }

        return report;
    }

    private string DbPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "OICQStickerManager", "stickers.json");

    public async Task SaveDatabaseAsync()
    {
        try
        {
            // 1. 确保目录存在
            string directory = Path.GetDirectoryName(DbPath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            // 2. 序列化配置：写得漂亮一点（带缩进）
            var options = new JsonSerializerOptions { WriteIndented = true };

            // 3. 执行“脱水”过程：UI 线程只做快照（集合只许 UI 线程碰），
            //    序列化进后台——它是整库 O(n) 纯 CPU，图库大了以后在 UI 线程上是每次发送一笔可观开销
            var snapshot = Stickers.ToList();
            string jsonString = await Task.Run(() => JsonSerializer.Serialize(snapshot, options));

            // 4. 写入文件（原子 + 备份 + 写锁串行，防止截断 JSON 丢标签、并发保存互抢 tmp）
            await WriteJsonWithBackupAsync(DbPath, jsonString, _dbWriteLock);

            StatusText = "数据已自动保存";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败: {ex.Message}";
        }
    }

    // 最近一次配置保存任务（退出前 FlushPendingConfigSave 等它落地，防止快速开关丢最后一步）
    private Task? _lastConfigSaveTask;

    public async Task SaveConfigAsync()
    {
        // 加载期禁写：LoadConfigCoreAsync 通过属性赋值逐项应用配置，而多数 setter 都会触发保存——
        // 加载中途的每一次赋值都会把"半加载状态"（绑定量还是空、决策还是默认）整份写盘，
        // 快速打开→关闭应用时最后一次落盘就是这种残缺快照 → 下轮启动绑定消失、设置重置
        // （2026-10-01 用户实测：绑定过 QQ 表情，开关几次后提示重新绑定 + 设置回默认）。
        if (_loadingConfig) return;
        var saveTask = SaveConfigCoreAsync();
        _lastConfigSaveTask = saveTask;
        await saveTask;
    }

    /// <summary>退出前调用：等最后一次配置保存落盘（最多 timeout 毫秒）。</summary>
    public void FlushPendingConfigSave(int timeoutMs = 2000)
    {
        try { _lastConfigSaveTask?.Wait(timeoutMs); } catch { /* 保存失败的警报链路另行提示 */ }
    }

    private async Task SaveConfigCoreAsync()
    {
        try
        {
            // 💡 确保目录存在（首次使用时配置目录可能还未建立）
            string directory = Path.GetDirectoryName(_configPath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            // 保存配置信息
            var config = new AppConfig
            {
                CurrentSendMode = this.CurrentSendMode,
                RestoreClipboardAfterSend = this.RestoreClipboardAfterSend,
                CaptureClipboardImages = this.CaptureClipboardImages,
                ToastDurationSeconds = this.ToastDurationSeconds,
                QqFavoriteAutoImport = this.QqFavoriteAutoImport,
                QqDeepSyncEnabled = this.QqDeepSyncEnabled,
                QqSyncStrategy = this.QqSyncStrategy,
                QqDbKey = this._qqDbKey,
                GallerySortMode = this.GallerySortMode,
                QuickPanelSortMode = this.QuickPanelSortMode,
                RecentPinnedCount = this.RecentPinnedCount,
                EnableQqCoexistTrigger = this.EnableQqCoexistTrigger,
                EnableWatcherPolling = this.EnableWatcherPolling,
                HotkeyModifiers = (int)this.HotkeyModifiers,
                HotkeyKey = (int)this.HotkeyKey,
                ThemeId = this.SelectedTheme,
                GlassOpacity = this.GlassOpacity,
                CloseToTray = this.CloseToTray,
                CloseBehaviorDecided = this._closeBehaviorDecided,
                SilentStart = this.SilentStart,
                EnableGifHoverPreview = this.EnableGifHoverPreview,
                QqBindings = new List<QqBindingInfo>(this._qqBindings),
                QqPromptDismissedUins = new List<string>(this._qqPromptDismissedUins),
                WebpNoticeDismissed = this._webpNoticeDismissed,
                AiTagApiKey = this._aiTagApiKey,
                AiTagProvider = this._aiTagProvider,
                AiTagModel = this._aiTagModel,
                AiTagBaseUrl = this._aiTagBaseUrl,
                AiTagEffort = this._aiTagEffort,
                AiKeyProfiles = this.AiKeyProfiles.ToList(),
                AiActiveProfileId = this._aiActiveProfileId
            };
            var configJson = JsonSerializer.Serialize(config);
            await WriteJsonWithBackupAsync(_configPath, configJson, _configWriteLock);
        }
        catch (Exception ex)
        {
            StatusText = $"配置保存失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 配置/图库数据统一原子落盘：先写 .tmp，旧文件转 .bak，再替换。
    /// 2026-09-30 用户实测连环事故（开关重置+QQ 绑定丢失）的根因：WriteAllTextAsync 直写
    /// 中途进程被杀（当日发布曾 4 次强杀）留下截断 JSON，加载端静默回退默认值。
    /// 同一文件的写锁必须持有：各属性 setter 都可能 fire-and-forget 保存，并发保存共享
    /// .tmp 时 A 的 Move 会抢走 B 的 tmp（实测报"Could not find file .tmp"，同日用户抓到）。
    /// </summary>
    private static readonly SemaphoreSlim _configWriteLock = new(1, 1);
    private static readonly SemaphoreSlim _dbWriteLock = new(1, 1);

    private static async Task WriteJsonWithBackupAsync(string path, string json, SemaphoreSlim writeLock)
    {
        await writeLock.WaitAsync();
        try
        {
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            // 备份与改名是同步文件 IO，也放到线程池：调用方多是 UI 线程上的 await 链
            await Task.Run(() =>
            {
                try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { /* 备份尽力 */ }
                File.Move(tmp, path, overwrite: true);
            });
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// 带自愈的 JSON 读取：主文件损坏/非法时尝试 .bak（恢复成功回写主文件），
    /// 仍失败则把坏文件改名 .bad-时间戳 留证（绝不静默销毁用户数据）并返回 null。
    /// </summary>
    private async Task<string?> ReadJsonRecoveringAsync(string path, Func<string, bool> validate)
    {
        string? broken = null;
        try
        {
            var main = await File.ReadAllTextAsync(path);
            if (validate(main)) return main;
            broken = main;
        }
        catch { /* 读失败按损坏处理 */ }

        try
        {
            var bakPath = path + ".bak";
            if (File.Exists(bakPath))
            {
                var backup = await File.ReadAllTextAsync(bakPath);
                if (validate(backup))
                {
                    try { File.Copy(bakPath, path, overwrite: true); } catch { }
                    StatusText = $"{Path.GetFileName(path)} 损坏，已自动从备份恢复";
                    return backup;
                }
            }
        }
        catch { /* 备份不可用则继续走留证路径 */ }

        if (broken != null)
        {
            try { File.Move(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true); } catch { }
            StatusText = $"{Path.GetFileName(path)} 无法读取（坏文件已保留为 .bad），本次使用默认值";
        }
        return null;
    }

    private ListCollectionView _stickersView;
    private string _searchText = string.Empty;

    /// <summary>快捷面板的独立数据视图（全量 + 热度排序，见构造函数）。</summary>
    public ListCollectionView QuickPanelView { get; }

    // ———— 快捷面板选项卡（悬浮切换视角）————

    /// <summary>「QQ」胶囊的保留视角值：QQ 收藏（未入库镜像）专用，用户标签撞名时让位。</summary>
    public const string QqPanelTabValue = "qq";

    /// <summary>面板选项卡 = 「全部」+ QQ（有绑定时）+ 标签（热度序，与侧栏同口径）。</summary>
    public ObservableCollection<PanelTabItem> PanelTabs { get; } = new();

    /// <summary>存在用户标签或 QQ 绑定才显示选项卡行（只有「全部」时整行隐藏，网格保持全高）。</summary>
    public bool HasPanelTabs => PanelTabs.Any(t => t.Value.Length > 0);

    // 当前面板视角：空串 = 全部
    private string _panelSelectedTab = "";

    public string PanelSelectedTab
    {
        get => _panelSelectedTab;
        set
        {
            value ??= "";
            if (_panelSelectedTab == value) return;
            _panelSelectedTab = value;
            foreach (var tab in PanelTabs) tab.IsSelected = tab.Value == value;
            OnPropertyChanged();
            QuickPanelView.Refresh(); // 切视角：重套过滤器，顺带按最新热度重排
        }
    }

    /// <summary>呼出面板前把视角复位为「全部」；真正生效靠 ResortForOpen 里紧接着的 Refresh 一次完成。</summary>
    public void ResetPanelTab()
    {
        if (_panelSelectedTab.Length == 0) return;
        _panelSelectedTab = "";
        foreach (var tab in PanelTabs) tab.IsSelected = tab.Value.Length == 0;
        OnPropertyChanged();
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var newText = value ?? "";
            if (newText == _searchText) return; // 同值短路：转译等路径可能同值回灌，触发重建纯属浪费

            bool wasSearching = !string.IsNullOrEmpty(_searchText);
            bool searching = !string.IsNullOrEmpty(newText);

            // 搜索=显式进入「最近」全库视图：进入时记忆原选项卡、清空时回跳——
            // 但搜索期间用户手动切过页签的，尊重当前所在页签不回跳
            // （从 QQ 界面搜索跳转也走同一条路径）
            if (searching && !wasSearching)
            {
                _tabBeforeSearch = SelectedTab == "最近" ? null : SelectedTab;
                if (SelectedTab != "最近") SelectedTab = "最近";
            }
            else if (!searching && wasSearching)
            {
                var back = _tabBeforeSearch;
                _tabBeforeSearch = null;
                // 直接回跳：TabTags 此刻还是搜索过滤后的列表，用它判存在会漏（搜无匹配词再清空
                // 就永远不回跳）。若期间标签真被删，setter 末尾的 UpdateTabTags 兜底拉回「最近」
                if (back != null && SelectedTab == "最近") SelectedTab = back;
            }

            _searchText = newText;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSearchText));
            _stickersView?.Refresh();  // 刷新图片列表
            UpdateTabTags();           // 刷新左侧选项卡
            ApplyQqSearchBuckets();    // QQ 页分桶重排（命中前/未命中后）
        }
    }

    // 进入搜索前的选项卡（清空搜索后回跳）；null=搜索前就在「最近」。
    // 搜索期间用户手动切过页签则弃用（尊重用户最后所在位置）
    private string? _tabBeforeSearch;

    // 搜索框是否有内容：驱动清空按钮与空状态文案
    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

    // ———— 排序规则（图库 / 快捷面板独立设置，2026-10-02 用户定案）————
    // 0=前 N 张最近使用置顶 + 其余热度（默认：最近发送的图常被高频连发，置顶便于快速再找）；
    // 1=全部热度（时序+频率复合，RankScore）；2=全部按名称（标签联合名序，稳定不变动）。
    // "最近置顶"的置顶集 = 各自范围内 LastUsedTime 最新的 N 张（图库=图库全体，面板=PanelItems）。

    public const int RecentPinnedMax = 20;

    private int _gallerySortMode;
    public int GallerySortMode
    {
        get => _gallerySortMode;
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (_gallerySortMode == value) return;
            _gallerySortMode = value;
            OnPropertyChanged();
            NotifySortBooleans(isGallery: true);
            _ = SaveConfigAsync();
            RecalcRecentPinned();
            _stickersView?.Refresh();
        }
    }

    private int _quickPanelSortMode;
    public int QuickPanelSortMode
    {
        get => _quickPanelSortMode;
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (_quickPanelSortMode == value) return;
            _quickPanelSortMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecentPinnedVisible));
            NotifySortBooleans(isGallery: false);
            _ = SaveConfigAsync();
            QuickPanelView?.Refresh();
        }
    }

    private int _recentPinnedCount = 5;
    public int RecentPinnedCount
    {
        get => _recentPinnedCount;
        set
        {
            value = Math.Clamp(value, 1, RecentPinnedMax);
            if (_recentPinnedCount == value) return;
            _recentPinnedCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecentPinnedCountText));
            _ = SaveConfigAsync();
            RecalcRecentPinned();
            _stickersView?.Refresh();
            QuickPanelView?.Refresh();
        }
    }

    public string RecentPinnedCountText => $"{_recentPinnedCount} 张";

    private void NotifySortBooleans(bool isGallery)
    {
        if (isGallery)
        {
            OnPropertyChanged(nameof(IsGallerySortRecent));
            OnPropertyChanged(nameof(IsGallerySortScore));
            OnPropertyChanged(nameof(IsGallerySortName));
        }
        else
        {
            OnPropertyChanged(nameof(IsPanelSortRecent));
            OnPropertyChanged(nameof(IsPanelSortScore));
            OnPropertyChanged(nameof(IsPanelSortName));
        }
    }

    // 分段控件的双向包装
    public bool IsGallerySortRecent { get => _gallerySortMode == 0; set { if (value) GallerySortMode = 0; } }
    public bool IsGallerySortScore { get => _gallerySortMode == 1; set { if (value) GallerySortMode = 1; } }
    public bool IsGallerySortName { get => _gallerySortMode == 2; set { if (value) GallerySortMode = 2; } }
    public bool IsPanelSortRecent { get => _quickPanelSortMode == 0; set { if (value) QuickPanelSortMode = 0; } }
    public bool IsPanelSortScore { get => _quickPanelSortMode == 1; set { if (value) QuickPanelSortMode = 1; } }
    public bool IsPanelSortName { get => _quickPanelSortMode == 2; set { if (value) QuickPanelSortMode = 2; } }

    /// <summary>任一视图处于"最近置顶"模式时，设置页才显示置顶张数行。</summary>
    public bool RecentPinnedVisible => _gallerySortMode == 0 || _quickPanelSortMode == 0;

    private HashSet<Guid> _galleryPinned = new();
    private HashSet<Guid> _panelPinned = new();

    /// <summary>重算图库的"最近置顶"集合（LastUsedTime 最新的 N 张）；发送/加载/设置变化时调用。</summary>
    private void RecalcRecentPinned()
    {
        _galleryPinned = Stickers
            .OrderByDescending(s => s.LastUsedTime)
            .Take(_recentPinnedCount)
            .Select(s => s.Id)
            .ToHashSet();
        OnPropertyChanged(nameof(RecentPinnedVisible));
    }

    /// <summary>统一比较器：按模式分派，未特判的路径回落热度（RankScore 降序 → 最近活跃降序 → Id 稳定序）。</summary>
    private int CompareWithMode(StickerModel a, StickerModel b, int mode, HashSet<Guid> pinned)
    {
        switch (mode)
        {
            case 2: // 按名称（标签联合名，稳定不变动）；无标签的"未命名表情"自然沉后
                var byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                break;
            case 0: // 前 N 张最近使用置顶（桶内按最近使用序），其余走热度
                bool pa = pinned.Contains(a.Id), pb = pinned.Contains(b.Id);
                if (pa != pb) return pa ? -1 : 1;
                if (pa) return b.LastUsedTime.CompareTo(a.LastUsedTime);
                break;
        }
        var byScore = b.RankScore.CompareTo(a.RankScore);
        return byScore != 0 ? byScore : b.LastUsedTime.CompareTo(a.LastUsedTime);
    }

    private int CompareGallery(StickerModel a, StickerModel b) => CompareWithMode(a, b, _gallerySortMode, _galleryPinned);
    private int ComparePanel(StickerModel a, StickerModel b) => CompareWithMode(a, b, _quickPanelSortMode, _panelPinned);

    // 界面配色主题
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = ThemeManager.Themes.Select(t => new ThemeOption(t)).ToList();

    private string _selectedTheme = ThemeManager.Current.Id;
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            var def = ThemeManager.Find(value);
            if (def == null || _selectedTheme == def.Id) return;
            _selectedTheme = def.Id;
            OnPropertyChanged();
            foreach (var option in ThemeOptions) option.IsSelected = option.Id == def.Id;
            ThemeManager.Apply(def.Id);
            _ = SaveConfigAsync();
        }
    }

    // 获取当前库中所有不重复的标签（标签池）
    // 排序 = 标签自身最近使用时间降序（最近被打上的在前，见 NoteTagUsage），无记录的按名称兜底
    public List<string> AllExistingTags => Stickers
        .SelectMany(s => s.Tags)
        .Distinct()
        .OrderByDescending(t => _tagStats.GetValueOrDefault(t, DateTime.MinValue))
        .ThenBy(t => t)
        .ToList();

    // ———— 标签使用统计（编辑器标签池的排序依据，tag-stats.json 持久化）————
    // "使用" = 标签最近一次被添加到某个表情上（标签编辑器保存时记录新增部分）

    private Dictionary<string, DateTime> _tagStats = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim _tagStatsWriteLock = new(1, 1);

    private static string TagStatsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "OICQStickerManager", "tag-stats.json");

    private async Task LoadTagStatsAsync()
    {
        try
        {
            if (!File.Exists(TagStatsPath)) return;
            var json = await File.ReadAllTextAsync(TagStatsPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
            if (loaded != null) _tagStats = new Dictionary<string, DateTime>(loaded, StringComparer.Ordinal);
        }
        catch { /* 统计损坏按无记录处理，保存时会重建 */ }
    }

    private async Task SaveTagStatsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(TagStatsPath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_tagStats);
            await WriteJsonWithBackupAsync(TagStatsPath, json, _tagStatsWriteLock);
        }
        catch { /* 统计落盘失败不拖累保存链路 */ }
    }

    /// <summary>记录标签被使用（添加到表情）：标签池随之按"最近用过"排序。</summary>
    public void NoteTagUsage(IEnumerable<string> tags)
    {
        bool changed = false;
        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            _tagStats[t] = DateTime.Now;
            changed = true;
        }
        if (changed) _ = SaveTagStatsAsync();
    }

    // ———— 标签级操作（侧栏标签右键菜单，2026-10-02 用户定案细分）————

    /// <summary>编辑标签：把所有表情上的「旧名」改为新名称（重合标签自动去重），并记一次新名使用。</summary>
    public async Task RenameTagAsync(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
        foreach (var s in Stickers)
        {
            if (!s.Tags.Contains(oldName)) continue;
            s.Tags = s.Tags.Select(t => t == oldName ? newName : t).Distinct().ToList();
        }
        await SaveDatabaseAsync();
        UpdateTabTags();
        RefreshQqMirrorFlags();
        NoteTagUsage(new[] { newName });
    }

    /// <summary>批量增删标签（差量应用）：added 加到每张、removed 从每张移除，各张其余标签保留——
    /// 批量编辑不能覆盖式赋值，否则会抹掉各图片独有的标签。</summary>
    public void ApplyTagAdjust(IEnumerable<StickerModel> stickers, IEnumerable<string> added, IEnumerable<string> removed)
    {
        var add = new HashSet<string>(added);
        var rem = new HashSet<string>(removed);
        if (add.Count == 0 && rem.Count == 0) return;
        foreach (var s in stickers)
        {
            s.Tags = s.Tags.Union(add).Except(rem).ToList();
        }
    }

    /// <summary>删除标签（从所有表情上移除该标签，图片保留）；无载体的标签随页签重建自然消失。</summary>
    public async Task RemoveTagAsync(string tagName)
    {
        foreach (var s in Stickers.Where(x => x.Tags.Contains(tagName)).ToList())
        {
            s.Tags = s.Tags.Where(t => t != tagName).ToList();
        }
        await SaveDatabaseAsync();
        UpdateTabTags();
        RefreshQqMirrorFlags();
    }

    // 💡 这是一个小技巧：当标签更新后，通知 UI 刷新标签池
    public void RefreshTagPool() => OnPropertyChanged(nameof(AllExistingTags));

    // 2. 增加设置属性
    private SendMode _currentSendMode = SendMode.DoubleClick;
    public SendMode CurrentSendMode
    {
        get => _currentSendMode;
        set
        {
            if (_currentSendMode != value)
            {
                _currentSendMode = value;
                OnPropertyChanged();
                // 💡 用户一键切换，立即后台保存
                _ = SaveConfigAsync();
            }
        }
    }

    public bool IsSingleClick
    {
        get => CurrentSendMode == SendMode.SingleClick;
        set { if (value) { CurrentSendMode = SendMode.SingleClick; OnPropertyChanged(); } }
    }

    public bool IsDoubleClick
    {
        get => CurrentSendMode == SendMode.DoubleClick;
        set { if (value) { CurrentSendMode = SendMode.DoubleClick; OnPropertyChanged(); } }
    }

    // 发送后是否恢复剪贴板原内容（默认开；关闭则表情保留在剪贴板，便于连续粘贴）
    private bool _restoreClipboardAfterSend = true;
    public bool RestoreClipboardAfterSend
    {
        get => _restoreClipboardAfterSend;
        set
        {
            if (_restoreClipboardAfterSend != value)
            {
                _restoreClipboardAfterSend = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // M2 剪贴板捕获：在任意应用里「复制图片」后弹非激活轻提示一键入库（默认开）
    private bool _captureClipboardImages = true;
    public bool CaptureClipboardImages
    {
        get => _captureClipboardImages;
        set
        {
            if (_captureClipboardImages != value)
            {
                _captureClipboardImages = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToastDurationVisible));
                _ = SaveConfigAsync();
            }
        }
    }

    // 入库轻提示停留时长（秒，3-15）：下一次弹窗即用新值（弹窗在 Show 时向 VM 取数）
    private int _toastDurationSeconds = 8;
    public int ToastDurationSeconds
    {
        get => _toastDurationSeconds;
        set
        {
            value = Math.Clamp(value, 3, 15);
            if (_toastDurationSeconds != value)
            {
                _toastDurationSeconds = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // 时长行只在捕获开关打开时有意义（跟随开关显隐）
    public bool ToastDurationVisible => _captureClipboardImages;

    // M4 联动：QQ 里点「添加到表情」后自动复制进图库（默认关，避免未经确认写入图库）
    private bool _qqFavoriteAutoImport;
    public bool QqFavoriteAutoImport
    {
        get => _qqFavoriteAutoImport;
        set
        {
            if (_qqFavoriteAutoImport != value)
            {
                _qqFavoriteAutoImport = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // QQ 表情面板共存触发器开关：QQ 面板弹出时自动在旁边打开快捷面板
    private bool _enableQqCoexistTrigger = true;
    public bool EnableQqCoexistTrigger
    {
        get => _enableQqCoexistTrigger;
        set
        {
            if (_enableQqCoexistTrigger != value)
            {
                _enableQqCoexistTrigger = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // 共存触发器的轮询保底（默认关；事件在个别 QQ 版本上失灵时开启）
    private bool _enableWatcherPolling = false;
    public bool EnableWatcherPolling
    {
        get => _enableWatcherPolling;
        set
        {
            if (_enableWatcherPolling != value)
            {
                _enableWatcherPolling = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // 玻璃材质不透明度（50-100）：雾面的白度与不透明程度，改变实时生效到所有玻璃窗口
    private int _glassOpacity = 70;
    public int GlassOpacity
    {
        get => _glassOpacity;
        set
        {
            value = Math.Clamp(value, 50, 100);
            if (_glassOpacity != value)
            {
                _glassOpacity = value;
                OnPropertyChanged();
                GlassMaterial.OpacityPercent = value;
                _ = SaveConfigAsync();
            }
        }
    }

    // ———— 通用：启动与托盘 ————

    // 开机自启动：注册表 Run 键是唯一事实源，不进 config.json。
    // 写注册表失败（极少见）时不更新状态，回读真实值让开关回弹，并把错误送进状态栏 → Alert
    private bool _launchOnStartup = AutoStartManager.IsEnabled;
    public bool LaunchOnStartup
    {
        get => _launchOnStartup;
        set
        {
            if (_launchOnStartup == value) return;
            try
            {
                AutoStartManager.SetEnabled(value);
                _launchOnStartup = value;
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                StatusText = $"配置保存失败: {ex.Message}";
                _launchOnStartup = AutoStartManager.IsEnabled;
                OnPropertyChanged();
            }
        }
    }

    // 主窗口 ✕ 的行为：开=隐藏到托盘（从托盘菜单退出），关=退出程序。
    // 用户在设置里动过这个开关，或回答过首次关闭询问，即视为已做过选择（CloseBehaviorDecided）；
    // LoadConfigAsync 的程序性赋值不算（_loadingConfig 守卫），否则老用户永远等不到首次询问
    private bool _closeToTray = false;
    private bool _loadingConfig;
    private bool _closeBehaviorDecided = false;
    public bool CloseBehaviorDecided => _closeBehaviorDecided;
    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (_closeToTray != value)
            {
                _closeToTray = value;
                if (!_loadingConfig) _closeBehaviorDecided = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CloseBehaviorDecided));
                _ = SaveConfigAsync();
            }
        }
    }

    /// <summary>首次关闭询问的落点：记下用户选择并持久化（设置页开关走 CloseToTray setter 同样生效）。</summary>
    public void DecideCloseBehavior(bool closeToTray)
    {
        _closeBehaviorDecided = true;
        if (CloseToTray != closeToTray) CloseToTray = closeToTray; // setter 内保存
        else _ = SaveConfigAsync(); // 选择与现状一致（如选了退出而默认即退出）也要把"已选择"落盘
    }

    // 启动后不显示主窗口，直接驻留托盘；开启时托盘图标强制常驻（否则藏起来就唤不回了）
    private bool _silentStart = false;
    public bool SilentStart
    {
        get => _silentStart;
        set
        {
            if (_silentStart != value)
            {
                _silentStart = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // 图库悬浮 400ms 的 GIF 动图预览气泡（默认开）
    private bool _enableGifHoverPreview = true;
    public bool EnableGifHoverPreview
    {
        get => _enableGifHoverPreview;
        set
        {
            if (_enableGifHoverPreview != value)
            {
                _enableGifHoverPreview = value;
                OnPropertyChanged();
                _ = SaveConfigAsync();
            }
        }
    }

    // 快捷面板热键：Win32 MOD_* 标志（1=Alt 2=Ctrl 4=Shift 8=Win，与 WPF ModifierKeys 数值一致）+ 虚拟键码，默认 Ctrl+Alt+D
    // （按配置原样加载，无迁移——任何用户设置的键位一律尊重）
    private uint _hotkeyModifiers = 0x3;
    public uint HotkeyModifiers
    {
        get => _hotkeyModifiers;
        set
        {
            if (_hotkeyModifiers != value)
            {
                _hotkeyModifiers = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HotkeyDisplay));
                OnPropertyChanged(nameof(HotkeyKeycaps));
                _ = SaveConfigAsync();
            }
        }
    }

    private uint _hotkeyKey = 0x44; // VK_D（历史默认变迁 E→K→D；配置值原样加载，无迁移）
    public uint HotkeyKey
    {
        get => _hotkeyKey;
        set
        {
            if (_hotkeyKey != value)
            {
                _hotkeyKey = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HotkeyDisplay));
                OnPropertyChanged(nameof(HotkeyKeycaps));
                _ = SaveConfigAsync();
            }
        }
    }

    public string HotkeyDisplay
    {
        get
        {
            var parts = new List<string>();
            if ((_hotkeyModifiers & 0x2) != 0) parts.Add("Ctrl");
            if ((_hotkeyModifiers & 0x1) != 0) parts.Add("Alt");
            if ((_hotkeyModifiers & 0x4) != 0) parts.Add("Shift");
            if ((_hotkeyModifiers & 0x8) != 0) parts.Add("Win");
            try { parts.Add(KeyInterop.KeyFromVirtualKey((int)_hotkeyKey).ToString()); }
            catch { }
            return string.Join(" + ", parts);
        }
    }

    // 快捷面板标题栏的键帽展示（Ctrl + Alt + E → ["Ctrl","Alt","E"]）
    public IReadOnlyList<string> HotkeyKeycaps
    {
        get
        {
            var parts = new List<string>();
            if ((_hotkeyModifiers & 0x2) != 0) parts.Add("Ctrl");
            if ((_hotkeyModifiers & 0x1) != 0) parts.Add("Alt");
            if ((_hotkeyModifiers & 0x4) != 0) parts.Add("Shift");
            if ((_hotkeyModifiers & 0x8) != 0) parts.Add("Win");
            try { parts.Add(KeyInterop.KeyFromVirtualKey((int)_hotkeyKey).ToString()); }
            catch { }
            return parts;
        }
    }

    // 共存模式的目标 QQ 窗口：由快捷面板在打开时设置，发送路由据此选择粘贴方式
    private IntPtr _coexistTargetHwnd = IntPtr.Zero;
    public void SetQuickPanelCoexistTarget(IntPtr qqHwnd) => _coexistTargetHwnd = qqHwnd;

    // 1. 打开文件夹命令
    public ICommand OpenLibraryCommand { get; }

    // 2. 单击逻辑
    public RelayCommand<StickerModel> HandleStickerClickCommand { get; }

    // 3. 双击逻辑
    public RelayCommand<StickerModel> HandleStickerDoubleClickCommand { get; }

    private Task OpenLibraryAsync()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OICQStickerManager", "Library");
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        return Task.CompletedTask;
    }

    // 单击发送（QQ 页的镜像表情直接从 QQ 缓存零拷贝发送，走同一条链路）
    private Task HandleStickerClickAsync(StickerModel? s)
    {
        if (CurrentSendMode == SendMode.SingleClick && s != null)
            return ExecuteSendAsync(s);
        return Task.CompletedTask;
    }

    // 双击发送
    private Task HandleStickerDoubleClickAsync(StickerModel? s)
    {
        if (CurrentSendMode == SendMode.DoubleClick && s != null)
            return ExecuteSendAsync(s);
        return Task.CompletedTask;
    }

    // 快捷面板的单击/双击（尊重主界面设置的发送模式），走不抢焦点的发送路径
    public RelayCommand<StickerModel> PanelStickerClickCommand { get; }
    public RelayCommand<StickerModel> PanelStickerDoubleClickCommand { get; }

    private Task PanelStickerClickAsync(StickerModel? s)
    {
        if (CurrentSendMode == SendMode.SingleClick && s != null)
            return SendFromQuickPanelAsync(s);
        return Task.CompletedTask;
    }

    private Task PanelStickerDoubleClickAsync(StickerModel? s)
    {
        if (CurrentSendMode == SendMode.DoubleClick && s != null)
            return SendFromQuickPanelAsync(s);
        return Task.CompletedTask;
    }

    // 记录每个表情上次发送的时间：把同一次双击的多次按下合并为一次发送
    private readonly Dictionary<Guid, DateTime> _lastSendTimes = new();

    // 快捷面板发起发送的瞬间触发（粘贴/剪贴板恢复的 1s 等待之前）：
    // 面板借此立即收起（对齐 QQ 原生面板点完即关），共存模式还会同步关闭 QQ 原生面板
    public event EventHandler? QuickPanelSendInitiated;

    // 💡 提取公共的发送方法
    private async Task ExecuteSendAsync(StickerModel s)
        => await ExecuteSendCoreAsync(s, path => _windowService.SendImageToActiveWindowAsync(path, RestoreClipboardAfterSend));

    // 快捷面板发送：不最小化、不抢焦点，直接粘贴给前台窗口；
    // 若面板以 QQ 共存模式打开，则先把焦点还给 QQ 输入框再粘贴；
    // 发送完成后面板立即收起，并尽力同步关闭 QQ 原生表情面板（2026-10-01 用户定案）
    private async Task SendFromQuickPanelAsync(StickerModel s)
    {
        // 收起与同步关 QQ 面板必须在发起瞬间：等发送完成（含剪贴板恢复的固定 1s 等待）再收，
        // 面板会滞留秒级（2026-10-01 用户实测）
        QuickPanelSendInitiated?.Invoke(this, EventArgs.Empty);
        var coexistTarget = _coexistTargetHwnd;
        await ExecuteSendCoreAsync(s, async path =>
        {
            if (coexistTarget == IntPtr.Zero)
            {
                await _windowService.QuickPasteToForegroundAsync(path, RestoreClipboardAfterSend);
                return;
            }
            // CoexistPasteAsync 返回 false = 粘贴前焦点已离开 QQ 被闸门拦下（宁可不发也不错发）
            bool sent = await _windowService.CoexistPasteAsync(coexistTarget, path, RestoreClipboardAfterSend);
            if (!sent) StatusText = "发送已取消：粘贴前焦点已切离 QQ（防止表情误发到其他窗口）";
        });
    }

    private async Task ExecuteSendCoreAsync(StickerModel s, Func<string, Task> send)
    {
        // 产品约定：无论单击还是双击模式，一次操作只发送一张。
        // 双击的第二、三次按下落在系统双击时限内，在此被拦下；
        // 想快速重发同一张，请自行复制粘贴。
        var now = DateTime.Now;
        if (_lastSendTimes.TryGetValue(s.Id, out var lastSend) &&
            (now - lastSend).TotalMilliseconds < _windowService.DoubleClickTimeMs)
        {
            return;
        }
        _lastSendTimes[s.Id] = now;

        // 使用统计：次数 +1（频次因子输入）+ 最近活跃时间刷新（新近度因子输入），发送后此表情升至列表顶部
        s.UseCount++;
        s.LastUsedTime = DateTime.Now;
        // QQ 页镜像发送：镜像条目不落盘也不参与排序，使用记录要记到图库同源副本（按 Md5 对账）上才不漏统计
        if (s is QqStickerModel qq)
        {
            var twin = Stickers.FirstOrDefault(x => x.Md5 == qq.Md5);
            if (twin != null) { twin.UseCount++; twin.LastUsedTime = s.LastUsedTime; }
            // 未入库镜像没有落盘载体：统计单独记 qq-stats.json，快捷面板混排时与图库同尺排序
            if (!string.IsNullOrEmpty(qq.Md5))
            {
                _qqStats[qq.Md5] = new QqUsageStat { N = qq.UseCount, T = qq.LastUsedTime };
                _ = SaveQqStatsAsync();
            }
        }
        await send(s.FullPath);
        await SaveDatabaseAsync();
        RecalcRecentPinned();  // 刚发送的图进入"最近置顶"集
        _stickersView.Refresh();
    }

    public async Task LoadConfigAsync()
    {
        // 程序性赋值不算用户决策（否则 CloseToTray 一加载就把"已选择"置位，首次关闭询问永远不弹）
        // 同时也是 SaveConfigAsync 的加载期禁写窗口（防半加载快照覆盖好档）
        _loadingConfig = true;
        try
        {
            await LoadConfigCoreAsync();
        }
        finally
        {
            _loadingConfig = false;
        }
        // 放出"配置已就绪"信号：主窗口的热键注册等它（启动时默认键抢先注册会误报被占用）
        _configLoaded.TrySetResult();
    }

    /// <summary>配置加载中标志：热键注册等加载完成后才执行（IsLoadingConfig 供窗口守卫）。</summary>
    public bool IsLoadingConfig => _loadingConfig;

    /// <summary>config 加载完成信号（无论成败都会触发）：热键注册的启动时序锚点。</summary>
    public Task ConfigLoaded => _configLoaded.Task;
    private readonly TaskCompletionSource _configLoaded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>加载 config.json 到各属性。热键迁移链已删（2026-10-01 用户实测误伤：
    /// 用户主动设置的 Ctrl+Alt+E 每次启动都被当成"旧默认"强迁回 D）——迁移使命早已完成，
    /// 任何用户设置的键位从此一律尊重；旧默认 E 若真被 QQ 占用，注册失败有提示可自行改键。</summary>
    private async Task LoadConfigCoreAsync()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = await ReadJsonRecoveringAsync(_configPath,
                    s => { try { return JsonSerializer.Deserialize<AppConfig>(s) != null; } catch { return false; } });
                if (json == null) return; // 损坏且无备份：保持默认值（坏文件已留证，状态栏有提示）
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    // 注意：这里赋值会通过 IsSingleClick/IsDoubleClick 自动更新 UI
                    this.CurrentSendMode = config.CurrentSendMode;
                    this.RestoreClipboardAfterSend = config.RestoreClipboardAfterSend;
                    this._captureClipboardImages = config.CaptureClipboardImages;
                    this._toastDurationSeconds = Math.Clamp(config.ToastDurationSeconds, 3, 15);
                    this._qqFavoriteAutoImport = config.QqFavoriteAutoImport;
                    this._qqDeepSyncEnabled = config.QqDeepSyncEnabled;
                    this._qqSyncStrategy = config.QqSyncStrategy;
                    this._qqDbKey = config.QqDbKey ?? "";
                    NotifyQqDeepSyncStatus();
                    this._gallerySortMode = Math.Clamp(config.GallerySortMode, 0, 2);
                    this._quickPanelSortMode = Math.Clamp(config.QuickPanelSortMode, 0, 2);
                    this._recentPinnedCount = Math.Clamp(config.RecentPinnedCount, 1, RecentPinnedMax);
                    this.EnableQqCoexistTrigger = config.EnableQqCoexistTrigger;
                    this.EnableWatcherPolling = config.EnableWatcherPolling;
                    // 热键按配置原样加载（无迁移）
                    this.HotkeyModifiers = (uint)config.HotkeyModifiers;
                    this.HotkeyKey = (uint)config.HotkeyKey;
                    GlassMaterial.OpacityPercent = config.GlassOpacity;
                    this.CloseToTray = config.CloseToTray;
                    this._closeBehaviorDecided = config.CloseBehaviorDecided;
                    this.SilentStart = config.SilentStart;
                    this.EnableGifHoverPreview = config.EnableGifHoverPreview;
                    // 主题已在启动时由 ThemeManager.ApplyInitial 同步应用；这里仅对齐 VM 状态
                    this.SelectedTheme = ThemeManager.Current.Id;
                    // QQ 绑定：按配置恢复（每绑定一个镜像服务），随后选项卡插入 QQ（账号）页
                    InitializeQqBindings(config.QqBindings ?? new List<QqBindingInfo>(),
                        config.QqPromptDismissedUins ?? new List<string>());
                    this._webpNoticeDismissed = config.WebpNoticeDismissed;
                    // AI 识别配置：按配置恢复（Key/服务商/模型/自定义地址/思考强度）
                    this._aiTagApiKey = config.AiTagApiKey ?? "";
                    this._aiTagProvider = string.IsNullOrEmpty(config.AiTagProvider) ? "auto" : config.AiTagProvider;
                    this._aiTagModel = config.AiTagModel ?? "";
                    this._aiTagBaseUrl = config.AiTagBaseUrl ?? "";
                    this._aiTagEffort = config.AiTagEffort ?? "";
                    // 档案恢复 + 老配置迁移：有 Key 但没有档案（升级前配置）→ 自动包成一份档案并激活，
                    // 老用户无感进入多档案管理；此后档案列表是唯一事实源
                    this.AiKeyProfiles.Clear();
                    foreach (var p in config.AiKeyProfiles ?? new List<AiKeyProfile>())
                        this.AiKeyProfiles.Add(p);
                    if (this.AiKeyProfiles.Count == 0 && this._aiTagApiKey.Length > 0)
                    {
                        var legacy = new AiKeyProfile { Name = "我的 Key", VerifiedAt = DateTime.Now };
                        legacy.ProviderId = this._aiTagProvider;
                        legacy.ApiKey = this._aiTagApiKey;
                        legacy.BaseUrl = this._aiTagBaseUrl;
                        legacy.Model = this._aiTagModel;
                        legacy.Effort = this._aiTagEffort;
                        this.AiKeyProfiles.Add(legacy);
                        config.AiActiveProfileId = legacy.Id;
                    }
                    this._aiActiveProfileId = this.AiKeyProfiles.Any(p => p.Id == config.AiActiveProfileId)
                        ? config.AiActiveProfileId : "";
                    // 触发 UI 绑定更新
                    OnPropertyChanged(nameof(IsSingleClick));
                    OnPropertyChanged(nameof(IsDoubleClick));
                    // 上面几处直写后备字段（避免加载期触发对账/保存副作用），绑定控件拿不到
                    // PropertyChanged 就会渲染成默认态——开关显示"关"而状态行显示"已开启"
                    // 的矛盾即此（2026-09-30 用户实测）。加载完毕统一补发通知对齐界面。
                    OnPropertyChanged(nameof(QqDeepSyncEnabled));
                    OnPropertyChanged(nameof(QqFavoriteAutoImport));
                    OnPropertyChanged(nameof(CaptureClipboardImages));
                    OnPropertyChanged(nameof(ToastDurationSeconds));
                    OnPropertyChanged(nameof(ToastDurationVisible));
                    OnPropertyChanged(nameof(StrategyIsMark));
                    OnPropertyChanged(nameof(StrategyIsRemove));
                    OnPropertyChanged(nameof(StrategyIsAdopt));
                    OnPropertyChanged(nameof(QqSyncStrategyVisible));
                    OnPropertyChanged(nameof(AdoptBatchButtonVisible));
                    OnPropertyChanged(nameof(GallerySortMode));
                    OnPropertyChanged(nameof(QuickPanelSortMode));
                    OnPropertyChanged(nameof(RecentPinnedCount));
                    OnPropertyChanged(nameof(RecentPinnedCountText));
                    OnPropertyChanged(nameof(RecentPinnedVisible));
                    OnPropertyChanged(nameof(IsGallerySortRecent));
                    OnPropertyChanged(nameof(IsGallerySortScore));
                    OnPropertyChanged(nameof(IsGallerySortName));
                    OnPropertyChanged(nameof(IsPanelSortRecent));
                    OnPropertyChanged(nameof(IsPanelSortScore));
                    OnPropertyChanged(nameof(IsPanelSortName));
                    NotifyAiConfigState();
                }
            }
            catch { /* 如果配置损坏则使用默认值 */ }
        }
    }
}