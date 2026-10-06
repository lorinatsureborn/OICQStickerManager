using System.Diagnostics;
using System.Windows.Automation;

internal static class QqStructureProbe
{
    internal static int Run(IntPtr hwnd)
    {
        var watch = Stopwatch.StartNew();
        int samples = 0;
        var ready = Task.Run(() =>
        {
            var root = AutomationElement.FromHandle(hwnd);
            Automation.AddStructureChangedEventHandler(root, TreeScope.Descendants, (_, args) =>
            {
                if (watch.ElapsedMilliseconds > 60_000 || Interlocked.Increment(ref samples) > 40) return;
                try
                {
                    var element = (AutomationElement)_;
                    var current = element.Current;
                    Console.WriteLine($"{watch.ElapsedMilliseconds}ms {args.StructureChangeType}: "
                        + $"pid={current.ProcessId}, type={current.ControlType.ProgrammaticName}, class={current.ClassName}, "
                        + $"rect={current.BoundingRectangle}, offscreen={current.IsOffscreen}, "
                        + $"senderId={string.Join(',', element.GetRuntimeId())}, eventId={string.Join(',', args.GetRuntimeId())}");
                }
                catch (Exception ex) { Console.WriteLine("Event read failed: " + ex.GetType().Name); }
            });
        });
        try
        {
            if (!ready.Wait(3000)) { Console.WriteLine("Subscription timed out without changing QQ."); return 1; }
            Console.WriteLine("Read-only structure probe ready; observation ends after 60 seconds.");
            Thread.Sleep((int)Math.Max(0, 60_000 - watch.ElapsedMilliseconds));
            Console.WriteLine("Read-only structure probe complete.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex.GetBaseException().GetType().Name); return 1; }
    }
}
