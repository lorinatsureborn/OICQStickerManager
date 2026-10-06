using System.Windows;
using OICQStickerManager.Services;

internal static class SendTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Send transaction reports accepted input and restores its clipboard", () => Program.RunAsync(SuccessRestores())),
        ("Send transaction preserves a new user clipboard copy", () => Program.RunAsync(NewCopySurvives())),
        ("Send transaction rejects focus changes during clipboard retry", () => Program.RunAsync(FocusChangesDuringRetry())),
        ("Send transaction reports clipboard contention as failure", () => Program.RunAsync(ClipboardContentionFails())),
        ("Send transaction reports rejected input as failure", () => Program.RunAsync(InputRejectionFails())),
        ("Send transactions serialize different images through restoration", () => Program.RunAsync(SendsAreSerialized())),
    ];

    private static readonly SendTarget Target = new((IntPtr)42, 123);
    private static string Image()
    {
        var path = System.IO.Path.Combine(Program.NewDirectory(), "image.png");
        System.IO.File.WriteAllBytes(path, [1]);
        return path;
    }

    private static async Task SuccessRestores()
    {
        var environment = new FakeEnvironment();
        var result = await new SendCoordinator(environment).SendAsync(Target, Image(), true, () => Task.FromResult(true));
        Program.Require(result.Succeeded && environment.Pastes == 1, "accepted input was not reported");
        Program.Require(environment.Restored && environment.Value == "backup", "owned clipboard was not restored");
    }

    private static async Task NewCopySurvives()
    {
        var environment = new FakeEnvironment();
        environment.OnDelay = _ => { environment.UserCopy(); return Task.CompletedTask; };
        var result = await new SendCoordinator(environment).SendAsync(Target, Image(), true, () => Task.FromResult(true));
        Program.Require(result.Succeeded && environment.Value == "user-copy" && !environment.Restored, "clipboard restoration overwrote a new user copy");
    }

    private static async Task FocusChangesDuringRetry()
    {
        var environment = new FakeEnvironment { FailWrites = 1 };
        environment.OnDelay = _ => { environment.Focused = false; return Task.CompletedTask; };
        var result = await new SendCoordinator(environment).SendAsync(Target, Image(), true, () => Task.FromResult(true));
        Program.Require(result.Status == SendStatus.TargetChanged && environment.Pastes == 0, "a changed foreground received paste input");
        Program.Require(environment.Value == "backup", "aborted send leaked its clipboard payload");
    }

    private static async Task ClipboardContentionFails()
    {
        var environment = new FakeEnvironment { FailWrites = 100 };
        var result = await new SendCoordinator(environment).SendAsync(Target, Image(), true, () => Task.FromResult(true));
        Program.Require(result.Status == SendStatus.ClipboardBusy && environment.Pastes == 0, "failed clipboard write was reported as a send");
    }

    private static async Task InputRejectionFails()
    {
        var environment = new FakeEnvironment { AcceptInput = false };
        var result = await new SendCoordinator(environment).SendAsync(Target, Image(), true, () => Task.FromResult(true));
        Program.Require(result.Status == SendStatus.InputRejected && environment.Value == "backup", "input rejection was lost or the clipboard was not restored");
    }

    private static async Task SendsAreSerialized()
    {
        var environment = new FakeEnvironment();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.OnDelay = _ => barrier.Task;
        var coordinator = new SendCoordinator(environment);
        var first = coordinator.SendAsync(Target, Image(), true, () => Task.FromResult(true));
        var second = coordinator.SendAsync(Target, Image(), true, () => Task.FromResult(true));
        try { Program.Require(environment.Writes == 1 && !second.IsCompleted, "another image overwrote the in-flight clipboard"); }
        finally { barrier.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Program.Require(results.All(result => result.Succeeded) && environment.Pastes == 2, "queued sends were lost");
        Program.Require(!ClipboardCapture.Suppress, "clipboard capture remained suppressed");
    }

    private sealed class FakeEnvironment : ISendEnvironment
    {
        internal bool Focused = true, Restored, AcceptInput = true;
        internal int FailWrites, Writes, Pastes;
        internal string Value = "backup";
        internal Func<int, Task>? OnDelay;
        public uint ClipboardSequence { get; private set; } = 1;
        public bool IsValid(SendTarget target) => target == Target;
        public bool IsFocused(SendTarget target) => target == Target && Focused;
        public IDataObject CaptureClipboard() => new DataObject(DataFormats.Text, Value);
        public bool TryWriteFile(string path)
        {
            if (FailWrites-- > 0) return false;
            Writes++; Value = path; ClipboardSequence++; return true;
        }
        public void RestoreClipboard(IDataObject backup) { Restored = true; Value = (string)backup.GetData(DataFormats.Text); ClipboardSequence++; }
        public bool TryPaste(SendTarget target) { if (!IsFocused(target) || !AcceptInput) return false; Pastes++; return true; }
        public Task DelayAsync(int milliseconds) => OnDelay?.Invoke(milliseconds) ?? Task.CompletedTask;
        internal void UserCopy() { Value = "user-copy"; ClipboardSequence++; }
    }
}
