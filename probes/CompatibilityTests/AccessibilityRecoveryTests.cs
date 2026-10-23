using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using OICQStickerManager.Services;

internal static class AccessibilityRecoveryTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Modern QQ without accessible controls can use button recognition", ModernQqCanRecover),
        ("Another chat's button cache cannot block recognition", AnotherChatCanRecover),
        ("A stale button position cannot block recognition", ChangedLayoutCanRecover),
        ("A valid cached button avoids redundant pixel recognition", CachedButtonUsesNormalPath),
        ("Pixel recovery rejects windows outside the known QQ processes", OtherProcessIsRejected),
        ("A pixel-confirmed modern button survives UIA verification without focus events", PixelEvidenceSurvivesVerification),
    ];

    private static void ModernQqCanRecover()
    {
        using var fixture = new Fixture();
        Program.Require(fixture.NeedsPixels(fixture.Host.Handle, new(-29_950, -29_600)),
            "modern QQ still requires accessible focus events before the fallback can run");
    }
    private static void AnotherChatCanRecover()
    {
        using var fixture = new Fixture();
        using var other = new HwndSource(new HwndSourceParameters("other-chat")
            { Width = 800, Height = 600, PositionX = -28000, PositionY = -28000 });
        fixture.CacheButton();
        Program.Require(fixture.NeedsPixels(other.Handle, new(-27_950, -27_600)),
            "one chat's cache disabled recovery in another chat");
    }
    private static void ChangedLayoutCanRecover()
    {
        using var fixture = new Fixture();
        fixture.CacheButton();
        Program.Require(fixture.NeedsPixels(fixture.Host.Handle, new(-29_850, -29_600)),
            "a cache outside the clicked button disabled recovery after the layout changed");
    }
    private static void CachedButtonUsesNormalPath()
    {
        using var fixture = new Fixture();
        fixture.CacheButton();
        Program.Require(!fixture.NeedsPixels(fixture.Host.Handle, new(-29_950, -29_600)),
            "a valid button unnecessarily entered pixel recognition");
    }
    private static void OtherProcessIsRejected()
    {
        using var fixture = new Fixture();
        fixture.Set("_qqPids", new HashSet<int>());
        Program.Require(!fixture.NeedsPixels(fixture.Host.Handle, new(-29_950, -29_600))
            && !fixture.NeedsPixels(IntPtr.Zero, new(0, 0)), "non-QQ input entered pixel recovery");
    }
    private static void PixelEvidenceSurvivesVerification()
    {
        using var fixture = new Fixture();
        var cache = typeof(QqPanelWatcher).GetMethod("CachePixelButton", Instance);
        Program.Require(cache != null, "pixel matches do not enable the UIA-independent verification path");
        cache!.Invoke(fixture.Watcher, [fixture.Host.Handle, Environment.ProcessId, new Rect(-29_965, -29_615, 30, 30)]);
        var result = ((bool open, IntPtr hwnd, Rect rect))typeof(QqPanelWatcher).GetMethod("ScanQqWindows", Instance)!
            .Invoke(fixture.Watcher, [new HashSet<int> { Environment.ProcessId }, false])!;
        Program.Require(result.open && result.hwnd == fixture.Host.Handle && result.rect.Width >= 400,
            "UIA verification immediately dismissed a pixel-confirmed modern QQ button");
    }

    private sealed class Fixture : IDisposable
    {
        internal HwndSource Host { get; } = new(new HwndSourceParameters("ime-recovery")
        { Width = 800, Height = 600, PositionX = -30000, PositionY = -30000, WindowStyle = unchecked((int)0x90000000) });
        internal QqPanelWatcher Watcher { get; } = new(_ => { });
        internal Fixture()
        {
            Set("_qqPids", new HashSet<int> { Environment.ProcessId });
            var capabilities = (QqProcessCapabilities)typeof(QqPanelWatcher).GetField("_capabilities", Instance)!.GetValue(Watcher)!;
            capabilities.Refresh([new(Environment.ProcessId, 1, "9.9.36-53644")]);
        }
        internal void Set(string name, object value) => typeof(QqPanelWatcher).GetField(name, Instance)!.SetValue(Watcher, value);
        internal void CacheButton()
        {
            Set("_emojiBtnHwnd", Host.Handle); Set("_emojiBtnPid", Environment.ProcessId);
            Set("_emojiBtnRectRel", new Rect(35, 385, 30, 30));
        }
        internal bool NeedsPixels(IntPtr host, Point point)
        {
            var method = typeof(QqPanelWatcher).GetMethod("NeedsPixelFallback", Instance);
            Program.Require(method != null, "pixel eligibility still depends on a QQ version or a global button cache");
            return (bool)method!.Invoke(Watcher, [host, point])!;
        }
        public void Dispose() { Watcher.Dispose(); Host.Dispose(); }
    }
}
