namespace OICQStickerManager.Models;

/// <summary>一次批量入库的结果：新进库的表情 + 各类跳过计数（用于标签编辑器副标题或提示）。</summary>
public class ImportReport
{
    public List<StickerModel> Added { get; } = new();

    /// <summary>因 MD5 重复被跳过的数量。</summary>
    public int Duplicates { get; set; }

    /// <summary>因格式不支持（WebP/未知）被跳过的数量。</summary>
    public int Unsupported { get; set; }
}
