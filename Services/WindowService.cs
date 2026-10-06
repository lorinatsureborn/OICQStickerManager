using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;

namespace OICQStickerManager.Services;

public class WindowService
{
    private readonly SendCoordinator _sender = new(new NativeSendEnvironment());
    // --- Windows 底层 API (P/Invoke) ---

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // 系统双击时限（毫秒）：两次按下间隔小于该值时，系统视为同一次双击操作
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public double DoubleClickTimeMs => GetDoubleClickTime();

    /// <summary>
    /// 主窗口发送：最小化自身让焦点回到上一个窗口，再模拟粘贴。
    /// 历史路径，焦点恢复依赖系统行为；快捷面板上线后保留兼容。
    /// </summary>
    public Task<SendResult> SendImageToActiveWindowAsync(string imagePath, bool restoreClipboard = true)
    {
        var main = Application.Current.MainWindow;
        var mainHwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
        var target = CapturePreviousTarget(mainHwnd);
        var foreground = GetForegroundWindow();
        return _sender.SendAsync(target, imagePath, restoreClipboard, async () =>
        {
            if (GetForegroundWindow() != foreground && !FocusRootIsQq(target.Hwnd)) return false;
            main.WindowState = WindowState.Minimized;
            await Task.Delay(300);
            return true;
        });
    }

    /// <summary>
    /// 快捷面板发送（热键模式）：面板是非激活窗口，焦点从未离开目标应用，
    /// 直接把粘贴动作发给当前前台窗口即可，落点确定、无需等待切换。
    /// </summary>
    public Task<SendResult> QuickPasteToForegroundAsync(string imagePath, bool restoreClipboard = true)
        => _sender.SendAsync(CaptureTarget(GetForegroundWindow()), imagePath, restoreClipboard, () => Task.FromResult(true));

