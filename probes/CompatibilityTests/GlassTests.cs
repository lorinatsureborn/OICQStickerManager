using System.Windows.Media;
using OICQStickerManager.Services;

internal static class GlassTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Unavailable blur uses an opaque readable material", UnavailableBlur),
        ("Available blur preserves the user's material opacity", AvailableBlur),
    ];
    private static void UnavailableBlur()
    {
        var color = Color.FromArgb(50, 120, 160, 180);
        var brush = GlassMaterial.CreateBrush(color, false);
        Program.Require(brush.IsFrozen && brush.Color == Color.FromArgb(255, 120, 160, 180), "fallback remained transparent or changed theme colors");
    }
    private static void AvailableBlur()
    {
        var color = Color.FromRgb(120, 160, 180);
        var brush = GlassMaterial.CreateBrush(color, true);
        Program.Require(brush.IsFrozen && brush.Color.A == (byte)Math.Round(GlassMaterial.OpacityPercent * 2.55), "blur ignored the configured opacity");
    }
}
