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
    /// 直接粘贴会落空。因此先把 QQ 窗口拉回前台，再用 UIA 把焦点设回聊天输入框（ProseMirror 编辑器），
    /// 然后粘贴。QQ 表情面板全程保持打开。
    /// </summary>
    public async Task CoexistPasteAsync(IntPtr qqHwnd, string imagePath, bool restoreClipboard = true)
    {
        ClipboardCapture.Suppress = true;
        try
        {
            IDataObject? backup = restoreClipboard ? CaptureClipboardSnapshot() : null;

            // 1. QQ 拉回前台（UIA SetFocus 对后台窗口会静默失效，必须先前台化）
            SetForegroundWindow(qqHwnd);
            await Task.Delay(150);

            // 2. UIA SetFocus 把焦点还给聊天输入框；定位串失配时退化为普通粘贴（焦点若仍在输入框则仍可成功）
            try
            {
                var root = AutomationElement.FromHandle(qqHwnd);
                AutomationElement? editor = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "ProseMirror ExEditor-qq-msg-editor is-empty"));
                if (editor == null)
                {
                    // 输入框非空时 class 会去掉 is-empty 后缀，按包含匹配兜底
                    var all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    foreach (AutomationElement e in all)
                    {
                        string? cls = null;
                        try { cls = e.Current.ClassName; } catch { }
                        if (cls != null && cls.Contains("ExEditor-qq-msg-editor")) { editor = e; break; }
                    }
                }
                editor?.SetFocus();
                await Task.Delay(250);
            }
            catch { /* 焦点修复失败不阻断：若焦点本就在输入框，粘贴仍会成功 */ }

            if (!await TryCopyFileToClipboardAsync(imagePath)) return;

            SimulateCtrlV();
            await RestoreClipboardAfterDelayAsync(backup);
        }
        finally { ClipboardCapture.Suppress = false; }
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
}
