using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace OICQStickerManager.Services;

public class QqPanelEventArgs : EventArgs
{
    public IntPtr HostHwnd { get; }
    public Rect PanelRect { get; } // 物理像素；首次乐观打开时可能为 Empty（本会话还没见过面板）
    public Rect EmojiButtonRect { get; } // 表情按钮矩形（物理像素），PanelRect 为 Empty 时用作定位锚

    public QqPanelEventArgs(IntPtr hostHwnd, Rect panelRect, Rect emojiButtonRect = default)
    {
        HostHwnd = hostHwnd;
        PanelRect = panelRect;
        EmojiButtonRect = emojiButtonRect;
    }
}

/// <summary>
/// 监听 QQ 聊天窗口内原生表情面板（UIA 标识：Window Class='sticker-panel'）的出现与消失，
/// 驱动快捷面板的共存模式。纯事件驱动，零常驻成本：
/// - 打开：低级鼠标钩子在按下表情按钮的物理瞬间乐观打开。点击先经三重校验（窗口相对矩形
///   按当前位置重算 + WindowFromPoint 根窗口一致 + 进程一致），防陈旧矩形对任意窗口/应用误触发；
///   焦点命中按钮仅作钩子没覆盖的点击的兜底（见 OnFocusChanged）。
/// - 关闭：与打开对称的乐观路径——面板开着时完整点击按钮（down+up 都落在按钮上）立即收起；
///   焦点进入聊天输入框也立即收起（面板浮在输入框上层，焦点能到输入框 = 面板已被 QQ 收起）。
///   乐观误判可自愈：尾迹验证发现 QQ 面板仍在则重新报 APPEARED，OpenForCoexist 的淡出中重开接住。
///   另有 QQ 窗口的 UIA 结构变化事件（节流后验证）与点击后的补验尾迹兜底。
/// 轮询保底（可选设置项，默认关）：事件在个别 QQ 版本上失灵时的兜底，常开会持续查询 QQ。
/// 打开保障：QQ 窗口订阅/获得焦点时主动发 MSAA 查询激活其无障碍树——Chromium 只在检测到
/// 屏幕阅读器式查询时才把 DOM 挂进 UIA，被动等待在个别环境（Win11 实报）永不发生（Issue #2）。
/// 定位串随 QQ 版本更新可能失配：连续异常自动降级并提示，失败模式良性（热键不受影响）。
/// 纯只读 UIA，不注入不挂钩（LL 鼠标钩子只做全局观察，不进 QQ 进程）。
/// </summary>
public class QqPanelWatcher : IDisposable
{
    private const int VerifyMinIntervalMs = 150; // 事件合并窗口：只限制风暴中的重复验证，不延迟首次验证
    private const int CloseConfirmDelayMs = 100; // 关闭确认的延迟复核间隔（乐观关闭已是主路径，此处只兜无点击信号的关闭）
    private const int TailVerifyIntervalMs = 100; // 按钮点击后的补验尾迹间隔：缓存面板元素的命中测试单次 5–20ms，100ms 安全
    private const int TailWindowMs = 2500;        // 补验尾迹时长（每次按钮点击重新计时）
    private const int FastPollMs = 125;          // 轮询保底开启且共存期间：快速感知关闭
    private const int IdlePollMs = 1000;         // 轮询保底开启且常态：低频兜底
    private const int DegradedPollIntervalMs = 5000;
    private const int DegradeThreshold = 20;

    private const string EmojiButtonName = "表情";
    private const string EmojiButtonClass = "icon-item";
    private const string PanelClassName = "sticker-panel";

    // 旧版 NTQQ（9.9.19/9.9.21 实测）：表情工具栏按钮不可聚焦，点开后焦点落进面板底部分类标签
    // （如「切换默认表情按钮」）；且该代 Chromium 对 UIA 客户端只暴露剪枝树
    // （diag visited≈17，仅窗口骨架），sticker-panel 全树扫描必扑空。
    // 故旧版把「焦点命中面板标签」也计作表情按钮信号，面板可见性走 ScanQqWindows 里的标签元素兜底。
    private static readonly HashSet<string> LegacyPanelTabNames = new()
    {
        "切换默认表情按钮", "切换GIF热图按钮", "切换我的收藏按钮",
    };
    // 锚信号 TTL 按证据强度分级：TAB 命中（焦点进了面板）= 面板确实开着，长 TTL；
    // BUTTON 命中（点了表情按钮）= 只是开/关动作，短 TTL——真正打开后 ~400ms 内必有 TAB 命中续期
    // （实测），而关闭后不会有 TAB 命中，短 TTL 让"开着"状态及时解除（否则状态卡死，
    // 鼠标钩子的乐观重开被 !_panelOpen 挡住，后续点击永远无反应）。
    // TAB 命中（焦点进了面板）= 面板确实开着。旧值 20s 有"空闲过期误关"问题：面板开着但用户
    // 只是浏览不打扰焦点，TTL 一到锚过期、下一次无关事件触发验证就把快捷面板收起（实测 18:50）。
    // 现在所有主动关闭路径（按钮点击乐观关闭/编辑框光灭/close-now）都会主动清锚，TTL 只是
    // 兜底，可以放长——误关（体验差）远比晚关（罕见路径才发生）伤。
    private const int LegacyTabHitTtlMs = 60_000;
    private const int LegacyButtonHitTtlMs = 2_500;
    // 打开证据（按钮真实点击）的锚 TTL：QQ 面板打开后焦点续期（TAB 命中）依赖 Chromium
    // 无障碍管线，窗口移动/渲染进程冷时可达数秒（实测 18:46 >2.6s）——TTL 短于续期就会
    // "锚过期收起 → TAB 珊珊来迟又弹出"，正是用户报告的"关闭又快速弹出"闪烁。
    // 打开证据是物理点击，误开代价小；误关（面板开着却被收起）才是最伤体验的一侧。
    private const int LegacyOpenEvidenceTtlMs = 10_000;
    private const int LegacyCloseEvidenceTtlMs = 250; // 面板开着时焦点落回按钮 = toggle 关闭证据：短 TTL + 尾迹，关闭 ~0.4s 内被确认（原 600ms 用户反馈关闭拖沓；误关自愈路径不变：下一次按钮/标签命中重开）

    // 开面板焦点编排保护窗：QQ 打开面板的过程本身会把焦点送到表情按钮上（9.9.19 a11y 激活后
    // 实测 +290ms/+442ms 两连发，全程零 TAB 命中）——窗口内的按钮焦点命中是"打开回声"，
    // 不是 toggle 关闭证据。照关闭证据处理会把锚 TTL 砍到 250ms，开面板 ~0.75s 后被时间确认
    // 关闭（快捷面板"弹出后 ~1s 消失"，2026-10-04 日志实锤）。与编辑框光灭的同名保护窗同值。
    internal const int OpenChoreographyGuardMs = 1500;

    /// <summary>「面板开着 + 焦点命中表情按钮」的语义分级：距面板 APPEARED 超过保护窗才算
    /// toggle 关闭证据（QQ 真关闭面板后焦点落回按钮）；窗口内是开面板的焦点编排回声。</summary>
    internal static bool IsLegacyButtonCloseEvidence(bool panelOpen, long appearedTicks, long nowTicks) =>
        panelOpen && nowTicks - appearedTicks > OpenChoreographyGuardMs;
    private AutomationElement? _legacyTabElement;    // 最近一次焦点命中的按钮/标签
    private IntPtr _legacyTabHwnd;
    private long _legacyTabShownTicks;
    private int _legacyOpenTtlMs = LegacyButtonHitTtlMs; // 最近一次锚刷新采用的 TTL
    private Rect _legacyAnchorRect = Rect.Empty;     // 命中时刻光标附近的小矩形：剪枝树读不到元素矩形，用光标点定位
    private bool _legacyFallbackLogged;              // 兜底首次生效打点（防刷屏）
    private bool _legacyMode;                        // 一旦观测到全扫阻塞（剪枝树特征，实测 ~35s）即闩死：此后完全跳过全扫
    private const string EditorClassKey = "ExEditor-qq-msg-editor";

    // 无障碍树主动激活：WM_GETOBJECT/OBJID_CLIENT 是屏幕阅读器的标准查询
    private const uint WM_GETOBJECT = 0x003D;
    private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int WarmupIntervalMs = 60_000;

    /// <summary>
    /// 表情按钮的宽松匹配：NTQQ 各版本的类名/文案会单独漂移（旧版如 9.9.19 可能改类名或加修饰），
    /// 名称精确命中即认（聊天窗口里叫「表情」的元素就是表情按钮；误判代价只是面板贴错位置，可自愈）。
    /// </summary>
    private static bool LooksLikeEmojiButton(string name, string cls) =>
        name.Trim() == EmojiButtonName;

    private readonly Action<string> _status;
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, AutomationElement> _subscribed = new();
    private readonly Dictionary<IntPtr, long> _warmupTicks = new();
    private readonly HashSet<IntPtr> _warmupLogged = new();

    private CancellationTokenSource? _pollCts;
    private Task? _pollLoop;
    private long _tailUntilTicks;
    private int _tailRunning;
    private volatile bool _panelOpen;
    private int _openMisses;
    private HashSet<int> _qqPids = new();
    private DateTime _lastPidRefresh = DateTime.MinValue;
    private long _lastVerifyRanTicks;
    private int _verifyScheduled;
    private int _verifying;
    private int _consecutiveErrors;
    private bool _degraded;
    private bool _disposed;

    public bool PollingEnabled { get; private set; }
    public bool Running { get; private set; }

    public event EventHandler<QqPanelEventArgs>? PanelAppeared;
    public event EventHandler? PanelDisappeared;

    /// <summary>用户按下 QQ 表情按钮（mousedown 瞬间的焦点信号）。携带缓存的上次面板矩形（可能为 Empty），接收方应立即打开共存面板。</summary>
    public event EventHandler<QqPanelEventArgs>? EmojiButtonClicked;

    public QqPanelWatcher(Action<string> status)
    {
        _status = status;
        _mouseHookProc = MouseHookProc; // 提前物化并终身持有，防止钩子委托被 GC 回收
        _active = this;
    }

    // 当前活动实例：快捷面板共存发送后"同步关闭 QQ 原生面板"经此转发（2026-10-01 用户定案）
    private static QqPanelWatcher? _active;

