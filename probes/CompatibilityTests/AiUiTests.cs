using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.Views;

internal static class AiUiTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("AI masked key input, settings refresh, stale verification and sticker result isolation", Run),
    ];
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Build the actual WPF controls, without showing a window or activating native hooks.
    private static void Run()
    {
        var root = Program.NewDirectory();
        var previous = Environment.GetEnvironmentVariable("ASUKA_DATA_DIR");
        var previousContext = SynchronizationContext.Current;
        Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", root);
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/Asuka;component/Themes/Palette.Aurora.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/Asuka;component/Themes/Tokens.xaml") });
            var window = new MainWindow();
            var vm = OICQStickerManager.App.SharedViewModel;
            Program.RunAsync(vm.Initialization);
            Invoke(window, "OpenAiEditor", new object?[] { null });
            vm.AiTagApiKey = "sk-proj-ui-synthetic-key";
            Program.RunAsync(Task.Delay(20));
            var password = Element(window, "AiKeyBox") as System.Windows.Controls.PasswordBox;
            Program.Require(password != null && password.Password == vm.AiTagApiKey, "API key input is unmasked or did not synchronize the loaded key");
            var peer = new System.Windows.Automation.Peers.PasswordBoxAutomationPeer(password!);
            var valuePattern = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Value) as System.Windows.Automation.Provider.IValueProvider;
            var readable = false;
            try { readable = valuePattern?.Value is { Length: > 0 }; }
            catch (InvalidOperationException) { /* WPF exposes the pattern but forbids reading a password. */ }
            Program.Require(peer.IsPassword() && !readable, "automation can read the plaintext API key");
            password!.Password = "sk-proj-ui-typed-key";
            Program.Require(vm.AiTagApiKey == password.Password, "masked input did not commit the edited key");
            vm.MarkAiDraftVerified(); var saved = vm.SaveCurrentAsProfile("Synthetic UI", []);
            Invoke(window, "OpenAiEditor", new object?[] { null });
            Program.RunAsync(Task.Delay(20));
            Program.Require(password.Password == "", "new draft retained the previous profile's key in the input");
            vm.ActivateAiProfile(saved.Id); Program.RunAsync(Task.Delay(20));
            Program.Require(password.Password == "sk-proj-ui-typed-key", "profile activation did not synchronize the decrypted key");
            Program.Require(Element(window, "AiProviderKnownSection").Visibility == Visibility.Visible, "pasting a recognized key did not refresh provider controls");
            Program.Require(Element(window, "AiModelConfigurationSection").Visibility == Visibility.Visible, "model selection was inaccessible before verification");
            vm.AiTagProvider = "custom";
            Program.RunAsync(Task.Delay(20));
            Program.Require(Element(window, "AiModelConfigurationSection").Visibility == Visibility.Visible
                && Element(window, "AiVerifiedSection").Visibility == Visibility.Collapsed, "custom model input requires an impossible successful test first");
            vm.AiTagProvider = "openai";
            vm.AiTagApiKey = "sk-proj-ui-synthetic-key";

            using var handler = new DelayedHandler();
            vm.AiTag.Dispose();
            typeof(OICQStickerManager.ViewModels.MainViewModel).GetField("<AiTag>k__BackingField", Private)!.SetValue(vm, new AiTagService(handler));
            Invoke(window, "AiTest_Click", new object[] { window, new RoutedEventArgs() });
            Program.RunAsync(handler.Started.Task);
            vm.AiTagApiKey = "sk-proj-ui-replacement-key";
            handler.Reply();
            WaitUntil(() => ((System.Windows.Controls.Button)Element(window, "AiTestButton")).IsEnabled);
            Program.Require(!vm.AiDraftVerified, "the previous key's late response verified its replacement");

            var image = Path.Combine(root, "sticker.png");
            File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
            var first = new StickerModel { FullPath = image, Md5 = "synthetic-ui-first" };
            var second = new StickerModel { FullPath = image, Md5 = "synthetic-ui-second" };
            handler.Reset();
            Invoke(window, "ShowTagEditor", new object[] { first });
            var pending = (Task)Invoke(window, "RunEditorAiAsync", new object[] { false })!;
            Program.RunAsync(handler.Started.Task);
            Invoke(window, "ShowTagEditor", new object[] { second });
            handler.Reply(); Program.RunAsync(pending);
            var suggestions = (IList)typeof(MainWindow).GetField("_aiSuggestions", Private)!.GetValue(window)!;
            Program.Require(suggestions.Count == 0, "a late result for the first sticker appeared in the second sticker's editor");
            vm.Dispose(); vm.FlushPendingConfigSave();
        }
        finally
        {
            OICQStickerManager.App.SharedViewModel.Dispose();
            OICQStickerManager.App.SharedViewModel.FlushPendingConfigSave();
            Program.RunAsync(AiTagCache.FlushAsync()); AiTagCache.ResetForTests();
            Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", previous);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static FrameworkElement Element(MainWindow window, string name) => (FrameworkElement)window.FindName(name);
    private static object? Invoke(MainWindow window, string method, object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline) Program.RunAsync(Task.Delay(10));
        Program.Require(condition(), "hidden UI request did not finish");
    }
    private sealed class DelayedHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<HttpResponseMessage> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Reset() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); _reply = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void Reply() => _reply.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"[\\\"小鸟\\\",\\\"开心\\\"]\"}}]}") });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Started.TrySetResult(); return _reply.Task; }
    }
}
