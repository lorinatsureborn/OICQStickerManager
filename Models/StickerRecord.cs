namespace OICQStickerManager.Models;

internal sealed record StickerRecord(Guid Id, string? Md5, string FullPath, IReadOnlyList<string> Tags, DateTime LastUsedTime, int UseCount)
{
    internal static StickerRecord Capture(StickerModel sticker) =>
        new(sticker.Id, sticker.Md5, sticker.FullPath, Array.AsReadOnly(sticker.Tags.ToArray()), sticker.LastUsedTime, sticker.UseCount);
}
