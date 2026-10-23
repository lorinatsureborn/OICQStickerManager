using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OICQStickerManager.Services;
using OICQStickerManager.Views;

internal static class QuickPanelPlacementTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Repeated coexist notifications do not move the visible quick panel", RepeatedAnchor),
        ("A changed coexist anchor moves directly to its final position", ChangedAnchor),
        ("The first quick-panel show already has its final physical bounds", FirstShow),
        ("A quick panel can be positioned before its first warm-up", ColdShow),
        ("Opening during warm-up cannot hide the freshly positioned panel", OpenDuringWarmUp),
        ("Each attached monitor receives the panel at its own DPI without an intermediate move", AttachedMonitors),
    ];

    private static void RepeatedAnchor()
    {
        using var fixture = new Fixture();
        fixture.Open(fixture.Anchor);
        var original = fixture.Bounds;
        fixture.Moves.Clear();
        fixture.Open(fixture.Anchor);
        Program.Require(fixture.Bounds == original, "a duplicate notification changed the final panel bounds");
        Program.Require(fixture.Moves.Count == 0,
            "a duplicate notification moved the visible panel through " + string.Join(" -> ", fixture.Moves));
    }

    private static void ChangedAnchor()
    {
        using var fixture = new Fixture();
        fixture.Open(fixture.Anchor);
        var original = fixture.Bounds;
        fixture.Moves.Clear();
        var next = fixture.Anchor;
        next.Offset(0, original.Top > fixture.Work.Top + 30 ? -10 : 10);
        fixture.Open(next);
        Program.Require(fixture.Bounds != original, "a changed native panel did not update the quick panel");
        Program.Require(fixture.Moves.Count == 1 && fixture.Moves[0] == fixture.Bounds,
            "the panel took an intermediate visible position: " + string.Join(" -> ", fixture.Moves));
    }

    private static void FirstShow()
    {
        using var fixture = new Fixture();
        var visibleBounds = new List<Rect>();
        fixture.Panel.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) visibleBounds.Add(fixture.Bounds);
        };
        fixture.Open(fixture.Anchor);
        var final = fixture.Bounds;
        Program.Require(visibleBounds.Count == 1 && visibleBounds[0] == final,
            "the first show exposed bounds that were later corrected");
        Program.Require(fixture.Work.Contains(final) && !final.IntersectsWith(fixture.Anchor),
            "the first show escaped the work area or covered the native panel");
    }

    private static void ColdShow()
    {
        using var fixture = new Fixture(warm: false);
        fixture.Open(fixture.Anchor);
        Program.Require(fixture.Panel.IsVisible && fixture.Work.Contains(fixture.Bounds),
            "a cold quick panel was not shown inside the work area");
    }

    private static void OpenDuringWarmUp()
    {
        using var fixture = new Fixture(warm: false);
        fixture.Panel.WarmUp();
        fixture.Open(fixture.Anchor);
        Program.Require(fixture.Panel.IsVisible && fixture.Work.Contains(fixture.Bounds),
            "the pending warm-up frame hid the panel after a real opening");
    }

    private static void AttachedMonitors()
    {
        using var fixture = new Fixture();
        var screens = System.Windows.Forms.Screen.AllScreens;
        foreach (var screen in screens.Concat(screens.Reverse()))
        {
            var area = screen.WorkingArea;
            var anchor = new Rect(area.Left + area.Width * 0.55, area.Top + 50, area.Width * 0.2, 150);
            fixture.Moves.Clear();
            fixture.Open(anchor);
            var bounds = fixture.Bounds;
            var scale = GetDpiForWindow(new WindowInteropHelper(fixture.Panel).Handle) / 96.0;
            Console.WriteLine($"  Native monitor fixture: {screen.DeviceName}, scale={scale:0.##}, bounds={bounds}, moves={string.Join(" -> ", fixture.Moves)}");
            Program.Require(new Rect(area.Left, area.Top, area.Width, area.Height).Contains(bounds),
                "the panel did not reach the destination monitor's work area");
            Program.Require(bounds.Height == Math.Floor(Math.Min(540 * scale, area.Height - 16 * scale)),
                $"the panel retained the previous monitor's pixel size: height={bounds.Height}, expected={Math.Floor(Math.Min(540 * scale, area.Height - 16 * scale))}");
            Program.Require(System.Windows.Media.VisualTreeHelper.GetDpi(fixture.Panel).DpiScaleX == scale,
                "native placement prevented WPF from updating its render DPI");
            Program.Require(fixture.Moves.Count <= 1 && fixture.Moves.All(rect => rect == bounds),
                "a monitor transition exposed an intermediate position: " + string.Join(" -> ", fixture.Moves));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal QuickPanelWindow Panel { get; }
        internal Rect Work { get; }
        internal Rect Anchor { get; }
        internal List<Rect> Moves { get; } = [];
        private HwndSource? _source;
        private readonly OICQStickerManager.ViewModels.MainViewModel _viewModel;
        internal Fixture(bool warm = true)
        {
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Asuka;component/Themes/Palette.Aurora.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Asuka;component/Themes/Tokens.xaml") });
            }
            _viewModel = LibraryTests.Create(Program.NewDirectory());
            Program.RunAsync(_viewModel.Initialization);
            Panel = new QuickPanelWindow(_viewModel) { Opacity = 0 };
            // Exercise a visible HWND without drawing a panel, glass tiles or installing input hooks.
            typeof(GlassWindow).GetField("_tilesReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Panel, true);
            ((IDisposable)typeof(QuickPanelWindow).GetField("_coexistLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Panel)!).Dispose();
            Panel.SourceInitialized += (_, _) =>
            {
                _source = HwndSource.FromHwnd(new WindowInteropHelper(Panel).Handle);
                _source.AddHook(ObservePosition);
            };
            if (warm)
            {
                Panel.WarmUp();
                Program.RunAsync(Task.Delay(100));
                Program.Require(!Panel.IsVisible, "warm-up did not finish hiding the test panel");
            }
            var monitor = MonitorFromWindow(new WindowInteropHelper(Panel).Handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Program.Require(GetMonitorInfo(monitor, ref info), "test monitor work area was unavailable");
            Work = info.Work.ToRect();
            Anchor = new Rect(Work.Left + Work.Width * 0.55, Work.Top + 50, Work.Width * 0.2, 150);
        }
        internal Rect Bounds
        {
            get
            {
                Program.Require(GetWindowRect(new WindowInteropHelper(Panel).Handle, out var bounds), "test HWND disappeared");
                return bounds.ToRect();
            }
        }
        internal void Open(Rect anchor)
        {
            Panel.OpenForCoexist(anchor, IntPtr.Zero);
            Program.RunAsync(Task.Delay(20));
        }
        private IntPtr ObservePosition(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0047 && Panel.IsVisible)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                if ((position.Flags & 3) != 3) Moves.Add(Bounds);
            }
            return IntPtr.Zero;
        }
        public void Dispose()
        {
            _source?.RemoveHook(ObservePosition);
            Panel.Close();
            _viewModel.Dispose();
            _viewModel.FlushPendingConfigSave();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        internal readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Hwnd, After; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
