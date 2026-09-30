using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace OICQStickerManager.Services;

/// <summary>
/// WebP 解码能力探测：WPF 自身不带 WebP 解码器，依赖系统安装的 WIC 解码器
/// （微软商店免费的「WebP 图像扩展」）。用内嵌的 1×1 微型 WebP 做真实解码探测，
/// 比查注册表/商店包名可靠（WIC 解码器有系统级/用户级多种安装形态）。
/// 结果进程内缓存，68 字节解码 &lt;5ms，只在首次启动通知时用一次。
/// </summary>
public static class WebpSupportProbe
{
    // 1×1 红色 WebP（68 字节，PIL 生成）
    private const string TinyWebPBase64 =
        "UklGRjwAAABXRUJQVlA4IDAAAADQAQCdASoBAAEAAUAmJaACdLoB+AADsAD+8ut//NgVzXPv9//S4P0uD9Lg/9KQAAA=";

    private static bool? _isSupported;

    public static bool IsSupported => _isSupported ??= Probe();

    /// <summary>强制重新解码探测（设置页"重新检测"用）：装/卸扩展后刷新进程内缓存的结果。</summary>
    public static bool Recheck()
    {
        _isSupported = Probe();
        return _isSupported.Value;
    }

    private static bool Probe()
    {
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(TinyWebPBase64));
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开微软商店的「WebP 图像扩展」安装页（官方免费，ProductId 9MVDQCNCXSTG）。</summary>
    public static void OpenStorePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-windows-store://pdp/?ProductId=9MVDQCNCXSTG",
            UseShellExecute = true,
        });
    }
}
