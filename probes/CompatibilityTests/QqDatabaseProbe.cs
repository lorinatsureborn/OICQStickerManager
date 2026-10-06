using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OICQStickerManager.Models;
using OICQStickerManager.Services;

internal static class QqDatabaseProbe
{
    internal static int Run(string profileDirectory)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(profileDirectory, "config.json")));
            var key = ProtectedSecret.Unprotect(config.RootElement.GetProperty("QqDbKey").GetString() ?? "");
            var bindings = config.RootElement.GetProperty("QqBindings").Deserialize<List<QqBindingInfo>>() ?? [];
            if (string.IsNullOrEmpty(key) || bindings.Count == 0) return 1;
            int failed = 0;
            foreach (var binding in bindings)
            {
                var root = string.IsNullOrEmpty(binding.AccountDirectory)
                    ? Path.Combine(QqEmojiService.TencentFilesRoot, binding.Uin) : binding.AccountDirectory;
                var path = Path.Combine(root, "nt_qq", "nt_db", "emoji.db");
                var snapshot = QqDatabaseSnapshot.ReadAsync(path).GetAwaiter().GetResult();
                Console.WriteLine($"Account tail: {binding.Uin[^Math.Min(5, binding.Uin.Length)..]}, "
                    + $"database bytes={snapshot.Database.Length}, WAL bytes={snapshot.Wal?.Length ?? 0}");
                byte[]? plain = null;
                string? tempDirectory = null;
                try
                {
                    plain = SqlcipherDecryptor.Decrypt(snapshot.Database, key, snapshot.Wal);
                    Console.WriteLine($"Production authentication passed: pages={plain.Length / 4096}");
                    tempDirectory = Path.Combine(Path.GetTempPath(), "asuka-database-inspection", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDirectory);
                    var plainPath = Path.Combine(tempDirectory, "plain.db");
                    File.WriteAllBytes(plainPath, plain);
                    var readIndex = typeof(QqDeepSyncService).GetMethod("ReadFavMd5s", BindingFlags.NonPublic | BindingFlags.Static)!;
                    var index = (HashSet<string>)readIndex.Invoke(null, [plainPath])!;
                    Console.WriteLine($"Production SQLite integrity/index passed: favorites={index.Count}");
                }
                catch (CryptographicException ex)
                {
                    Console.WriteLine("Production authentication failed: " + ex.Message);
                    failed++;
                }
                finally
                {
                    if (plain != null) CryptographicOperations.ZeroMemory(plain);
                    if (tempDirectory != null) Directory.Delete(tempDirectory, recursive: true);
                }
            }
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Database inspection failed: " + ex.GetBaseException().GetType().Name);
            return 1;
        }
    }

}
