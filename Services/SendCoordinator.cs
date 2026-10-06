using System.Windows;
using System.IO;

namespace OICQStickerManager.Services;

public readonly record struct SendTarget(IntPtr Hwnd, uint ProcessId);

public enum SendStatus { Sent, TargetChanged, TargetUnavailable, FocusUnavailable, ClipboardBusy, ClipboardChanged, InputRejected, Failed }

public readonly record struct SendResult(SendStatus Status)
{
    public bool Succeeded => Status == SendStatus.Sent;
    public string Message => Status switch
    {
        SendStatus.Sent => "表情已粘贴到目标窗口",
        SendStatus.TargetChanged => "发送已取消：目标窗口焦点已改变",
        SendStatus.TargetUnavailable => "发送失败：目标窗口或图片已不可用",
        SendStatus.FocusUnavailable => "发送失败：未能确认聊天输入框焦点",
        SendStatus.ClipboardBusy => "发送失败：剪贴板被占用，请稍后重试",
        SendStatus.ClipboardChanged => "发送已取消：剪贴板内容已改变",
        SendStatus.InputRejected => "发送失败：输入未被系统接受，请检查权限或修饰键",
        _ => "发送失败：请重试",
    };
}

public interface ISendEnvironment
{
    bool IsValid(SendTarget target);
    bool IsFocused(SendTarget target);
    IDataObject? CaptureClipboard();
    uint ClipboardSequence { get; }
    bool TryWriteFile(string path);
    void RestoreClipboard(IDataObject backup);
    bool TryPaste(SendTarget target);
    Task DelayAsync(int milliseconds);
}

public sealed class SendCoordinator(ISendEnvironment environment)
{
    private static readonly SemaphoreSlim SendGate = new(1, 1);

    public async Task<SendResult> SendAsync(SendTarget target, string path, bool restoreClipboard, Func<Task<bool>> prepare)
    {
        await SendGate.WaitAsync();
        using var suppression = ClipboardCapture.BeginSuppression();
        IDataObject? backup = null;
        uint ownedSequence = 0;
        bool pasted = false;
        try
        {
            if (!File.Exists(path) || !environment.IsValid(target)) return new(SendStatus.TargetUnavailable);
            if (!await prepare()) return new(SendStatus.FocusUnavailable);
            if (!environment.IsFocused(target)) return new(SendStatus.TargetChanged);
            backup = restoreClipboard ? environment.CaptureClipboard() : null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!environment.IsValid(target)) return new(SendStatus.TargetUnavailable);
                if (!environment.IsFocused(target)) return new(SendStatus.TargetChanged);
                if (environment.TryWriteFile(path))
                {
                    ownedSequence = environment.ClipboardSequence;
                    break;
                }
                if (attempt < 2) await environment.DelayAsync(100);
            }
            if (ownedSequence == 0) return new(SendStatus.ClipboardBusy);
            if (!environment.IsFocused(target)) return new(SendStatus.TargetChanged);
            if (environment.ClipboardSequence != ownedSequence) return new(SendStatus.ClipboardChanged);
            pasted = environment.TryPaste(target);
            return new(pasted ? SendStatus.Sent : SendStatus.InputRejected);
        }
        catch (Exception ex)
        {
            QqPanelWatcher.Log("send failed: " + ex.GetType().Name);
            return new(SendStatus.Failed);
        }
        finally
        {
            try
            {
                if (backup != null && ownedSequence != 0)
                {
                    if (pasted) await environment.DelayAsync(1000);
                    if (environment.ClipboardSequence == ownedSequence) environment.RestoreClipboard(backup);
                }
            }
            catch { /* Never turn accepted input into a retry after a restoration failure. */ }
            finally { SendGate.Release(); }
        }
    }
}