    /// <summary>
    /// 快捷面板发送（QQ 共存模式）：QQ 原生表情面板打开时，聊天输入框会失焦（焦点停在表情按钮/面板搜索框上），
    /// 直接粘贴会落空。因此确认目标 QQ 仍在前台，再把焦点还给聊天输入框（ProseMirror 编辑器），然后粘贴。
    /// 旧版 QQ 先关闭原生面板，再等待焦点编排平息；新版通过编辑区点击收起面板。
    /// IME 兼容（搜狗实测 2026-10-02 探针 D:\AsukaProbe）：焦点恢复必须以「放置插入符的点击」完成，绝不能用
    /// UIA SetFocus——UIA 焦点只设置 activeElement 不放置插入符，搜狗对编辑区做布局查询（GetTextExt）
    /// 拿不到光标矩形，候选框回退锚定屏幕边缘，且该毒化不可自愈（点击/换焦点/重启搜狗都救不回，
    /// 只能重启 QQ），用户发送一次表情后打字就全程跑屏边。
    /// 点击形态：首选向渲染子窗投递鼠标消息（用户无感、光标不动），未生效才退回 SendInput 真实点击
    /// （光标会动一下，仅此兜底）。点击顺带让 QQ 原生面板光灭（light-dismiss），无需 invoke 表情按钮
    /// （toggle 时序有重开风险）。旧版必须确认到新的编辑焦点才允许粘贴。
    /// </summary>
    public async Task<SendResult> CoexistPasteAsync(IntPtr qqHwnd, string imagePath, bool restoreClipboard = true)
    {
        var target = CaptureTarget(qqHwnd);
        long closeGeneration = QqPanelWatcher.UserActionGen;
        long interaction = QqCoexistLifetime.LastInteractionTicks;
        long editorVerifiedAt = 0;
        bool CanContinue() => FocusRootIsQq(qqHwnd)
            && QqPanelWatcher.UserActionGen == closeGeneration
            && QqCoexistLifetime.LastInteractionTicks == interaction;
        var result = await _sender.SendAsync(target, imagePath, restoreClipboard, async () =>
        {
            if (!CanContinue()) return false;

            // 1.5 旧版先收起 QQ 原生面板再点编辑框：这代 QQ 的面板光灭回收会把焦点从编辑框
            // 抢回表情按钮（2026-10-04 实测：编辑框点击后 35ms 焦点即被抢回，Ctrl+V 落空）——
            // 先关面板就没有光灭，编辑框焦点稳。新版 QQ 光灭不抢焦点，保持原时序。
            if (QqPanelWatcher.IsLegacyWindow(qqHwnd))
            {
                bool invoked = await Task.Run(() => QqPanelWatcher.TryCloseQqPanelNow(qqHwnd));
                QqPanelWatcher.Log(invoked ? "coexist: qq panel closed before editor focus" : "coexist: qq panel not open/closable, continue");
                // 等 QQ 收面板的焦点编排平息：QQ 会在 ~0.8s 内连续回摆焦点（按钮→标签→编辑框→
                // 按钮，实测 18:23），过早点编辑框会被随后的回摆覆盖——表情不落框的根因。
                // 以"QQ 焦点事件静默 300ms"为准，上限 1.5s，之后留 120ms 余量。
                var waitStart = Environment.TickCount64;
                await WaitForFocusSettlementAsync(() => Environment.TickCount64,
                    () => QqPanelWatcher.LastQqFocusTicks, milliseconds => Task.Delay(milliseconds));
                QqPanelWatcher.Log($"coexist: focus choreography settled in {Environment.TickCount64 - waitStart} ms");
            }
            if (!CanContinue()) return false;

            // 2. 焦点还给聊天输入框：只允许真实鼠标点击（IME 安全）。定位串失配时退化为窗口相对启发点，
            //    仍然是真实点击——绝不退回 UIA SetFocus（会毒化搜狗候选框锚定且不可自愈）
            long sendFocusStarted = Environment.TickCount64;
            bool clicked = await RestoreEditorFocusAsync(() => Task.Run(() => TryClickFocusEditor(qqHwnd, sendFocusStarted, CanContinue)),
                    CanContinue,
                    () => WaitForFocusSettlementAsync(() => Environment.TickCount64,
                        () => QqPanelWatcher.LastQqFocusTicks, milliseconds => Task.Delay(milliseconds)));
            QqPanelWatcher.Log(clicked ? "focus: editor clicked (IME-safe)" : "focus: click paths exhausted (gate on pre-paste focus check)");
            if (!clicked)
            {
                // legacy：编辑框焦点找回失败 = 插入符没回到输入框（粘贴必落空或错位）。
                // 宁可取消并提示，不静默粘到按钮/面板上（用户实测"焦点不回输入框，表情没上屏"）
                QqPanelWatcher.Log("send aborted: editor focus not restored (no caret)");
                return false;
            }
            editorVerifiedAt = Environment.TickCount64;
            await Task.Delay(100);

            // 2.5 粘贴前最后闸门：Ctrl+V 永远落在本时刻的键盘焦点窗口上。前面校验+等待的秒级空档里
            // 用户切走窗口的话，粘贴会打进别的应用（2026-10-04 实测：发送卡顿期间切窗，表情进了其他软件）。
            // 此时剪贴板尚未写入，取消发送零副作用——错发到别的应用比不发严重得多。
            if (!CanContinue())
            {
                QqPanelWatcher.Log("send aborted before paste: keyboard focus left QQ");
                return false;
            }

            return true;
        }, () => CanContinue() && EditorFocusStillVerified(QqPanelWatcher.IsLegacyWindow(qqHwnd),
            QqPanelWatcher.EditorFocusEchoTicks(qqHwnd), QqPanelWatcher.LastQqFocusTicks, editorVerifiedAt));
        return result;
    }

    internal static async Task WaitForFocusSettlementAsync(Func<long> now, Func<long> lastFocus, Func<int, Task> delay)
    {
        var start = now();
        while (now() - start < 1500
            && (now() - start < 600 || now() - Math.Max(start, lastFocus()) < 300))
            await delay(60);
        await delay(120);
    }

