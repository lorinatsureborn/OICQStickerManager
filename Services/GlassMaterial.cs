using System.Windows;
using System.Windows.Media;

namespace OICQStickerManager.Services;

/// <summary>
/// 玻璃材质浓度：全窗口雾面（GlassOverlayBrush）的不透明度，用户可在设置页实时调整。
/// 变白/不透明度统一由内容层材质决定（拼片色调已弃用——它只覆盖内接矩形，会造成
/// 清晰带与磨砂区色差）。主题切换会重置调色板，故订阅 ThemeChanged 在其后重涂用户浓度。
/// </summary>
public static class GlassMaterial
{
    private static int _opacityPercent = 70;
    private static bool _themeHooked;

    internal static SolidColorBrush CreateBrush(Color color, bool blurAvailable)
    {
        var brush = new SolidColorBrush(Color.FromArgb(blurAvailable ? (byte)Math.Round(OpacityPercent * 2.55) : (byte)255, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>材质不透明度（百分比 50-100，默认 70）。</summary>
    public static int OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            value = Math.Clamp(value, 50, 100);
            if (_opacityPercent == value) return;
            _opacityPercent = value;
            Apply();
        }
    }

    /// <summary>App 启动时调用一次：订阅主题切换，切换后在新调色板上重涂用户浓度。</summary>
    public static void EnsureHooked()
    {
        if (_themeHooked) return;
        _themeHooked = true;
        ThemeManager.ThemeChanged += (_, _) => Apply();
    }

    /// <summary>
    /// 以当前 GlassOverlayBrush 的颜色 + 用户浓度重写 Application 资源。
    /// 对根字典的索引赋值会广播资源失效（与 ThemeManager 换肤同款机制），所有引用即时更新。
    /// </summary>
    public static void Apply()
    {
        if (Application.Current?.TryFindResource("GlassOverlayBrush") is not SolidColorBrush brush) return;
        var c = brush.Color;
        Application.Current.Resources["GlassOverlayBrush"] = CreateBrush(c, true);
    }
}
