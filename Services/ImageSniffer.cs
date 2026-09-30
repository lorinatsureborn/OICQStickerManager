using System.IO;
using System.Windows.Media.Imaging;

namespace OICQStickerManager.Services;

public enum ImageKind
{
    Jpeg,
    Gif,
    Png,
    WebP,
    Bmp,
    Unknown,
}

/// <summary>
/// 按文件头魔数判断真实图片格式。
/// QQ 表情缓存里约 1/3 文件后缀是错的（GIF 普遍被存成 .jpg），落盘与判定必须以魔数为准。
/// </summary>
public static class ImageSniffer
{
    public static ImageKind Detect(string path)
    {
        try
        {
            var header = new byte[16];
            using var fs = File.OpenRead(path);
            int read = 0;
            while (read < header.Length)
            {
                int n = fs.Read(header, read, header.Length - read);
                if (n == 0) break;
                read += n;
            }

            if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ImageKind.Jpeg;
            if (read >= 6 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F'
                && header[3] == '8' && (header[4] == '7' || header[4] == '9') && header[5] == 'a') return ImageKind.Gif;
            if (read >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return ImageKind.Png;
            if (read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P') return ImageKind.WebP;
            if (read >= 2 && header[0] == 'B' && header[1] == 'M') return ImageKind.Bmp;
            return ImageKind.Unknown;
        }
        catch
        {
            return ImageKind.Unknown;
        }
    }

    /// <summary>真实格式对应的标准扩展名；Unknown 返回 null（调用方回退到原扩展名）。</summary>
    public static string? ExtensionFor(ImageKind kind) => kind switch
    {
        ImageKind.Jpeg => ".jpg",
        ImageKind.Gif => ".gif",
        ImageKind.Png => ".png",
        ImageKind.WebP => ".webp",
        ImageKind.Bmp => ".bmp",
        _ => null,
    };

    /// <summary>
    /// 入库前先嗅探真实格式，并返回图库落盘扩展名。
    /// WebP：本机装了"WebP 图像扩展"（WIC 解码器）就转码成 PNG 落库（对齐 QQ 收藏管线
    /// "落地永远是标准格式"的做法，保持透明通道）；解不动才返回 false 让调用方跳过计数。
    /// </summary>
    public static bool TryGetImportExtension(string path, out string extension, out ImageKind kind)
    {
        kind = Detect(path);
        var ext = ExtensionFor(kind);
        if (ext == null || (kind == ImageKind.WebP && !IsDecodableByWic(path)))
        {
            extension = "";
            return false;
        }
        extension = kind == ImageKind.WebP ? ".png" : ext;
        return true;
    }

    /// <summary>图库内落盘扩展名：WebP 一律转 PNG，其余按真实格式。</summary>
    public static string LibraryExtensionFor(ImageKind kind)
        => kind == ImageKind.WebP ? ".png" : ExtensionFor(kind)!;

    /// <summary>
    /// 把源文件落成图库文件：普通格式直接复制；WebP 用 WIC 解码 + WPF 内置 PNG 编码器转码
    /// （零外部依赖；动图 WebP 只取第一帧；PreservePixelFormat 保住透明通道）。
    /// </summary>
    public static async Task<string> MaterializeAsync(string sourcePath, string libraryDir, string md5, ImageKind kind)
    {
        var destination = Path.Combine(libraryDir, md5 + LibraryExtensionFor(kind));

        if (kind != ImageKind.WebP)
        {
            await Task.Run(() => File.Copy(sourcePath, destination));
            return destination;
        }

        await Task.Run(() =>
        {
            var decoder = BitmapDecoder.Create(new Uri(sourcePath),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var fs = File.Create(destination);
            encoder.Save(fs);
        });
        return destination;
    }

    /// <summary>WPF 只认 WIC：能冻结解码即视为本机可显示。注意动图 WebP 也只会显示第一帧。</summary>
    private static bool IsDecodableByWic(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
