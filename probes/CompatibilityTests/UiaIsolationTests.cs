using System.Diagnostics;
using System.Text.Json;
using OICQStickerManager.Services;

internal static class UiaIsolationTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("A hung UIA lease is reclaimed despite live heartbeats", () => Program.RunAsync(HungLeaseIsReclaimed())),
        ("Disposing an isolated UIA worker releases its process", () => Program.RunAsync(WorkerDisposes())),
        ("A throwing status observer cannot stop UIA reclamation", () => Program.RunAsync(ThrowingStatusStillReclaims())),
        ("Worker responses publish state before completing correlated requests", () => Program.RunAsync(ResponsesPublishFirst())),
        ("A timed out UIA request reaps its worker", () => Program.RunAsync(RequestTimeoutReapsWorker())),
    ];

    internal static int Fixture(string mode)
    {
        Console.WriteLine("{\"Kind\":\"ready\"}");
        if (mode == "reply")
        {
            while (Console.ReadLine() is { } line)
            {
                var request = JsonSerializer.Deserialize<QqWorkerFrame>(line)!;
                Console.WriteLine(JsonSerializer.Serialize(new QqWorkerFrame
                {
                    Kind = "response", Id = request.Id, Text = request.Command,
                    State = new() { Generation = request.Generation }
                }));
            }
            return 0;
        }
        if (mode == "hang") Console.WriteLine("{\"Kind\":\"lease-start\",\"Id\":1,\"DeadlineMs\":300}");
        else Console.WriteLine("{\"Kind\":\"pid\",\"Id\":" + Environment.ProcessId + "}");
        while (true) { Console.WriteLine("{\"Kind\":\"heartbeat\"}"); Thread.Sleep(50); }
    }

    private static async Task HungLeaseIsReclaimed()
    {
        int launches = 0, firstPid = 0, recoveredPid = 0;
        ProcessStartInfo Factory()
        {
            var mode = Interlocked.Increment(ref launches) == 1 ? "hang" : "healthy";
            return Info(mode);
        }
        Action<string> receive = line =>
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("Kind").GetString() == "pid")
                Interlocked.Exchange(ref recoveredPid, json.RootElement.GetProperty("Id").GetInt32());
        };
        using var client = new QqUiaWorkerClient(Factory, receive, _ => { });
        client.Start();
        var stop = Stopwatch.StartNew();
        while (recoveredPid == 0 && stop.ElapsedMilliseconds < 3000)
        {
            if (firstPid == 0) firstPid = client.ProcessId;
            await Task.Delay(10);
        }
        Program.Require(firstPid != 0 && recoveredPid != 0 && recoveredPid != firstPid,
            "a live but blocked UIA worker was not replaced before the deadline");
        Program.Require(!Alive(firstPid), "the blocked worker process survived replacement");
    }

    private static async Task WorkerDisposes()
    {
        using var client = new QqUiaWorkerClient(() => Info("healthy"), _ => { }, _ => { });
        client.Start();
        int pid = 0;
        var stop = Stopwatch.StartNew();
        while (pid == 0 && stop.ElapsedMilliseconds < 2000)
        {
            pid = client.ProcessId;
            await Task.Delay(10);
        }
        Program.Require(pid != 0, "worker never started");
        client.Dispose();
        Program.Require(!Alive(pid), "UIA worker survived owner disposal");
    }

    private static async Task ThrowingStatusStillReclaims()
    {
        int launches = 0, recoveredPid = 0, firstPid;
        using var client = new QqUiaWorkerClient(() => Info(Interlocked.Increment(ref launches) == 1 ? "hang" : "healthy"),
            line =>
            {
                var frame = JsonSerializer.Deserialize<QqWorkerFrame>(line)!;
                if (frame.Kind == "pid") Interlocked.Exchange(ref recoveredPid, (int)frame.Id);
            }, _ => throw new InvalidOperationException("observer failed"));
        client.Start();
        firstPid = await StartedPid(client);
        var stop = Stopwatch.StartNew();
        while (Volatile.Read(ref recoveredPid) == 0 && stop.ElapsedMilliseconds < 2500) await Task.Delay(10);
        Program.Require(recoveredPid != 0 && recoveredPid != firstPid && !Alive(firstPid),
            "status callback failure prevented reclamation of the hung worker");
    }

    private static async Task ResponsesPublishFirst()
    {
        long published = 0;
        using var client = new QqUiaWorkerClient(() => Info("reply"), line =>
        {
            var frame = JsonSerializer.Deserialize<QqWorkerFrame>(line)!;
            if (frame.Kind == "response") Interlocked.Exchange(ref published, frame.State!.Generation);
        }, _ => { });
        client.Start();
        await StartedPid(client);
        var first = await client.RequestAsync(new() { Command = "first", Generation = 37 }, TimeSpan.FromSeconds(2));
        Program.Require(first != null && JsonSerializer.Deserialize<QqWorkerFrame>(first)!.Text == "first" && published == 37,
            "request completed before its response state was published");
        var second = await client.RequestAsync(new() { Command = "second", Generation = 91 }, TimeSpan.FromSeconds(2));
        Program.Require(second != null && JsonSerializer.Deserialize<QqWorkerFrame>(second)!.Text == "second" && published == 91,
            "a later request reused an earlier response");
    }

    private static async Task RequestTimeoutReapsWorker()
    {
        using var client = new QqUiaWorkerClient(() => Info("healthy"), _ => { }, _ => { });
        client.Start();
        int pid = await StartedPid(client);
        var response = await client.RequestAsync(new() { Command = "unanswered" }, TimeSpan.FromMilliseconds(150));
        var stop = Stopwatch.StartNew();
        while (Alive(pid) && stop.ElapsedMilliseconds < 2000) await Task.Delay(10);
        Program.Require(response == null && !Alive(pid), "request timed out without reaping its worker");
    }

    private static async Task<int> StartedPid(QqUiaWorkerClient client)
    {
        var stop = Stopwatch.StartNew();
        while (client.ProcessId == 0 && stop.ElapsedMilliseconds < 2000) await Task.Delay(10);
        Program.Require(client.ProcessId != 0, "worker never started");
        return client.ProcessId;
    }

    private static ProcessStartInfo Info(string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!);
        info.ArgumentList.Add("--uia-fixture"); info.ArgumentList.Add(mode);
        return info;
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
