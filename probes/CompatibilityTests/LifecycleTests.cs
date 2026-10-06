using OICQStickerManager.Services;

internal static class LifecycleTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Mouse hook uses an OS thread and releases its message pump", () => Program.RunAsync(HookStops())),
        ("Disposed mouse hook cannot start late", () => Program.RunAsync(DisposedHookDoesNotStart())),
        ("Injected mouse input is not recorded as a user click", InjectedClickIsNotUserInput),
    ];

    private static async Task HookStops()
    {
        using var hook = new MouseHookPump((_, _, _) => IntPtr.Zero, _ => { });
        Program.Require(await hook.StartAsync(), "mouse hook did not install");
        Program.Require(hook.NativeThreadId != 0 && hook.IsRunning, "message thread was not ready");
        hook.Dispose();
        Program.Require(!hook.IsRunning, "message pump survived disposal");
    }

    private static async Task DisposedHookDoesNotStart()
    {
        using var hook = new MouseHookPump((_, _, _) => IntPtr.Zero, _ => { });
        hook.Dispose();
        Program.Require(!await hook.StartAsync() && !hook.IsRunning, "disposed hook installed late");
    }
    private static void InjectedClickIsNotUserInput()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        using var watcher = new QqPanelWatcher(_ => { });
        typeof(QqPanelWatcher).GetProperty("Running")!.SetValue(watcher, true);
        var tickField = typeof(QqPanelWatcher).GetField("_lastMouseDownTicks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previous = tickField.GetValue(null);
        tickField.SetValue(null, 0L);
        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(new byte[32], 0, pointer, 32);
            System.Runtime.InteropServices.Marshal.WriteInt32(pointer, 12, 1); // MSLLHOOKSTRUCT.flags = LLMHF_INJECTED.
            typeof(QqPanelWatcher).GetMethod("MouseHookProc", flags)!.Invoke(watcher, new object[] { 0, new IntPtr(0x201), pointer });
            var ticks = (long)tickField.GetValue(null)!;
            Program.Require(ticks == 0, "programmatic mouse input became a user-action or editor-focus signal");
        }
        finally { tickField.SetValue(null, previous); System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer); }
    }
}
