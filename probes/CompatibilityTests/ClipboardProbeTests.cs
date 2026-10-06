using OICQStickerManager.Services;

internal static class ClipboardProbeTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Clipboard probe rejects an older request completing last", OlderResult),
        ("Clipboard probe rejects content changed during extraction", ChangedClipboard),
        ("Clipboard probe rejects a result arriving during a send", SuppressedResult),
    ];
    private static void OlderResult()
    {
        var guard = new ClipboardProbeGuard();
        guard.Invalidate();
        var old = guard.Capture(1);
        guard.Invalidate();
        var latest = guard.Capture(2);
        Program.Require(guard.CanPublish(latest, 2) && !guard.CanPublish(old, 1), "old extraction replaced a newer clipboard request");
    }
    private static void ChangedClipboard()
    {
        var guard = new ClipboardProbeGuard();
        Program.Require(!guard.CanPublish(guard.Capture(1), 2), "clipboard change during a blocked extraction was accepted");
    }
    private static void SuppressedResult()
    {
        var guard = new ClipboardProbeGuard();
        var ticket = guard.Capture(1);
        using var scope = ClipboardCapture.BeginSuppression();
        Program.Require(!guard.CanPublish(ticket, 1), "capture displayed during a send transaction");
    }
}