    internal static async Task<bool> RestoreEditorFocusAsync(Func<Task<bool>> click, Func<bool> canContinue, Func<Task> settle)
    {
        if (!canContinue()) return false;
        if (await click()) return canContinue();
        if (!canContinue()) return false;
        QqPanelWatcher.Log("focus: cold editor recovery, settle and retry once");
        await settle();
        return canContinue() && await click() && canContinue();
    }

    internal static bool TryGuardedFocusClick(Func<bool> canContinue, Func<bool> click)
        => canContinue() && click() && canContinue();

    internal static bool EditorFocusStillVerified(bool legacy, long editorEcho, long lastFocus, long verifiedAt)
        => editorEcho > 0 ? editorEcho >= lastFocus : !legacy && verifiedAt > 0 && lastFocus <= verifiedAt;

    /// <summary>
    /// 真实点击 QQ 聊天输入框，归还焦点并放置插入符（IME 锚定源），顺带让 QQ 原生面板光灭。
    /// 两级落点：① UIA 定位编辑器元素（树醒时），点矩形下部文本区——矩形上半是功能条图标行，
    /// 中心点会点到功能条上；② UIA 不可用（树休眠/定位串失配）时按窗口几何取启发点
    /// （水平 62%、距底 6%——NTQQ 输入区恒在聊天窗口右下）。两级都是真实点击。
    /// 每次点击前经 WindowFromPoint 遮挡检查；点后轮询焦点校验（Chromium 焦点传播是异步的，
    /// 冷启动可达数百 ms，读不到不等于没点上）——必须确认焦点属于聊天编辑框；
    /// 校验期间绝不退回 UIA SetFocus（毒化搜狗锚定，见类注释）。
    /// </summary>
    private static bool TryClickFocusEditor(IntPtr qqHwnd, long sendFocusStarted = 0, Func<bool>? canContinue = null)
    {
        bool Click(Func<bool> action) => TryGuardedFocusClick(canContinue ?? (() => FocusRootIsQq(qqHwnd)), action);
        // ⓪ legacy：优先用焦点事件缓存的输入框矩形（免费且精确——用户点过/编辑过输入框即有缓存）。
        //    剪枝树上 UIA 定位全树扫 ~0.8s、窗口几何启发点随布局可能落偏，都是"焦点不回输入框"的祸源。
        if (QqPanelWatcher.TryGetCachedEditorRect(qqHwnd, out var cachedEditor))
        {
            try
            {
                var editorPt = new NativePoint
                {
                    X = (int)(cachedEditor.Left + cachedEditor.Width / 2),
                    Y = (int)(cachedEditor.Bottom - Math.Min(25, cachedEditor.Height / 4))
                };
                if (IsPointOnQq(editorPt, qqHwnd) && Click(() => SendClickAndVerify(editorPt, qqHwnd, sendFocusStarted))) return true;
            }
            catch { /* 缓存矩形失效（窗口变动/元素重建），走下面的通用路径 */ }
        }

        // ① UIA 精确定位。旧版剪枝树跳过：全树扫描实测 ~0.8s（2026-10-04 发送卡顿主项之一），
        //    且剪枝树上元素矩形本身不可靠——直接走窗口几何启发点（输入区恒在窗口右下）。
        var editorRect = QqPanelWatcher.IsLegacyWindow(qqHwnd) ? Rect.Empty
            : QqUiaWorker.QueryAsync("editor", qqHwnd).GetAwaiter().GetResult()?.Rect?.ToRect() ?? Rect.Empty;
        if (!editorRect.IsEmpty)
        {
            try
            {
                var r = editorRect;
                if (r.Width >= 10 && r.Height >= 10)
                {
                    var pt = new NativePoint
                    {
                        X = (int)(r.Left + r.Width / 2),
                        Y = (int)(r.Bottom - Math.Min(25, r.Height / 4))
                    };
                    if (IsPointOnQq(pt, qqHwnd))
                    {
                        // 首选隐形点击：向渲染子窗直接投递鼠标消息（光标不动，用户无感），
                        // Chromium 输入管线照常放置插入符，搜狗锚定与真实点击一致；
                        // 消息点击未生效再退回真实点击兜底（光标会动一下，落点已校验）。
                        if (Click(() => MessageClickAndVerify(pt, qqHwnd, sendFocusStarted))) return true;
                        if (Click(() => SendClickAndVerify(pt, qqHwnd, sendFocusStarted))) return true;
                    }
                }
            }
            catch { /* 编辑器元素中途失效，走启发点 */ }
        }

        // ② 窗口相对启发点（NTQQ 输入区恒在窗口右下，且多显示器/缩放下 UIA 物理矩形本身可靠）。
        // 旧版剪枝树直接用真实点击（跳过隐形消息点击）：投递的鼠标消息在这代 Chromium 上不迁移
        // 键盘焦点、也不触发 QQ 面板光灭（2026-10-04 实测：焦点根窗口校验通过但焦点仍停在面板
        // 标签上，Ctrl+V 落空）——真实点击是 IME 安全的金标准，物理点击必然迁移焦点+放置插入符，
        // 顺带光灭 QQ 原生面板，代价只是光标瞬移一下（现有兜底本就接受）。
        if (GetWindowRect(qqHwnd, out var wr) && wr.R - wr.L > 200)
        {
            var pt = new NativePoint
            {
                X = wr.L + (int)((wr.R - wr.L) * 0.62),
                Y = wr.B - (int)((wr.B - wr.T) * 0.06)
            };
            if (IsPointOnQq(pt, qqHwnd))
            {
                if (QqPanelWatcher.IsLegacyWindow(qqHwnd))
                {
                    if (Click(() => SendClickAndVerify(pt, qqHwnd, sendFocusStarted))) return true;
                }
                else
                {
                    if (Click(() => MessageClickAndVerify(pt, qqHwnd, sendFocusStarted))) return true;
                    if (Click(() => SendClickAndVerify(pt, qqHwnd, sendFocusStarted))) return true;
                }
            }
        }

        QqPanelWatcher.Log("focus: both click paths rejected (occluded or focus left QQ)");
        return false;
    }

