using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OICQStickerManager.Models;
using OICQStickerManager.Services;

internal static class AiSecurityTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("AI profiles persist only DPAPI ciphertext and unlock on reload", ProfileRoundTrip),
        ("Unsaved AI draft keys never reach configuration or backup", DraftStaysInMemory),
        ("Old plaintext AI configuration is not accepted or migrated", RejectOldPlaintext),
        ("Unreadable AI ciphertext preserves the profile but disables its key", UnreadableProfile),
        ("An unversioned protected AI value is rejected as plaintext", RejectUnversionedValue),
        ("Configuration backups discard obsolete plaintext AI fields", BackupDiscardsPlaintext),
        ("A failed configuration replacement leaves only protected keys in its temp file", ProtectedTemporaryFile),
        ("AI request options do not expose the key through ToString", OptionsDoNotExposeKey),
        ("Tag parse diagnostics redact arbitrary provider keys", ParseErrorRedactsKey),
        ("Model-list authentication errors redact arbitrary provider keys", () => Program.RunAsync(ModelErrorRedactsKey())),
        ("Tag HTTP errors and logs redact arbitrary provider keys", () => Program.RunAsync(TagErrorRedactsKey())),
        ("Unexpected transport exceptions cannot leak an AI key", () => Program.RunAsync(TransportErrorRedactsKey())),
    ];

    private const string Key = "custom-synthetic-secret-7428";
    private static string ConfigPath(string root) => Path.Combine(root, "config.json");

    private static void ProfileRoundTrip()
    {
        var root = Program.NewDirectory();
        string id;
        using (var vm = LibraryTests.Create(root))
        {
            Program.RunAsync(vm.Initialization);
            vm.AiTagProvider = "custom"; vm.AiTagApiKey = Key;
            vm.AiTagBaseUrl = "https://example.invalid/v1"; vm.AiTagModel = "synthetic-vision";
            vm.MarkAiDraftVerified(); id = vm.SaveCurrentAsProfile("Synthetic", []).Id;
            vm.FlushPendingConfigSave();
            Program.RunAsync(vm.SaveConfigAsync());
        }
        var json = File.ReadAllText(ConfigPath(root));
        using var doc = JsonDocument.Parse(json);
        Program.Require(!json.Contains(Key) && !doc.RootElement.TryGetProperty("AiTagApiKey", out _), "configuration retained the raw AI key");
        var profile = doc.RootElement.GetProperty("AiKeyProfiles")[0];
        Program.Require(!profile.TryGetProperty("ApiKey", out _), "profile still has a plaintext key field");
        var cipher = profile.GetProperty("ProtectedApiKey").GetString()!;
        Program.Require(cipher.StartsWith("dpapi:v1:") && ProtectedSecret.Unprotect(cipher) == Key, "AI key was not protected for the Windows user");
        Program.Require(!File.ReadAllText(ConfigPath(root) + ".bak").Contains(Key), "backup contains the AI key");
        using var reload = LibraryTests.Create(root); Program.RunAsync(reload.Initialization);
        Program.Require(reload.AiActiveProfileId == id && reload.AiTagApiKey == Key && reload.AiDraftVerified, "encrypted active profile did not unlock");
    }

    private static void DraftStaysInMemory()
    {
        var root = Program.NewDirectory();
        using (var vm = LibraryTests.Create(root))
        {
            Program.RunAsync(vm.Initialization); vm.AiTagProvider = "custom"; vm.AiTagApiKey = Key;
            Program.RunAsync(vm.SaveConfigAsync()); Program.RunAsync(vm.SaveConfigAsync()); vm.FlushPendingConfigSave();
        }
        foreach (var file in Directory.GetFiles(root, "config.json*"))
            Program.Require(!File.ReadAllText(file).Contains(Key), "an unsaved draft key reached disk");
        using var reload = LibraryTests.Create(root); Program.RunAsync(reload.Initialization);
        Program.Require(reload.AiTagApiKey == "" && reload.AiKeyProfiles.Count == 0, "draft secret survived restart");
    }

    private static void RejectOldPlaintext()
    {
        var root = Program.NewDirectory();
        File.WriteAllText(ConfigPath(root), JsonSerializer.Serialize(new
        {
            AiTagApiKey = Key, AiTagProvider = "custom", AiTagModel = "synthetic-vision", AiActiveProfileId = "old",
            AiKeyProfiles = new[] { new { Id = "old", ApiKey = Key, ProviderId = "custom", VerifiedAt = DateTime.Now } },
        }));
        using var vm = LibraryTests.Create(root); Program.RunAsync(vm.Initialization);
        Program.Require(vm.AiTagApiKey == "" && !vm.AiDraftVerified, "obsolete plaintext AI fields were accepted");
    }

    private static void UnreadableProfile() => RejectProtectedValue("dpapi:v1:AQID");
    private static void RejectUnversionedValue() => RejectProtectedValue(Key);
    private static void RejectProtectedValue(string protectedValue)
    {
        var root = Program.NewDirectory();
        File.WriteAllText(ConfigPath(root), JsonSerializer.Serialize(new
        {
            GallerySortMode = 2, AiActiveProfileId = "locked",
            AiKeyProfiles = new[] { new { Id = "locked", Name = "Locked", ProtectedApiKey = protectedValue,
                ProviderId = "custom", Model = "synthetic-vision", VerifiedAt = DateTime.Now } },
        }));
        using var vm = LibraryTests.Create(root); Program.RunAsync(vm.Initialization);
        Program.Require(vm.GallerySortMode == 2 && vm.AiKeyProfiles.Single().Id == "locked", "one unreadable key discarded unrelated configuration");
        Program.Require(vm.AiTagApiKey == "" && !vm.AiDraftVerified && !vm.AiTagConfigured && vm.AiStatusText.Contains("重新输入"), "unreadable AI key was usable or had no recovery hint");
        Program.RunAsync(vm.SaveConfigAsync()); vm.FlushPendingConfigSave();
        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath(root)));
        var expected = protectedValue.StartsWith("dpapi:v1:") ? protectedValue : "";
        Program.Require(saved.RootElement.GetProperty("AiKeyProfiles")[0].GetProperty("ProtectedApiKey").GetString() == expected, "invalid ciphertext was lost or plaintext was persisted");
        vm.BeginNewAiDraft();
        Program.Require(!vm.AiStatusText.Contains("无法解锁"), "a new draft retained the previous profile's unlock error");
    }

    private static void ProtectedTemporaryFile()
    {
        var path = ConfigPath(Program.NewDirectory());
        File.WriteAllText(path, "{}");
        var json = JsonSerializer.Serialize(new { QqDbKey = "synthetic-qq", AiTagApiKey = Key,
            AiKeyProfiles = new[] { new { Id = "old", ApiKey = Key, ProtectedApiKey = ProtectedSecret.ProtectApiKey(Key) } } });
        using var gate = new SemaphoreSlim(1, 1);
        // Windows allows reading the existing file for backup but denies its atomic replacement.
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = (Task)typeof(OICQStickerManager.ViewModels.MainViewModel)
            .GetMethod("WriteJsonWithBackupAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [path, json, gate])!;
        var failed = false;
        try { Program.RunAsync(write); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Program.Require(failed && File.Exists(path + ".tmp"), "fixture did not reach an interrupted atomic replacement");
        var temporary = File.ReadAllText(path + ".tmp");
        using var doc = JsonDocument.Parse(temporary);
        Program.Require(!temporary.Contains(Key) && !temporary.Contains("synthetic-qq")
            && !doc.RootElement.TryGetProperty("AiTagApiKey", out _) && !doc.RootElement.GetProperty("AiKeyProfiles")[0].TryGetProperty("ApiKey", out _), "temporary configuration contains plaintext keys");
    }

    private static void BackupDiscardsPlaintext()
    {
        var json = JsonSerializer.Serialize(new { QqDbKey = "synthetic-qq", AiTagApiKey = Key,
            AiKeyProfiles = new[] { new { Id = "old", ApiKey = Key, Model = "keep-model" } } });
        var sanitized = ProtectedSecret.ProtectConfigKeys(json);
        using var doc = JsonDocument.Parse(sanitized);
        Program.Require(!sanitized.Contains(Key) && !doc.RootElement.TryGetProperty("AiTagApiKey", out _)
            && !doc.RootElement.GetProperty("AiKeyProfiles")[0].TryGetProperty("ApiKey", out _), "backup preserves obsolete AI plaintext");
        Program.Require(doc.RootElement.GetProperty("AiKeyProfiles")[0].GetProperty("Model").GetString() == "keep-model"
            && ProtectedSecret.Unprotect(doc.RootElement.GetProperty("QqDbKey").GetString()!) == "synthetic-qq", "backup sanitization lost other fields");
    }

    private static AiTagOptions Options() => AiTagService.BuildOptions("custom", Key, "synthetic-vision", "https://example.invalid/v1");
    private static void OptionsDoNotExposeKey() => Program.Require(!Options().ToString().Contains(Key), "record ToString exposes the API key");
    private static void CheckException(AiTagException ex) => Program.Require(!(ex.Message + ex.Detail + ex.ToString()).Contains(Key), "exception exposes the API key");

    private static void ParseErrorRedactsKey()
    {
        try { AiTagService.ParseTags("invalid output " + Key, Options(), "raw " + Key); }
        catch (AiTagException ex) { CheckException(ex); return; }
        throw new Exception("malformed output was accepted");
    }
    private static async Task ModelErrorRedactsKey()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid key " + Key) });
        using var service = new AiTagService(handler);
        try { await service.ListVisionModelsAsync(Options()); }
        catch (AiTagException ex) { CheckException(ex); return; }
        throw new Exception("authentication failure was accepted");
    }
    private static async Task TagErrorRedactsKey() => await CheckTagError(new Handler(_ =>
        new(HttpStatusCode.Unauthorized) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = "invalid key " + Key } })) }));
    private static async Task TransportErrorRedactsKey() => await CheckTagError(new Handler(_ => throw new InvalidOperationException("transport " + Key)));
    private static async Task CheckTagError(Handler handler)
    {
        using (handler)
        using (var service = new AiTagService(handler))
        {
            var image = Path.Combine(Program.NewDirectory(), "sticker.png");
            File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
            var start = File.Exists(AiTagService.LogFilePath) ? new FileInfo(AiTagService.LogFilePath).Length : 0;
            try { await service.SuggestTagsAsync(image, Options(), []); }
            catch (AiTagException ex)
            {
                CheckException(ex);
                using var stream = File.OpenRead(AiTagService.LogFilePath); stream.Position = start;
                using var reader = new StreamReader(stream);
                Program.Require(!(await reader.ReadToEndAsync()).Contains(Key), "request log exposes the API key"); return;
            }
            throw new Exception("transport failure was accepted");
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
