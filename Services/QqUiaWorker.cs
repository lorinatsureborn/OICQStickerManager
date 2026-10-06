using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;

namespace OICQStickerManager.Services;

internal static class QqUiaWorker
{
    private static readonly object OutputGate = new();
    private static long _nextLease;
    internal static bool IsWorker { get; private set; }
    internal static int UiOwnerPid { get; private set; } = Environment.ProcessId;

    internal static ProcessStartInfo StartInfo(string mode, params string[] arguments)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Asuka.exe");
        var info = new ProcessStartInfo(executable);
        info.ArgumentList.Add(mode);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task RunAsync(bool polling, int ownerPid)
    {
        Initialize(ownerPid);
        using var watcher = new QqPanelWatcher(text => Write(new() { Kind = "status", Text = text }));
        watcher.PanelAppeared += (_, e) => Event("appeared", e);
        watcher.EmojiButtonClicked += (_, e) => Event("clicked", e);
        watcher.PanelDisappeared += (_, _) => Write(new() { Kind = "disappeared", State = watcher.ExportState() });
        watcher.SetPollingFallback(polling);
        Write(new() { Kind = "ready" });
        watcher.Start();
        using var stopped = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!stopped.IsCancellationRequested)
            {
                Write(new() { Kind = "heartbeat", State = watcher.ExportState() });
                await Task.Delay(1000, stopped.Token).ConfigureAwait(false);
            }
        });
        try
        {
            while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 65536) break;
                QqWorkerFrame? request;
                try { request = JsonSerializer.Deserialize<QqWorkerFrame>(line); } catch (JsonException) { continue; }
                if (request == null) continue;
                var response = new QqWorkerFrame { Kind = "response", Id = request.Id };
                using (Lease(5000))
                {
                    switch (request.Command)
                    {
                        case "poll": watcher.SetPollingFallback(request.Value); response.Value = true; break;
                        case "close":
                            if (request.Generation < 0 || request.Generation == QqPanelWatcher.UserActionGen)
                                response.Value = QqPanelWatcher.TryCloseQqPanelNow(new IntPtr(request.Hwnd));
                            break;
                        case "dump": await QqPanelWatcher.DumpDiagnosticsNowAsync().ConfigureAwait(false); response.Value = true; break;
                    }
                }
                response.State = watcher.ExportState();
                Write(response);
            }
        }
        finally
        {
            stopped.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        void Event(string kind, QqPanelEventArgs e) => Write(new()
        {
            Kind = kind, Hwnd = e.HostHwnd.ToInt64(), Rect = PixelRect.From(e.PanelRect),
            ButtonRect = PixelRect.From(e.EmojiButtonRect), State = watcher.ExportState()
        });
    }

    internal static async Task<QqWorkerFrame?> QueryAsync(string command, IntPtr hwnd = default, TimeSpan? timeout = null)
    {
        QqWorkerFrame? result = null;
        try
        {
            var outcome = await ChildProcessRunner.RunAsync(StartInfo("--qq-uia-query", command, hwnd.ToInt64().ToString()),
                timeout ?? TimeSpan.FromSeconds(command == "dump" ? 6 : 2), line =>
                {
                    if (line.Length > 65536) return;
                    try { result = JsonSerializer.Deserialize<QqWorkerFrame>(line); } catch (JsonException) { }
                }).ConfigureAwait(false);
            return !outcome.TimedOut && outcome.ExitCode == 0 ? result : null;
        }
        catch (Exception ex) { QqPanelWatcher.Log("isolated UIA query failed: " + ex.GetType().Name); return null; }
    }

    internal static async Task RunQueryAsync(string command, IntPtr hwnd)
    {
        Initialize(Environment.ProcessId);
        var response = new QqWorkerFrame { Kind = "response" };
        switch (command)
        {
            case "editor": response.Rect = PixelRect.From(FindEditorRect(hwnd)); break;
            case "focus":
                var focused = AutomationElement.FocusedElement;
                response.Value = focused != null && (focused.Current.ClassName ?? "").Contains("ExEditor-qq-msg-editor");
                break;
            case "dump": await QqPanelWatcher.DumpDiagnosticsNowAsync().ConfigureAwait(false); response.Value = true; break;
        }
        Write(response);
    }

    private static Rect FindEditorRect(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var editor = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "ProseMirror ExEditor-qq-msg-editor is-empty"));
        if (editor != null) return editor.Current.BoundingRectangle;
        var cache = new CacheRequest();
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        using (cache.Activate())
        {
            var all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement element in all)
                try
                {
                    if ((element.Cached.ClassName ?? "").Contains("ExEditor-qq-msg-editor")) return element.Cached.BoundingRectangle;
                }
                catch (ElementNotAvailableException) { }
        }
        return Rect.Empty;
    }

    private static void Initialize(int ownerPid)
    {
        IsWorker = true;
        UiOwnerPid = ownerPid;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
    }

    internal static IDisposable? Lease(int deadlineMs = 2000)
    {
        if (!IsWorker) return null;
        long id = Interlocked.Increment(ref _nextLease);
        Write(new() { Kind = "lease-start", Id = id, DeadlineMs = deadlineMs });
        return new OperationLease(id);
    }
    private sealed class OperationLease(long id) : IDisposable
    {
        public void Dispose() => Write(new() { Kind = "lease-end", Id = id, State = QqPanelWatcher.ActiveState() });
    }
    private static void Write(QqWorkerFrame frame)
    {
        lock (OutputGate) Console.WriteLine(JsonSerializer.Serialize(frame));
    }
}
