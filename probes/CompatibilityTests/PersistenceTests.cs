using System.Text.Json;
using OICQStickerManager.Models;
using OICQStickerManager.Services;

internal static class PersistenceTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Persistence snapshot does not share mutable tags", ImmutableSnapshot),
        ("Persistence writes preserve request order", () => Program.RunAsync(WritesStayOrdered())),
        ("Null legacy tags load as an empty list", NullTagsAreNormalized),
        ("Portable data directory is respected before loading configuration", PortableDirectory),
        ("Emoji button templates stay inside the selected data directory", PortableTemplateDirectory),
        ("Exit flush waits for pending-delete metadata", () => FlushMetadata("_pendingDeleteWriteLock", true)),
        ("Exit flush waits for tag usage metadata", () => FlushMetadata("_tagStatsWriteLock", false)),
        ("Early settings changes cannot overwrite an unread profile", EarlySettingsPreserveProfile),
        ("Ordered configuration saves preserve AI profiles and the protected QQ key", AiProfilesSurviveSave),
        ("An inactive AI profile does not replace the saved draft on startup", InactiveAiProfilePreservesDraft),
    ];

    private static void PortableDirectory()
    {
        var previous = Environment.GetEnvironmentVariable("ASUKA_DATA_DIR");
        var root = Program.NewDirectory();
        try
        {
            Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", root);
            Program.Require(OICQStickerManager.ViewModels.MainViewModel.GetConfigPath() == System.IO.Path.Combine(root, "config.json"), "portable profile was ignored");
        }
        finally { Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", previous); }
    }

    private static void PortableTemplateDirectory()
    {
        var previous = Environment.GetEnvironmentVariable("ASUKA_DATA_DIR");
        var root = Program.NewDirectory();
        try
        {
            Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", root);
            var method = typeof(EmojiButtonTemplate).GetMethod("StorePath", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var path = (string)method.Invoke(null, null)!;
            Program.Require(path == System.IO.Path.Combine(root, "emoji-btn-template.json"), "isolated template path escaped into the user's default profile");
        }
        finally { Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", previous); }
    }

    private static void FlushMetadata(string fieldName, bool deletion)
    {
        var root = Program.NewDirectory();
        var vm = LibraryTests.Create(root);
        Program.RunAsync(Task.Delay(200));
        var field = typeof(OICQStickerManager.ViewModels.MainViewModel).GetField(fieldName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var gate = (SemaphoreSlim)field.GetValue(null)!;
        gate.Wait();
        Task? release = null;
        try
        {
            if (deletion) vm.QueuePendingDelete(System.IO.Path.Combine(root, "Library", "pending.png"), "test");
            else vm.NoteTagUsage(new[] { "recent" });
            release = Task.Run(async () => { await Task.Delay(120); gate.Release(); });
            vm.FlushPendingConfigSave();
            var path = System.IO.Path.Combine(root, deletion ? "pending-deletes.json" : "tag-stats.json");
            Program.Require(System.IO.File.Exists(path), "exit returned before the metadata file was written");
        }
        finally { if (release == null) gate.Release(); else Program.RunAsync(release); }
    }

    private static void EarlySettingsPreserveProfile()
    {
        var root = Program.NewDirectory();
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "qq-stats.json"), "{}" + new string(' ', 1024 * 1024));
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "config.json"), JsonSerializer.Serialize(new AppConfig { QqDbKey = "audit-key-only!1", GallerySortMode = 2 }));
        var gate = (SemaphoreSlim)typeof(OICQStickerManager.ViewModels.MainViewModel).GetField("_configWriteLock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        gate.Wait();
        using var vm = LibraryTests.Create(root);
        try
        {
            vm.GallerySortMode = 1;
            Program.RunAsync(vm.Initialization);
        }
        finally { gate.Release(); }
        vm.FlushPendingConfigSave();
        var saved = JsonSerializer.Deserialize<AppConfig>(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "config.json")))!;
        Program.Require(saved.GallerySortMode == 2 && saved.QqDbKey == "audit-key-only!1", "pre-load defaults replaced a complete saved profile");
    }

    private static void ImmutableSnapshot()
    {
        var model = new StickerModel { FullPath = "test.png", Tags = ["before"], UseCount = 2 };
        var snapshot = StickerRecord.Capture(model);
        model.Tags.Add("after");
        model.UseCount = 3;
        var saved = JsonSerializer.Deserialize<StickerModel>(JsonSerializer.Serialize(snapshot))!;
        Program.Require(saved.Id == model.Id && saved.UseCount == 2 && saved.Tags.SequenceEqual(new[] { "before" }), "snapshot serialized later mutations");
    }

    private static void AiProfilesSurviveSave()
    {
        var root = Program.NewDirectory();
        var path = System.IO.Path.Combine(root, "config.json");
        var profile = new AiKeyProfile
        {
            Id = "merge-fixture-profile", Name = "Synthetic profile", ProviderId = "custom",
            ProtectedApiKey = ProtectedSecret.ProtectApiKey("synthetic-ai-key"), BaseUrl = "https://example.invalid/v1",
            Model = "synthetic-vision", Effort = "high", DetectedModels = ["synthetic-vision"],
        };
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new AppConfig
        {
            QqDbKey = "synthetic-qq-key", AiKeyProfiles = [profile], AiActiveProfileId = profile.Id,
        }));
        using var vm = LibraryTests.Create(root);
        Program.RunAsync(vm.Initialization);
        vm.GallerySortMode = 2;
        Program.RunAsync(vm.SaveConfigAsync());
        vm.FlushPendingConfigSave();
        var saved = JsonSerializer.Deserialize<AppConfig>(System.IO.File.ReadAllText(path))!;
        var restored = saved.AiKeyProfiles.Single();
        Program.Require(saved.AiActiveProfileId == profile.Id && restored.Id == profile.Id
            && restored.ProtectedApiKey == profile.ProtectedApiKey && restored.BaseUrl == profile.BaseUrl
            && restored.Model == profile.Model && restored.Effort == profile.Effort
            && restored.DetectedModels.SequenceEqual(profile.DetectedModels), "AI profile fields were lost during an ordered save");
        Program.Require(vm.AiTagApiKey == "synthetic-ai-key" && saved.AiTagModel == profile.Model
            && saved.AiTagProvider == profile.ProviderId && saved.AiTagBaseUrl == profile.BaseUrl
            && saved.AiTagEffort == profile.Effort
            && saved.GallerySortMode == 2, "active AI configuration or local settings did not survive the save");
        Program.Require(saved.QqDbKey != "synthetic-qq-key"
            && ProtectedSecret.Unprotect(saved.QqDbKey) == "synthetic-qq-key", "QQ key protection was lost while saving AI settings");
    }

    private static void InactiveAiProfilePreservesDraft()
    {
        var root = Program.NewDirectory();
        var path = System.IO.Path.Combine(root, "config.json");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new AppConfig
        {
            AiTagModel = "draft-model", AiActiveProfileId = "deleted-profile",
            AiKeyProfiles = [new AiKeyProfile { Id = "inactive-profile", ProtectedApiKey = ProtectedSecret.ProtectApiKey("synthetic-profile-key"), Model = "profile-model" }],
        }));
        using var vm = LibraryTests.Create(root);
        Program.RunAsync(vm.Initialization);
        Program.Require(vm.AiActiveProfileId == "" && vm.AiTagApiKey == ""
            && vm.AiTagModel == "draft-model", "an inactive profile overwrote the saved draft");
    }

    private static async Task WritesStayOrdered()
    {
        var path = System.IO.Path.Combine(Program.NewDirectory(), "ordered.json");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new OrderedJsonWriter();
        int calls = 0;
        async Task Write(string json)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) { entered.TrySetResult(); await release.Task; }
            await System.IO.File.WriteAllTextAsync(path, json);
        }
        var first = writer.SaveAsync(new { Value = "old" }, Write);
        var started = await Task.WhenAny(entered.Task, Task.Delay(500));
        Program.Require(started == entered.Task, "first save did not write");
        var second = writer.SaveAsync(new { Value = "new" }, Write);
        try { Program.Require(calls == 1 && !second.IsCompleted, "second save overtook an earlier request"); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second, writer.Completion);
        using var saved = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(path));
        Program.Require(saved.RootElement.GetProperty("Value").GetString() == "new", "old snapshot overwrote the latest save");
    }

    private static void NullTagsAreNormalized()
    {
        var sticker = JsonSerializer.Deserialize<StickerModel>("{\"Tags\":null}")!;
        Program.Require(sticker.Tags != null && sticker.DisplayName.Length > 0, "legacy null tags broke the model");
    }
}
