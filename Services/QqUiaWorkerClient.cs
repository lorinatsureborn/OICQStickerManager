using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OICQStickerManager.Services;

internal sealed class QqUiaWorkerClient(Func<ProcessStartInfo> startInfo, Action<string> receive, Action<string> status) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> _requests = new();
    private Task? _loop;
    private Process? _process;
    private OwnedProcessJob? _job;
    private CancellationTokenSource? _session;
    private long _nextId;
    private int _disposed;
    internal int ProcessId { get { lock (_gate) return _process?.Id ?? 0; } }

    internal void Start()
    {
        lock (_gate)
            if (_disposed == 0 && _loop == null) _loop = Task.Run(RunAsync);
    }

    internal async Task<string?> RequestAsync(QqWorkerFrame request, TimeSpan timeout)
    {
        if (_disposed != 0) return null;
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Id = Interlocked.Increment(ref _nextId);
        Process? process;
        CancellationTokenSource? session;
        lock (_gate) { process = _process; session = _session; }
        if (process == null || session == null) return null;
        _requests[request.Id] = source;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, session.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await _writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            return await source.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { try { session.Cancel(); } catch (ObjectDisposedException) { } return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
        finally { _requests.TryRemove(request.Id, out _); }
    }

    private async Task RunAsync()
    {
        int failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            Process? process = null;
            OwnedProcessJob? job = null;
            Task reader = Task.CompletedTask, stderr = Task.CompletedTask, watchdog = Task.CompletedTask;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            try
            {
                var info = startInfo();
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
                info.StandardInputEncoding = info.StandardOutputEncoding = info.StandardErrorEncoding = new UTF8Encoding(false);
                process = Process.Start(info) ?? throw new IOException("Cannot start UIA worker");
                job = OwnedProcessJob.Attach(process);
                lock (_gate) { _process = process; _job = job; _session = session; }
                Deliver("{\"Kind\":\"reset\"}");
                var leases = new ConcurrentDictionary<long, long>();
                long lastFrame = Environment.TickCount64;
                bool ready = false;
                reader = ReadAsync();
                stderr = process.StandardError.ReadToEndAsync();
                watchdog = Task.Run(async () =>
                {
                    while (!session.IsCancellationRequested)
                    {
                        await Task.Delay(50, session.Token).ConfigureAwait(false);
                        var now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref lastFrame) > (Volatile.Read(ref ready) ? 5000 : 10000)
                            || leases.Values.Any(end => now >= end))
                        {
                            session.Cancel();
                            ReportStatus("QQ 界面探测超时，正在回收并重启隔离进程；快捷键仍可使用");
                        }
                    }
                });
                await process.WaitForExitAsync(session.Token).ConfigureAwait(false);

                async Task ReadAsync()
                {
                    while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (session.IsCancellationRequested || _lifetime.IsCancellationRequested) break;
                        if (line.Length > 65536) { session.Cancel(); break; }
                        QqWorkerFrame? frame;
                        try { frame = JsonSerializer.Deserialize<QqWorkerFrame>(line); }
                        catch (JsonException) { continue; }
                        if (frame == null) continue;
                        Interlocked.Exchange(ref lastFrame, Environment.TickCount64);
                        if (frame.Kind == "ready") Volatile.Write(ref ready, true);
                        if (frame.Kind == "lease-start")
                            leases[frame.Id] = Environment.TickCount64 + Math.Clamp(frame.DeadlineMs, 100, 10000);
                        if (frame.Kind == "lease-end") leases.TryRemove(frame.Id, out _);
                        Deliver(line);
                        if (frame.Kind == "response" && _requests.TryGetValue(frame.Id, out var request)) request.TrySetResult(line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReportStatus("QQ 隔离探测启动失败：" + ex.GetType().Name); }
            finally
            {
                session.Cancel();
                lock (_gate) { _process = null; _job = null; _session = null; }
                job?.Dispose();
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
                    catch (InvalidOperationException) { }
                    try { await Task.WhenAll(reader, stderr, watchdog).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
                    process.Dispose();
                }
                foreach (var request in _requests.Values) request.TrySetResult(null);
                _requests.Clear();
                Deliver("{\"Kind\":\"reset\"}");
            }
            if (_lifetime.IsCancellationRequested) break;
            try { await Task.Delay(Math.Min(5000, 250 * (1 << Math.Min(failures++, 4))), _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Deliver(string line)
    {
        if (_disposed != 0) return;
        try { receive(line); } catch { /* UI observers cannot keep a failed worker alive. */ }
    }

    private void ReportStatus(string message)
    {
        if (_disposed != 0) return;
        try { status(message); } catch { /* Reporting cannot interrupt worker cleanup. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        lock (_gate) { _session?.Cancel(); _job?.Dispose(); }
        try { _loop?.Wait(2000); } catch { }
        // A startup can finish after cancellation; the loop still owns and reaps that process.
    }
}

internal sealed class QqWorkerFrame
{
    public string Kind { get; set; } = "";
    public long Id { get; set; }
    public int DeadlineMs { get; set; }
    public string Command { get; set; } = "";
    public long Hwnd { get; set; }
    public long Generation { get; set; } = -1;
    public bool Value { get; set; }
    public string Text { get; set; } = "";
    public QqWorkerState? State { get; set; }
    public PixelRect? Rect { get; set; }
    public PixelRect? ButtonRect { get; set; }
}

internal sealed record PixelRect(double X, double Y, double Width, double Height)
{
    internal static PixelRect? From(System.Windows.Rect rect) => rect.IsEmpty ? null : new(rect.X, rect.Y, rect.Width, rect.Height);
    internal System.Windows.Rect ToRect() => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
        && double.IsFinite(Height) && Width > 0 && Height > 0 ? new(X, Y, Width, Height) : System.Windows.Rect.Empty;
}

internal sealed class QqWorkerState
{
    public long EditorHwnd { get; set; }
    public int EditorPid { get; set; }
    public PixelRect? EditorRect { get; set; }
    public long EditorEcho { get; set; }
    public long FocusTicks { get; set; }
    public long Generation { get; set; }
    public bool PanelOpen { get; set; }
    public long ButtonHwnd { get; set; }
    public int ButtonPid { get; set; }
    public PixelRect? ButtonRect { get; set; }
    public long TabHwnd { get; set; }
    public PixelRect? TabRect { get; set; }
    public int[] LegacyPids { get; set; } = [];
}
