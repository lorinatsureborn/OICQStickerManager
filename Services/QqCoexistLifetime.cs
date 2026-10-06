using System.Runtime.InteropServices;
using System.Windows;

namespace OICQStickerManager.Services;

// No UIA calls: dismissal continues while the isolated QQ provider is blocked.
internal sealed class QqCoexistLifetime(Action<IntPtr, string> dismiss, Action<Action> dispatch,
    Action<IntPtr>? verifyPanel = null) : IDisposable
{
    private sealed record Session(IntPtr Host, uint Pid, IntPtr Own, Rect Panel, Rect Button);
    private Session? _session;
    private MouseHookPump? _mouse, _keyboard;
    private Timer? _timer;
    private bool _disposed;
    private static long _lastInteractionTicks;
    internal static long LastInteractionTicks => Volatile.Read(ref _lastInteractionTicks);

    internal void Follow(IntPtr host, IntPtr own, Rect panel, Rect button)
    {
        if (_disposed) return;
        GetWindowThreadProcessId(host, out var pid);
        Volatile.Write(ref _session, new(host, pid, own, panel, button));
        if (_mouse != null) return;
        _mouse = new MouseHookPump(Mouse, QqPanelWatcher.Log);
        _keyboard = new MouseHookPump(Keyboard, QqPanelWatcher.Log, 13);
        _ = _mouse.StartAsync(); _ = _keyboard.StartAsync();
        _timer = new Timer(_ => ProbeHost(), null, 50, 50);
    }
    internal void Stop() => Volatile.Write(ref _session, null);
    private void ProbeHost()
    {
        var session = Volatile.Read(ref _session);
        if (session == null) return;
        GetWindowThreadProcessId(session.Host, out var pid);
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        if (ShouldDismissHost(IsWindow(session.Host), IsWindowVisible(session.Host), IsIconic(session.Host),
            session.Pid, pid, session.Host, foreground)) Close(session, "QQ hidden/minimized/destroyed or foreground left host");
    }
    internal static bool ShouldDismissHost(bool exists, bool visible, bool minimized, uint expectedPid,
        uint actualPid, IntPtr host, IntPtr foreground) =>
        !exists || !visible || minimized || expectedPid == 0 || expectedPid != actualPid || host != foreground;
    internal static bool ShouldDismissClick(Point point, IntPtr hitRoot, IntPtr host, IntPtr own, Rect panel, Rect button) =>
        hitRoot != own && (hitRoot != host || (!panel.Contains(point) && !button.Contains(point)));
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        int msg = message.ToInt32();
        if (msg is not (0x201 or 0x204 or 0x207 or 0x20B)) return IntPtr.Zero;
        var input = Marshal.PtrToStructure<MouseData>(data);
        if (input.Extra == NativeInput.InputMarker) return IntPtr.Zero;
        Volatile.Write(ref _lastInteractionTicks, Environment.TickCount64);
        if (Volatile.Read(ref _session) == null) return IntPtr.Zero;
        var root = GetAncestor(WindowFromPoint(new(input.X, input.Y)), 2);
        HandleClick(new(input.X, input.Y), root);
        return IntPtr.Zero;
    }
    internal void HandleClick(Point point, IntPtr root)
    {
        var session = Volatile.Read(ref _session);
        if (session == null) return;
        if (ShouldDismissClick(point, root, session.Host, session.Own, session.Panel, session.Button))
            Close(session, "mouse down outside native and quick panels");
        else if (root == session.Host && session.Panel.Contains(point) && !session.Button.Contains(point))
            dispatch(() =>
            {
                if (!_disposed && ReferenceEquals(Volatile.Read(ref _session), session))
                    (verifyPanel ?? QqPanelWatcher.VerifyCoexist)(session.Host);
            });
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (message.ToInt32() is not (0x100 or 0x104)) return IntPtr.Zero;
        var session = Volatile.Read(ref _session);
        // Escape does not dismiss every QQ version/panel. Verify actual visibility;
        // do not hide our panel just because the key was pressed.
        if (session != null && Marshal.ReadInt32(data) == 0x1B)
            dispatch(() => { if (ReferenceEquals(Volatile.Read(ref _session), session)) QqPanelWatcher.VerifyCoexist(session.Host); });
        return IntPtr.Zero;
    }
    private void Close(Session session, string reason)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session)) return;
        dispatch(() => { if (!_disposed && Volatile.Read(ref _session) == null) dismiss(session.Host, reason); });
    }
    public void Dispose()
    {
        _disposed = true; Stop(); _timer?.Dispose(); _mouse?.Dispose(); _keyboard?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
