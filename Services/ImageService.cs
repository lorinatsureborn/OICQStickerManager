using System.IO;
using OICQStickerManager.Models;

namespace OICQStickerManager.Services;

public class ImageService
{
    // 支持的图片格式
    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

    /// <summary>
    /// 异步获取指定目录下的所有表情包文件
    /// </summary>
    /// <param name="folderPath">目标文件夹路径</param>
    public async Task<List<StickerModel>> GetStickersAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return new List<StickerModel>();

        return await Task.Run(() =>
        {
            var directoryInfo = new DirectoryInfo(folderPath);

            return directoryInfo.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                .Where(file => SupportedExtensions.Contains(file.Extension.ToLower()))
                .Select(file => new StickerModel
                {
                    FullPath = file.FullName,
                    Tags = new List<string> {},
                    // "最近活跃"基线 = 文件时间：Library 按 MD5 命名，mtime ≈ 入库时间。
                    // 让从未使用的表情也有合理的新近度起点（而非 MinValue 压到冷却兜底档）；
                    // stickers.json 里有真实使用时间时会在加载合并阶段覆盖它
                    LastUsedTime = file.LastWriteTime
                })
                .ToList();
        });
    }
}