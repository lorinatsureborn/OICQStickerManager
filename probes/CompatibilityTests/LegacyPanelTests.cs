using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using OICQStickerManager.Services;

internal static class LegacyPanelTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static (string Name, Action Run)[] Cases =>
    [
        ("A correlated legacy panel remains open after its button evidence expires", VisiblePanelOutlivesButton),
        ("A hidden legacy panel overrides still-fresh button evidence", HiddenPanelCloses),
        ("Unrelated legacy structure nodes cannot become the panel", UnrelatedNodesAreRejected),
        ("A recycled legacy panel node cannot reopen at unrelated geometry", RecycledNodeCloses),
        ("Reopening a reused legacy panel does not require another structure event", ReusedNodeReopens),
        ("A verified button open survives missing UIA panel events until explicit dismissal", MissingPanelEvents),
    ];

    private static void VisiblePanelOutlivesButton()
    {
        using var fixture = new Fixture();
        Program.Require(fixture.Observe(fixture.Provider), "visible panel event was not correlated");
        fixture.Set("_legacyTabShownTicks", Environment.TickCount64 - 11_000);
        Program.Require(fixture.Scan().open, "a visible native panel was closed when button evidence expired");
    }

    private static void HiddenPanelCloses()
    {
        using var fixture = new Fixture();
        Program.Require(fixture.Observe(fixture.Provider), "visible panel event was not correlated");
        fixture.Provider.Hidden = true;
        Program.Require(!fixture.Scan().open, "hidden panel stayed open until the button evidence expired");
    }
    private static void RecycledNodeCloses()
    {
        using var fixture = new Fixture();
        Program.Require(fixture.Observe(fixture.Provider), "visible fixture was not correlated");
        fixture.Provider.Rect = new(-29980, -29400, 220, 140);
        Program.Require(!fixture.Scan().open, "recycled node geometry kept a dismissed native panel alive");
    }
    private static void ReusedNodeReopens()
    {
        using var fixture = new Fixture();
        Program.Require(fixture.Observe(fixture.Provider), "initial panel was not correlated");
        fixture.Close();
        Program.Require(!fixture.Scan().open, "retained node reopened the dismissed operation");
        fixture.ReopenWithoutStructureEvent();
        Program.Require(fixture.Scan().open, "QQ reused its panel without another structure event and the quick panel expired");
        fixture.Provider.Hidden = true;
        Program.Require(!fixture.Scan().open, "rebound node stopped detecting actual native closure");
    }
    private static void MissingPanelEvents()
    {
        using var fixture = new Fixture();
        var latch = typeof(QqPanelWatcher).GetField("_legacyButtonOpenLatched", Fields);
        Program.Require(latch != null, "verified button opens still expire when QQ omits panel events");
        latch!.SetValue(fixture.Watcher, true);
        fixture.Set("_legacyTabShownTicks", Environment.TickCount64 - 65_000);
        Program.Require(fixture.Scan().open, "a verified native button open expired while QQ remained visible");
        fixture.Close();
        Program.Require(!fixture.Scan().open && !(bool)latch.GetValue(fixture.Watcher)!, "explicit dismissal retained the button-open latch");
    }

    private static void UnrelatedNodesAreRejected()
    {
        using var fixture = new Fixture();
        Program.Require(!fixture.Observe(new PanelProvider(new Rect(-29_980, -29_980, 448, 286))),
            "a content item was mistaken for the panel container");
        Program.Require(!fixture.Observe(new PanelProvider(new Rect(-29_980, -29_980, 450, 336)) { Pid = Environment.ProcessId + 1 }),
            "an element from another process became the QQ panel");
        fixture.Set("_legacyAnchorRect", new Rect(-29_980, -29_980, 32, 32));
        Program.Require(!fixture.Observe(new PanelProvider(new Rect(-29_980, -29_980, 32, 32))),
            "a cursor-only anchor promoted an arbitrary small element to the native panel");
        fixture.Set("_legacyTabShownTicks", Environment.TickCount64 - 11_000);
        Program.Require(!fixture.Scan().open, "unrelated nodes kept an unconfirmed panel open");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HwndSource _window = new(new HwndSourceParameters("audit-legacy-panel")
        {
            Width = 800, Height = 600, PositionX = -30_000, PositionY = -30_000,
            WindowStyle = unchecked((int)0x90000000)
        });
        private readonly QqPanelWatcher _watcher = new(_ => { });
        internal QqPanelWatcher Watcher => _watcher;
        internal PanelProvider Provider { get; } = new(new Rect(-29_980, -29_980, 450, 336));
        internal Fixture()
        {
            Set("_legacyTabHwnd", _window.Handle);
            Set("_legacyAnchorRect", new Rect(-29_980, -29_980, 450, 336));
            Set("_legacyTabShownTicks", Environment.TickCount64);
            Set("_legacyOpenTtlMs", 10_000);
            var capabilities = (QqProcessCapabilities)typeof(QqPanelWatcher).GetField("_capabilities", Fields)!.GetValue(_watcher)!;
            capabilities.Refresh([new(Environment.ProcessId, 1, "9.9.19.35469")]);
        }
        internal void Set(string name, object value) => typeof(QqPanelWatcher).GetField(name, Fields)!.SetValue(_watcher, value);
        internal void Close() => typeof(QqPanelWatcher).GetMethod("OptimisticClosePanel", Fields)!.Invoke(_watcher, ["fixture dismissal"]);
        internal void ReopenWithoutStructureEvent()
        {
            typeof(QqPanelWatcher).GetMethod("BumpUserAction", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            Set("_legacyAnchorRect", Provider.Rect);
            Set("_legacyTabShownTicks", Environment.TickCount64 - 11_000);
        }
        internal bool Observe(PanelProvider provider)
        {
            var element = AutomationElement.FromLocalProvider(provider);
            Program.Require(element.Current.BoundingRectangle == (provider.Hidden ? Rect.Empty : provider.Rect), "provider fixture geometry was not exposed: " + element.Current.BoundingRectangle);
            var observe = typeof(QqPanelWatcher).GetMethod("TryObserveLegacyPanel", Fields);
            Program.Require(observe != null, "legacy structure evidence is not consumed");
            return (bool)observe!.Invoke(_watcher, [element, _window.Handle, QqPanelWatcher.UserActionGen])!;
        }
        internal (bool open, IntPtr hwnd, Rect rect) Scan() =>
            ((bool, IntPtr, Rect))typeof(QqPanelWatcher).GetMethod("ScanQqWindows", Fields)!.Invoke(_watcher, [new HashSet<int> { Environment.ProcessId }, false])!;
        public void Dispose() { _watcher.Dispose(); _window.Dispose(); }
    }

    internal sealed class PanelProvider(Rect rect) : IRawElementProviderSimple, IRawElementProviderFragmentRoot
    {
        private static int _nextId;
        private readonly int _id = Interlocked.Increment(ref _nextId);
        internal Rect Rect { get; set; } = rect;
        internal bool Hidden { get; set; }
        internal int Pid { get; init; } = Environment.ProcessId;
        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public IRawElementProviderSimple HostRawElementProvider => null!;
        public Rect BoundingRectangle => Hidden ? System.Windows.Rect.Empty : Rect;
        public IRawElementProviderFragmentRoot FragmentRoot => this;
        public IRawElementProviderFragment? Navigate(NavigateDirection direction) => null;
        public int[] GetRuntimeId() => [Environment.ProcessId, _id];
        public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
        public void SetFocus() => throw new NotSupportedException();
        public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y) => null;
        public IRawElementProviderFragment? GetFocus() => null;
        public object? GetPatternProvider(int id) => null;
        public object? GetPropertyValue(int id)
        {
            if (id == AutomationElement.ProcessIdProperty.Id) return Pid;
            if (id == AutomationElement.ControlTypeProperty.Id) return ControlType.Custom.Id;
            if (id == AutomationElement.IsOffscreenProperty.Id) return Hidden;
            if (id == AutomationElement.BoundingRectangleProperty.Id)
                return Hidden ? System.Windows.Rect.Empty : Rect;
            return null;
        }
    }
}
