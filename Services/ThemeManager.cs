using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace OICQStickerManager.Services;

/// <summary>
/// 一套完整配色：资源字典路径 + 雾玻璃底色 + 色板圆点。
/// GlassTintAbgr 是 Win32 SetWindowCompositionAttribute 用的 0xAABBGGRR 雾玻璃底色。
/// </summary>
public sealed record ThemeDef(
    string Id,
    string Name,
    string ResourcePath,
    uint GlassTintAbgr,
    bool IsDark,
    Color SwatchColor)
{
    public SolidColorBrush SwatchBrush { get; } = Freeze(new SolidColorBrush(SwatchColor));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 主题管理器：运行时替换 Application 资源里的调色板字典（样式全部 DynamicResource 引用，
/// 切换即时生效），并通知所有玻璃窗口刷新雾玻璃底色。
/// </summary>
public static class ThemeManager
{
    // GlassTintAbgr 已弃用：拼片色调只覆盖内接矩形、不覆盖清晰带，曾导致"清晰带与磨砂区
    // 颜色不一"。变白/不透明度统一由全窗口的内容层材质（Palette 的 GlassOverlayBrush）决定。
    // 字段保留兼容，alpha 固定 0（拼片侧已按无色调处理）。
    public static readonly ThemeDef[] Themes =
    {
        new("aurora",   "晨雾", "Themes/Palette.Aurora.xaml",   0x00FCF9F7, false, Color.FromRgb(0x00, 0x7A, 0xFF)),
        new("mint",     "薄荷", "Themes/Palette.Mint.xaml",     0x00F6F9F2, false, Color.FromRgb(0x00, 0xA6, 0x93)),
        new("midnight", "子夜", "Themes/Palette.Midnight.xaml", 0x00201C1C, true,  Color.FromRgb(0x0A, 0x84, 0xFF)),
        new("ember",    "暮色", "Themes/Palette.Ember.xaml",    0x00161A1F, true,  Color.FromRgb(0xFF, 0x9F, 0x0A)),
    };

    public static ThemeDef Current { get; private set; } = Themes[0];

    /// <summary>调色板切换后触发；玻璃窗口借此刷新雾玻璃底色。</summary>
    public static event EventHandler? ThemeChanged;

    public static ThemeDef? Find(string? id) => Themes.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// 应用启动时同步加载已保存的主题（config.json 很小，同步读避免主窗口先以错误配色渲染一帧）。
    /// 无配置或配置损坏时回落到默认主题。
    /// </summary>
    public static void ApplyInitial()
    {
        var target = Themes[0];
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "OICQStickerManager", "config.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("ThemeId", out var id))
                    target = Find(id.GetString()) ?? Themes[0];
            }
        }
        catch { /* 配置损坏则使用默认主题 */ }

        Apply(target);
    }

    public static void Apply(string id)
    {
        var def = Find(id);
        if (def == null || def == Current) return;
        Apply(def);
    }

    // 调色板键直接写入 Application.Resources 根字典：对根字典的索引赋值会广播
    // 资源失效通知，所有 DynamicResource（含 Style Setter 里的）都会重新取值。
    // 放在合并子字典里赋值则只更新取值、不广播——这是运行时换肤的关键区别。

    private static void Apply(ThemeDef def)
    {
        EnsurePaletteLoaded();

        var source = new ResourceDictionary { Source = new Uri(def.ResourcePath, UriKind.Relative) };
        var appResources = Application.Current.Resources;
        foreach (var key in source.Keys)
            appResources[key] = source[key];

        Current = def;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool _paletteLoaded;

    private static void EnsurePaletteLoaded()
    {
        if (_paletteLoaded) return;
        _paletteLoaded = true;

        var source = new ResourceDictionary { Source = new Uri(Themes[0].ResourcePath, UriKind.Relative) };
        var appResources = Application.Current.Resources;
        foreach (var key in source.Keys)
            appResources[key] = source[key];
    }
}
