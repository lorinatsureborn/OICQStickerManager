using System.Windows;
using OICQStickerManager.Services;

internal static class PlacementTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Panel placement uses physical pixels on a negative-coordinate 150-percent screen", NegativeMonitor),
        ("Panel placement stays inside a small 200-percent working area", SmallWorkArea),
        ("Panel placement falls back when DPI is unavailable", MissingDpi),
        ("Coexist placement narrows to the right at 300-percent DPI without covering QQ", NarrowRight),
        ("Coexist placement narrows to the larger left side on a negative-coordinate screen", NarrowLeft),
        ("Coexist placement keeps its preferred width when the right side has room", FullRight),
    ];
    private static void NegativeMonitor()
    {
        var rect = PanelPlacement.Place(new Rect(-2560, -200, 2560, 1440), new Size(400, 480), new Point(-1600, 200), 1.5, true, 450);
        Program.Require(rect == new Rect(-2212, 200, 600, 720), "monitor origin was scaled as a DIP coordinate");
    }
    private static void SmallWorkArea()
    {
        var work = new Rect(1920, 0, 600, 700);
        var rect = PanelPlacement.Place(work, new Size(400, 480), new Point(2500, 680), 2, false);
        Program.Require(!rect.IsEmpty && work.Contains(rect) && rect.Width > 0 && rect.Height > 0, "panel escaped its working area");
    }
    private static void MissingDpi()
    {
        var rect = PanelPlacement.Place(new Rect(0, 0, 1920, 1040), new Size(400, 480), new Point(300, 300), 0, false);
        Program.Require(rect == new Rect(270, 270, 400, 480), "failed DPI probe produced invalid coordinates");
    }
    private static void NarrowRight()
    {
        var rect = PanelPlacement.Place(new Rect(0, 0, 3840, 2016), new Size(680, 540), new Point(942, 384), 3, true, 1350);
        Program.Require(rect == new Rect(2316, 372, 1500, 1620), "full-width clamping covered the native QQ panel");
    }
    private static void NarrowLeft()
    {
        var rect = PanelPlacement.Place(new Rect(-3840, 0, 3840, 2016), new Size(680, 540), new Point(-2280, 384), 3, true, 1350);
        Program.Require(rect == new Rect(-3816, 372, 1512, 1620), "placement ignored the larger left-side working area");
    }
    private static void FullRight()
    {
        var rect = PanelPlacement.Place(new Rect(0, 0, 7680, 2400), new Size(680, 540), new Point(942, 384), 3, true, 1350);
        Program.Require(rect == new Rect(2316, 384, 2040, 1620), "placement unnecessarily reduced the preferred panel width");
    }
}
