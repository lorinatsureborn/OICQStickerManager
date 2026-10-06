using OICQStickerManager.Services;
using System.IO;
using System.Reflection;

internal static class CapabilityTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Old and new QQ processes retain separate capabilities", SeparateProcesses),
        ("QQ restart clears the previous process tree latch", RestartClearsLatch),
        ("A slow UIA tree degrades only its own QQ process", SlowTreeIsLocal),
        ("Running QQ module version overrides its stale launcher version", RunningModuleVersion),
        ("Running QQ module wins over a pending launcher upgrade", PendingUpgrade),
        ("QQ child processes resolve the launcher's active application build", ActiveApplicationVersion),
        ("Missing active QQ modules retain the launcher fallback without guessing", InvalidActiveVersion),
        ("Corrected QQ version metadata clears only the version-based legacy decision", CorrectedMetadata),
    ];
    private static void SeparateProcesses()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.19.35469"), new(2, 1, "9.9.21-38711"), new(3, 1, "9.9.36.48000")]);
        Program.Require(capabilities.IsLegacy(1) && capabilities.IsLegacy(2) && !capabilities.IsLegacy(3), "mixed QQ builds shared a legacy decision");
    }
    private static void RestartClearsLatch()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.19.35469")]);
        capabilities.MarkTreeUnavailable(1);
        capabilities.Refresh([new(1, 2, "9.9.36.48000")]);
        Program.Require(!capabilities.IsLegacy(1), "reused PID inherited the previous process latch");
        capabilities.Refresh([]);
        Program.Require(!capabilities.HasLegacy, "exited QQ left stale capabilities");
    }
    private static void SlowTreeIsLocal()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.36.48000"), new(2, 1, "9.9.36.48000")]);
        capabilities.MarkTreeUnavailable(1);
        capabilities.Refresh([new(1, 1, "9.9.36.48000"), new(2, 1, "9.9.36.48000")]);
        Program.Require(capabilities.IsLegacy(1) && !capabilities.IsLegacy(2), "tree failure was lost or spread to another process");
    }

    private static string Resolve(string exe, string? wrapper, string fallback)
    {
        var method = typeof(QqProcessCapabilities).GetMethod("ResolveVersion", BindingFlags.Static | BindingFlags.NonPublic);
        Program.Require(method != null, "QQ compatibility still reads the stale launcher file version");
        return (string)method!.Invoke(null, [exe, wrapper, fallback])!;
    }
    private static (string Exe, string Old, string Current) Installation(string active = "9.9.36-53644")
    {
        var root = Program.NewDirectory();
        var exe = Path.Combine(root, "QQ.exe");
        File.WriteAllText(exe, "fixture");
        string Module(string version)
        {
            var path = Path.Combine(root, "versions", version, "resources", "app", "wrapper.node");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }
        var old = Module("9.9.19-34740");
        var current = Module("9.9.36-53644");
        File.WriteAllText(Path.Combine(root, "versions", "config.json"),
            System.Text.Json.JsonSerializer.Serialize(new { baseVersion = "9.9.19-34740", curVersion = active }));
        return (exe, old, current);
    }
    private static void RunningModuleVersion()
    {
        var files = Installation("9.9.19-34740");
        Program.Require(Resolve(files.Exe, files.Current, "9.9.19.34740") == "9.9.36-53644",
            "actual loaded module was replaced by the old launcher/config version");
    }
    private static void PendingUpgrade()
    {
        var files = Installation();
        Program.Require(Resolve(files.Exe, files.Old, "9.9.19.34740") == "9.9.19-34740",
            "a pending upgrade changed the compatibility of a still-running older QQ");
    }
    private static void ActiveApplicationVersion()
    {
        var files = Installation();
        Program.Require(Resolve(files.Exe, null, "9.9.19.34740") == "9.9.36-53644",
            "renderer processes without wrapper.node kept the stale launcher version");
    }
    private static void InvalidActiveVersion()
    {
        var files = Installation("9.9.99-missing");
        Program.Require(Resolve(files.Exe, null, "9.9.19.34740") == "9.9.19.34740",
            "a missing active module guessed a build from another installed directory");
    }
    private static void CorrectedMetadata()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.19.34740"), new(2, 1, "9.9.19.34740")]);
        capabilities.MarkTreeUnavailable(2);
        capabilities.Refresh([new(1, 1, "9.9.36-53644"), new(2, 1, "9.9.36-53644")]);
        Program.Require(!capabilities.IsLegacy(1) && capabilities.IsLegacy(2),
            "version correction either kept the stale version latch or discarded actual slow-tree evidence");
    }
}
