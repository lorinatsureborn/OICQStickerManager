namespace OICQStickerManager.Services;

internal sealed class ClipboardProbeGuard
{
    internal readonly record struct Ticket(int Generation, uint Sequence);
    private int _generation;
    internal void Invalidate() => Interlocked.Increment(ref _generation);
    internal Ticket Capture(uint sequence) => new(Volatile.Read(ref _generation), sequence);
    internal bool CanPublish(Ticket ticket, uint currentSequence) => ticket.Generation == Volatile.Read(ref _generation)
        && ticket.Sequence == currentSequence && !ClipboardCapture.Suppress;
}
