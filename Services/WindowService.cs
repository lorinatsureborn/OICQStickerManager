using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace OICQStickerManager.Services;

public class WindowService
{
    // --- Windows 底层 API (P/Invoke) ---

    // 模拟键盘按键（虽然 SendKeys 更简单，但底层更稳定）
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // 系统双击时限（毫秒）：两次按下间隔小于该值时，系统视为同一次双击操作
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public double DoubleClickTimeMs => GetDoubleClickTime();

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// 主窗口发送：最小化自身让焦点回到上一个窗口，再模拟粘贴。
    /// 历史路径，焦点恢复依赖系统行为；快捷面板上线后保留兼容。
    /// </summary>
    public async Task SendImageToActiveWindowAsync(string imagePath, bool restoreClipboard = true)
    {
        ClipboardCapture.Suppress = true; // 发送+恢复全程静默剪贴板捕获（M2），防止捕获自己
        try
        {
            IDataObject? backup = restoreClipboard ? CaptureClipboardSnapshot() : null;

            if (!await TryCopyFileToClipboardAsync(imagePath)) return;

            // 最小化让焦点自动回到上一个窗口（QQ/微信）
            var currentWindow = Application.Current.MainWindow;
            currentWindow.WindowState = WindowState.Minimized;

            await Task.Delay(300); // 给系统一点切换窗口的时间

            SimulateCtrlV();
            await RestoreClipboardAfterDelayAsync(backup);
        }
        finally { ClipboardCapture.Suppress = false; }
    }

    /// <summary>
    /// 快捷面板发送（热键模式）：面板是非激活窗口，焦点从未离开目标应用，
    /// 直接把粘贴动作发给当前前台窗口即可，落点确定、无需等待切换。
    /// </summary>
    public async Task QuickPasteToForegroundAsync(string imagePath, bool restoreClipboard = true)
    {
        ClipboardCapture.Suppress = true;
        try
        {
            IDataObject? backup = restoreClipboard ? CaptureClipboardSnapshot() : null;

            if (!await TryCopyFileToClipboardAsync(imagePath)) return;

            SimulateCtrlV();
            await RestoreClipboardAfterDelayAsync(backup);
        }
        finally { ClipboardCapture.Suppress = false; }
    }

    /// <summary>
    /// 快捷面板发送（QQ 共存模式）：QQ 原生表情面板打开时，聊天输入框会失焦（焦点停在表情按钮/面板搜索框上），
    /// 直接粘贴会落空。因此先把 QQ 窗口拉回前台，再把焦点还给聊天输入框（ProseMirror 编辑器），然后粘贴。
    /// QQ 表情面板全程保持打开。
    /// IME 兼容（搜狗实测 2026-10-02 探针 D:\AsukaProbe）：焦点恢复必须以「放置插入符的点击」完成，绝不能用
    /// UIA SetFocus——UIA 焦点只设置 activeElement 不放置插入符，搜狗对编辑区做布局查询（GetTextExt）
    /// 拿不到光标矩形，候选框回退锚定屏幕边缘，且该毒化不可自愈（点击/换焦点/重启搜狗都救不回，
    /// 只能重启 QQ），用户发送一次表情后打字就全程跑屏边。
    /// 点击形态：首选向渲染子窗投递鼠标消息（用户无感、光标不动），未生效才退回 SendInput 真实点击
    /// （光标会动一下，仅此兜底）。点击顺带让 QQ 原生面板光灭（light-dismiss），无需 invoke 表情按钮
    /// （toggle 时序有重开风险）。两级落点失败时只粘贴不抢焦点并记日志。
    /// </summary>
    public async Task CoexistPasteAsync(IntPtr qqHwnd, string imagePath, bool restoreClipboard = true)
    {
        ClipboardCapture.Suppress = true;
        try
        {
            IDataObject? backup = restoreClipboard ? CaptureClipboardSnapshot() : null;

            // 1. QQ 拉回前台（对后台窗口操作会静默失效，必须先前台化）
            SetForegroundWindow(qqHwnd);
            await Task.Delay(150);

            // 2. 焦点还给聊天输入框：只允许真实鼠标点击（IME 安全）。定位串失配时退化为窗口相对启发点，
            //    仍然是真实点击——绝不退回 UIA SetFocus（会毒化搜狗候选框锚定且不可自愈）
            bool clicked = await Task.Run(() => TryClickFocusEditor(qqHwnd));
            QqPanelWatcher.Log(clicked ? "focus: editor clicked (IME-safe)" : "focus: click paths exhausted, pasting anyway");
            await Task.Delay(250);

            if (!await TryCopyFileToClipboardAsync(imagePath)) return;

            SimulateCtrlV();

            // 3. 尽力关 QQ 原生表情面板：点击编辑框通常已让它光灭，但 watcher 需 ~0.5s 确认关闭状态，
            //    立刻 invoke 会在 _panelOpen 还是 true 时把面板 toggle 重开——延迟到确认窗之后，还开着才点。
            //    generation 守卫：若这 800ms 内用户又按了表情按钮（新一轮操作），绝不能把人家刚打开的面板关掉
            long closeGen = QqPanelWatcher.UserActionGen;
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                QqPanelWatcher.TryCloseQqPanel(closeGen);
            });

