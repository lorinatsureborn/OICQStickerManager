using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace OICQStickerManager.Services;

/// <summary>
/// NTQQ 数据库密钥获取（2026-09-30 定案）：
/// 纯内存启发式扫描已在本机证伪（密钥在运行时栈/堆，模块段扫描拿不到且 O(n·m) 不可用）。
/// 采用**引导官方脚本**路线：用户开启深度同步时，下载（或使用内置指引）QQBackup/qq-win-db-key
/// 的 windows_ntqq_get_key.ps1 —— 它静态分析 wrapper.node 定位密钥函数，以 DEBUG_ONLY_THIS_PROCESS
/// 启动调试版 QQ，用户在新窗口登录瞬间断点命中输出 16 字符密钥（常规 QQ 不受影响，脚本自行清理调试进程）。
/// 密钥持久化在 config（QqDbKey），QQ 大版本更新可能失效——届时重新引导一次。
/// 失败一律良性降级：QqDeepSyncService 回退到"目录镜像模式"。
/// </summary>
public static class QqKeyExtractor
{
    private const string ScriptUrl =
        "https://raw.githubusercontent.com/QQBackup/qq-win-db-key/master/scripts/windows/ntqq/windows_ntqq_get_key.ps1";

    /// <summary>密钥形态：16 字符可见 ASCII（形如 "Xq7_mLp2=Az9Kd3R"，示例为虚构）。</summary>
    private static readonly Regex KeyPattern = new(@"加密密钥\s*[:：]\s*([!-~]{16})", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>兜底：标签被编码问题弄乱时，抓"冒号收尾行的 16 位可见串"（须含符号，排除普通英文单词）。</summary>
    private static readonly Regex LooseKeyPattern = new(@":\s*([!-~]{16})\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>脚本完整输出落盘（含退出码/stderr），密钥读取异常时可事后诊断。</summary>
    public static string LogPath => Path.Combine(Path.GetTempPath(), "asuka-keyflow.log");

    /// <summary>脚本 stdout 的实时落盘文件：经 cmd 重定向由子进程直接写盘，
    /// 应用中途退出密钥也不丢（2026-09-30 实测：应用重启导致管道里的密钥随风而去）。</summary>
    public static string OutputCapturePath => Path.Combine(Path.GetTempPath(), "asuka-keyflow-output.txt");

    /// <summary>PS 5.1 子进程的管道输出用系统 OEM 码页（中文系统=CP936/GBK）；
    /// 曾用 UTF-8 解码，"加密密钥:"标签变乱码致正则永失（2026-09-30 用户实测登录后抓不到的根因）。
    /// v3 经 cmd 重定向到文件后由我们自己按 GBK 读回，码页语义不变。</summary>
    private static readonly Encoding ConsoleEncoding = InitConsoleEncoding();

    private static Encoding InitConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch { return Encoding.UTF8; }
    }

    public static string ScriptPath => Path.Combine(Path.GetTempPath(), "asuka-qq-get-key.ps1");

    /// <summary>下载官方脚本（幂等）。返回 false = 网络失败。</summary>
    public static async Task<bool> EnsureScriptAsync()
    {
        if (File.Exists(ScriptPath) && new FileInfo(ScriptPath).Length > 10_000) return true;
        AppendLog("official script not cached, downloading...");
        try
        {
            using var http = new HttpClient();
            var content = await http.GetStringAsync(ScriptUrl);
            if (content.Length < 10_000 || !content.Contains("nt_sqlite3_key_v2"))
            {
                AppendLog($"download failed: length={content.Length}, contains-marker={content.Contains("nt_sqlite3_key_v2")}");
                return false;
            }
            await File.WriteAllTextAsync(ScriptPath, content);
            AppendLog($"downloaded ok ({content.Length} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("download exception: " + ex.Message);
            return false;
        }
    }

    /// <summary>脚本输出特征 → 用户可读阶段（特征串与官方 ps1 的 Write-Host 文案逐字对齐，GBK 解码后匹配）。</summary>
    private static readonly (string Marker, string Phase)[] Phases =
    {
        ("正在自动检测已安装的QQ", "正在检测本机 QQ 安装信息…"),
        ("目标字符串 RVA", "正在分析 QQ 模块定位密钥函数（约 1-2 分钟）…"),
        ("=== 动态调试QQ进程 ===", "分析完成，正在拉起 QQ 登录窗口…"),
        ("QQ 进程已启动", "QQ 登录窗口已拉起，正在初始化…"),
        ("请在 QQ 窗口中登录", "请在弹出的 QQ 窗口登录你的账号…"),
        ("已请求目标进程终止", "密钥已捕获，调试 QQ 窗口已关闭，正在解析收尾…"),
        ("=== 最终结果 ===", "密钥已捕获，正在解析收尾…"),
    };

    /// <summary>从脚本输出里解析当前所处阶段（取最后一个命中的特征）。</summary>
    private static string? DetectPhase(string output)
    {
        string? current = null;
        foreach (var (marker, phase) in Phases)
        {
            if (output.Contains(marker)) current = phase;
        }
        return current;
    }

    /// <summary>
    /// 运行官方脚本提取密钥。脚本会启动调试版 QQ 并等待用户在新窗口登录（最长 5 分钟超时）。
    /// stdout 经 cmd 重定向直落 OutputCapturePath（应用中途退出密钥也不丢），并每秒轮询：
    /// 日志里一出现密钥就立即返回（不等脚本清理收尾），同时按脚本输出特征回报当前阶段。
    /// 返回 null = 用户取消/超时/脚本失败；完整输出已落盘 LogPath 供诊断。
    /// </summary>
    public static async Task<string?> RunScriptAndExtractAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!await EnsureScriptAsync()) return null;

        try { if (File.Exists(OutputCapturePath)) File.Delete(OutputCapturePath); } catch { }
        AppendLog($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} launching script ----");

        // 经 cmd 重定向：子进程直接写文件，绕开应用侧管道（应用死亡=密钥丢失的教训）
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c powershell -NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\" > \"{OutputCapturePath}\" 2>&1",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process == null) { AppendLog("Process.Start returned null"); return null; }
        AppendLog($"script process started pid={process.Id}");

