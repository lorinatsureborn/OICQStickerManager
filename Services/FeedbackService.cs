using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace OICQStickerManager.Services;

/// <summary>
/// 一键反馈：组装 GitHub issue 预填链接（title + 正文含自动收集的诊断信息），
/// 用户在浏览器里补一句描述点提交即可。诊断日志先做脱敏与精选：
/// - 只取 asuka-watcher.log 中诊断/降级/错误相关行（面板共存适配的关键现场）
/// - 聊天窗口标题（可能含联系人/群名）打码；控件名保留（无隐私、有诊断价值）
/// 不走 GitHub API：那需要内嵌 token，开源仓库里等于公开授权滥用。
/// </summary>
public static class FeedbackService
{
    public const string IssueNewUrl = "https://github.com/lorinatsureborn/OICQStickerManager/issues/new";

    private const int BodyBudget = 3000;   // 正文上限（URL 编码前），保证浏览器与 GitHub 都吃得下
    private const int LogBudget = 2200;    // 日志段预算

    /// <summary>组装预填 issue 链接。truncated = 日志因超长被截断。</summary>
    public static (string Url, bool Truncated) BuildIssueUrl(string hotkeyDisplay)
    {
        var body = BuildBody(hotkeyDisplay, out var truncated);
        var title = $"反馈：飞鸟 v{AppVersion()}";
        var sep = IssueNewUrl.Contains('?') ? "&" : "?";
        return (IssueNewUrl + sep + "title=" + Uri.EscapeDataString(title) +
                "&body=" + Uri.EscapeDataString(body), truncated);
    }

    private static string AppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未知";

    private static string BuildBody(string hotkeyDisplay, out bool truncated)
    {
        truncated = false;
        var log = Task.Run(() => SelectWatcherLog()).GetAwaiter().GetResult();
        var sb = new StringBuilder();
        sb.AppendLine("### 问题描述");
        sb.AppendLine();
        sb.AppendLine("<!-- 在这里描述你遇到的问题：做了什么操作、预期是什么、实际发生了什么 -->");
        sb.AppendLine();
        sb.AppendLine("### 环境信息（自动收集）");
        sb.AppendLine($"- 飞鸟版本：v{AppVersion()}");
        sb.AppendLine($"- 系统：{Environment.OSVersion.VersionString}");
        sb.AppendLine($"- QQ 版本：{GetQqVersion()}");
        sb.AppendLine($"- 快捷面板热键：{hotkeyDisplay}");
        sb.AppendLine();
        sb.AppendLine("### 诊断日志（自动附带，已脱敏）");
        sb.AppendLine("<details><summary>asuka-watcher.log 精选</summary>");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(log);
        sb.AppendLine("```");
        sb.AppendLine("</details>");

        if (sb.Length > BodyBudget)
        {
            truncated = true;
            return sb.ToString(0, BodyBudget) + "\n…（内容过长已截断，完整日志见 %TEMP%\\asuka-watcher.log）";
        }
        return sb.ToString();
    }

    /// <summary>当前运行的 QQ 版本号（如 9.9.36-53644）；取不到给明确说法。</summary>
    internal static string GetQqVersion()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("QQ"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    var v = vi.ProductVersion;
                    if (string.IsNullOrEmpty(v)) v = vi.FileVersion;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { /* MainModule 跨位数/权限失败，试下一个进程 */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return "未检测到运行中的 QQ";
    }

    /// <summary>watcher 日志精选：面板检测/降级/错误/诊断 dump 相关行，脱敏后带时间轴。</summary>
    private static string SelectWatcherLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "asuka-watcher.log");
        try
        {
            if (!File.Exists(path)) return "（本机没有 asuka-watcher.log：共存触发器未开启或尚未产生记录）";

            string[] lines;
            using (var reader = new StreamReader(path))
            {
                var all = new List<string>();
                string? line;
                while ((line = reader.ReadLine()) != null) all.Add(line);
                lines = all.ToArray();
            }

            string[] keywords =
            {
                "diag ", "CANDIDATE", "FAILED", "fail", "error", "degraded",
                "aborted", "APPEARED", "DISAPPEARED", "optimistic",
            };
            var picked = new List<string>();
            // 大日志只回扫尾部 8000 行，避免反馈动作卡顿
            for (int i = Math.Max(0, lines.Length - 8000); i < lines.Length; i++)
            {
                var l = lines[i];
                foreach (var k in keywords)
                {
                    if (l.Contains(k, StringComparison.OrdinalIgnoreCase))
                    {
                        picked.Add(l);
                        break;
                    }
                }
            }
            if (picked.Count == 0)
                picked = lines.TakeLast(25).ToList(); // 无命中给个时间轴尾巴，证明日志在滚
            if (picked.Count > 160)
                picked = picked.TakeLast(160).ToList();

            var text = string.Join("\n", picked);
            // 聊天窗口标题可能带联系人/群名：diag window 行的 name 打码（保留前 2 字符示意）
            text = Regex.Replace(text,
                @"(diag window[^\n]*?name="")(.*?)("")",
                m => m.Groups[1].Value + Redact(m.Groups[2].Value) + "\"");
            if (text.Length > LogBudget)
                text = "…（前段略）\n" + text[^LogBudget..];
            return text;
        }
        catch (Exception ex)
        {
            return "（日志读取失败：" + ex.Message + "）";
        }
    }

    private static string Redact(string s)
        => string.IsNullOrEmpty(s) ? "（空）" : s.Length <= 2 ? s + "***" : s[..2] + "***";
}
