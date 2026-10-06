using System.Diagnostics;
using System.IO;
using OICQStickerManager.Services;

internal static class SecurityTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Saved QQ keys are protected for the current Windows user", ProtectedKeyRoundTrip),
        ("Configuration never serializes a plaintext QQ key", ConfigDoesNotExposeKey),
        ("Owned key helper exits on timeout", () => Program.RunAsync(HelperTimeout())),
        ("Owned key helper exits on cancellation", () => Program.RunAsync(HelperCancellation())),
        ("Key helper rejects modified scripts regardless of length", RejectModifiedScript),
        ("Key parsing ignores unrelated 16-character output", UnrelatedOutputIsNotAKey),
        ("Key helper cleanup closes inherited child output pipes", () => Program.RunAsync(DescendantPipeCleanup())),
    ];
    private const string Key = "audit-key-only!1";
    private static void ProtectedKeyRoundTrip()
    {
        var protectedValue = ProtectedSecret.Protect(Key);
        Program.Require(protectedValue != Key && ProtectedSecret.Unprotect(protectedValue) == Key, "key was stored in plaintext or failed round trip");
        Program.Require(ProtectedSecret.Unprotect(Key) == Key, "legacy plaintext key migration was broken");
    }
    private static void ConfigDoesNotExposeKey()
    {
        var root = Program.NewDirectory();
        File.WriteAllText(Path.Combine(root, "config.json"), System.Text.Json.JsonSerializer.Serialize(new OICQStickerManager.Models.AppConfig { QqDbKey = Key }));
        var vm = LibraryTests.Create(root);
        Program.RunAsync(Task.Delay(150));
        vm.CompleteKeyAcquisition(Key);
        vm.FlushPendingConfigSave();
        Program.Require(!File.ReadAllText(Path.Combine(root, "config.json")).Contains(Key), "configuration exposed the raw QQ key");
        Program.Require(!File.ReadAllText(Path.Combine(root, "config.json.bak")).Contains(Key), "legacy key leaked through the configuration backup");
    }
    private static ProcessStartInfo Helper()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--wait-helper"); return info;
    }
    private static async Task HelperTimeout()
    {
        int pid = 0;
        var result = await ChildProcessRunner.RunAsync(Helper(), TimeSpan.FromMilliseconds(600), line => { if (int.TryParse(line, out int value)) pid = value; });
        Program.Require(result.TimedOut && pid != 0 && Exited(pid), "timeout returned without stopping its owned helper");
    }
    private static async Task HelperCancellation()
    {
        int pid = 0;
        using var cancellation = new CancellationTokenSource(600);
        bool cancelled = false;
        try { await ChildProcessRunner.RunAsync(Helper(), TimeSpan.FromSeconds(10), line => { if (int.TryParse(line, out int value)) pid = value; }, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Program.Require(cancelled && pid != 0 && Exited(pid), "cancellation returned without stopping its owned helper");
    }
    private static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static void RejectModifiedScript()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("nt_sqlite3_key_v2" + new string('x', 44_705));
        Program.Require(!QqKeyExtractor.IsTrustedScript(bytes), "a long modified script was accepted without its pinned digest");
    }
    private static void UnrelatedOutputIsNotAKey()
    {
        var parse = typeof(QqKeyExtractor).GetMethod("TryExtractKey", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Program.Require(parse.Invoke(null, new object[] { "analysis: abcdefg_12345678" }) == null, "unrelated output was treated as a database key");
        Program.Require((string?)parse.Invoke(null, new object[] { "加密密钥: " + Key }) == Key, "the key label did not parse");
        Program.Require(parse.Invoke(null, new object[] { "加密密钥: " + Key + "x" }) == null, "an oversized key was truncated into a false success");
    }
    private static async Task DescendantPipeCleanup()
    {
        int childPid = 0;
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--pipe-parent");
        using var cancellation = new CancellationTokenSource();
        var run = ChildProcessRunner.RunAsync(info, TimeSpan.FromMilliseconds(600), line =>
        { if (line.StartsWith("descendant:")) int.TryParse(line[11..], out childPid); }, cancellation.Token);
        try
        {
            Program.Require(await Task.WhenAny(run, Task.Delay(1500)) == run, "parent exit left inherited output pipes open indefinitely");
            await run;
            Program.Require(childPid != 0 && Exited(childPid), "an owned descendant survived helper cleanup");
        }
        finally
        {
            cancellation.Cancel();
            if (childPid != 0 && !Exited(childPid))
            {
                using var child = Process.GetProcessById(childPid);
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            try { await run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
    }
}
