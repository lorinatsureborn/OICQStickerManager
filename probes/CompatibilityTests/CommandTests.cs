using OICQStickerManager.Services;

internal static class CommandTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Async command rejects a repeated long-running operation", () => Program.RunAsync(NonReentrant())),
        ("Explicitly concurrent commands preserve repeated image sends", () => Program.RunAsync(Concurrent())),
        ("Async command reports an exception and becomes available again", () => Program.RunAsync(ReportsFailure())),
    ];
    private static async Task NonReentrant()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var command = new RelayCommand(async () => { calls++; await release.Task; });
        command.Execute(null);
        bool busy = !command.CanExecute(null);
        command.Execute(null);
        release.TrySetResult();
        await Task.Delay(20);
        Program.Require(busy && calls == 1 && command.CanExecute(null), "a busy command ran twice or remained disabled");
    }
    private static async Task Concurrent()
    {
        int calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new RelayCommand(async () => { calls++; await release.Task; }, allowConcurrent: true);
        command.Execute(null);
        command.Execute(null);
        release.TrySetResult();
        await Task.Delay(20);
        Program.Require(calls == 2 && command.CanExecute(null), "repeat sends were disabled by the command guard");
    }
    private static async Task ReportsFailure()
    {
        Exception? reported = null;
        var command = new RelayCommand(async () => { await Task.Yield(); throw new InvalidOperationException("controlled failure"); }, onError: ex => reported = ex);
        command.Execute(null);
        await Task.Delay(20);
        Program.Require(reported is InvalidOperationException && command.CanExecute(null), "command exception escaped or left the command disabled");
    }
}