    // 旧版闩锁的进程级快照（WindowService 发送链路用）：闩上后 FocusedElement 查询只回顶层窗口根、
    // 全树扫描必然昂贵——发送链路据此跳过编辑器 UIA 定位（实测 ~0.8s）、焦点校验改用键盘焦点根窗口
    // 判定（剪枝树上类名校验必失败，2×600ms 纯空等，2026-10-04 实测发送卡 2–3s 的主项）。
    // 静态不随 watcher 重建复位：QQ 升级后残留只会让发送链路继续走启发点（本就可靠），可接受。
    private static int _legacyScanModeLatch;
    internal static bool LegacyScanMode => Volatile.Read(ref _legacyScanModeLatch) == 1;
    private static void LatchLegacyScanMode() => Interlocked.Exchange(ref _legacyScanModeLatch, 1);

    // 用户表情按钮激活代数：每次按下 QQ 表情按钮自增（鼠标钩子乐观路径 + 焦点命中兜底路径）。
    // 共存发送的"延迟关 QQ 面板"凭发送时刻的代数守卫——用户已开始新一轮操作就放弃关闭，
    // 否则 800ms 延迟的 close-q 会把用户刚重新打开的面板 toggle 掉（2026-10-02 日志实锤）
    private static long _userActionGen;
    internal static long UserActionGen => Interlocked.Read(ref _userActionGen);
    private static void BumpUserAction() => Interlocked.Increment(ref _userActionGen);

    /// <summary>尽力同步关闭 QQ 原生表情面板：在缓存的表情按钮矩形中心取 UIA 元素，
    /// 名称/类名对得上才 Invoke——面板开着时点表情按钮即关闭（NTQQ toggle 语义）。
    /// 面板已被认为关闭、按钮找不到/对不上（窗口移动、树懒加载）都静默放弃，
    /// 由既有的关闭跟随兜底。UIA 调用在后台线程执行，不占 UI。
    /// expectedGen = 发送时刻的 UserActionGen；此后用户若又按过表情按钮则放弃关闭。</summary>
    public static void TryCloseQqPanel(long expectedGen)
    {
        var w = _active;
        if (w == null || !w._panelOpen) return; // 面板已关就别点按钮了——toggle 会把它重新打开
        if (Interlocked.Read(ref _userActionGen) != expectedGen)
        {
            Log("close-q skipped: newer user interaction since send");
            return;
        }
        Task.Run(() =>
        {
            try
            {
                if (Interlocked.Read(ref _userActionGen) != expectedGen)
                {
                    Log("close-q skipped in-flight: newer user interaction since send");
                    return;
                }
                w.TryInvokeEmojiButtonCore();
            }
            catch (Exception ex) { Log("close-q failed: " + ex.Message); }
        });
    }

    /// <summary>发送链路专用的同步版关闭：面板开着时在调用线程直接 invoke 表情按钮，
    /// 返回是否真的执行了 invoke。旧版 QQ 的面板光灭会把焦点从编辑框抢回表情按钮
    /// （2026-10-04 实测：编辑框点击后 35ms 焦点即被抢回，Ctrl+V 落空）——发送前先关面板，
    /// 编辑框点击后没有光灭可抢，焦点才稳。调用时机是用户刚点完表情的发送流程，无需代数守卫。
    /// 成功后立即把状态校正为"关"并武装回声抑制：否则锚 TTL 窗口内延迟的 800ms 补关
    /// （TryCloseQqPanel）会误判面板仍开、invoke 把刚关的面板再弹开。</summary>
    public static bool TryCloseQqPanelNow()
    {
        var w = _active;
        if (w == null || !w._panelOpen) return false;
        w._panelOpen = false; // 先校正状态：防后续补关误判 + 钩子路径把本次关闭当"面板开着"
        w._openMisses = 0;
        w._legacyAnchorRect = Rect.Empty;
        Volatile.Write(ref _legacyOptimisticCloseUntil, Environment.TickCount64 + 1500);
        bool ok = w.TryInvokeEmojiButtonCore();
        Log(ok ? "close-now: panel closed by send flow (state corrected)" : "close-now: invoke/click failed, watcher state reset anyway");
        return ok;
    }

    /// <summary>close-q 的按钮矩形解析：优先真实按钮缓存；没有时（面板经 TAB 路径打开、
    /// 按钮从未获得焦点）从 TAB 矩形推导——按钮恒在 TAB 左 9px、下 45px（窗口相对，多轮实测）。</summary>
    private bool TryResolveButtonRectForClose(out Rect rect)
    {
        if (TryResolveEmojiButtonRect(out rect)) return true;
        var hwnd = _tabRectHwnd;
        var rel = _tabRectRel;
        if (hwnd != IntPtr.Zero && !rel.IsEmpty && rel.Width > 0 && GetWindowRect(hwnd, out var wr))
        {
            rect = new Rect(wr.Left + rel.Left - 9, wr.Top + rel.Top + 45, rel.Width, rel.Height);
            return true;
        }
        rect = Rect.Empty;
        return false;
    }

