using System.Text.Json;

namespace OICQStickerManager.Services;

internal sealed class OrderedJsonWriter
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    internal Task Completion { get { lock (_gate) return _tail; } }
    internal Task SaveAsync<T>(T snapshot, Func<string, Task> write)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(async _ =>
            {
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await write(json).ConfigureAwait(false);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _tail;
        }
    }
}
