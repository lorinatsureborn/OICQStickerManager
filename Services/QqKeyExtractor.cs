using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OICQStickerManager.Services;

/// <summary>Runs a pinned third-party key helper in an owned, time-bounded process.</summary>
public static class QqKeyExtractor
{
    private const string Commit = "b402e50133550efdff704e5a9921efbea5152bc1";
    private const string ScriptSha256 = "99306030F51A2264C0EFFDA6D395A969EC1B00CFE1BF262286769FEC0A78474A";
    private const string ScriptUrl = "https://raw.githubusercontent.com/QQBackup/qq-win-db-key/" + Commit + "/scripts/windows/ntqq/windows_ntqq_get_key.ps1";
    private static readonly SemaphoreSlim ScriptGate = new(1, 1);
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private static readonly Regex KeyPattern = new(@"加密密钥\s*[:：]\s*([!-~]{16})(?![!-~])", RegexOptions.Compiled);
    public static string LogPath => Path.Combine(Path.GetTempPath(), "asuka-keyflow.log");
    public static string ScriptPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Asuka", "KeyHelper", Commit, "helper.ps1");

    internal static bool IsTrustedScript(byte[] bytes) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(bytes), Convert.FromHexString(ScriptSha256));

    public static async Task<bool> EnsureScriptAsync(CancellationToken ct = default)
    {
        await ScriptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(ScriptPath) && IsTrustedScript(await File.ReadAllBytesAsync(ScriptPath, ct).ConfigureAwait(false))) return true;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var bytes = await http.GetByteArrayAsync(ScriptUrl, ct).ConfigureAwait(false);
            if (!IsTrustedScript(bytes)) { AppendLog("helper digest verification failed"); return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath)!);
            await File.WriteAllBytesAsync(ScriptPath + ".tmp", bytes, ct).ConfigureAwait(false);
            File.Move(ScriptPath + ".tmp", ScriptPath, overwrite: true);
            AppendLog("pinned helper verified");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppendLog("helper download failed: " + ex.GetType().Name); return false; }
        finally { ScriptGate.Release(); }
    }

    private static readonly (string Marker, string Phase)[] Phases =
    [
        ("正在自动检测已安装的QQ", "正在检测本机 QQ 安装信息"),
        ("目标字符串 RVA", "正在分析 QQ 模块定位密钥函数（约 1-2 分钟）"),
        ("=== 动态调试QQ进程 ===", "分析完成，正在拉起 QQ 登录窗口"),
        ("QQ 进程已启动", "QQ 登录窗口已拉起，正在初始化"),
        ("请在 QQ 窗口中登录", "请在弹出的 QQ 窗口中登录账号"),
        ("=== 最终结果 ===", "密钥已捕获，正在清理读取助手"),
    ];

    public static async Task<string?> RunScriptAndExtractAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        string? runDirectory = null;
        try
        {
            var installation = await Task.Run(QqKeyInstallation.Discover, ct).ConfigureAwait(false);
            if (!await EnsureScriptAsync(ct).ConfigureAwait(false)) return null;
            var bytes = await File.ReadAllBytesAsync(ScriptPath, ct).ConfigureAwait(false);
            if (!IsTrustedScript(bytes)) return null;
            runDirectory = Path.Combine(Path.GetTempPath(), "asuka-key-helper", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            var runScript = Path.Combine(runDirectory, "helper.ps1");
            // PowerShell 5.1 otherwise interprets UTF-8 source without BOM using the locale code page.
            await File.WriteAllTextAsync(runScript, Encoding.UTF8.GetString(bytes), new UTF8Encoding(true), ct).ConfigureAwait(false);
            var startInfo = CreateHelperStartInfo(runScript, installation.ExecutablePath, installation.WrapperPath);
            var gate = new object();
            string? key = null;
            var recoveryPath = Path.Combine(AppDataDirectory.Root, "key-recovery.dpapi");
            var result = await ChildProcessRunner.RunAsync(startInfo, TimeSpan.FromMinutes(5), line =>
            {
                lock (gate)
                {
                    var candidate = TryExtractKey(line);
                    if (candidate != null)
                    {
                        key = candidate;
                        try
                        {
                            SaveRecoveryKey(Path.GetDirectoryName(recoveryPath)!, candidate);
                        }
                        catch { /* Recovery is best effort; the live result is retained. */ }
                        onProgress?.Invoke("密钥已捕获，正在清理读取助手");
                    }
                    else
                        foreach (var (marker, phase) in Phases)
                            if (line.Contains(marker, StringComparison.Ordinal)) onProgress?.Invoke(phase);
                }
            }, ct).ConfigureAwait(false);
            AppendLog($"helper completed: exit={result.ExitCode}, timeout={result.TimedOut}, captured={key != null}");
            return key;
        }
        catch (OperationCanceledException) { AppendLog("helper cancelled and cleaned up"); return null; }
        catch (Exception ex) { AppendLog("helper failed: " + ex.GetType().Name); return null; }
        finally
        {
            if (runDirectory != null) try { Directory.Delete(runDirectory, recursive: true); } catch { }
            RunGate.Release();
        }
    }

    internal static ProcessStartInfo CreateHelperStartInfo(string script, string executable, string wrapper)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        // Inline invocation avoids the helper's PowerShell 5.1 reload/exit branch. The verified script
        // analyzes our explicit module; its debugger receives the matching executable without registry guessing.
        string command = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding; $OutputEncoding = [Console]::OutputEncoding; "
            + "$analysis = . ([scriptblock]::Create([System.IO.File]::ReadAllText(" + Literal(script) + ", [System.Text.Encoding]::UTF8)))"
            + " -WrapperNodePath " + Literal(wrapper) + " -NoDebugForKey; "
            + "Add-Type -TypeDefinition $DebugApiCode -Language CSharp; "
            + "Write-Host '=== 动态调试QQ进程 ==='; "
            + "$progress = [Action[string]] { param($message) Write-Host $message }; "
            + "$extractor = New-Object DebugApi.KeyExtractor(" + Literal(executable) + ", [uint64]$analysis.FunctionRVA, $progress, $null); "
            + "$key = $extractor.ExtractKey(); if ($key) { Write-Output ('加密密钥: ' + $key) } else { exit 1 }";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            info.ArgumentList.Add(argument);
        return info;
    }

    internal static string? ReadRecoveredKey(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, "key-recovery.dpapi");
            if (File.Exists(path)) return ProtectedSecret.Unprotect(File.ReadAllText(path));
        }
        catch { }
        return null;
    }

    internal static void SaveRecoveryKey(string dataRoot, string key)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "key-recovery.dpapi");
        File.WriteAllText(path + ".tmp", ProtectedSecret.Protect(key));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string? TryExtractKey(string output)
    {
        var match = KeyPattern.Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void AppendLog(string status)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {status}\n"); } catch { }
    }
}
