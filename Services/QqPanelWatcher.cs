using System.Diagnostics;
using System.IO;
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
/// - 打开：全局焦点变化事件——用户点击表情按钮的瞬间焦点落在按钮上，立即检测联动；
/// - 关闭：QQ 窗口的 UIA 结构变化事件（面板增删触发 ChildAdded/ChildRemoved，节流后验证），
///   以及焦点移回输入框等焦点变化（节流验证）。
/// 轮询保底（可选设置项，默认关）：事件在个别 QQ 版本上失灵时的兜底，常开会持续查询 QQ。
/// 定位串随 QQ 版本更新可能失配：连续异常自动降级并提示，失败模式良性（热键不受影响）。
/// 纯只读 UIA，不注入不挂钩。
/// </summary>
public class QqPanelWatcher : IDisposable
{
    private const int VerifyMinIntervalMs = 150; // 事件合并窗口：只限制风暴中的重复验证，不延迟首次验证
    private const int CloseConfirmDelayMs = 150; // 关闭确认的延迟复核间隔（误关可自愈：QQ 面板还开着则下一事件重新弹出）
    private const int TailVerifyIntervalMs = 200; // 按钮点击后的补验尾迹间隔
    private const int TailWindowMs = 2500;        // 补验尾迹时长（每次按钮点击重新计时）
    private const int FastPollMs = 125;          // 轮询保底开启且共存期间：快速感知关闭
    private const int IdlePollMs = 1000;         // 轮询保底开启且常态：低频兜底
    private const int DegradedPollIntervalMs = 5000;
    private const int DegradeThreshold = 20;

    private const string EmojiButtonName = "表情";
    private const string EmojiButtonClass = "icon-item";
    private const string PanelClassName = "sticker-panel";
    private const string EditorClassKey = "ExEditor-qq-msg-editor";

    /// <summary>
    /// 表情按钮的宽松匹配：NTQQ 各版本的类名/文案会单独漂移（旧版如 9.9.19 可能改类名或加修饰），
    /// 名称精确命中即认（聊天窗口里叫「表情」的元素就是表情按钮；误判代价只是面板贴错位置，可自愈）。
    /// </summary>
    private static bool LooksLikeEmojiButton(string name, string cls) =>
        name.Trim() == EmojiButtonName;

    private readonly Action<string> _status;
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, AutomationElement> _subscribed = new();

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
                var rect = w._emojiBtnRect;
                if (rect.Width <= 0 || rect.Height <= 0) { Log("close-q: no cached button rect"); return; }
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
                    return;
                }
                if (btn.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern)
                    is System.Windows.Automation.InvokePattern ip)
                {
                    ip.Invoke();
                    Log("close-q: emoji button invoked (panel should close)");
                }
            }
            catch (Exception ex) { Log("close-q failed: " + ex.Message); }
        });
    }

    public void Start()
    {
        if (Running) return;
        _qqPids = CollectQqPids();
        Running = true;
        Log("watcher started");

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
    }

    public void Dispose()
    {
        if (!Running) return;
        Running = false;
        _disposed = true;
        if (_active == this) _active = null;
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
            var el = AutomationElement.FocusedElement;
            if (el == null) return;
            int pid = el.Current.ProcessId;
            RefreshPidsIfNeeded(pid);

            if (!_qqPids.Contains(pid))
            {
                // 面板开着时焦点离开 QQ（点了桌面/其他应用）：QQ 面板大概率已被关掉，验证一次
                if (_panelOpen) ScheduleThrottledVerify();
                return;
            }

            // 懒订阅：正在使用的 QQ 窗口补订结构变化事件（新开聊天窗口也由此覆盖）
            try
            {
                var hwnd = (IntPtr)el.Current.NativeWindowHandle;
                if (hwnd != IntPtr.Zero) EnsureSubscribed(hwnd);
            }
            catch { }

            string name = SafeName(el);
            string cls = SafeClass(el);
            if (LooksLikeEmojiButton(name, cls))
            {
                Log($"focus hit emoji button (name={name}, class={cls})");

                // 缓存按钮矩形与宿主窗口：低级鼠标钩子的命中目标 + 乐观打开的粘贴目标
                try
                {
                    var br = el.Current.BoundingRectangle;
                    var topHwnd = GetTopWindowHwnd(el);
                    if (topHwnd == IntPtr.Zero) topHwnd = _lastPanelRectHwnd; // 走查失败时用面板检测已确认的 QQ 顶层窗口兜底
                    if (br.Width > 0 && topHwnd != IntPtr.Zero)
                    {
                        _emojiBtnRect = br;
                        _emojiBtnHwnd = topHwnd;
                        Log($"button rect cached {br} hwnd={topHwnd}");
                    }
                }
                catch { }

                // 焦点事件是异步投递的，到达时鼠标多半已松开——乐观打开主要靠鼠标钩子，
                // 这里仅作左键仍按住时的兜底（例如事件恰好即时送达的场景）。
                if (IsLeftButtonDown() && Running)
                {
                    var hostHwnd = GetTopWindowHwnd(el);
                    if (hostHwnd != IntPtr.Zero)
                    {
                        var cachedRect = hostHwnd == _lastPanelRectHwnd ? _lastPanelRect : Rect.Empty;
                        _optimisticPending = true;
                        ArmTailVerify();
                        BumpUserAction();
                        Log($"emoji button clicked via focus event (optimistic, cachedRect={cachedRect})");
                        EmojiButtonClicked?.Invoke(this, new QqPanelEventArgs(hostHwnd, cachedRect, _emojiBtnRect));
                    }
                }

                _ = VerifyWithOpenRetriesAsync();
            }
            else
            {
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
    private Rect _emojiBtnRect = Rect.Empty;     // 表情按钮矩形（鼠标钩子的命中目标）
    private IntPtr _emojiBtnHwnd;
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

        // 负缓存：最近一次全扫就没找到面板、且本轮无强制信号（点击重试/关闭确认）→ 直接报未开。
        // 只吃"全扫过且没找到"的结果；上面缓存元素刚失效不算（那本身就是新信息）。
        var nowTicks = Environment.TickCount64;
        if (missCacheable && nowTicks - _lastFullScanMissTicks < FullScanMissTtlMs)
            return (false, IntPtr.Zero, Rect.Empty);

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

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_hookAliveLogged)
        {
            _hookAliveLogged = true;
            Log("mouse hook alive (first event received)");
        }

        if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN && !_panelOpen && Running)
        {
            try
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var r = _emojiBtnRect;
                if (r.Width > 0 &&
                    s.pt.X >= r.Left && s.pt.X <= r.Right &&
                    s.pt.Y >= r.Top && s.pt.Y <= r.Bottom)
                {
                    // 按下表情按钮的物理瞬间：立即乐观打开（QQ 面板要等 mouse-up 才出现，我们更快）
                    var hwnd = _emojiBtnHwnd;
                    var crect = _lastPanelRectHwnd == hwnd ? _lastPanelRect : Rect.Empty;
                    _optimisticPending = true;
                    ArmTailVerify();
                    BumpUserAction();
                    Log($"mouse hook: emoji button clicked (optimistic, cachedRect={crect})");
                    Task.Run(() =>
                    {
                        try { EmojiButtonClicked?.Invoke(this, new QqPanelEventArgs(hwnd, crect, _emojiBtnRect)); }
                        catch (Exception ex) { Log("optimistic open failed: " + ex.Message); }
                    });
                    _ = VerifyWithOpenRetriesAsync();
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
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int VK_LBUTTON = 0x01;

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

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
