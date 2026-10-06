using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using OICQStickerManager.Services;

internal static class CoexistTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Our injected editor click remains evidence without becoming a user action", OwnClickEvidence),
        ("Dismissed coexist cannot reopen from delayed worker visibility", DelayedOpenIsRejected),
        ("A worker-confirmed close rejects its delayed opening frame", WorkerCloseRejectsDelayedOpen),
        ("An older close frame cannot hide a fresh button operation", OlderCloseCannotHideFreshOpen),
        ("A fresh button operation can reopen after dismissal", FreshOpenIsAccepted),
        ("Worker restart resets its native dismissal barrier", RestartResetsBarrier),
        ("Hidden QQ cannot remain open through fresh legacy evidence", HiddenHostCloses),
        ("Native dismissal clears pending open and legacy evidence", DismissClearsEvidence),
        ("Coexist lifetime covers hide, minimize, destroy, PID reuse and foreground change", HostLifetime),
        ("Outside clicks dismiss coexist while its own panel and native tabs remain usable", OutsideClicks),
        ("Button capture rejects a cursor over or near its template region", CursorCannotEnterTemplate),
        ("Releasing an opening button gesture elsewhere cancels optimistic coexist", AbortedOpenCloses),
    ];

    private static void OwnClickEvidence()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        typeof(QqPanelWatcher).GetProperty("Running")!.SetValue(watcher, true);
        long generation = QqPanelWatcher.UserActionGen;
        var pointer = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.Copy(new byte[32], 0, pointer, 32);
            Marshal.WriteInt32(pointer, 0, 400); Marshal.WriteInt32(pointer, 4, 700);
            Marshal.WriteInt32(pointer, 12, 1);
            Marshal.WriteIntPtr(pointer, 24, new IntPtr(0x4153554B));
            typeof(QqPanelWatcher).GetMethod("MouseHookProc", Instance)!.Invoke(watcher, [0, new IntPtr(0x201), pointer]);
            var evidence = typeof(QqPanelWatcher).GetField("_lastInjectedClickTicks", Static);
            Program.Require(evidence != null && (long)evidence.GetValue(null)! > 0,
                "own SendInput was discarded, so a pruned editor cannot acknowledge the click");
            Program.Require(QqPanelWatcher.UserActionGen == generation, "focus restoration toggled the user-operation generation");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void AbortedOpenCloses()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        typeof(QqPanelWatcher).GetProperty("Running")!.SetValue(watcher, true);
        var pending = typeof(QqPanelWatcher).GetField("_buttonOpenPending", Instance);
        Program.Require(pending != null, "opening mousedown has no cancellation when mouseup occurs elsewhere");
        pending!.SetValue(watcher, true);
        Set(watcher, "_panelOpen", true); Set(watcher, "_optimisticPending", true);
        var pointer = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.Copy(new byte[32], 0, pointer, 32);
            typeof(QqPanelWatcher).GetMethod("MouseHookProc", Instance)!.Invoke(watcher, [0, new IntPtr(0x202), pointer]);
            Program.Require(!watcher.ExportState().PanelOpen && !(bool)pending.GetValue(watcher)!,
                "an aborted opening gesture left the quick panel visible");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void Frame(QqPanelWatcher watcher, long generation)
    {
        var frame = new QqWorkerFrame { Kind = "appeared", Hwnd = 42,
            Rect = new(100, 100, 450, 336), State = new() { PanelOpen = true, Generation = generation } };
        typeof(QqPanelWatcher).GetMethod("HandleWorkerFrame", Instance)!.Invoke(watcher, [JsonSerializer.Serialize(frame)]);
    }

    private static void MarkDismissed(QqPanelWatcher watcher, long generation)
    {
        var field = typeof(QqPanelWatcher).GetField("_dismissedWorkerGeneration", Instance);
        Program.Require(field != null, "native dismissal has no barrier against delayed worker frames");
        field!.SetValue(watcher, generation);
    }
    private static void DelayedOpenIsRejected()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        MarkDismissed(watcher, 7);
        int opens = 0; watcher.PanelAppeared += (_, _) => opens++;
        Frame(watcher, 7);
        Program.Require(opens == 0 && !watcher.ExportState().PanelOpen, "an old appearance undid native dismissal");
    }
    private static void FreshOpenIsAccepted()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        MarkDismissed(watcher, 7);
        int opens = 0; watcher.PanelAppeared += (_, _) => opens++;
        Frame(watcher, 8);
        Program.Require(opens == 1 && watcher.ExportState().PanelOpen, "dismissal swallowed a new button operation");
    }
    private static void WorkerCloseRejectsDelayedOpen()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        typeof(QqPanelWatcher).GetMethod("HandleWorkerFrame", Instance)!.Invoke(watcher,
            [JsonSerializer.Serialize(new QqWorkerFrame { Kind = "disappeared", State = new() { PanelOpen = false, Generation = 7 } })]);
        int opens = 0; watcher.PanelAppeared += (_, _) => opens++;
        Frame(watcher, 7);
        Program.Require(opens == 0 && !watcher.ExportState().PanelOpen,
            "a delayed open revived an operation the worker had already closed");
    }
    private static void OlderCloseCannotHideFreshOpen()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        Frame(watcher, 8);
        int closes = 0; watcher.PanelDisappeared += (_, _) => closes++;
        typeof(QqPanelWatcher).GetMethod("HandleWorkerFrame", Instance)!.Invoke(watcher,
            [JsonSerializer.Serialize(new QqWorkerFrame { Kind = "disappeared", State = new() { PanelOpen = false, Generation = 7 } })]);
        Program.Require(closes == 0 && watcher.ExportState().PanelOpen, "an older operation hid the newly opened panel");
    }
    private static void RestartResetsBarrier()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        MarkDismissed(watcher, 12);
        typeof(QqPanelWatcher).GetMethod("HandleWorkerFrame", Instance)!.Invoke(watcher,
            [JsonSerializer.Serialize(new QqWorkerFrame { Kind = "reset" })]);
        int opens = 0; watcher.PanelAppeared += (_, _) => opens++;
        Frame(watcher, 1);
        Program.Require(opens == 1, "worker restart retained an earlier worker's dismissal generation");
    }
    private static void HiddenHostCloses()
    {
        using var host = new HwndSource(new HwndSourceParameters("hidden-qq-lifetime")
            { Width = 800, Height = 600, PositionX = -30000, PositionY = -30000, WindowStyle = unchecked((int)0x80000000) });
        using var watcher = new QqPanelWatcher(_ => { });
        var capabilities = (QqProcessCapabilities)typeof(QqPanelWatcher).GetField("_capabilities", Instance)!.GetValue(watcher)!;
        capabilities.Refresh([new(Environment.ProcessId, 1, "9.9.19.35469")]);
        Set(watcher, "_legacyTabHwnd", host.Handle); Set(watcher, "_legacyTabShownTicks", Environment.TickCount64);
        Set(watcher, "_legacyOpenTtlMs", 60000); Set(watcher, "_legacyAnchorRect", new Rect(-29980, -29980, 450, 336));
        var result = ((bool open, IntPtr hwnd, Rect rect))typeof(QqPanelWatcher).GetMethod("ScanQqWindows", Instance)!
            .Invoke(watcher, [new HashSet<int> { Environment.ProcessId }, false])!;
        Program.Require(!result.open, "invisible QQ was kept open by the legacy TTL");
    }
    private static void DismissClearsEvidence()
    {
        using var watcher = new QqPanelWatcher(_ => { });
        Set(watcher, "_panelOpen", true); Set(watcher, "_optimisticPending", true);
        Set(watcher, "_legacyAnchorRect", new Rect(10, 10, 450, 336));
        typeof(QqPanelWatcher).GetMethod("OptimisticClosePanel", Instance)!.Invoke(watcher, ["regression"]);
        Program.Require(!(bool)typeof(QqPanelWatcher).GetField("_optimisticPending", Instance)!.GetValue(watcher)!,
            "pending appearance survived a confirmed dismissal");
        Program.Require(!watcher.ExportState().PanelOpen, "panel state survived dismissal");
    }
    private static Type Lifetime() => typeof(QqPanelWatcher).Assembly.GetType("OICQStickerManager.Services.QqCoexistLifetime")
        ?? throw new InvalidOperationException("coexist has no native lifetime guard independent of UIA");
    private static void CursorCannotEnterTemplate()
    {
        var method = typeof(EmojiButtonTemplate).GetMethod("CursorClearOfTemplate", Static);
        Program.Require(method != null, "button templates can be captured with a cursor baked into them");
        var rect = new Rect(100, 100, 40, 40);
        bool Clear(Point cursor) => (bool)method!.Invoke(null, [rect, cursor, 1d])!;
        Program.Require(!Clear(new(120, 120)) && !Clear(new(80, 120)) && Clear(new(400, 400)),
            "cursor overlap or large cursor imagery was not excluded from button capture");
    }
    private static void HostLifetime()
    {
        var method = Lifetime().GetMethod("ShouldDismissHost", Static)!;
        bool Dismiss(bool exists = true, bool visible = true, bool iconic = false, uint actual = 12, long foreground = 42)
            => (bool)method.Invoke(null, [exists, visible, iconic, 12u, actual, new IntPtr(42), new IntPtr(foreground)])!;
        Program.Require(!Dismiss(), "valid foreground host was dismissed");
        Program.Require(Dismiss(exists:false) && Dismiss(visible:false) && Dismiss(iconic:true)
            && Dismiss(actual:13) && Dismiss(foreground:43), "one native closing path leaves coexist visible");
    }
    private static void OutsideClicks()
    {
        var method = Lifetime().GetMethod("ShouldDismissClick", Static)!;
        var panel = new Rect(100, 100, 450, 336); var button = new Rect(120, 450, 24, 24);
        bool Dismiss(Point point, long root) => (bool)method.Invoke(null,
            [point, new IntPtr(root), new IntPtr(42), new IntPtr(43), panel, button])!;
        Program.Require(!Dismiss(new(150, 400), 42) && !Dismiss(new(800, 500), 43) && !Dismiss(new(130, 460), 42),
            "native tabs, quick stickers or toggle button were dismissed on down");
        Program.Require(Dismiss(new(800, 300), 42) && Dismiss(new(800, 500), 44)
            && Dismiss(new(150, 400), 44), "QQ content or another app at the same coordinates was allowed to retain coexist");
    }
    private static void Set(object target, string field, object value)
        => typeof(QqPanelWatcher).GetField(field, Instance)!.SetValue(target, value);
}
