using OICQStickerManager.Models;
using OICQStickerManager.Services;

internal static class SyncTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Failed QQ adoption preserves its mirror item", () => Program.RunAsync(FailedAdoptionPreservesItem())),
        ("QQ re-favorite clears its stale orphan flag", () => Program.RunAsync(ReFavoriteClearsFlag())),
        ("Unreadable later account does not partially reconcile earlier accounts", () => Program.RunAsync(AccountsAreReadBeforeChanges())),
        ("A database authentication failure is distinguished from a missing index", () => Program.RunAsync(AuthenticationFailure())),
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
}
