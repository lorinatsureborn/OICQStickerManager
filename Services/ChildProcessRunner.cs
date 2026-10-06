using System.Diagnostics;
using System.Text;

namespace OICQStickerManager.Services;

internal sealed record ChildProcessResult(int ExitCode, bool TimedOut);

internal static class ChildProcessRunner
{
    internal static async Task<ChildProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout,
        Action<string> output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = startInfo.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动密钥读取助手");
        OwnedProcessJob? job = null;
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            job = OwnedProcessJob.Attach(process);
            stdout = PumpAsync(process.StandardOutput, output);
            stderr = PumpAsync(process.StandardError, output);
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                job.Dispose();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new(process.ExitCode, true);
            }
            // Children can inherit these pipes even after the parent exits.
            job.Dispose();
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode, false);
        }
        finally
        {
            job?.Dispose();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(System.IO.StreamReader reader, Action<string> output)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            try { output(line); } catch { /* A progress observer cannot strand the owned process. */ }
    }
}