        string? captured = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited) break;

            // 轮询落盘输出：密钥一出现立即返回（不等脚本收尾清理）；同时按特征回报阶段
            if (File.Exists(OutputCapturePath))
            {
                try
                {
                    var soFar = File.ReadAllText(OutputCapturePath, ConsoleEncoding);
                    captured = TryExtractKey(soFar);
                    if (captured != null)
                    {
                        AppendLog($"key captured from live output after {sw.Elapsed.TotalSeconds:F0}s");
                        onProgress?.Invoke("密钥已捕获，正在解析保存…");
                        return captured;
                    }
                    var phase = DetectPhase(soFar);
                    if (phase != null)
                        onProgress?.Invoke($"{phase}（已运行 {sw.Elapsed.TotalSeconds:F0}s）");
                }
                catch { /* 文件可能正被写入，下轮再试 */ }
            }

            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }

        await Task.Delay(500); // 给子进程的文件写入留一点收尾时间
        var output = "";
        try { if (File.Exists(OutputCapturePath)) output = File.ReadAllText(OutputCapturePath, ConsoleEncoding); } catch { }
        AppendLog($"script exited code={process.ExitCode} after {sw.Elapsed.TotalSeconds:F0}s, output {output.Length} chars");
        AppendLog(TrimForLog(output));

        var key = TryExtractKey(output);
        if (key == null)
        {
            // 兜底：即使标签乱码，密钥本体（16 位可见 ASCII、必含符号）也该能抓到
            foreach (Match m in LooseKeyPattern.Matches(output))
            {
                var candidate = m.Groups[1].Value;
                if (candidate.Any(c => !char.IsLetterOrDigit(c))) { key = candidate; break; }
            }
        }
        return key;
    }

    private static string? TryExtractKey(string output)
    {
        var match = KeyPattern.Match(output);
        if (match.Success) return match.Groups[1].Value;
        foreach (Match m in LooseKeyPattern.Matches(output))
        {
            var candidate = m.Groups[1].Value;
            if (candidate.Any(c => !char.IsLetterOrDigit(c))) return candidate;
        }
        return null;
    }

    private static void AppendLog(string content)
    {
        try { File.AppendAllText(LogPath, content + "\n"); } catch { /* 日志失败不影响主流程 */ }
    }

    private static string TrimForLog(string s) =>
        s.Length <= 8000 ? s : s[..2000] + "\n…(中间省略)…\n" + s[^2000..];
}