    /// <summary>invoke 表情按钮的核心（同步、调用线程执行 UIA 跨进程调用）。</summary>
    private bool TryInvokeEmojiButtonCore()
    {
        try
        {
            if (!TryResolveButtonRectForClose(out var rect)) { Log("close-q: no cached button/tab rect"); return false; }
            var pt = new System.Windows.Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);
            var el = System.Windows.Automation.AutomationElement.FromPoint(pt);
            // 矩形中心常命中按钮内部的 svg 图标（/q-svg-icon q-icon），沿控制树向上回溯找
            // 表情按钮本体（与面板检测的祖先回溯同款手法，≤12 级封顶）
            var btn = el;
            for (int hop = 0; hop < 12 && !LooksLikeEmojiButton(SafeName(btn), SafeClass(btn)); hop++)
            {
                var parent = System.Windows.Automation.TreeWalker.ControlViewWalker.GetParent(btn);
                if (parent == null || parent == System.Windows.Automation.AutomationElement.RootElement)
                {
                    btn = null;
                    break;
                }
                btn = parent;
            }
            if (btn == null)
            {
                Log("close-q: emoji button ancestor not found, skip");
                return false;
            }
            // Invoke 模式不稳定（树状态波动，实测偶发"不支持的模式"异常从 Invoke() 抛出）：
            // 单独 try 住，异常/模式缺失都退化为真实点击按钮中心——toggle 语义不变，
            // LL 钩子会把它识别为按钮按下（关闭证据），时序自洽。
            try
            {
                if (btn.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern)
                    is System.Windows.Automation.InvokePattern ip)
                {
                    ip.Invoke();
                    Log("close-q: emoji button invoked (panel should close)");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("close-q invoke failed, SendInput fallback: " + ex.Message);
            }
            SendClickAt(new POINT { X = (int)pt.X, Y = (int)pt.Y });
            Log("close-q: emoji button clicked via SendInput fallback");
            return true;
        }
        catch (Exception ex) { Log("close-q failed: " + ex.Message); return false; }
    }

    /// <summary>最近一次焦点命中的聊天输入框矩形（窗口相对偏移，按 qqHwnd 当前位置重算）。
    /// 焦点事件免费送上门的精确点位：剪枝树上 UIA 定位全树扫 ~0.8s、启发点随窗口布局可能落偏
    /// （用户实测"焦点不回输入框，表情没上屏"）——用户点过/编辑过输入框后这里就有真矩形。</summary>
    public static bool TryGetCachedEditorRect(IntPtr qqHwnd, out Rect editorRect)
    {
        editorRect = Rect.Empty;
        var w = _active;
        if (w == null || w._editorRectRel.IsEmpty || w._editorRectRel.Width <= 0) return false;
        if (!GetWindowRect(qqHwnd, out var wr)) return false;
        var rel = w._editorRectRel;
        editorRect = new Rect(wr.Left + rel.Left, wr.Top + rel.Top, rel.Width, rel.Height);
        return true;
    }

    /// <summary>真实点击（SendInput）：UIA Invoke 不可用时的关闭兜底，与 WindowService 同款实现。</summary>
    private static void SendClickAt(POINT pt)
    {
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000,
                   MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var move = new INPUT { type = 0 };
        move.mi = new MOUSEINPUT { dx = (pt.X - vx) * 65536 / vw, dy = (pt.Y - vy) * 65536 / vh, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK };
        var down = new INPUT { type = 0 };
        down.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN };
        var up = new INPUT { type = 0 };
        up.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP };
        SendInput(1, new[] { move }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(40);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }

    public void Start()
    {
        if (Running) return;
        _qqPids = CollectQqPids();
        Running = true;
        // 环境上下文随日志落盘：裸日志脱离反馈表单也能对上号（Issue #2 的反馈附了裸日志，
        // 应用版本错报、Win11 与 Win10 的 UIA 差异都得靠 issue 正文才拼得出来）
        var asmVer = Assembly.GetEntryAssembly()?.GetName().Version;
        Log($"watcher started (Asuka v{asmVer?.ToString(3) ?? "未知"}, {OsTag()}, QQ {FeedbackService.GetQqVersion()})");

        // 低级鼠标钩子装在专用消息泵线程上：LL 钩子对响应超时零容忍，
        // 装在 UI 线程会因预热等卡顿被 Windows 静默摘除（实测发生过），专用线程永不超时。
        _hookThread = new Thread(() =>
        {
            _hookThreadId = (uint)Environment.CurrentManagedThreadId;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                Log("mouse hook install FAILED: " + Marshal.GetLastWin32Error());
                return;
            }
            Log("mouse hook installed on dedicated thread");
            _hookAliveLogged = false;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            Log("mouse hook thread exited");
        })
        {
            IsBackground = true,
            Name = "AsukaMouseHook"
        };
        _hookThread.Start();
        Task.Run(() =>
        {
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);
                Log("focus handler registered");
            }
            catch (Exception ex)
            {
                Log("focus handler register FAILED: " + ex.Message);
            }
        });

        // 已存在的 QQ 窗口立即订阅结构变化事件（含主窗口）
        foreach (var hwnd in GetVisibleWindowsOf(CollectQqPids()))
        {
            EnsureSubscribed(hwnd);
        }

        // QQ 晚于本程序启动的兜底（重启电脑后自启动序不保证）：Start 时 QQ 不在，
        // 订阅与 warmup 全部落空，而懒订阅/焦点 warmup 都以"焦点事件携带有效 QQ 元素"
        // 为前提——a11y 壳层状态下点击信号永远不产生，链路死锁且零报错（2026-10-04 实证）。
        StartQqMonitorLoop();

        // 主动预热探测：跑一次 CheckNow。新版 QQ 上它毫秒级返回并确认 sticker-panel 检测可用；
        // 旧版 NTQQ 上这次全扫会阻塞 6–35s 然后闩进旧版快路径（_legacyMode）——若不做，
        // 闩锁要等首个结构/焦点事件触发验证才发生，用户的第一次点击会撞上阻塞扫描（面板闪一下就没了）。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500); // 给 QQ 启动/登录留点时间，避免对着初始化中的窗口扫
                if (_disposed || !Running) return;
                // 版本闸门先于 CheckNow：已知剪枝树代（9.9.19–9.9.2x 实测）直接预闩、完全跳过阻塞全扫——
                // 否则启动初期的结构事件会引发阻塞扫描连锁，UIA 管线整体堵死、焦点命中全丢（9.9.21 实测）。
                if (IsLegacyQqBuild())
                {
                    _legacyMode = true;
                    LatchLegacyScanMode();
                    Log("legacy scan mode latched (QQ version gate)");
                    return;
                }
                CheckNow();
            }
            catch { }
        });
    }

    private CancellationTokenSource? _qqMonitorCts;
    private Task? _qqMonitorLoop;

    /// <summary>轮询 QQ 进程与可见窗口：新进程出现或已知名冒出新窗口时补订阅（幂等，内置 warmup）。
    /// 同时覆盖 QQ 运行中重启（全新 pid）与主窗从托盘恢复等场景；已订阅窗口在 EnsureSubscribed
    /// 里立即返回，成本只是每轮一次进程枚举 + 窗口遍历。</summary>
    private void StartQqMonitorLoop()
    {
        _qqMonitorCts = new CancellationTokenSource();
        var ct = _qqMonitorCts.Token;
        var knownPids = new HashSet<int>(_qqPids);
        _qqMonitorLoop = Task.Run(async () =>
        {
            var firstScan = true;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, ct);
                    if (_disposed) break;
                    var pids = CollectQqPids();
                    var fresh = pids.Where(p => !knownPids.Contains(p)).ToList();
                    if (fresh.Count > 0)
                        Log($"qq monitor: process appeared (pid={string.Join(',', fresh)})");
                    var windows = GetVisibleWindowsOf(pids).ToList();
                    var unsubscribed = new List<IntPtr>();
                    lock (_gate)
                    {
                        foreach (var hwnd in windows)
                            if (!_subscribed.ContainsKey(hwnd)) unsubscribed.Add(hwnd);
                    }
                    if (unsubscribed.Count > 0)
                        Log($"qq monitor: {unsubscribed.Count} visible window(s) not subscribed, resubscribing");
                    foreach (var hwnd in unsubscribed)
                        EnsureSubscribed(hwnd);
                    if (firstScan)
                    {
                        firstScan = false;
                        Log($"qq monitor active (pid=[{string.Join(',', pids)}], windows={windows.Count}, scan every 5s)");
                    }
                    if (fresh.Count > 0 || unsubscribed.Count > 0)
                        PruneSubscriptions();
                    knownPids = pids;
                    if (pids.Count > 0) _qqPids = pids;
                }
                catch (OperationCanceledException) { break; }
                catch { /* 轮询兜底自身不许抛 */ }
            }
        }, ct);
    }

    private void StopQqMonitorLoop()
    {
        try
        {
            _qqMonitorCts?.Cancel();
            try { _qqMonitorLoop?.Wait(1500); } catch { }
            _qqMonitorCts?.Dispose();
        }
        catch { }
        _qqMonitorCts = null;
        _qqMonitorLoop = null;
    }

    /// <summary>QQ 版本早于 9.9.30 视为剪枝树代（9.9.19/9.9.21 实测剪枝；9.9.36 实测完整树）。</summary>
    private static bool IsLegacyQqBuild()
    {
        try
        {
            var core = FeedbackService.GetQqVersion().Split('-')[0];
            var parts = core.Split('.');
            if (parts.Length >= 3
                && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && int.TryParse(parts[2], out var c))
            {
                return a < 9 || (a == 9 && (b < 9 || (b == 9 && c < 30)));
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        if (!Running) return;
        Running = false;
        _disposed = true;
        if (_active == this) _active = null;
        StopQqMonitorLoop();
        StopPollLoop();
        if (_mouseHook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
        }
        if (_hookThread != null)
        {
            try { PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { _hookThread.Join(1000); } catch { }
            _hookThread = null;
        }
        // RemoveAllEventHandlers 会同步等所有在途回调完成，实测耗时 0s/1s/3min/永不 不等——
        // 在 UI 线程上调用它会把自己挂死（窗口关了进程不退，僵尸还握着单实例互斥锁，
        // 之后所有新启动都拉不起来）。挪到后台 MTA 线程异步拆，不等待；_disposed 已置位，
        // 拆卸完成前漏进来的残留事件会被各处理器入口的守卫丢弃，进程退出兜底清理。
        // 已知竞态：Dispose 后极快重建新 watcher 时，旧拆卸可能误删新注册——现实中只有
        // 设置开关"QQ 表情面板共存"会重建，人手速度远慢于拆卸，接受此权衡。
        _ = Task.Run(() => { try { Automation.RemoveAllEventHandlers(); } catch { } });
        lock (_gate) _subscribed.Clear();
        Log("watcher disposed");
    }

    /// <summary>轮询保底开关（可选设置项）。事件失效时开启，常态保持关闭。</summary>
    public void SetPollingFallback(bool enabled)
    {
        PollingEnabled = enabled;
        if (!Running) return;
        if (enabled) StartPollLoop(); else StopPollLoop();
        Log("polling fallback = " + enabled);
    }

    private void StartPollLoop()
    {
        lock (_gate)
        {
            if (_pollLoop != null) return;
            _pollCts = new CancellationTokenSource();
            var ct = _pollCts.Token;
            _pollLoop = Task.Run(async () =>
            {
                var consecutiveErrors = 0;
                var degraded = false;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        CheckNow(missCacheable: true); // 轮询保底本就接受秒级感知延迟，可吃负缓存
                        consecutiveErrors = 0;
                        if (degraded) { degraded = false; _status("QQ 表情面板监听已恢复"); }
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        Log($"poll error #{consecutiveErrors}: {ex.Message}");
                        if (consecutiveErrors >= DegradeThreshold && !degraded)
                        {
                            degraded = true;
                            _status("QQ 表情面板监听已降级（QQ 界面结构可能变化），快捷键不受影响");
                        }
                    }

                    var interval = _panelOpen ? FastPollMs : IdlePollMs;
                    if (degraded) interval = DegradedPollIntervalMs;
                    try { await Task.Delay(interval, ct); }
                    catch (OperationCanceledException) { return; }
                }
            });
            Log("poll loop started");
        }
    }

    private void StopPollLoop()
    {
        lock (_gate)
        {
            _pollCts?.Cancel();
            try { _pollLoop?.Wait(1500); } catch { }
            _pollCts?.Dispose();
            _pollCts = null;
            _pollLoop = null;
        }
    }

    // --- 焦点事件路径 ---

    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        if (_disposed || !Running) return;
        try
        {
            // 事件自带的 sender 才是真正聚焦的元素。旧版 NTQQ（9.9.19/9.9.21 实测）剪枝树下
            // FocusedElement 查询只返回顶层窗口根（name="QQ"），内容元素必须从事件取。
            var el = sender as AutomationElement ?? AutomationElement.FocusedElement;
            if (el == null) return;
            int pid = el.Current.ProcessId;
            RefreshPidsIfNeeded(pid);

            if (!_qqPids.Contains(pid))
            {
                // 焦点离开 QQ（点了桌面/其他应用）：QQ 面板大概率已被关掉，验证一次
                if (_panelOpen) ScheduleThrottledVerify();
                _legacyTabElement = null;  // 旧版兜底锚同步失效：切走即收，缓解面板滞留
                _legacyAnchorRect = Rect.Empty;
                return;
            }

            Volatile.Write(ref _lastQqFocusTicks, Environment.TickCount64);

            // 懒订阅：正在使用的 QQ 窗口补订结构变化事件（新开聊天窗口也由此覆盖）
            try
            {
                var hwnd = (IntPtr)el.Current.NativeWindowHandle;
                if (hwnd != IntPtr.Zero) EnsureSubscribed(hwnd);
            }
            catch { }

            // 用户正在交互 = 即将需要 DOM（表情按钮焦点命中/面板扫描）：确保无障碍树已激活
            try { WarmUpAccessibility(GetTopWindowHwnd(el)); } catch { }

            string name = SafeName(el);
            string cls = SafeClass(el);
            bool legacyTab = LegacyPanelTabNames.Contains(name.Trim());
            if (LooksLikeEmojiButton(name, cls) || legacyTab)
            {
                Log($"focus hit emoji button (name={name}, class={cls}{(legacyTab ? ", legacy" : "")})");

                // 缓存按钮矩形与宿主窗口：鼠标钩子的校验目标 + 乐观打开的合成锚点。
                // 宿主句柄必须与点击时刻同路解析（按钮中心 WindowFromPoint→GA_ROOT）：钩子校验时
                // 命中的根窗口若和这里缓存的不是同一个句柄，三重校验会把真实点击也拒掉。
                // GetTopWindowHwnd 的 UIA 走查结果只作 FromPoint 失败时的兜底。
                // TAB 命中（面板内部标签）不缓存：矩形在面板里，会覆盖真按钮矩形，
                // 导致钩子拒绝用户下一次对真按钮的点击（实测 18:43）。
                if (!legacyTab)
                {
                    try
                    {
                        var br = el.Current.BoundingRectangle;
                        var topHwnd = GetTopWindowHwnd(el);
                        if (topHwnd == IntPtr.Zero) topHwnd = _lastPanelRectHwnd; // 走查失败时用面板检测已确认的 QQ 顶层窗口兜底
                        if (br.Width > 0)
                        {
                            var center = new POINT { X = (int)(br.Left + br.Width / 2), Y = (int)(br.Top + br.Height / 2) };
                            var hitRoot = RootWindowFromPoint(center);
                            GetWindowThreadProcessId(hitRoot, out var hitPid);
                            if (hitRoot != IntPtr.Zero && hitPid == (uint)pid) topHwnd = hitRoot; // 只接受 QQ 自己的窗口，防面板叠在按钮上时缓存错宿主
                        }
                        if (br.Width > 0 && topHwnd != IntPtr.Zero)
                        {
                            _emojiBtnRect = br;
                            _emojiBtnHwnd = topHwnd;
                            _emojiBtnPid = pid;
                            _emojiBtnRectRel = ToWindowRelative(topHwnd, br);
                            Log($"button rect cached {br} rel={_emojiBtnRectRel} hwnd={topHwnd}");
                        }
                    }
                    catch { }
                }

                // TAB 命中（面板内部标签）不缓存按钮矩形（会覆盖真按钮矩形、破坏钩子校验），
                // 但要单独记 TAB 矩形：面板经 TAB 路径打开时按钮从未获得焦点，close-q（发送预关闭）
                // 靠它推导按钮位置（按钮恒在 TAB 左 9px、下 45px，实测多轮一致）。
                // 否则预关闭失败 → 编辑框点击触发面板光灭 → 焦点被编排抢回按钮 → Ctrl+V 落空。
                if (legacyTab)
                {
                    try
                    {
                        var tbr = el.Current.BoundingRectangle;
                        var guiT = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                        IntPtr troot = IntPtr.Zero;
                        if (GetGUIThreadInfo(0, ref guiT) && guiT.hwndFocus != IntPtr.Zero)
                            troot = GetAncestor(guiT.hwndFocus, GA_ROOT);
                        if (tbr.Width > 0 && troot != IntPtr.Zero && GetWindowRect(troot, out var twr))
                        {
                            _tabRectRel = new Rect(tbr.Left - twr.Left, tbr.Top - twr.Top, tbr.Width, tbr.Height);
                            _tabRectHwnd = troot;
                        }
                    }
                    catch { }
                }

                // 乐观关闭的焦点回声抑制：关闭点击/收面板编排会把焦点先后送回按钮和面板标签
                // （事件在关闭后 ~1s 内连续到达），若照常刷新锚会把刚关闭的状态翻回"开"
                // （闪烁重开）。窗口内只保持尾迹验证；真正的重新打开由鼠标钩子处理（不经此路）。
                if (Environment.TickCount64 < Volatile.Read(ref _legacyOptimisticCloseUntil))
                {
                    ArmTailVerify();
                    return;
                }

                // 旧版 NTQQ 兜底锚：命中即刷新锚 + 按证据强度分级 TTL（见各分支注释）。
                // TAB 命中=面板确实开着（长 TTL）；BUTTON 命中=开/关动作（关闭证据=短 TTL，
                // 打开证据=长 TTL 等 TAB 续期，防状态卡死/闪烁重开）。
                _legacyTabElement = el;
                _legacyTabHwnd = GetTopWindowHwnd(el);
                _legacyTabShownTicks = Environment.TickCount64;
                if (legacyTab)
                {
                    _legacyOpenTtlMs = LegacyTabHitTtlMs;
                }
                else if (IsLegacyButtonCloseEvidence(_panelOpen, _lastPanelAppearedTicks, Environment.TickCount64))
                {
                    // 面板开着且已过开面板编排保护窗时焦点落回按钮 = toggle 关闭证据：短 TTL 并启动
                    // 尾迹验证，否则无轮询/无事件时过期永不被发现（实测"关了以后再点没反应"的成因之一）。
                    _legacyOpenTtlMs = LegacyCloseEvidenceTtlMs;
                    ArmTailVerify();
                }
                else
                {
                    // 焦点落到按钮（面板关着，或开面板保护窗内的编排回声）= 打开证据：长 TTL 等 TAB
                    // 续期（短 TTL 会在 TAB 迟到时误判关闭→闪烁重开）
                    _legacyOpenTtlMs = LegacyOpenEvidenceTtlMs;
                }
                // 锚矩形：BUTTON 命中直接用按钮矩形（精确、确定性；光标在合成点击/焦点事件
                // 异步处理时可能错位——实测 18:52 锚跑到 800px 外）；TAB 命中在面板内部，
                // 其矩形会把快捷面板带进面板里，保留光标近似。
                if (!legacyTab && _emojiBtnRect.Width > 0)
                {
                    _legacyAnchorRect = _emojiBtnRect;
                }
                else
                {
                    try
                    {
                        if (GetCursorPos(out var pt))
                            _legacyAnchorRect = new Rect(pt.X - 16, pt.Y - 16, 32, 32);
                    }
                    catch { }
                }

                // 焦点事件是异步投递的——乐观打开主要由鼠标钩子在 mousedown 瞬间完成。这里只兜
                // 钩子没覆盖住的点击：钩子已为本次 mousedown 触发过则跳过（重复事件只会让面板重定位）；
                // 钩子装失败、或点击瞬间按钮矩形尚未缓存（本会话第一次点，钩子无从命中）时由此兜底。
                // 兜底同样必须过点击校验：最近一次 mousedown ≤250ms 且落点在重算后的按钮矩形上——
                // 否则窗口打开/切回时 Chromium 恢复旧焦点落在表情按钮、用户恰好在别处按着鼠标，
                // 就会被误判成点击而弹面板（实测误触发源）。
                if (Running && IsLeftButtonDown() && !HookCoveredLastMouseDown())
                {
                    var hostHwnd = GetTopWindowHwnd(el);
                    if (hostHwnd != IntPtr.Zero && FocusPathClickIsOnButton())
                    {
                        var cachedRect = hostHwnd == _lastPanelRectHwnd ? _lastPanelRect : Rect.Empty;
                        _optimisticPending = true;
                        ArmTailVerify();
                        BumpUserAction();
                        Log($"emoji button clicked via focus event (optimistic fallback, cachedRect={cachedRect})");
                        EmojiButtonClicked?.Invoke(this, new QqPanelEventArgs(hostHwnd, cachedRect, _emojiBtnRect));
                    }
                }

                _ = VerifyWithOpenRetriesAsync();
            }
            else
            {
                // 编辑框系元素判定（两种树模式取并集）：新版/完全模式 cls 含 EditorClassKey；
                // legacy 基本模式（无深度无障碍客户端时）焦点元素 ClassName 不可读，cls==""。
                // 实测 18:12/18:14 两轮诊断：同一编辑框事件在两种模式下分别是 cls="ProseMirror
                // ExEditor-..." 与 cls=""（name=会话标题）——单一签名都会漏。按钮/面板标签有
                // 可读标识，走不到这个分支。注意：QQ 面板内部元素在基本模式下同为空 cls，
                // 空签名只是初筛，真判据在下面的"编辑区证据"（底半区 + 按钮下方）。
                bool editorLike = cls.Contains(EditorClassKey) || cls.Length == 0;

                if (editorLike)
                {
                    // 编辑框焦点回声：发送链路找回焦点后的正向验证信号（要求晚于点击到达，
                    // 见 PollFocusVerified；剪枝树上 caret 恒为系统零值、类名不可读，
                    // 这是唯一可观测的"焦点真的回到输入框"证据）
                    Volatile.Write(ref _editorFocusEchoTicks, Environment.TickCount64);

                    // 编辑区证据判定 + 锚点缓存：焦点所在根窗口用 GUITHREADINFO 取（纯本地，
                    // UIA 走查在基本模式会失败）。锚点优先元素矩形（完全模式，权威），
                    // 退化用光标位置（用户点编辑框时焦点事件必伴随光标在编辑区内）。
                    // 底半区排除消息区/面板骨架；"光标在表情按钮下方"排除面板内部元素
                    // （按钮在编辑器工具栏上，其下才是文本区）——18:23 实测面板内部空 cls
                    // 焦点 + 光标停在按钮上，曾把按钮位置误判成编辑框引发假光灭关闭（闪烁）。
                    IntPtr eroot = IntPtr.Zero;
                    Rect erel = Rect.Empty;
                    bool editorConfirmed = false;
                    bool rectOk = false;
                    var anchor = new POINT { X = -1, Y = -1 };
                    try
                    {
                        var guiF = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                        if (GetGUIThreadInfo(0, ref guiF) && guiF.hwndFocus != IntPtr.Zero)
                            eroot = GetAncestor(guiF.hwndFocus, GA_ROOT);
                        if (eroot != IntPtr.Zero && GetWindowRect(eroot, out var gwr))
                        {
                            try
                            {
                                var r = el.Current.BoundingRectangle;
                                if (r.Width > 0)
                                {
                                    anchor.X = (int)(r.Left + r.Width / 2);
                                    anchor.Y = (int)(r.Bottom - Math.Min(25, r.Height / 4));
                                    rectOk = true;
                                }
                            }
                            catch { }
                            if (!rectOk && GetCursorPos(out var cp)) { anchor.X = cp.X; anchor.Y = cp.Y; }

                            if (anchor.X >= 0 && anchor.Y > gwr.Top + (gwr.Bottom - gwr.Top) * 0.55
                                && (rectOk || _emojiBtnRect.Width <= 0 || anchor.Y > _emojiBtnRect.Bottom + 10))
                            {
                                // 缓存 40x20 的点击区（窗口相对）：发送链路按此找回焦点
                                erel = new Rect(anchor.X - gwr.Left - 20, anchor.Y - gwr.Top - 10, 40, 20);
                                editorConfirmed = true;
                            }
                        }
                    }
                    catch { }

                    if (editorConfirmed)
                    {
                        // 光灭乐观关闭：面板开着时焦点进编辑区 = 用户点了输入框、QQ 已收面板。
                        // 距面板打开 <1.5s 内不判：QQ 开面板的焦点编排也会路过编辑区形态的
                        // 空 cls 元素（18:23 实测 667ms 处假关闭→闪烁重开），窗口期内交给锚 TTL。
                        if (_panelOpen && Environment.TickCount64 - _lastPanelAppearedTicks > OpenChoreographyGuardMs)
                        {
                            OptimisticClosePanel("focus hit chat editor (light-dismiss)");
                        }

                        _editorHwnd = eroot;
                        _editorPid = pid;
                        _editorRectRel = erel;
                        var logKey = $"{eroot}:{(int)erel.Left},{(int)erel.Top}";
                        if (_editorRectLogKey != logKey)
                        {
                            _editorRectLogKey = logKey;
                            Log($"editor anchor cached rel=({(int)erel.Left},{(int)erel.Top}) hwnd={eroot} rectOk={rectOk}");
                        }
                    }
                    else if (_editorRejects < 5)
                    {
                        _editorRejects++;
                        Log($"editor-like focus without editor evidence (panel internal/message area), ignored");
                    }
                }

                // 焦点在 QQ 窗口内移动：面板可能被收起（如点击输入框），节流验证
                ScheduleThrottledVerify();
            }
        }
        catch { }
    }

    // --- 结构变化事件路径（不按类型过滤：面板关闭可能是 ChildrenBulkRemoved 整棵子树批量移除，
    //     只认 ChildAdded/ChildRemoved 会漏掉关闭信号；处理函数零跨进程成本，靠节流防刷） ---

    private readonly HashSet<int> _seenStructureTypes = new();

    private void OnStructureChanged(object sender, StructureChangedEventArgs e)
    {
        if (_disposed || !Running) return;

        // 诊断：记录每种结构变化类型的首现，确认 QQ 实际派发哪些类型
        lock (_gate)
        {
            if (_seenStructureTypes.Add((int)e.StructureChangeType))
            {
                Log($"structure event type first seen: {e.StructureChangeType}");
            }
        }

        ScheduleThrottledVerify();
    }

    private void ScheduleThrottledVerify()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastVerifyRanTicks);
        if (now - last >= VerifyMinIntervalMs)
        {
            // 窗口外：立即验证（首次响应零延迟）
            RunVerify();
            return;
        }
        // 窗口内（事件风暴）：合并为窗口末尾的一次验证
        if (Interlocked.Exchange(ref _verifyScheduled, 1) == 0)
        {
            var delay = (int)Math.Max(VerifyMinIntervalMs - (now - last), 20);
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                Interlocked.Exchange(ref _verifyScheduled, 0);
                if (!_disposed) CheckNow(missCacheable: true); // 风暴合并出的验证没有点击信号，可吃负缓存
            });
        }
    }

    private void RunVerify()
    {
        Interlocked.Exchange(ref _lastVerifyRanTicks, Environment.TickCount64);
        CheckNow();
    }

    private async Task VerifyWithOpenRetriesAsync()
    {
        // QQ 在鼠标按下时把焦点给按钮，mouse-up 后才弹出面板；80ms 步进尽快逮到面板出现。
        // 序列覆盖到 700ms 以容忍长按；乐观打开后面板一直没出现（如拖出按钮 aborted click）则收起共存面板。
        foreach (var delay in new[] { 0, 80, 160, 240, 320, 480, 700 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (_disposed) return;
            if (CheckNow())
            {
                _optimisticPending = false;
                return;
            }
        }

        if (_optimisticPending && !_panelOpen)
        {
            _optimisticPending = false;
            Log("optimistic open aborted (QQ panel never appeared)");
            PanelDisappeared?.Invoke(this, EventArgs.Empty);
            // 用户明确点了表情按钮却始终检测不到面板 → 高概率是本机 QQ 版本的类名失配
            // （如旧版 9.9.x 面板不叫 sticker-panel）。dump 一次 QQ 窗口树概要进日志，
            // 反馈者把 %TEMP%\asuka-watcher.log 发回来即可定位该版本的真实标识。
            _ = DumpQqTreeDiagnosticsCoreAsync("panel never detected after emoji button click");
        }
    }

    // ———— UIA 结构诊断（旧版 QQ 兼容定位）————

    private long _lastDumpTicks;

    /// <summary>设置页手动转储入口：豁免 90 秒限频（用户点了就要有产出）。
    /// 共存监听没开也能转储——诊断对象是本机 QQ 的 UIA 树，与监听是否在跑无关。
    /// 自动 dump 的唯一入口在"点击信号已产生"之后；点击信号本身失配不产生时
    /// （旧版 QQ 焦点/类名双双对不上，Issue #2 实证），这个手动入口是死区里唯一的诊断出口。</summary>
    public static Task DumpDiagnosticsNowAsync()
    {
        var w = _active;
        if (w != null) return w.DumpQqTreeDiagnosticsCoreAsync("manual dump from settings", force: true);
        return DumpQqTreeWalkAsync("manual dump from settings (watcher off)", cancelled: null);
    }

    private Task DumpQqTreeDiagnosticsCoreAsync(string reason, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastDumpTicks < 90_000) return Task.CompletedTask;
        _lastDumpTicks = now;
        return DumpQqTreeWalkAsync(reason, cancelled: () => _disposed);
    }

    /// <summary>
    /// 把 QQ 可见窗口的 UIA 树概要（顶层窗口类名 + 前 N 层含面板/表情关键词的类名）写进
    /// asuka-watcher.log。跨版本适配的地雷是写死的类名，这份日志直接暴露本机 QQ 的真实标识。
    /// 限频 90 秒，遍历限深 3 层、命中上限 40 条，避免 Chromium 大树拖慢后台线程。
    /// </summary>
    private static Task DumpQqTreeWalkAsync(string reason, Func<bool>? cancelled)
    {
        return Task.Run(() =>
        {
            try
            {
                var pids = CollectQqPids();
                if (pids.Count == 0) { Log($"diag ({reason}): no QQ process found"); return; }
                Log($"diag ({reason}): QQ pids=[{string.Join(',', pids)}], dumping window tree summary");
                // 焦点现场：焦点路径的前提是"点表情按钮瞬间焦点落按钮上"。转储前把焦点放聊天输入框
                // 或表情按钮，这条直接暴露本机的焦点元素长什么样（名字对不上/焦点压根不进 QQ 一眼定位）
                try
                {
                    var focused = AutomationElement.FocusedElement;
                    if (focused != null)
                    {
                        var fpid = focused.Current.ProcessId;
                        Log($"diag   focused pid={fpid} ({(pids.Contains(fpid) ? "QQ" : "other")}) class=\"{SafeClass(focused)}\" name=\"{TruncateForLog(SafeName(focused))}\"");
                    }
                }
                catch (Exception ex) { Log($"diag   focused element read failed: {ex.Message}"); }
                foreach (var hwnd in GetVisibleWindowsOf(pids))
                {
                    if (cancelled?.Invoke() == true) return;
                    try
                    {
                        var root = AutomationElement.FromHandle(hwnd);
                        Log($"diag window hwnd={hwnd} class=\"{SafeClass(root)}\" name=\"{SafeName(root)}\"");
                        var visited = 0;
                        var skeletonLines = 0;
                        DumpCandidateClasses(root, depth: 0, maxDepth: 3, hits: new List<string>(), ref visited, ref skeletonLines, cancelled);
                        // visited = UIA 在这棵树里能看见多少节点：个位数 ≈ QQ 的无障碍树根本没暴露
                        // （Chromium a11y 未激活，适配类名无从谈起），数百 ≈ 树在但标识对不上。
                        // 两种失配的修法完全不同，必须能区分。
                        Log($"diag   window walk done: visited={visited}");
                    }
                    catch (Exception ex)
                    {
                        Log($"diag window hwnd={hwnd} failed: {ex.Message}");
                    }
                }
                Log("diag dump complete");
            }
            catch (Exception ex)
            {
                Log("diag dump failed: " + ex.Message);
            }
        });
    }

    /// <summary>限深收集类名/名称含关键词的元素（面板候选）与全部子窗口类名，逐条落日志。
    /// 访问节点总数单独封顶：hits 只限命中数，不封遍历量的话一棵不匹配的 Chromium 大树
    /// 会被完整走完（实测 300+ 节点、上千次跨进程往返、后台线程 3–10 秒）。
    /// 前 2 层额外记骨架（逐类名，限量）：树长什么样比命中与否更能定位失配发生在哪一层。</summary>
    private static void DumpCandidateClasses(AutomationElement element, int depth, int maxDepth,
        List<string> hits, ref int visited, ref int skeletonLines, Func<bool>? cancelled)
    {
        if (cancelled?.Invoke() == true || depth > maxDepth || hits.Count > 40 || visited > 400) return;
        try
        {
            var children = element.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                if (cancelled?.Invoke() == true) return;
                visited++; // 限的是跨进程成本（FindAll 遍历 + 每个子元素 2 次属性读），不是命中数
                var cls = SafeClass(child);
                var name = SafeName(child);
                if (cls.Length > 0)
                {
                    bool interesting = cls.Contains("sticker", StringComparison.OrdinalIgnoreCase)
                        || cls.Contains("panel", StringComparison.OrdinalIgnoreCase)
                        || cls.Contains("emoji", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("表情", StringComparison.Ordinal);
                    if (interesting)
                    {
                        hits.Add($"class=\"{cls}\" name=\"{name}\"");
                        Log($"diag   depth={depth} CANDIDATE class=\"{cls}\" name=\"{name}\"");
                    }
                    else if (depth <= 2 && skeletonLines < 24) // 骨架只记浅层，限量防日志爆炸
                    {
                        skeletonLines++;
                        Log($"diag   depth={depth} child class=\"{cls}\" name=\"{TruncateForLog(name)}\"");
                    }
                }
                DumpCandidateClasses(child, depth + 1, maxDepth, hits, ref visited, ref skeletonLines, cancelled);
            }
        }
        catch { /* 元素中途失效即止 */ }
    }

    private static string TruncateForLog(string s) => s.Length <= 12 ? s : s[..12] + "…";

    // --- 点击后补验尾迹 ---

    /// <summary>
    /// 表情按钮点击后的短尾迹补验。快速连点按钮关闭时 QQ 面板会"闪现"（关→开→关），
    /// 焦点全程停在按钮上不再变化，而 QQ 侧对闪现的最终关闭可能不派发任何结构/焦点事件
    /// （透明隐藏 + 滞留元素复用）——纯事件驱动会停在"面板开着"的陈旧状态，快捷面板不跟随关闭。
    /// 尾迹保证点击后的短时间内持续验证，把最终关闭补检出来；每次点击重新计时，零常态成本。
    /// </summary>
    private void ArmTailVerify()
    {
        Interlocked.Exchange(ref _tailUntilTicks, Environment.TickCount64 + TailWindowMs);
        if (Interlocked.Exchange(ref _tailRunning, 1) == 1) return;
        Log("tail verify loop started");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed)
                {
                    var until = Interlocked.Read(ref _tailUntilTicks);
                    var now = Environment.TickCount64;
                    if (now >= until)
                    {
                        // 宽限一拍再退出：ArmTail 可能刚续期而本循环尚未看见
                        await Task.Delay(60);
                        if (Interlocked.Read(ref _tailUntilTicks) <= Environment.TickCount64) break;
                        continue;
                    }
                    await Task.Delay((int)Math.Min(TailVerifyIntervalMs, until - now));
                    if (_disposed) break;
                    CheckNow();
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _tailRunning, 0);
                // 退出瞬间恰好被续期的兜底：还有效就再起一轮
                if (!_disposed && Interlocked.Read(ref _tailUntilTicks) > Environment.TickCount64) ArmTailVerify();
            }
        });
    }

    // --- 面板检测（所有信号共用，互斥防重入；错误计入降级统计） ---

    /// <param name="missCacheable">
    /// true = 允许吃"未命中负缓存"：面板未开时的全树 FindFirst 是单次验证最贵的一段
    /// （对真实 QQ 窗口实测 18–66 ms，全部是 QQ 进程内的同步跨进程成本），
    /// 输入法打字等引起的结构事件风暴会把它打到节流上限 6.7 次/秒。
    /// 负缓存把无信号的常态验证降到 ≤1 次/秒——事件驱动的面板出现（乐观打开/点击重试/关闭确认）
    /// 一律强制全扫，不受缓存影响；无信号"惊现"的面板最多延迟 1s 被发现（与轮询保底的常态间隔同级）。
    /// </param>
    private bool CheckNow(bool missCacheable = false)
    {
        if (Interlocked.Exchange(ref _verifying, 1) == 1) return _panelOpen;
        Interlocked.Exchange(ref _lastVerifyRanTicks, Environment.TickCount64);
        try
        {
            RefreshPidsIfNeeded();
            var (open, hwnd, rect) = ScanQqWindows(_qqPids, missCacheable);
            if (open)
            {
                _openMisses = 0;
                if (!_panelOpen)
                {
                    _panelOpen = true;
                    _optimisticPending = false;
                    _lastPanelRect = rect;
                    _lastPanelRectHwnd = hwnd;
                    _lastPanelAppearedTicks = Environment.TickCount64;
                    Log($"panel APPEARED hwnd={hwnd} rect={rect}");
                    PanelAppeared?.Invoke(this, new QqPanelEventArgs(hwnd, rect));
                }
            }
            else if (_panelOpen)
            {
                // 关闭确认采用时间窗而非事件次数：事件路径可能只触发一次验证（节流合并），
                // 首次 miss 后安排一次延迟复核，届时仍 miss 即收起
                if (_openMisses == 0)
                {
                    _openMisses = 1;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(CloseConfirmDelayMs);
                        if (!_disposed && _panelOpen) CheckNow();
                    });
                }
                else
                {
                    _openMisses = 0;
                    _panelOpen = false;
                    Log("panel DISAPPEARED (time-confirmed)");
                    PanelDisappeared?.Invoke(this, EventArgs.Empty);
                }
            }
            return _panelOpen;
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            Log($"verify error #{_consecutiveErrors}: {ex.Message}");
            if (_consecutiveErrors >= DegradeThreshold && !_degraded)
            {
                _degraded = true;
                _status("QQ 表情面板监听已降级（QQ 界面结构可能变化），快捷键不受影响");
            }
            return _panelOpen;
        }
        finally
        {
            Interlocked.Exchange(ref _verifying, 0);
        }
    }

    /// <summary>编辑框焦点回声的原始时刻（TickCount64；0 = 从未）。
    /// 发送链路验证"焦点真的回到输入框"用：回声必须晚于点击派发时刻才算数。</summary>
    public static long EditorFocusEchoTicks() => Volatile.Read(ref _editorFocusEchoTicks);

    /// <summary>QQ 内最近一次焦点事件距今是否已静默 minQuietMs 以上。
    /// QQ 收面板会连续 ~0.8s 回摆焦点（按钮→标签→编辑框→按钮），发送预关闭后
    /// 必须等这套编排平息再点编辑框，否则编辑框焦点会被随后的回摆覆盖（表情不落框）。</summary>
    public static bool QqFocusQuiescent(int minQuietMs) =>
        Environment.TickCount64 - Volatile.Read(ref _lastQqFocusTicks) >= minQuietMs;

    private void RefreshPidsIfNeeded(int focusedPid)
    {
        if (_qqPids.Contains(focusedPid)) return;
        if ((DateTime.Now - _lastPidRefresh).TotalMilliseconds < 2000) return;
        _lastPidRefresh = DateTime.Now;
        _qqPids = CollectQqPids();
    }

    private void RefreshPidsIfNeeded()
    {
        if ((DateTime.Now - _lastPidRefresh).TotalMilliseconds < 2000) return;
        _lastPidRefresh = DateTime.Now;
        _qqPids = CollectQqPids();
        PruneSubscriptions();
    }

    // --- 结构变化事件订阅管理（懒订阅 + 清理） ---

    private void EnsureSubscribed(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_subscribed.ContainsKey(hwnd)) return;
            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                Automation.AddStructureChangedEventHandler(el, TreeScope.Descendants, OnStructureChanged);
                _subscribed[hwnd] = el;
                Log($"structure events subscribed hwnd={hwnd}");
            }
            catch (Exception ex)
            {
                Log($"subscribe failed hwnd={hwnd}: {ex.Message}");
            }
        }
        WarmUpAccessibility(hwnd);
    }

    /// <summary>主动激活 QQ 的无障碍树：向 QQ 顶层窗口发一次 MSAA WM_GETOBJECT(OBJID_CLIENT)。
    /// Chromium/CEF 只在检测到屏幕阅读器式查询时才开启渲染进程 accessibility，被动等待在
    /// 个别环境永远不会发生——DOM 不进 UIA，表情按钮焦点/sticker-panel 扫描全部落空且零报错
    /// （Issue #2 的实证形态）。本机实测：空闲冷树仅 5 个壳层节点，一发查询即可在树中找到
    /// 「表情」按钮；激活按渲染进程生效且持续，隐藏窗口保持挂起（等可见/聚焦时再暖）。
    /// 60s 节流；SMTO_ABORTIFHUNG：目标窗口卡死时 500ms 内放弃，不拖累调用线程。</summary>
    private void WarmUpAccessibility(IntPtr topHwnd)
    {
        if (topHwnd == IntPtr.Zero) return;
        var now = Environment.TickCount64;
        bool firstTime;
        lock (_gate)
        {
            if (_warmupTicks.TryGetValue(topHwnd, out var last) && now - last < WarmupIntervalMs) return;
            _warmupTicks[topHwnd] = now;
            firstTime = _warmupLogged.Add(topHwnd);
        }
        Task.Run(() =>
        {
            try
            {
                SendMessageTimeout(topHwnd, WM_GETOBJECT, IntPtr.Zero, (IntPtr)OBJID_CLIENT,
                    SMTO_ABORTIFHUNG, 500, out var lresult);
                if (firstTime) Log($"a11y warmup sent hwnd={topHwnd} lresult=0x{lresult.ToInt64():X}");
            }
            catch { }
        });
    }

    private void PruneSubscriptions()
    {
        lock (_gate)
        {
            // 窗口销毁/隐藏后不能只删字典引用：订阅本身还挂在 UIA 上（结构事件照常派发、
            // 底层 Event 句柄不回收）。逐元素退订与 RemoveAllEventHandlers 一样可能被在途
            // 回调拖住，放后台线程拆，不等（漏进的残留事件由入口守卫丢弃）。
            var dead = new List<(IntPtr Hwnd, AutomationElement Element)>();
            foreach (var kv in _subscribed)
            {
                if (!IsWindowVisible(kv.Key)) dead.Add((kv.Key, kv.Value));
            }
            foreach (var (hwnd, element) in dead)
            {
                _subscribed.Remove(hwnd);
                _warmupTicks.Remove(hwnd);
                _warmupLogged.Remove(hwnd);
                // 宿主窗口已消失：同步清按钮缓存。句柄值之后可能被系统复用，
                // 留着陈旧的窗口相对偏移会在新窗口里误命中（钩子点击校验的第一道就失效）
                if (hwnd == _emojiBtnHwnd)
                {
                    _emojiBtnHwnd = IntPtr.Zero;
                    _emojiBtnPid = 0;
                    _emojiBtnRect = Rect.Empty;
                    _emojiBtnRectRel = Rect.Empty;
                }
                _ = Task.Run(() =>
                {
                    try { Automation.RemoveStructureChangedEventHandler(element, OnStructureChanged); }
                    catch { /* 元素已失效 = 订阅已随进程/窗口消亡 */ }
                });
            }
            if (dead.Count > 0) Log($"pruned {dead.Count} stale subscriptions (unsubscribed)");
        }
    }

    // --- 扫描 ---

    private AutomationElement? _cachedPanel;     // 上次找到的面板元素：验证时先廉价探测，避免每次全树扫描
    private IntPtr _cachedPanelHwnd;
    private long _lastFullScanMissTicks = long.MinValue; // 上次全扫未命中的时刻：无信号验证在 TTL 内直接复用结果
    private const int FullScanMissTtlMs = 1000;
    private Rect _lastPanelRect = Rect.Empty;    // 上次面板矩形（物理像素）：乐观打开时的定位来源
    private IntPtr _lastPanelRectHwnd;
    private volatile bool _optimisticPending;    // 已按按下时点乐观打开，等待 QQ 面板真正出现
    private Rect _emojiBtnRect = Rect.Empty;     // 表情按钮绝对矩形（物理像素）：合成锚点/close-q 点按来源，每次验证命中随窗口位置刷新
    private Rect _emojiBtnRectRel = Rect.Empty;  // 表情按钮相对宿主窗口的偏移矩形：点击时按 GetWindowRect 重算，窗口拖动/重开不陈旧
    private IntPtr _emojiBtnHwnd;                // 表情按钮所在顶层窗口（与点击时刻 WindowFromPoint+GA_ROOT 同路解析，保证可对上）
    private int _emojiBtnPid;                    // 缓存矩形时刻的 QQ pid：QQ 重启换 pid 后陈旧句柄/矩形直接失效
    private Rect _editorRectRel = Rect.Empty;    // 聊天输入框相对宿主窗口的偏移矩形：焦点命中编辑框时缓存，发送链路找回焦点的精确点位
    private IntPtr _editorHwnd;                  // 输入框所在顶层窗口（诊断用；重算按调用方给的 qqHwnd）
    private int _editorPid;
    private Rect _tabRectRel = Rect.Empty;       // 面板分类标签（TAB）窗口相对矩形：按钮没获得过焦点时（TAB 路径开面板），close-q 从它推导按钮位置
    private IntPtr _tabRectHwnd;
    private string? _editorRectLogKey;           // 输入框矩形/宿主变化时才落日志（防每次点击编辑框刷屏）
    private int _editorRejects;                  // 空 cls 但无编辑区证据的焦点事件：限量落日志（诊断用）
    private long _lastPanelAppearedTicks;        // 最近一次面板 APPEARED 时刻：光灭关闭的编排保护窗（开面板 1.5s 内不判光灭）
    private static long _lastQqFocusTicks;       // QQ 内任意焦点事件时刻：发送预关闭后等"焦点编排静默"用
    private static long _editorFocusEchoTicks;   // 最近一次"编辑框系元素获得焦点"的时刻：发送链路找回焦点的正向验证信号
    private static long _legacyOptimisticCloseUntil; // 乐观关闭的焦点回声抑制窗：关闭点击/收面板编排的焦点回声，窗口内不得把状态翻回"开"
    private bool _closePending;                  // 仅钩子线程读写：面板开着时按下了按钮，等 mouseup 也落在按钮上（完整点击）再乐观关闭
    private static long _lastMouseDownTicks;     // 钩子记录的全局最近一次左键按下时刻：焦点兜底路径把焦点信号和真实点击对上号
    private static POINT _lastMouseDownPt;
    private static long _lastHookFireTicks;      // 钩子最近一次乐观开/关触发时刻：焦点路径据此识别"本次点击钩子已处理"，不重复发事件
    // 快捷面板「共存模式可见」镜像（QuickPanelWindow 在 UI 线程经 IsVisibleChanged 维护，钩子线程只读）：
    // 钩子判断开/关不能只看 _panelOpen——QQ 面板出现要等 mouse-up 后 UIA 扫描确认（几十~几百 ms），
    // 快速双击的第二下常落在确认之前，_panelOpen 仍为 false，会误走乐观打开分支把面板又"开"一遍；
    // 此时共存面板明明已经显示着，物理点击就是 toggle 关闭（2026-10-05 用户实测"双击后面板很久不收"）。
    internal static int CoexistPanelShowing; // 0/1，经 Volatile.Read/Write 跨线程访问
    private IntPtr _mouseHook;
    private readonly LowLevelMouseProc _mouseHookProc; // 必须持有委托强引用，否则 GC 回收后钩子回调访问已释放 thunk → 闪退
    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _hookAliveLogged;

    private (bool open, IntPtr hwnd, Rect rect) ScanQqWindows(HashSet<int> pids, bool missCacheable)
    {
        var myPid = Environment.ProcessId;

        // 快路径：上次的面板元素仍有效时只读单个元素 + 命中测试（毫秒级），不做全树遍历
        var cached = _cachedPanel;
        var cachedHwnd = _cachedPanelHwnd;
        if (cached != null && cachedHwnd != IntPtr.Zero && IsWindowVisible(cachedHwnd))
        {
            try
            {
                if (SafeClass(cached) == PanelClassName)
                {
                    var crect = cached.Current.BoundingRectangle;
                    if (IsPanelVisuallyRendered(cached, crect, myPid))
                        return (true, cachedHwnd, crect);
                    // 命中测试判负 = 面板已被透明隐藏（产品语义即关闭）：元素是滞留的陈旧引用，
                    // 丢弃它，让后续验证直接吃到全扫未命中的负缓存（零跨进程）
                    _cachedPanel = null;
                }
            }
            catch
            {
                _cachedPanel = null; // 元素已失效（QQ 重建/关闭），走全树扫描
            }
        }

        // 旧版 NTQQ 快路径（必须在全扫之前）：剪枝树下全树 FindFirst 会阻塞数十秒（实测 ~35s）
        // 且必然扑空。闩进旧版模式后完全跳过全扫：锚信号新鲜 = 面板开着；过期 = 关着
        // （时间确认在 CheckNow 里走 _openMisses 常规收起）。必须以 _legacyMode 闩死为前提——
        // 锚信号在剪枝树不可探测的新版上也会被焦点命中写入，无闩门会污染新版的 sticker-panel 精准检测。
        var nowTicksL = Environment.TickCount64;
        bool legacySignalFresh = !_legacyAnchorRect.IsEmpty && nowTicksL - _legacyTabShownTicks < _legacyOpenTtlMs;
        if (_legacyMode)
        {
            if (legacySignalFresh)
            {
                var hwndL = _legacyTabHwnd;
                if (hwndL == IntPtr.Zero)
                {
                    foreach (var visible in GetVisibleWindowsOf(pids)) { hwndL = visible; break; }
                }
                if (hwndL != IntPtr.Zero)
                {
                    if (!_legacyFallbackLogged)
                    {
                        _legacyFallbackLogged = true;
                        Log($"legacy panel fallback engaged (anchor={_legacyAnchorRect}, hwnd={hwndL})");
                    }
                    _cachedPanel = null;
                    return (true, hwndL, _legacyAnchorRect);
                }
            }
            return (false, IntPtr.Zero, Rect.Empty); // 闩锁后无论锚新鲜与否都不做全扫（全扫在此树上=阻塞数十秒的毒药）
        }

        // 负缓存：最近一次全扫就没找到面板、且本轮无强制信号（点击重试/关闭确认）→ 直接报未开。
        // 只吃"全扫过且没找到"的结果；上面缓存元素刚失效不算（那本身就是新信息）。
        var nowTicks = Environment.TickCount64;
        if (missCacheable && nowTicks - _lastFullScanMissTicks < FullScanMissTtlMs)
            return (false, IntPtr.Zero, Rect.Empty);

        var scanStart = Environment.TickCount64;
        foreach (var hwnd in GetVisibleWindowsOf(pids))
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var cond = new PropertyCondition(AutomationElement.ClassNameProperty, PanelClassName);
                var panel = root.FindFirst(TreeScope.Descendants, cond);
                if (panel == null) continue;

                // 关键：QQ 关闭面板时可能仅用透明度隐藏（元素滞留在树里，矩形/IsOffscreen 均不变），
                // 元素存在 ≠ 面板可见。必须用命中测试确认：面板矩形中心 FromPoint 后回溯祖先链，
                // 能命中 sticker-panel 才算可见；命中的是下层元素（消息区等）说明面板已被透明隐藏。
                var rect = panel.Current.BoundingRectangle;
                if (!IsPanelVisuallyRendered(panel, rect, myPid)) continue;

                _cachedPanel = panel;
                _cachedPanelHwnd = hwnd;
                return (true, hwnd, rect);
            }
            catch { /* 单个窗口失败（树未激活/元素失效）不影响整体 */ }
        }
        _lastFullScanMissTicks = Environment.TickCount64; // 完整扫过且未命中，才开始计时
        // 剪枝树特征：全扫未命中且耗时超过数秒（实测旧版 ~35s，新版 18–66ms）→ 闩进旧版模式，
        // 此后本函数走顶部的旧版快路径，不再做全扫（新版正常环境永不触发）。
        if (Environment.TickCount64 - scanStart > 3000)
        {
            _legacyMode = true;
            LatchLegacyScanMode();
            Log($"legacy scan mode latched (full scan took {Environment.TickCount64 - scanStart} ms, pruned a11y tree)");
        }
        return (false, IntPtr.Zero, Rect.Empty);
    }

    private bool IsPanelVisuallyRendered(AutomationElement panel, Rect rect, int myPid)
    {
        try
        {
            if (rect.Width < 10 || rect.Height < 10) return false;
            var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            var hit = AutomationElement.FromPoint(center);
            var cur = hit;
            for (int i = 0; i < 12 && cur != null; i++)
            {
                if (SafeClass(cur) == PanelClassName) return true;
                try
                {
                    if (cur.Current.ProcessId == myPid) return true; // 打到我们自己的窗口：无法判定，保守视为打开
                }
                catch { }
                try { cur = TreeWalker.RawViewWalker.GetParent(cur); } catch { return true; }
            }
            return false; // 命中的是面板之下的元素 → 面板已被透明隐藏
        }
        catch
        {
            return true; // 命中测试失败时保守视为打开，避免闪烁
        }
    }

    private static HashSet<int> CollectQqPids() =>
        Process.GetProcessesByName("QQ").Select(p => p.Id).ToHashSet();

    private static IEnumerable<IntPtr> GetVisibleWindowsOf(HashSet<int> pids)
    {
        var result = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && IsWindowVisible(h))
            {
                result.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "asuka-watcher.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    /// <summary>OS 友好名 + 版本：NT 内核在 Win11 上仍报 10.0，须按 build 号（≥22000）区分。</summary>
    private static string OsTag()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} {v.Major}.{v.Minor}.{v.Build}";
    }

    // --- 名称/类名/矩形安全读取（Chromium 树里部分元素属性不可读） ---

    private static string SafeName(AutomationElement e) { try { var n = e.Current.Name; return n ?? ""; } catch { return ""; } }
    private static string SafeClass(AutomationElement e) { try { var c = e.Current.ClassName; return c ?? ""; } catch { return ""; } }

    private static bool IsLeftButtonDown()
    {
        try { return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
        catch { return false; }
    }

    // 从焦点元素向上找顶层 Window 元素，取其 hwnd（Chromium 的子元素 NativeWindowHandle 不可信）
    private static IntPtr GetTopWindowHwnd(AutomationElement start)
    {
        // 实测 NTQQ 从按钮到根 Window 约 20+ 级（中间经过 Chrome_RenderWidgetHostHWND/多层 View），深度须给足
        try
        {
            var cur = start;
            for (int i = 0; i < 30; i++)
            {
                if (cur.Current.ControlType == System.Windows.Automation.ControlType.Window)
                {
                    var h = cur.Current.NativeWindowHandle;
                    return h != 0 ? new IntPtr(h) : IntPtr.Zero;
                }
                var parent = TreeWalker.RawViewWalker.GetParent(cur);
                if (parent == null) return IntPtr.Zero;
                cur = parent;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    // --- 表情按钮点击校验（钩子误触发防线，全部本地 user32 调用，LL 钩子线程安全） ---

    /// <summary>把按钮的绝对矩形换算成宿主窗口相对偏移（物理像素），缓存时刻调用一次。</summary>
    private static Rect ToWindowRelative(IntPtr hwnd, Rect abs)
    {
        try
        {
            if (!GetWindowRect(hwnd, out var wr)) return Rect.Empty;
            return new Rect(abs.Left - wr.Left, abs.Top - wr.Top, abs.Width, abs.Height);
        }
        catch { return Rect.Empty; }
    }

    /// <summary>按宿主窗口当前位置重算表情按钮的绝对矩形：缓存的是窗口相对偏移，窗口拖动/
    /// 最小化恢复/重开后偏移不变而绝对坐标变，点击时重算即可精确命中，零 UIA 跨进程成本。
    /// 句柄失效（QQ 重启/窗口关闭）时 GetWindowRect 失败，返回 false。</summary>
    private bool TryResolveEmojiButtonRect(out Rect rect)
    {
        rect = Rect.Empty;
        var hwnd = _emojiBtnHwnd;
        var rel = _emojiBtnRectRel;
        if (hwnd == IntPtr.Zero || rel.IsEmpty || rel.Width <= 0) return false;
        if (!GetWindowRect(hwnd, out var wr)) return false;
        rect = new Rect(wr.Left + rel.Left, wr.Top + rel.Top, rel.Width, rel.Height);
        return true;
    }

    /// <summary>点击点是否真的落在（当前位置重算后的）表情按钮上：矩形包含 + 命中点的根窗口
    /// 必须是缓存矩形时的宿主 + 进程一致。陈旧矩形、其他 QQ 窗口、其他应用占据同屏位置、
    /// QQ 已退出都在此拒绝——否则钩子按一个全局坐标矩形对任意窗口的点击弹面板
    /// （实测 QQ 未运行也弹）。新窗口首次点击时矩形尚未缓存，校验返回 false，
    /// 由焦点兜底路径在矩形缓存完成后接住（见 FocusPathClickIsOnButton）。</summary>
    private bool IsEmojiButtonClick(POINT pt, out Rect resolvedRect)
    {
        resolvedRect = Rect.Empty;
        if (!TryResolveEmojiButtonRect(out var r)) return false;
        if (pt.X < r.Left || pt.X > r.Right || pt.Y < r.Top || pt.Y > r.Bottom) return false;
        if (RootWindowFromPoint(new POINT { X = pt.X, Y = pt.Y }) != _emojiBtnHwnd) return false;
        GetWindowThreadProcessId(_emojiBtnHwnd, out var wpid);
        if (_emojiBtnPid != 0 && wpid != (uint)_emojiBtnPid) return false;
        resolvedRect = r;
        return true;
    }

    private static IntPtr RootWindowFromPoint(POINT pt)
    {
        var hit = WindowFromPoint(pt);
        return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
    }

    /// <summary>焦点兜底路径的点击校验：优先用钩子记录的最近 mousedown 落点（≤250ms），
    /// 钩子未装（无记录）时退化为当前光标位置。落点必须通过 IsEmojiButtonClick 的三重校验。</summary>
    private bool FocusPathClickIsOnButton()
    {
        var downTicks = Volatile.Read(ref _lastMouseDownTicks);
        if (downTicks > 0 && Environment.TickCount64 - downTicks <= 250)
            return IsEmojiButtonClick(_lastMouseDownPt, out _);
        if (downTicks == 0 && GetCursorPos(out var cp)) // 钩子未安装：没有 mousedown 记录可用
            return IsEmojiButtonClick(cp, out _);
        return false;
    }

    /// <summary>钩子是否已为最近一次 mousedown 触发过乐观开/关（钩子线程写、任意线程读）。</summary>
    private static bool HookCoveredLastMouseDown() =>
        Volatile.Read(ref _lastMouseDownTicks) > 0 &&
        Volatile.Read(ref _lastHookFireTicks) >= Volatile.Read(ref _lastMouseDownTicks);

    /// <summary>乐观关闭：立即收起快捷面板并置状态为关，随后尾迹验证兜底——若 QQ 面板其实
    /// 还开着，CheckNow 重新报 APPEARED，OpenForCoexist 的"已可见重定位/淡出中恢复"接住。</summary>
    private void OptimisticClosePanel(string reason)
    {
        _panelOpen = false;
        _openMisses = 0;
        if (_legacyMode)
        {
            // 旧版的"开"状态就是锚本身：乐观关闭必须连锚一起清，否则下一次验证又把面板报回来
            _legacyAnchorRect = Rect.Empty;
        }
        // 回声抑制窗：关闭动作自己的焦点回声（mousedown 送焦点回按钮、QQ 收面板编排回摆）
        // 会在随后 ~1s 内到达，若照常刷新锚会立刻把状态翻回"开"→ 关了又弹（实测闪烁形态）。
        Volatile.Write(ref _legacyOptimisticCloseUntil, Environment.TickCount64 + 1200);
        Log($"optimistic panel close ({reason})");
        Task.Run(() =>
        {
            try { PanelDisappeared?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log("optimistic close dispatch failed: " + ex.Message); }
        });
        ArmTailVerify();
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_hookAliveLogged)
        {
            _hookAliveLogged = true;
            Log("mouse hook alive (first event received)");
        }

        if (nCode >= 0 && Running)
        {
            try
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var msg = wParam.ToInt32();

                if (msg == WM_LBUTTONDOWN)
                {
                    // 全局记录最近一次左键按下：焦点兜底路径靠它把"焦点落在按钮上"和
                    // "这次焦点变化确实是点击按钮造成的"对上号
                    _lastMouseDownPt = s.pt;
                    Volatile.Write(ref _lastMouseDownTicks, Environment.TickCount64);

                    if (IsEmojiButtonClick(s.pt, out var rectNow))
                    {
                        _emojiBtnRect = rectNow; // 跟随窗口当前位置刷新：合成锚点/close-q 点按不再陈旧
                        if (_legacyMode)
                        {
                            if (_panelOpen || Volatile.Read(ref CoexistPanelShowing) == 1)
                            {
                                // 旧版语义：面板开着时点按钮 = toggle 关闭。锚 TTL 砍到关闭证据级并保持尾迹，
                                // 让"开着"状态尽快解除（否则无轮询/无事件时过期永不被发现，状态卡死，
                                // 后续点击全部被乐观路径的 !_panelOpen 挡住——实测正是"再点没反应"的成因）。
                                // mouseup 也落在按钮内（完整点击）→ 乐观关闭立即收起快捷面板（不等 TTL）；
                                // 回声抑制窗防止这次 mousedown 的焦点事件把状态翻回"开"。
                                // 判据并上 CoexistPanelShowing：快速双击的第二下常落在 _panelOpen 的 UIA
                                // 确认之前，此时共存面板已显示，物理点击就是 toggle，再走乐观打开会
                                // 把面板重新锚定、随后又无人关闭（2026-10-05 "双击后面板很久不收"）。
                                _legacyOpenTtlMs = LegacyCloseEvidenceTtlMs;
                                _legacyTabShownTicks = Environment.TickCount64;
                                _legacyAnchorRect = new Rect(s.pt.X - 16, s.pt.Y - 16, 32, 32);
                                _closePending = true;
                                Volatile.Write(ref _legacyOptimisticCloseUntil, Environment.TickCount64 + 1000);
                                BumpUserAction();
                                ArmTailVerify();
                                Log("mouse hook: emoji button down while panel open (legacy close pending)");
                            }
                            else
                            {
                                // 按下表情按钮的物理瞬间：立即乐观打开（QQ 面板要等 mouse-up 才出现，我们更快）。
                                // 物理点击本身就是旧版模式的"打开"证据：粘滞焦点下重开没有焦点事件可依赖，
                                // 必须在此刷新锚（长 TTL，见 LegacyOpenEvidenceTtlMs 注释），否则乐观打开
                                // 会被随后的全扫报关 abort 掉。
                                var hwnd = _emojiBtnHwnd;
                                _legacyOpenTtlMs = LegacyOpenEvidenceTtlMs;
                                _legacyTabShownTicks = Environment.TickCount64;
                                _legacyTabHwnd = hwnd;
                                _legacyAnchorRect = new Rect(s.pt.X - 16, s.pt.Y - 16, 32, 32);
                                var crect = _lastPanelRectHwnd == hwnd ? _lastPanelRect : Rect.Empty;
                                _optimisticPending = true;
                                ArmTailVerify();
                                BumpUserAction();
                                Volatile.Write(ref _lastHookFireTicks, Environment.TickCount64);
                                Log($"mouse hook: emoji button clicked (optimistic, resolvedRect={rectNow}, cachedRect={crect})");
                                Task.Run(() =>
                                {
                                    try { EmojiButtonClicked?.Invoke(this, new QqPanelEventArgs(hwnd, crect, rectNow)); }
                                    catch (Exception ex) { Log("optimistic open failed: " + ex.Message); }
                                });
                                _ = VerifyWithOpenRetriesAsync();
                            }
                        }
                        else if (_panelOpen || Volatile.Read(ref CoexistPanelShowing) == 1)
                        {
                            // 新版 toggle 关闭：QQ 在 mouse-up 才动作，先记 pending 等 mouseup 也落在
                            // 按钮上（完整点击）再乐观关闭——按住拖走（aborted click）QQ 不 toggle。
                            // 按下即武装尾迹：拖走导致的真实关闭、或乐观关闭误判，都在 ~100ms 内被校正。
                            // 判据并上 CoexistPanelShowing：快速双击的第二下常落在 _panelOpen 的 UIA
                            // 确认之前，共存面板已显示时物理点击就是 toggle（同旧版分支注释）。
                            _closePending = true;
                            Volatile.Write(ref _lastHookFireTicks, Environment.TickCount64);
                            BumpUserAction();
                            ArmTailVerify();
                            Log("mouse hook: emoji button down while panel open (close pending mouseup)");
                        }
                        else
                        {
                            // 按下表情按钮的物理瞬间：立即乐观打开（QQ 面板要等 mouse-up 才出现，我们更快）。
                            var hwnd = _emojiBtnHwnd;
                            var crect = _lastPanelRectHwnd == hwnd ? _lastPanelRect : Rect.Empty;
                            _optimisticPending = true;
                            ArmTailVerify();
                            BumpUserAction();
                            Volatile.Write(ref _lastHookFireTicks, Environment.TickCount64);
                            Log($"mouse hook: emoji button clicked (optimistic, resolvedRect={rectNow}, cachedRect={crect})");
                            Task.Run(() =>
                            {
                                try { EmojiButtonClicked?.Invoke(this, new QqPanelEventArgs(hwnd, crect, rectNow)); }
                                catch (Exception ex) { Log("optimistic open failed: " + ex.Message); }
                            });
                            _ = VerifyWithOpenRetriesAsync();
                        }
                    }
                }
                else if (msg == WM_LBUTTONUP && _closePending)
                {
                    _closePending = false; // 本字段仅钩子线程读写
                    if (IsEmojiButtonClick(s.pt, out _)) // up 也落在按钮上 = 完整点击，QQ 才会 toggle
                    {
                        Volatile.Write(ref _lastHookFireTicks, Environment.TickCount64);
                        OptimisticClosePanel("emoji button full click (toggle close)");
                    }
                }
            }
            catch { }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO gui);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
