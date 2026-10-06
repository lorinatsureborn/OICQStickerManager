using System.Runtime.InteropServices;

namespace OICQStickerManager.Services;

internal sealed class MouseHookPump(Func<int, IntPtr, IntPtr, IntPtr> callback, Action<string> log) : IDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HookProc? _nativeCallback;
    private Thread? _thread;
    private volatile bool _disposed;
    private uint _threadId;
    internal bool IsRunning => _thread?.IsAlive == true;
    internal uint NativeThreadId => Volatile.Read(ref _threadId);

    internal Task<bool> StartAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            if (_thread != null) return _started.Task;
            _nativeCallback = (code, message, data) =>
            {
                try { if (code >= 0 && !_disposed) callback(code, message, data); }
                catch { }
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            };
            _thread = new Thread(Run) { IsBackground = true, Name = "AsukaMouseHook" };
            _thread.Start();
            return _started.Task;
        }
    }

    private void Run()
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (_disposed) return;
            hook = SetWindowsHookEx(14, _nativeCallback!, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { log("mouse hook install failed: " + Marshal.GetLastWin32Error()); return; }
            log("mouse hook installed on dedicated native thread");
            _started.TrySetResult(true);
            while (!_disposed && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { log("mouse hook failed: " + ex.GetType().Name); }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            _started.TrySetResult(false);
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate) { _disposed = true; thread = _thread; }
        uint id = NativeThreadId;
        if (id != 0) PostThreadMessage(id, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (thread != null && id != GetCurrentThreadId()) thread.Join(1500);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint first, uint last, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr hwnd, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
}
