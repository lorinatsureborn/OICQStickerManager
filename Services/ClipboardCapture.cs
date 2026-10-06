using System.Runtime.InteropServices;

namespace OICQStickerManager.Services;

/// <summary>
/// 剪贴板图片捕获（M2）：主窗口注册 WM_CLIPBOARDUPDATE 监听，用户在 QQ/浏览器里
/// 「复制图片」后弹非激活轻提示一键入库。发送通道写剪贴板（FileDropList + 粘贴 + 延迟恢复）
/// 期间用 Suppress 静默，防止捕获自己。
/// </summary>
public static class ClipboardCapture
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>发送到恢复完成的整个窗口期置 true（WindowService 三个发送方法 try/finally 管理）。</summary>
    public static bool Suppress => Volatile.Read(ref _suppressionCount) > 0;
    private static int _suppressionCount;

    public static IDisposable BeginSuppression()
    {
        Interlocked.Increment(ref _suppressionCount);
        return new SuppressionScope();
    }

    private sealed class SuppressionScope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _suppressionCount);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwndOwner);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwndOwner);

    public static bool Register(IntPtr hwnd) => AddClipboardFormatListener(hwnd);

    public static bool Unregister(IntPtr hwnd) => RemoveClipboardFormatListener(hwnd);
}
