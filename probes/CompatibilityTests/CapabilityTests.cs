using OICQStickerManager.Services;

internal static class CapabilityTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Old and new QQ processes retain separate capabilities", SeparateProcesses),
        ("QQ restart clears the previous process tree latch", RestartClearsLatch),
        ("A slow UIA tree degrades only its own QQ process", SlowTreeIsLocal),
    ];
    private static void SeparateProcesses()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.19.35469"), new(2, 1, "9.9.21-38711"), new(3, 1, "9.9.36.48000")]);
        Program.Require(capabilities.IsLegacy(1) && capabilities.IsLegacy(2) && !capabilities.IsLegacy(3), "mixed QQ builds shared a legacy decision");
    }
    private static void RestartClearsLatch()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.19.35469")]);
        capabilities.MarkTreeUnavailable(1);
        capabilities.Refresh([new(1, 2, "9.9.36.48000")]);
        Program.Require(!capabilities.IsLegacy(1), "reused PID inherited the previous process latch");
        capabilities.Refresh([]);
        Program.Require(!capabilities.HasLegacy, "exited QQ left stale capabilities");
    }
    private static void SlowTreeIsLocal()
    {
        var capabilities = new QqProcessCapabilities();
        capabilities.Refresh([new(1, 1, "9.9.36.48000"), new(2, 1, "9.9.36.48000")]);
        capabilities.MarkTreeUnavailable(1);
        capabilities.Refresh([new(1, 1, "9.9.36.48000"), new(2, 1, "9.9.36.48000")]);
        Program.Require(capabilities.IsLegacy(1) && !capabilities.IsLegacy(2), "tree failure was lost or spread to another process");
    }
}
