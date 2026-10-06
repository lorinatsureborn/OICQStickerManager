using System.Diagnostics;
using System.IO;
using System.Text;
using OICQStickerManager.Services;

internal static class KeyInstallationTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Key helper resolves the active launcher version among multiple directories", ActiveVersion),
        ("Key helper rejects a running build that differs from the launcher's next version", LoadedVersion),
        ("Key helper rejects a missing active version instead of guessing", MissingVersion),
        ("Key helper rejects a version that escapes the installation", EscapingVersion),
        ("Key helper retains single-version fallback for older launchers", SingleVersion),
        ("Key helper bootstrap bypasses registry detection and quotes paths safely", () => Program.RunAsync(Bootstrap(), 20000)),
    ];

    private static (string Root, string Exe) Installation(params string[] versions)
    {
        var root = Program.NewDirectory();
        var exe = Path.Combine(root, "QQ.exe");
        File.WriteAllText(exe, "fixture");
        foreach (var version in versions)
        {
            var module = Module(root, version);
            Directory.CreateDirectory(Path.GetDirectoryName(module)!);
            File.WriteAllText(module, "fixture");
        }
        return (root, exe);
    }
    private static string Module(string root, string version) => Path.Combine(root, "versions", version, "resources", "app", "wrapper.node");
    private static void Config(string root, string version) => File.WriteAllText(Path.Combine(root, "versions", "config.json"),
        System.Text.Json.JsonSerializer.Serialize(new { curVersion = version }));
    private static QqKeyInstallation Resolve(string exe, string? loaded = null) => QqKeyInstallation.Resolve(exe, loaded);
    private static string Wrapper(QqKeyInstallation result) => result.WrapperPath;
    private static void ActiveVersion()
    {
        var (root, exe) = Installation("9.9.19-35469", "9.9.36-53644");
        Config(root, "9.9.19-35469");
        Program.Require(Wrapper(Resolve(exe)) == Module(root, "9.9.19-35469"), "active version was replaced by a newer directory");
    }
    private static void LoadedVersion()
    {
        var (root, exe) = Installation("9.9.19-35469", "9.9.36-53644");
        Config(root, "9.9.36-53644");
        MustReject(exe, Module(root, "9.9.19-35469"));
    }
    private static void MustReject(string exe, string? loaded = null)
    {
        try { Resolve(exe, loaded); }
        catch (InvalidDataException) { return; }
        throw new Exception("an invalid active version was silently replaced");
    }
    private static void MissingVersion()
    {
        var (root, exe) = Installation("9.9.36-53644");
        Config(root, "9.9.19-35469");
        MustReject(exe);
    }
    private static void EscapingVersion()
    {
        var (root, exe) = Installation("9.9.19-35469");
        Config(root, "..\\outside");
        MustReject(exe);
    }
    private static void SingleVersion()
    {
        var (root, exe) = Installation("9.9.19-35469");
        Program.Require(Wrapper(Resolve(exe)) == Module(root, "9.9.19-35469"), "legacy single-version installation was rejected");
    }
    private static async Task Bootstrap()
    {
        var root = Path.Combine(Program.NewDirectory(), "path ' with spaces 中文");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "helper.ps1");
        var exe = Path.Combine(root, "QQ.exe");
        var wrapper = Path.Combine(root, "wrapper.node");
        File.WriteAllText(script, """
            param([string]$WrapperNodePath, [switch]$NoDebugForKey)
            if ($PSVersionTable.PSVersion.Major -le 5 -and $MyInvocation.MyCommand.CommandType -eq 'ExternalScript') { exit 99 }
            function Get-InstalledQQInfo { throw 'registry fallback must not run' }
            if (-not $NoDebugForKey -or $WrapperNodePath -ne $env:ASUKA_FIXTURE_WRAPPER) { throw 'wrong analysis target' }
            $DebugApiCode = @'
            using System;
            namespace DebugApi {
                public class KeyExtractor {
                    public KeyExtractor(string exe, ulong rva, Action<string> log, Action<string> verbose) {
                        if (exe != Environment.GetEnvironmentVariable("ASUKA_FIXTURE_EXE") || rva != 4660) throw new Exception("wrong debug target");
                    }
                    public string ExtractKey() { return "1234567890abcdef"; }
                }
            }
            '@
            [pscustomobject]@{ FunctionRVA = [uint64]4660 }
            """, new UTF8Encoding(true));
        var info = QqKeyExtractor.CreateHelperStartInfo(script, exe, wrapper);
        info.Environment["ASUKA_FIXTURE_EXE"] = exe;
        info.Environment["ASUKA_FIXTURE_WRAPPER"] = wrapper;
        var output = new List<string>();
        var result = await ChildProcessRunner.RunAsync(info, TimeSpan.FromSeconds(15), line => { lock (output) output.Add(line); });
        Program.Require(!result.TimedOut && result.ExitCode == 0, "explicit helper invocation failed: " + string.Join("; ", output));
        Program.Require(output.Any(line => line.Contains("1234567890abcdef")), "helper did not return its fixture key");
    }
}
