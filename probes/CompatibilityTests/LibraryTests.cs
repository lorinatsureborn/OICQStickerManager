using System.IO;
using System.Text.Json;
using OICQStickerManager.Models;
using OICQStickerManager.ViewModels;

internal static class LibraryTests
{
    private static readonly List<MainViewModel> Instances = new();
    internal static void Cleanup()
    {
        foreach (var vm in Instances) { vm.Dispose(); vm.FlushPendingConfigSave(); }
        Instances.Clear();
    }
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Library reload preserves sticker identity and tags after moving the profile", RelocatedProfile),
        ("A restored library record cancels a stale pending deletion at startup", RestoredRecordIsNotDeleted),
        ("Re-importing a pending file restores its library record", ReImportPendingFile),
        ("Batch deletion queues a locked file without resurrecting its record", LockedBatchDeletion),
        ("Pending deletion rejects paths outside the owned library", PendingDeleteRejectsExternalPath),
        ("QQ binding restores its custom account directory", CustomQqBinding),
        ("An existing QQ binding can move to a custom account directory", UpdateCustomQqBinding),
        ("Missing QQ database does not erase a saved key or reopen acquisition", MissingDatabasePreservesKey),
        ("A protected recovery key survives application restart", RecoveryKey),
        ("Initialization awaits the first QQ mirror scan", InitializationAwaitsMirror),
        ("Concurrent imports create only one library record", ConcurrentImports),
        ("Closing during initialization cannot start a late QQ mirror", DisposeDuringInitialization),
    ];
    internal static MainViewModel Create(string root)
    {
        var previous = Environment.GetEnvironmentVariable("ASUKA_DATA_DIR");
        var context = SynchronizationContext.Current;
        try
        {
            Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", root);
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            var vm = new MainViewModel();
            Instances.Add(vm);
            return vm;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", previous);
        }
    }
    private static string Image(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        var path = Path.Combine(root, "Library", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
        return path;
    }
    private static void Load(MainViewModel vm)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (vm.Stickers.Count == 0 && DateTime.UtcNow < end) Program.RunAsync(Task.Delay(20));
        Program.Require(vm.Stickers.Count == 1, "library did not load its test image");
        Program.RunAsync(Task.Delay(100));
        vm.FlushPendingConfigSave();
    }
    private static void RelocatedProfile()
    {
        var root = Program.NewDirectory();
        var path = Image(root);
        var saved = new StickerModel { FullPath = Path.Combine(root, "previous", Path.GetFileName(path)), Md5 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Tags = ["preserved"], UseCount = 42 };
        File.WriteAllText(Path.Combine(root, "stickers.json"), JsonSerializer.Serialize(new[] { saved }));
        var vm = Create(root);
        Load(vm);
        Program.Require(vm.Stickers[0].Id == saved.Id && vm.Stickers[0].UseCount == 42 && vm.Stickers[0].Tags.SequenceEqual(saved.Tags), "moving the library lost its ID or metadata");
    }
    private static void RestoredRecordIsNotDeleted()
    {
        var root = Program.NewDirectory();
        var path = Image(root);
        File.WriteAllText(Path.Combine(root, "stickers.json"), JsonSerializer.Serialize(new[] { new StickerModel { FullPath = path, Tags = ["restored"] } }));
        File.WriteAllText(Path.Combine(root, "pending-deletes.json"), JsonSerializer.Serialize(new[] { new { Path = path, Md5 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", RequestedAt = DateTime.Now } }));
        var vm = Create(root);
        Program.RunAsync(Task.Delay(300));
        vm.FlushPendingConfigSave();
        Program.Require(File.Exists(path), "startup deleted a file still referenced by restored metadata");
    }
    private static void ReImportPendingFile()
    {
        var root = Program.NewDirectory();
        var vm = Create(root);
        Program.RunAsync(Task.Delay(150));
        var source = Image(Program.NewDirectory());
        var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(source)));
        var destination = Path.Combine(root, "Library", hash + ".png");
        File.Copy(source, destination);
        vm.QueuePendingDelete(destination, hash);
        ImportReport? result = null;
        Program.RunAsync(Import());
        async Task Import() { result = await vm.AddStickersFromPathAsync(source); }
        vm.FlushPendingConfigSave();
        Program.Require(result!.Added.Count == 1 && vm.Stickers.Count == 1, "existing pending file cancelled deletion but was never added to the library");
    }

    private static void LockedBatchDeletion()
    {
        var root = Program.NewDirectory();
        var path = Image(root);
        var vm = Create(root);
        Load(vm);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int pending = 0;
        Program.RunAsync(Delete());
        async Task Delete() { pending = await vm.DeleteStickersAsync(vm.Stickers.ToArray()); }
        vm.FlushPendingConfigSave();
        Program.Require(pending == 1 && vm.Stickers.Count == 0 && File.Exists(path), "locked batch deletion lost its pending work or retained its record");
        Program.Require(File.ReadAllText(Path.Combine(root, "pending-deletes.json")).Contains(path.Replace("\\", "\\\\")), "locked file was not persisted for retry");
    }
    private static void PendingDeleteRejectsExternalPath()
    {
        var root = Program.NewDirectory();
        var vm = Create(root);
        Program.RunAsync(Task.Delay(150));
        var path = Image(Program.NewDirectory());
        vm.QueuePendingDelete(path, "test");
        var retry = typeof(MainViewModel).GetMethod("RetryPendingDeletesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Program.RunAsync((Task)retry.Invoke(vm, new object?[] { null })!);
        Program.Require(File.Exists(path), "pending queue deleted a file outside the app library");
    }

    private static void CustomQqBinding()
    {
        var root = Program.NewDirectory();
        var account = Path.Combine(Program.NewDirectory(), "123456789");
        var ori = Path.Combine(account, "nt_qq", "nt_data", "Emoji", "personal_emoji", "Ori");
        Directory.CreateDirectory(ori);
        File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(new AppConfig { QqBindings = [new QqBindingInfo { Uin = "123456789", AccountDirectory = account }] }));
        var vm = Create(root);
        Program.RunAsync(Task.Delay(250));
        vm.SelectedTab = "qq:123456789";
        var service = vm.CurrentQqService;
        try
        {
            Program.Require(service != null && service.OriDir == ori, "configured QQ data directory was ignored");
            Program.Require(service!.DatabasePath == Path.Combine(account, "nt_qq", "nt_db", "emoji.db"), "deep sync still uses the default Documents path");
        }
        finally { if (service != null) Program.RunAsync(vm.UnbindQqAccountAsync("123456789")); }
    }

    private static void UpdateCustomQqBinding()
    {
        var vm = Create(Program.NewDirectory());
        Program.RunAsync(Task.Delay(150));
        var first = Path.Combine(Program.NewDirectory(), "123456789");
        var second = Path.Combine(Program.NewDirectory(), "123456789");
        Directory.CreateDirectory(Path.Combine(first, "nt_qq"));
        Directory.CreateDirectory(Path.Combine(second, "nt_qq"));
        Program.RunAsync(vm.BindCustomQqAccountAsync(first));
        Program.RunAsync(vm.BindCustomQqAccountAsync(second));
        try { Program.Require(vm.QqBindings.Single().AccountDirectory == second, "existing binding kept its old directory"); }
        finally { Program.RunAsync(vm.UnbindQqAccountAsync("123456789")); }
    }
    private static void MissingDatabasePreservesKey()
    {
        var root = Program.NewDirectory();
        var account = Path.Combine(Program.NewDirectory(), "123456789");
        Directory.CreateDirectory(Path.Combine(account, "nt_qq"));
        File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(new AppConfig { QqDbKey = "audit-key-only!1", QqDeepSyncEnabled = true,
            QqBindings = [new QqBindingInfo { Uin = "123456789", AccountDirectory = account }] }));
        var vm = Create(root);
        bool requested = false;
        vm.KeyAcquisitionRequested += (_, _) => { requested = true; vm.CompleteKeyAcquisition(null); };
        Program.RunAsync(Task.Delay(250));
        Program.RunAsync(vm.ReconcileNowAsync());
        try { Program.Require(!requested && vm.QqDbKey == "audit-key-only!1", "a missing database invalidated the key and opened acquisition"); }
        finally { vm.QqDeepSyncEnabled = false; Program.RunAsync(vm.UnbindQqAccountAsync("123456789")); }
    }
    private static void RecoveryKey()
    {
        var root = Program.NewDirectory();
        File.WriteAllText(Path.Combine(root, "key-recovery.dpapi"), OICQStickerManager.Services.ProtectedSecret.Protect("audit-key-only!1"));
        var vm = Create(root);
        Program.RunAsync(Task.Delay(200));
        Program.Require(vm.QqDbKey == "audit-key-only!1", "captured recovery key was not restored");
    }
    private static void InitializationAwaitsMirror()
    {
        var root = Program.NewDirectory();
        var account = Path.Combine(Program.NewDirectory(), "123456789");
        var ori = Path.Combine(account, "nt_qq", "nt_data", "Emoji", "personal_emoji", "Ori");
        Directory.CreateDirectory(ori);
        File.WriteAllBytes(Path.Combine(ori, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.png"), [0x89, 0x50, 0x4e, 0x47]);
        File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(new AppConfig { QqBindings = [new QqBindingInfo { Uin = "123456789", AccountDirectory = account }] }));
        var vm = Create(root);
        try
        {
            var initialization = typeof(MainViewModel).GetProperty("Initialization")?.GetValue(vm) as Task;
            Program.Require(initialization != null, "initialization is still fire-and-forget");
            Program.RunAsync(initialization!);
            vm.SelectedTab = "qq:123456789";
            Program.Require(vm.CurrentQqService?.Mirror.Count == 1, "initialization completed before the first mirror scan");
        }
        finally { Program.RunAsync(Task.Delay(200)); Program.RunAsync(vm.UnbindQqAccountAsync("123456789")); }
    }
    private static void ConcurrentImports()
    {
        using var vm = Create(Program.NewDirectory());
        Program.RunAsync(vm.Initialization);
        var source = Image(Program.NewDirectory());
        using (var padded = new FileStream(source, FileMode.Open, FileAccess.Write)) padded.SetLength(16 * 1024 * 1024);
        string hash;
        using (var stream = File.OpenRead(source)) hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        var library = typeof(MainViewModel).GetMethod("EnsureLibraryFolder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null) as string;
        File.Copy(source, Path.Combine(library!, hash + ".png"));
        ImportReport[]? reports = null;
        Program.RunAsync(Import());
        async Task Import() { reports = await Task.WhenAll(vm.AddStickersFromPathAsync(source), vm.AddStickersFromPathAsync(source)); }
        Program.Require(vm.Stickers.Count == 1 && reports!.Sum(r => r.Added.Count) == 1, "the same image was imported twice concurrently");
    }
    private static void DisposeDuringInitialization()
    {
        var root = Program.NewDirectory();
        var account = Path.Combine(Program.NewDirectory(), "123456789");
        Directory.CreateDirectory(Path.Combine(account, "nt_qq"));
        File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(new AppConfig { QqBindings = [new QqBindingInfo { Uin = "123456789", AccountDirectory = account }] }));
        var vm = Create(root);
        vm.Dispose();
        Program.RunAsync(vm.Initialization);
        var services = (System.Collections.IDictionary)typeof(MainViewModel).GetField("_qqServices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
        Program.Require(services.Count == 0, "closing the app still created a late mirror watcher");
    }
}
