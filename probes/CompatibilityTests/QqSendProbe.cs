using System.Windows.Threading;
using OICQStickerManager.Services;

internal static class QqSendProbe
{
    // Explicit real-QQ integration diagnostic. Inserts only a supplied image into
    // the current draft, using production focus/clipboard code; never presses Enter.
    internal static int Run(IntPtr hwnd, string path)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var watcher = new QqPanelWatcher(message => Console.WriteLine(message));
        try
        {
            watcher.Start();
            var work = Exercise();
            Program.RunAsync(work, 15000);
            return work.Result.Succeeded ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine(ex.GetBaseException().Message); return 1; }
        finally { watcher.Dispose(); SynchronizationContext.SetSynchronizationContext(context); }

        async Task<SendResult> Exercise()
        {
            await Task.Delay(1800);
            var start = Environment.TickCount64;
            var result = await new WindowService().CoexistPasteAsync(hwnd, path);
            Console.WriteLine($"Production QQ draft insertion: {result.Status}, {Environment.TickCount64 - start} ms");
            return result;
        }
    }
}