    /// <summary>
    /// 隐形点击：把鼠标消息直接投递给光标落点处的 QQ 子窗口（CEF 渲染窗），不移动真实光标。
    /// WM_MOUSEMOVE → WM_LBUTTONDOWN → WM_LBUTTONUP，Chromium 按正常输入处理，
    /// 渲染层放置插入符并通知 TSF——搜狗锚定行为与物理点击一致（2026-10-02 实测）。
    /// 仅当窗口已在前台/焦点已在 QQ 时使用（共存发送即如此）；发送后轮询焦点校验。
    /// </summary>
    private static bool MessageClickAndVerify(NativePoint pt, IntPtr qqHwnd, long sendFocusStarted = 0)
    {
        try
        {
            GetCursorPos(out var before);

            var clientPt = new NativePoint { X = pt.X, Y = pt.Y };
            ScreenToClient(qqHwnd, ref clientPt);
            IntPtr target = ChildWindowFromPointEx(qqHwnd, clientPt, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
            if (target == IntPtr.Zero) target = qqHwnd;
            if (target != qqHwnd)
            {
                // 客户区→客户区换算只能用 MapWindowPoints：ScreenToClient 把入参当屏幕坐标，
                // 拿 qqHwnd 客户区坐标"再转一次"会叠加 -qqHwnd客户区原点 的偏移——窗口不在屏幕
                // 原点时落点成千像素跑偏，投出的点击全部落空（2026-10-02 事故：发送全挂）
                MapWindowPoints(qqHwnd, target, ref clientPt, 1);
                QqPanelWatcher.Log($"focus: message-click target child class={WindowClassOf(target)} client=({clientPt.X},{clientPt.Y})");
            }
            IntPtr lp = MakeLParam(clientPt.X, clientPt.Y);

            long clickAt = Environment.TickCount64;
            PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(30);
            PostMessage(target, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), lp);
            Thread.Sleep(40);
            PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, lp);

            bool verified = PollFocusVerified(qqHwnd, out bool focusInQq, sendFocusStarted > 0 ? sendFocusStarted : clickAt);
            GetCursorPos(out var after);
            if (verified)
            {
                QqPanelWatcher.Log($"focus: message-click verified after {waitedLog}ms, cursor {before.X},{before.Y}→{after.X},{after.Y} (untouched)");
                return true;
            }
            // 不凭"键盘焦点在 QQ 窗口内"信任点击：焦点在表情按钮上也成立，此时盲粘贴粘空
            // （2026-10-02 事故）。未确认到编辑器就退回真实点击——真实点击本就是 IME 安全的
            // 金标准（毒化搜狗锚定的是 UIA SetFocus，不是真实点击），代价只是光标瞬移回弹。
            QqPanelWatcher.Log($"focus: message-click not verified after {waitedLog}ms (focusInQq={focusInQq}), falling back to real click");
            return false;
        }
        catch { return false; }
    }

    /// <summary>真实点击兜底：SendInput 物理点击（光标会移动），仅当消息点击未生效时使用。</summary>
    private static bool SendClickAndVerify(NativePoint pt, IntPtr qqHwnd, long sendFocusStarted = 0)
    {
        GetCursorPos(out var saved);
        long clickAt = Environment.TickCount64;
        if (!SendClickAt(pt)) return false;
        SetCursorPos(saved.X, saved.Y);
        bool verified = PollFocusVerified(qqHwnd, out bool focusInQq, sendFocusStarted > 0 ? sendFocusStarted : clickAt);
        // legacy：焦点根==QQ 不再单独作为通过条件——焦点停在表情按钮/面板标签上时同样成立，
        // 正是"表情没上屏"的形态。编辑框回声须晚于本轮恢复开始，重试时仍有效。
        return verified;
    }

    // 探测耗时也计入总截止时间，不能给每次跨进程查询重新分配完整超时。
    private static int waitedLog;

    private static bool PollFocusVerified(IntPtr qqHwnd, out bool focusInQq, long echoAfterTicks = 0)
    {
        focusInQq = false;
        long start = Environment.TickCount64;
        bool verified = PollEditorFocus(remaining =>
        {
            if (!FocusRootIsQq(qqHwnd)) return false;
            if (EditorEchoBelongsToSend(QqPanelWatcher.EditorFocusEchoTicks(qqHwnd), echoAfterTicks,
                QqPanelWatcher.LastQqFocusTicks)) return true;
            if (QqPanelWatcher.IsLegacyWindow(qqHwnd)) return false;
            try
            {
                return QqUiaWorker.QueryAsync("focus", timeout: TimeSpan.FromMilliseconds(remaining))
                    .GetAwaiter().GetResult()?.Value == true && FocusRootIsQq(qqHwnd);
            }
            catch { return false; }
        }, () => Environment.TickCount64, Thread.Sleep, 1000);
        waitedLog = (int)Math.Min(int.MaxValue, Environment.TickCount64 - start);
        focusInQq = FocusRootIsQq(qqHwnd);
        return verified;
    }

    internal static bool PollEditorFocus(Func<int, bool> probe, Func<long> now, Action<int> delay, int timeoutMs)
    {
        long start = now();
        while (now() - start < timeoutMs)
        {
            delay((int)Math.Min(100, timeoutMs - (now() - start)));
            int remaining = (int)Math.Max(0, timeoutMs - (now() - start));
            if (remaining > 0 && probe(remaining)) return true;
        }
        return false;
    }
    internal static bool EditorEchoBelongsToSend(long echo, long sendStarted, long lastFocus) =>
        echo > 0 && echo > sendStarted && echo >= lastFocus;

    /// <summary>键盘焦点所在的根窗口是否为 qqHwnd（GUITHREADINFO，本地调用零跨进程成本）。</summary>
    private static bool FocusRootIsQq(IntPtr qqHwnd)
    {
        try
        {
            var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            GetGUIThreadInfo(0, ref gui);
            return gui.hwndFocus != IntPtr.Zero && GetAncestor(gui.hwndFocus, GA_ROOT) == qqHwnd;
        }
        catch { return false; }
    }

    // 点击点是否落在 QQ 自己的顶层窗口上（被别的窗挡住就点不得）
    private static bool IsPointOnQq(NativePoint pt, IntPtr qqHwnd)
    {
        IntPtr hit = WindowFromPoint(pt);
        if (GetAncestor(hit, GA_ROOT) == qqHwnd) return true;

        // 挡路的是自己的快捷面板（共存布局可能盖住编辑区，发送本就要收起它）：立即隐藏后重试
        GetWindowThreadProcessId(hit, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is Views.QuickPanelWindow qp && qp.IsVisible) { qp.Hide(); break; }
            });
            hit = WindowFromPoint(pt);
            if (GetAncestor(hit, GA_ROOT) == qqHwnd)
            {
                QqPanelWatcher.Log("focus: hid own quick panel blocking the editor point");
                return true;
            }
        }
        QqPanelWatcher.Log($"focus: click point {pt.X},{pt.Y} occluded (root≠QQ)");
        return false;
    }

    private static bool SendClickAt(NativePoint pt) => NativeInput.TryClick(pt.X, pt.Y);

    // 写入 FileDropList（剪贴板是争用资源，被占用时重试几次）
    private static bool TryCopyFileToClipboard(string imagePath)
    {
        var data = new DataObject();
        var fileList = new StringCollection { imagePath };
        data.SetFileDropList(fileList);

        try
        {
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (COMException) { return false; }
    }

    // 备份当前剪贴板全部可读格式（best-effort：个别格式读不出就放弃该格式）
    private static IDataObject? CaptureClipboardSnapshot()
    {
        try
        {
            var src = Clipboard.GetDataObject();
            if (src == null) return null;

            var snapshot = new DataObject();
            foreach (var format in src.GetFormats())
            {
                try
                {
                    var value = src.GetData(format);
                    if (value != null) snapshot.SetData(format, value);
                }
                catch { /* 该格式无法物化，跳过 */ }
            }
            return snapshot;
        }
        catch { return null; }
    }

    private static SendTarget CaptureTarget(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return new(hwnd, pid);
    }

    private static SendTarget CapturePreviousTarget(IntPtr mainHwnd)
    {
        for (var hwnd = GetWindow(mainHwnd, 2); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, 2))
        {
            var target = CaptureTarget(hwnd);
            if (target.ProcessId != (uint)Environment.ProcessId && IsWindowVisible(hwnd)
                && (GetWindowLong(hwnd, -20) & (0x08000000 | 0x80)) == 0) return target;
        }
        return default;
    }

    private sealed class NativeSendEnvironment : ISendEnvironment
    {
        public bool IsValid(SendTarget target) => target.Hwnd != IntPtr.Zero && IsWindow(target.Hwnd)
            && CaptureTarget(target.Hwnd) == target && target.ProcessId != (uint)Environment.ProcessId;
        public bool IsFocused(SendTarget target) => IsValid(target) && FocusRootIsQq(target.Hwnd);
        public IDataObject? CaptureClipboard() => CaptureClipboardSnapshot();
        public uint ClipboardSequence => GetClipboardSequenceNumber();
        public bool TryWriteFile(string path) => TryCopyFileToClipboard(path);
        public void RestoreClipboard(IDataObject backup) => Clipboard.SetDataObject(backup, true);
        public bool TryPaste(SendTarget target) => IsFocused(target) && NativeInput.TryPaste();
        public Task DelayAsync(int milliseconds) => Task.Delay(milliseconds);
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);

    // --- 真实点击注入（共存发送的焦点恢复，见 CoexistPasteAsync 注释） ---

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint pt);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int L, T, R, B;
    }

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
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // --- 隐形点击（向渲染子窗投递鼠标消息，光标不动；见 MessageClickAndVerify） ---

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int MK_LBUTTON = 0x0001;
    private const uint CWP_SKIPINVISIBLE = 0x0001, CWP_SKIPTRANSPARENT = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint pt);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref NativePoint pt, uint cPoints);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int maxCount);

    private static string WindowClassOf(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            return GetClassName(hwnd, sb, 256) > 0 ? sb.ToString() : "?";
        }
        catch { return "?"; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint pt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
}
