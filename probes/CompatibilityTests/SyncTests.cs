using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;
using System.IO;
using System.Reflection;

internal static class SyncTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Failed QQ adoption preserves its mirror item", () => Program.RunAsync(FailedAdoptionPreservesItem())),
        ("QQ re-favorite clears its stale orphan flag", () => Program.RunAsync(ReFavoriteClearsFlag())),
        ("Unreadable later account does not partially reconcile earlier accounts", () => Program.RunAsync(AccountsAreReadBeforeChanges())),
        ("A database authentication failure is distinguished from a missing index", () => Program.RunAsync(AuthenticationFailure())),
        ("Batch orphan verification isolates unreadable accounts", () => Program.RunAsync(BatchAccountVerification())),
        ("Batch orphan verification preserves a stale flag after authentication failure", () => Program.RunAsync(BatchAuthenticationFailure())),
        ("QQ names distinguish unverified images, favorites and cache remnants", StatusLabels),
        ("QQ footer counts favorites, cache remnants and unverified images separately", StatusCounts),
        ("QQ full rescan preserves verified favorite classifications", () => Program.RunAsync(RescanPreservesStatus())),
        ("QQ same-file rewrite preserves verified favorite classifications", () => Program.RunAsync(RewritePreservesStatus())),
    ];
    private const string Md5 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static QqEmojiService Service(string uin)
    {
        var service = new QqEmojiService(uin, Program.NewDirectory(), action => action());
        service.Mirror.Add(new QqStickerModel { Uin = uin, Md5 = Md5 });
        return service;
    }
    private static Task<(HashSet<string>?, bool)> Index(params string[] md5s) => Task.FromResult<(HashSet<string>?, bool)>((new HashSet<string>(md5s), false));

    private static async Task FailedAdoptionPreservesItem()
    {
        using var service = Service("one");
        var sync = new QqDeepSyncService("test", _ => service, _ => Task.FromResult(false), _ => Index());
        int result = await sync.ReconcileAllAsync(["one"], QqSyncStrategy.Adopt);
        Program.Require(service.Mirror.Count == 1 && result < 0, "failed copy was removed as if it were already in the library");
    }

    private static async Task ReFavoriteClearsFlag()
    {
        using var service = Service("one");
        service.Mirror[0].IsOrphaned = true;
        var sync = new QqDeepSyncService("test", _ => service, _ => Task.FromResult(false), _ => Index(Md5));
        await sync.ReconcileAllAsync(["one"], QqSyncStrategy.Mark);
        Program.Require(!service.Mirror[0].IsOrphaned, "re-favorite kept a deletable stale orphan mark");
    }

    private static async Task AccountsAreReadBeforeChanges()
    {
        using var first = Service("one");
        using var second = Service("two");
        var sync = new QqDeepSyncService("test", uin => uin == "one" ? first : second, _ => Task.FromResult(false),
            uin => uin == "one" ? Index() : Task.FromResult<(HashSet<string>?, bool)>((null, false)));
        int result = await sync.ReconcileAllAsync(["one", "two"], QqSyncStrategy.Remove);
        Program.Require(result < 0 && first.Mirror.Count == 1 && second.Mirror.Count == 1, "a later read failure left earlier accounts partially changed");
    }
    private static async Task AuthenticationFailure()
    {
        var account = System.IO.Path.Combine(Program.NewDirectory(), "123456789", "nt_qq");
        var ori = System.IO.Path.Combine(account, "nt_data", "Emoji", "personal_emoji", "Ori");
        System.IO.Directory.CreateDirectory(ori);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(account, "nt_db"));
        await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(account, "nt_db", "emoji.db"), CipherTests.Encrypt(CipherTests.Plain()));
        using var service = new QqEmojiService("123456789", ori, action => action());
        service.Mirror.Add(new QqStickerModel { Md5 = Md5 });
        var sync = new QqDeepSyncService("incorrect-key", _ => service, _ => Task.FromResult(false));
        int result = await sync.ReconcileAllAsync(["123456789"], QqSyncStrategy.Remove);
        Program.Require(result == QqDeepSyncService.AuthenticationFailed && service.Mirror.Count == 1, "authentication failure was confused with an empty or unavailable index");
    }
    private static async Task BatchAccountVerification()
    {
        using var first = Service("one"); using var second = Service("two");
        int reads = 0;
        var sync = new QqDeepSyncService("test", uin => uin == "one" ? first : second, _ => Task.FromResult(false),
            uin => { reads++; return uin == "one" ? Index() : Task.FromResult<(HashSet<string>?, bool)>((null, false)); });
        var confirmed = await sync.ConfirmOrphansAsync(first.Mirror.Concat(second.Mirror));
        Program.Require(reads == 2 && confirmed.SequenceEqual(first.Mirror), "a failed account was treated as an empty index or contaminated another account");
    }
    private static async Task BatchAuthenticationFailure()
    {
        using var service = Service("one"); service.Mirror[0].IsOrphaned = true;
        var sync = new QqDeepSyncService("test", _ => service, _ => Task.FromResult(false),
            _ => Task.FromException<(HashSet<string>?, bool)>(new System.Security.Cryptography.CryptographicException("synthetic")));
        var confirmed = await sync.ConfirmOrphansAsync(service.Mirror);
        Program.Require(confirmed.Count == 0 && service.Mirror.Count == 1, "a stale orphan flag bypassed an authentication failure");
    }
    private static void StatusLabels()
    {
        var image = new QqStickerModel { Md5 = Md5 };
        Program.Require(image.DisplayName.Contains("待核验"), "an unverified cache file was labeled as a QQ favorite");
        var changed = new List<string?>(); image.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        image.IsOrphaned = true;
        Program.Require(image.DisplayName.Contains("缓存残留") && changed.Contains(nameof(QqStickerModel.DisplayName)), "removed favorite retained the QQ favorite name or did not notify its UI");
        image.IsOrphaned = false;
        Program.Require(image.DisplayName.StartsWith("QQ收藏表情"), "confirmed favorite was not labeled correctly");
    }
    private static void StatusCounts()
    {
        var vm = LibraryTests.Create(Program.NewDirectory()); Program.RunAsync(vm.Initialization);
        var service = BatchManagementTests.AddQqService(vm);
        service.Mirror.Add(new QqStickerModel { IsOrphaned = false });
        service.Mirror.Add(new QqStickerModel { IsOrphaned = true });
        service.Mirror.Add(new QqStickerModel());
        vm.SelectedTab = "qq:synthetic";
        Program.Require(vm.StickerCountText.Contains("收藏 1") && vm.StickerCountText.Contains("缓存残留 1") && vm.StickerCountText.Contains("待核验 1"),
            "QQ footer described removed or unverified cache files as current favorites");
    }
    private static string WriteImage(string directory)
    {
        var path = Path.Combine(directory, Md5 + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
        return path;
    }
    private static async Task RescanPreservesStatus()
    {
        var directory = Program.NewDirectory(); WriteImage(directory);
        using var service = new QqEmojiService("rescan", directory, action => action());
        await service.RescanAsync(); service.Mirror[0].IsOrphaned = true;
        await service.RescanAsync();
        Program.Require(service.Mirror[0].IsOrphaned && service.Mirror[0].DisplayName.Contains("缓存残留"), "directory rescan erased a verified cache-remnant classification");
        service.Mirror[0].IsOrphaned = false;
        await service.RescanAsync();
        Program.Require(service.Mirror[0].DisplayName.StartsWith("QQ收藏表情"), "directory rescan erased a verified current-favorite classification");
    }
    private static async Task RewritePreservesStatus()
    {
        var directory = Program.NewDirectory(); var path = WriteImage(directory);
        using var service = new QqEmojiService("rewrite", directory, action => action());
        await service.RescanAsync(); service.Mirror[0].IsOrphaned = true;
        await (Task)typeof(QqEmojiService).GetMethod("ApplyOneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [path])!;
        Program.Require(service.Mirror[0].IsOrphaned, "same-file rewrite erased the existing cache-remnant classification");
    }
}
