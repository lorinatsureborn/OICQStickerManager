using System.IO;

namespace OICQStickerManager.Services;

internal static class AppDataDirectory
{
    internal static string Root
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("ASUKA_DATA_DIR");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OICQStickerManager")
                : Path.GetFullPath(configured);
        }
    }
}