            await RestoreClipboardAfterDelayAsync(backup);
        }
        finally { ClipboardCapture.Suppress = false; }
    }

    /// <summary>
    /// 真实点击 QQ 聊天输入框，归还焦点并放置插入符（IME 锚定源），顺带让 QQ 原生面板光灭。
    /// 两级落点：① UIA 定位编辑器元素（树醒时），点矩形下部文本区——矩形上半是功能条图标行，
    /// 中心点会点到功能条上；② UIA 不可用（树休眠/定位串失配）时按窗口几何取启发点
    /// （水平 62%、距底 6%——NTQQ 输入区恒在聊天窗口右下）。两级都是真实点击。
    /// 每次点击前经 WindowFromPoint 遮挡检查；点后轮询焦点校验（Chromium 焦点传播是异步的，
    /// 冷启动可达数百 ms，读不到不等于没点上）——键盘焦点若已回到 QQ 窗口即信任点击；
    /// 校验期间绝不退回 UIA SetFocus（毒化搜狗锚定，见类注释）。
    /// </summary>
    private static bool TryClickFocusEditor(IntPtr qqHwnd)
    {
        // ① UIA 精确定位
        var editor = FindEditorElement(qqHwnd);
        if (editor != null)
        {
            try
            {
                var r = editor.Current.BoundingRectangle;
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
                        if (MessageClickAndVerify(pt, qqHwnd)) return true;
                        if (SendClickAndVerify(pt, qqHwnd)) return true;
                    }
                }
            }
            catch { /* 编辑器元素中途失效，走启发点 */ }
        }

        // ② 窗口相对启发点（NTQQ 输入区恒在窗口右下，且多显示器/缩放下 UIA 物理矩形本身可靠）
        if (GetWindowRect(qqHwnd, out var wr) && wr.R - wr.L > 200)
        {
            var pt = new NativePoint
            {
                X = wr.L + (int)((wr.R - wr.L) * 0.62),
                Y = wr.B - (int)((wr.B - wr.T) * 0.06)
            };
            if (IsPointOnQq(pt, qqHwnd))
            {
                if (MessageClickAndVerify(pt, qqHwnd)) return true;
                if (SendClickAndVerify(pt, qqHwnd)) return true;
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
    private static bool MessageClickAndVerify(NativePoint pt, IntPtr qqHwnd)
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

            PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(30);
            PostMessage(target, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), lp);
            Thread.Sleep(40);
            PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, lp);

            bool verified = PollFocusVerified(qqHwnd, out bool focusInQq);
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
    private static bool SendClickAndVerify(NativePoint pt, IntPtr qqHwnd)
    {
        GetCursorPos(out var saved);
        SendClickAt(pt);
        SetCursorPos(saved.X, saved.Y);
        bool verified = PollFocusVerified(qqHwnd, out bool focusInQq);
        return verified || focusInQq;
    }

    // 轮询焦点校验：确认到编辑器即成功；没确认到但键盘焦点已在 QQ 窗口内也信任点击
    // （插入符在点击瞬间已由 Chromium 放置，UIA 读数只是滞后）；焦点彻底不在 QQ 才判失败
    private static int waitedLog;

    private static bool PollFocusVerified(IntPtr qqHwnd, out bool focusInQq)
    {
        focusInQq = false;
        for (int waited = 0; waited < 600; waited += 100)
        {
            Thread.Sleep(100);
            try
            {
                var f = AutomationElement.FocusedElement;
                if (f != null && (f.Current.ClassName ?? "").Contains("ExEditor"))
                {
                    waitedLog = waited + 100;
                    return true;
                }
            }
            catch { }
        }
        waitedLog = 600;
        var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        GetGUIThreadInfo(0, ref gui);
        focusInQq = gui.hwndFocus != IntPtr.Zero && GetAncestor(gui.hwndFocus, GA_ROOT) == qqHwnd;
        return false;
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
            foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
            {
                if (w is Views.QuickPanelWindow qp && qp.IsVisible) { qp.Hide(); break; }
            }
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

    /// <summary>定位聊天输入框元素。类名带动态后缀（聚焦时追加 ProseMirror-focused、新旧版词序还会变），
    /// 精确匹配命中即回（最廉价），失配再全树 contains 兜底（CacheRequest 一次带回类名，不逐元素跨进程读）。</summary>
    private static AutomationElement? FindEditorElement(IntPtr qqHwnd)
    {
        var root = AutomationElement.FromHandle(qqHwnd);
        var editor = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "ProseMirror ExEditor-qq-msg-editor is-empty"));
        if (editor != null) return editor;
        var cache = new CacheRequest();
        cache.Add(AutomationElement.ClassNameProperty);
        using (cache.Activate())
        {
            var all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                string? cls = null;
                try { cls = e.Cached.ClassName; } catch { }
                if (cls != null && cls.Contains("ExEditor-qq-msg-editor")) return e;
            }
        }
        return null;
    }

    private static void SendClickAt(NativePoint pt)
    {
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

    // 写入 FileDropList（剪贴板是争用资源，被占用时重试几次）
    private async Task<bool> TryCopyFileToClipboardAsync(string imagePath)
    {
        var data = new DataObject();
        var fileList = new StringCollection { imagePath };
        data.SetFileDropList(fileList);

        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException)
            {
                await Task.Delay(100);
            }
        }
        return false;
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

    // 等目标应用完成粘贴读取后再恢复剪贴板
    private async Task RestoreClipboardAfterDelayAsync(IDataObject? backup)
    {
        if (backup == null) return;
        await Task.Delay(1000);
        try { Clipboard.SetDataObject(backup, true); }
        catch { /* 恢复失败保持现状，不提示 */ }
    }

    private static void SimulateCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_V, 0, 0, 0);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    // --- 真实点击注入（共存发送的焦点恢复，见 CoexistPasteAsync 注释） ---

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

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
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

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
