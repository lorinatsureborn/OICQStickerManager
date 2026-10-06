using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "asuka-compatibility-tests", Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-qq-database")
            return QqDatabaseProbe.Run(args[1]);
        if (args.Length == 2 && args[0] == "--inspect-qq-structure" && long.TryParse(args[1], out var hwnd))
            return QqStructureProbe.Run(new IntPtr(hwnd));
        if (args.Length == 2 && args[0] == "--inspect-qq-points")
        {
            var inspection = Task.Run(() =>
            {
                foreach (var pair in args[1].Split(';'))
                {
                    var coordinates = pair.Split(',');
                    var point = new Point(int.Parse(coordinates[0]), int.Parse(coordinates[1]));
                    var current = AutomationElement.FromPoint(point).Current;
                    Console.WriteLine($"Point {pair}: pid={current.ProcessId}, type={current.ControlType.ProgrammaticName}, "
                        + $"name={current.Name}, class={current.ClassName}, rect={current.BoundingRectangle}");
                }
            });
            try
            {
                if (!inspection.Wait(3000)) { Console.WriteLine("Point inspection timed out without changing QQ."); return 1; }
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.GetBaseException().Message); return 1; }
        }
        if (args.Length == 1 && args[0] == "--inspect-key-installation")
        {
            try
            {
                var installation = QqKeyInstallation.Discover();
                Console.WriteLine("QQ executable: " + installation.ExecutablePath);
                Console.WriteLine("QQ module: " + installation.WrapperPath);
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        if (args.Length == 2 && args[0] == "--inspect-key-module")
        {
            try
            {
                Console.WriteLine($"Key function RVA: 0x{QqKeyWatchService.StaticAnalysis.GetKeyFunctionRva(args[1]):X}");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        if (args.Contains("--wait-helper")) { Console.WriteLine(Environment.ProcessId); Thread.Sleep(30_000); return 0; }
        if (args.Contains("--pipe-parent"))
        {
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--wait-helper");
            using var child = System.Diagnostics.Process.Start(info)!;
            Console.WriteLine("descendant:" + child.Id);
            return 0;
        }
        if (args.Contains("--debuggee")) return DebuggerTests.Debuggee();
        if (args.Length == 2 && args[0] == "--uia-fixture") return UiaIsolationTests.Fixture(args[1]);
        Directory.CreateDirectory(Scratch);
        (string Name, Action Run)[] tests =
        [
            ("QQ mirror follows file creation and deletion", MirrorTracksChanges),
            ("Disposed QQ mirror ignores a rescan", DisposedMirrorDoesNotPublish),
            ("QQ mirror recovers when its Ori directory is created later", MirrorRecoversMissingDirectory),
            ("Missing QQ index preserves mirror in mark mode", () => MissingIndexPreservesMirror(QqSyncStrategy.Mark)),
            ("Missing QQ index preserves mirror in remove mode", () => MissingIndexPreservesMirror(QqSyncStrategy.Remove)),
            ("Missing QQ index preserves mirror in adopt mode", () => MissingIndexPreservesMirror(QqSyncStrategy.Adopt)),
            ("Active WAL defers reconciliation instead of invalidating the key", ActiveWalDefersReconciliation),
            ("QQ index rejects a missing MD5 column", IndexRejectsMissingColumn),
            ("QQ index rejects malformed MD5 values", IndexRejectsMalformedMd5),
            ("QQ index accepts an empty supported table", IndexAcceptsEmptyTable),
            ("Editor cache belongs to one chat window", EditorCacheRejectsAnotherWindow),
            ("Disposing an unstarted watcher invalidates its cache", DisposedUnstartedWatcherIsInactive),
            ("Unconfirmed empty-class focus does not acknowledge editor", UnconfirmedFocusDoesNotAcknowledgeEditor),
            ("Legacy tab focus opens coexist without a UIA ancestor chain", LegacyTabWithPrunedAncestors),
            ("A later button focus invalidates the legacy editor echo", LaterButtonInvalidatesEditorEcho),
            ("Pixel match to the left returns a usable location", PixelMatchReturnsBufferLocation),
            ("Pixel diagnostic clips a region at the image edge", PixelDiagnosticHandlesImageEdge),
            ("Pixel diagnostic accepts a 24-bit source image", PixelDiagnosticHandlesRgb24),
            ("JSON writer finishes while the UI dispatcher is waiting", JsonWriterDoesNotRequireDispatcher),
        ];
        tests = tests.Concat(SendTests.Cases).ToArray();
        tests = tests.Concat(LifecycleTests.Cases).ToArray();
        tests = tests.Concat(PersistenceTests.Cases).ToArray();
        tests = tests.Concat(SyncTests.Cases).ToArray();
        tests = tests.Concat(CapabilityTests.Cases).ToArray();
        tests = tests.Concat(LibraryTests.Cases).ToArray();
        tests = tests.Concat(CipherTests.Cases).ToArray();
        tests = tests.Concat(WalTests.Cases).ToArray();
        tests = tests.Concat(SecurityTests.Cases).ToArray();
        tests = tests.Concat(DebuggerTests.Cases).ToArray();
        tests = tests.Concat(KeyLocatorTests.Cases).ToArray();
        tests = tests.Concat(KeyInstallationTests.Cases).ToArray();
        tests = tests.Concat(CommandTests.Cases).ToArray();
        tests = tests.Concat(PlacementTests.Cases).ToArray();
        tests = tests.Concat(ClipboardProbeTests.Cases).ToArray();
        tests = tests.Concat(GlassTests.Cases).ToArray();
        tests = tests.Concat(UiaIsolationTests.Cases).ToArray();
        tests = tests.Concat(FocusSettlementTests.Cases).ToArray();
        tests = tests.Concat(LegacyPanelTests.Cases).ToArray();
        int failed = 0;
        try
        {
            foreach (var test in tests)
            {
                try { test.Run(); Console.WriteLine("PASS " + test.Name); }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.GetBaseException().Message);
                }
                PumpPendingWork();
            }
        }
        finally
        {
            LibraryTests.Cleanup();
            try { Directory.Delete(Scratch, recursive: true); } catch { }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void MirrorTracksChanges()
    {
        var dir = NewDirectory();
        var gate = new object();
        using var service = new QqEmojiService("audit", dir, action => { lock (gate) action(); });
        service.StartAsync().GetAwaiter().GetResult();
        var path = Path.Combine(dir, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
        WaitFor(() => { lock (gate) return service.Mirror.Count == 1; }, "new favorite never reached the mirror");
        lock (gate) Require(service.Mirror[0].FullPath == path, "wrong favorite published");
        File.Delete(path);
        WaitFor(() => { lock (gate) return service.Mirror.Count == 0; }, "deleted favorite stayed in the mirror");
    }

    private static void DisposedMirrorDoesNotPublish()
    {
        var dir = NewDirectory();
        File.WriteAllBytes(Path.Combine(dir, "sticker.png"), [0x89, 0x50, 0x4e, 0x47]);
        using var service = new QqEmojiService("audit", dir, action => action());
        service.Dispose();
        service.RescanAsync().GetAwaiter().GetResult();
        Require(service.Mirror.Count == 0, "disposed service repopulated its mirror");
    }

    private static void MirrorRecoversMissingDirectory()
    {
        var dir = Path.Combine(NewDirectory(), "Ori");
        var gate = new object();
        using var service = new QqEmojiService("audit", dir, action => { lock (gate) action(); });
        service.StartAsync().GetAwaiter().GetResult();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
        WaitFor(() => { lock (gate) return service.Mirror.Count == 1; }, "missing Ori never recovered after creation");
        File.Delete(path);
        WaitFor(() => { lock (gate) return service.Mirror.Count == 0; }, "recovered Ori did not restart monitoring");
    }

    private static void MissingIndexPreservesMirror(QqSyncStrategy strategy)
    {
        using var service = new QqEmojiService("audit-missing-" + Guid.NewGuid().ToString("N"), NewDirectory(), action => action());
        var item = new QqStickerModel { Uin = service.Uin, Md5 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        service.Mirror.Add(item);
        int adoptionCalls = 0;
        var sync = new QqDeepSyncService("unused-test-key", uin => service, _ => { adoptionCalls++; return Task.FromResult(false); });
        int result = sync.ReconcileAllAsync([service.Uin], strategy).GetAwaiter().GetResult();
        Require(result == -1, "unavailable database was treated as an authoritative empty index");
        Require(service.Mirror.Count == 1 && !item.IsOrphaned && adoptionCalls == 0, "mirror changed without a valid index");
    }

    private static void EditorCacheRejectsAnotherWindow()
    {
        using var first = NewWindow("audit-first");
        using var second = NewWindow("audit-second");
        using var watcher = new QqPanelWatcher(_ => { });
        Set(watcher, "_editorHwnd", first.Handle);
        Set(watcher, "_editorPid", Environment.ProcessId);
        Set(watcher, "_editorRectRel", new Rect(20, 350, 40, 20));
        Require(QqPanelWatcher.TryGetCachedEditorRect(first.Handle, out _), "original chat lost its cache");
        Require(!QqPanelWatcher.TryGetCachedEditorRect(second.Handle, out _), "another chat reused the first chat's editor coordinates");
    }

    private static void ActiveWalDefersReconciliation()
    {
        // A rooted account path confines the existing path resolver to this test's scratch tree.
        var account = NewDirectory();
        var databaseDirectory = Path.Combine(account, "nt_qq", "nt_db");
        Directory.CreateDirectory(databaseDirectory);
        var database = Path.Combine(databaseDirectory, "emoji.db");
        File.WriteAllBytes(database, [0]);
        File.WriteAllBytes(database + "-wal", [1]);
        using var service = new QqEmojiService(account, NewDirectory(), action => action());
        var item = new QqStickerModel { Uin = account, Md5 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        service.Mirror.Add(item);
        var sync = new QqDeepSyncService("unused-test-key", _ => service, _ => Task.FromResult(false));
        var result = sync.ReconcileAllAsync([account], QqSyncStrategy.Remove).GetAwaiter().GetResult();
        Require(result == -3, "busy database was reported as a key/schema failure");
        Require(service.Mirror.Count == 1 && !item.IsOrphaned, "active WAL did not preserve the mirror");
    }

    private static void IndexRejectsMissingColumn()
    {
        var path = CreateIndex("CREATE TABLE fav_emoji_info_storage_table (other TEXT); INSERT INTO fav_emoji_info_storage_table VALUES ('row');");
        RequireIndexFailure<Microsoft.Data.Sqlite.SqliteException>(path);
    }

    private static void IndexRejectsMalformedMd5()
    {
        var path = CreateIndex("CREATE TABLE fav_emoji_info_storage_table ([80011] TEXT); INSERT INTO fav_emoji_info_storage_table VALUES ('not-an-md5');");
        RequireIndexFailure<InvalidDataException>(path);
    }

    private static void IndexAcceptsEmptyTable()
    {
        var path = CreateIndex("CREATE TABLE fav_emoji_info_storage_table ([80011] TEXT);");
        var result = (HashSet<string>)typeof(QqDeepSyncService).GetMethod("ReadFavMd5s", PrivateStatic)!.Invoke(null, [path])!;
        Require(result.Count == 0, "a supported empty table was not accepted");
    }

    private static string CreateIndex(string sql)
    {
        var path = Path.Combine(NewDirectory(), "plain.db");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return path;
    }

    private static void RequireIndexFailure<TException>(string path) where TException : Exception
    {
        try { typeof(QqDeepSyncService).GetMethod("ReadFavMd5s", PrivateStatic)!.Invoke(null, [path]); }
        catch (TargetInvocationException ex) when (ex.InnerException is TException) { return; }
        throw new InvalidOperationException("invalid index was accepted as authoritative");
    }

    private static void DisposedUnstartedWatcherIsInactive()
    {
        using var source = NewWindow("audit-dispose");
        using var watcher = new QqPanelWatcher(_ => { });
        Set(watcher, "_editorHwnd", source.Handle);
        Set(watcher, "_editorPid", Environment.ProcessId);
        Set(watcher, "_editorRectRel", new Rect(20, 350, 40, 20));
        watcher.Dispose();
        Require(!QqPanelWatcher.TryGetCachedEditorRect(source.Handle, out _), "an unstarted disposed watcher remained globally active");
        watcher.Start();
        Require(!watcher.Running, "a disposed watcher restarted");
    }

    private static void UnconfirmedFocusDoesNotAcknowledgeEditor()
    {
        using var source = NewWindow("audit-focus");
        var focus = AutomationElement.FromLocalProvider(new FocusProbeProvider(source.Handle));
        using var watcher = new QqPanelWatcher(_ => { });
        Set(watcher, "<Running>k__BackingField", true);
        Set(watcher, "_qqPids", new HashSet<int> { Environment.ProcessId });
        Set(watcher, "_lastPidRefresh", DateTime.Now);
        var capabilities = (QqProcessCapabilities)typeof(QqPanelWatcher).GetField("_capabilities", PrivateInstance)!.GetValue(watcher)!;
        capabilities.Refresh([new(Environment.ProcessId, 1, "9.9.21.38711")]);
        SetStatic(typeof(QqPanelWatcher), "_editorFocusEchoTicks", 0L);
        typeof(QqPanelWatcher).GetMethod("OnFocusChanged", PrivateInstance)!.Invoke(watcher, [focus, null]);
        Require(QqPanelWatcher.EditorFocusEchoTicks() == 0, "an unconfirmed shell/panel element acknowledged editor focus");
        // No event handlers or hooks were installed by this test.
        Set(watcher, "<Running>k__BackingField", false);
    }

    private static void LegacyTabWithPrunedAncestors()
    {
        using var source = NewWindow("audit-pruned-tab");
        var focus = AutomationElement.FromLocalProvider(new FocusProbeProvider(source.Handle,
            "切换默认表情按钮", hasHost: false));
        using var watcher = new QqPanelWatcher(_ => { });
        Set(watcher, "<Running>k__BackingField", true);
        Set(watcher, "_qqPids", new HashSet<int> { Environment.ProcessId });
        Set(watcher, "_lastPidRefresh", DateTime.Now);
        var capabilities = (QqProcessCapabilities)typeof(QqPanelWatcher).GetField("_capabilities", PrivateInstance)!.GetValue(watcher)!;
        capabilities.Refresh([new(Environment.ProcessId, 1, "9.9.21.38711")]);
        IntPtr host = IntPtr.Zero;
        watcher.PanelAppeared += (_, e) => host = e.HostHwnd;
        try
        {
            typeof(QqPanelWatcher).GetMethod("OnFocusChanged", PrivateInstance)!.Invoke(watcher, [focus, null]);
            Require(host == source.Handle, "a real tab focus could not open coexist without UIA window ancestors");
        }
        finally { Set(watcher, "<Running>k__BackingField", false); }
    }

    private static void LaterButtonInvalidatesEditorEcho()
    {
        using var source = NewWindow("audit-stale-editor");
        using var watcher = new QqPanelWatcher(_ => { });
        Set(watcher, "_editorHwnd", source.Handle);
        Set(watcher, "_editorPid", Environment.ProcessId);
        SetStatic(typeof(QqPanelWatcher), "_editorFocusEchoTicks", 100L);
        SetStatic(typeof(QqPanelWatcher), "_lastQqFocusTicks", 200L);
        Require(QqPanelWatcher.EditorFocusEchoTicks(source.Handle) == 0,
            "a later panel/button focus left the earlier editor echo eligible for paste");
        SetStatic(typeof(QqPanelWatcher), "_lastQqFocusTicks", 100L);
        Require(QqPanelWatcher.EditorFocusEchoTicks(source.Handle) == 100L, "the current editor focus was discarded");
    }

    private static void PixelMatchReturnsBufferLocation()
    {
        var type = PixelType();
        var scaledType = type.GetNestedType("Scaled", BindingFlags.NonPublic)!;
        var template = Activator.CreateInstance(scaledType, nonPublic: true)!;
        var pixels = GlyphPixels(40);
        scaledType.GetField("Pixels")!.SetValue(template, pixels);
        scaledType.GetField("Size")!.SetValue(template, 40);
        scaledType.GetField("BgMedian")!.SetValue(template, new byte[] { 240, 240, 240 });
        scaledType.GetField("MaskCount")!.SetValue(template, 30);
        var region = SolidPixels(78);
        PlacePixels(region, 78, pixels, 40, 12, 14);
        object?[] args = [template, region, 78, 78 * 4, null, null, null];
        type.GetMethod("SearchBest", PrivateStatic)!.Invoke(null, args);
        Require((double)args[4]! == 0, "exact template was not matched");
        Require((int)args[5]! == 12 && (int)args[6]! == 14, "valid left/up match returned negative coordinates interpreted as failure by the caller");
    }

    private static void PixelDiagnosticHandlesImageEdge()
    {
        PreparePixelTemplate();
        var image = Path.Combine(NewDirectory(), "edge.png");
        WritePng(image, SolidPixels(50), 50, PixelFormats.Bgra32);
        var output = (string)PixelType().GetMethod("SelfTest")!.Invoke(null, [image, "0,0"])!;
        Require(output.Contains("click=(0,0)"), "edge diagnostic did not return a result");
    }

    private static void PixelDiagnosticHandlesRgb24()
    {
        PreparePixelTemplate();
        var pixels = SolidPixels(100);
        PlacePixels(pixels, 100, GlyphPixels(40), 40, 30, 30);
        var rgb = new byte[100 * 100 * 3];
        for (int i = 0; i < 100 * 100; i++)
        {
            rgb[i * 3] = pixels[i * 4 + 2];
            rgb[i * 3 + 1] = pixels[i * 4 + 1];
            rgb[i * 3 + 2] = pixels[i * 4];
        }
        var image = Path.Combine(NewDirectory(), "rgb.bmp");
        var rgbSource = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Rgb24, null, rgb, 300);
        var bmpEncoder = new BmpBitmapEncoder();
        bmpEncoder.Frames.Add(BitmapFrame.Create(rgbSource));
        using (var bmpFile = File.Create(image)) bmpEncoder.Save(bmpFile);
        var output = (string)PixelType().GetMethod("SelfTest")!.Invoke(null, [image, "50,50"])!;
        Require(output.Contains("xor=0.000"), "an identical 24-bit image was decoded as four bytes per pixel: " + output);
    }

    private static void JsonWriterDoesNotRequireDispatcher()
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var path = Path.Combine(NewDirectory(), "config.json");
            using var gate = new SemaphoreSlim(1, 1);
            var task = (Task)typeof(MainViewModel).GetMethod("WriteJsonWithBackupAsync", PrivateStatic)!.Invoke(null, [path, "{\"saved\":true}", gate])!;
            bool complete = task.Wait(1500);
            if (!complete) PumpPendingWorkUntil(task);
            Require(complete, "JSON write required the blocked UI dispatcher to finish");
            Require(File.ReadAllText(path) == "{\"saved\":true}", "saved JSON changed");
        }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
    }

    private static void PreparePixelTemplate()
    {
        var type = PixelType();
        var dataType = type.GetNestedType("Data", BindingFlags.NonPublic)!;
        var data = Activator.CreateInstance(dataType, nonPublic: true)!;
        var source = BitmapSource.Create(40, 40, 96, 96, PixelFormats.Bgra32, null, GlyphPixels(40), 160);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        dataType.GetProperty("PngBase64")!.SetValue(data, Convert.ToBase64String(stream.ToArray()));
        dataType.GetProperty("Size")!.SetValue(data, 40);
        dataType.GetProperty("Scale")!.SetValue(data, 1.0);
        SetStatic(type, "_disk", data);
        SetStatic(type, "_available", true);
        SetStatic(type, "_loadAttempted", 1);
        ((IDictionary)type.GetField("_scaled", PrivateStatic)!.GetValue(null)!).Clear();
    }

    private static Type PixelType() => typeof(QqPanelWatcher).Assembly.GetType("OICQStickerManager.Services.EmojiButtonTemplate")!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static void SetStatic(Type type, string name, object value) => type.GetField(name, PrivateStatic)!.SetValue(null, value);
    internal static string NewDirectory() { var path = Path.Combine(Scratch, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void RunAsync(Task task, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) { PumpPendingWork(); Thread.Sleep(5); }
        Require(task.IsCompleted, "asynchronous test timed out");
        task.GetAwaiter().GetResult();
    }
    private static HwndSource NewWindow(string name) => new(new HwndSourceParameters(name) { Width = 800, Height = 600, PositionX = -30000, PositionY = -30000, WindowStyle = unchecked((int)0x80000000) });
    private static void WaitFor(Func<bool> condition, string failure)
    {
        var stop = Environment.TickCount64 + 3500;
        while (Environment.TickCount64 < stop) { if (condition()) return; Thread.Sleep(30); }
        throw new InvalidOperationException(failure);
    }
    private static byte[] SolidPixels(int size)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 240; pixels[i + 3] = 255; }
        return pixels;
    }
    private static byte[] GlyphPixels(int size)
    {
        var pixels = SolidPixels(size);
        for (int y = 12; y < 22; y++)
            for (int x = 15; x < 18; x++) { int i = (y * size + x) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; }
        return pixels;
    }
    private static void PlacePixels(byte[] target, int width, byte[] source, int size, int x, int y)
    {
        for (int row = 0; row < size; row++) Buffer.BlockCopy(source, row * size * 4, target, ((y + row) * width + x) * 4, size * 4);
    }
    private static void WritePng(string path, byte[] pixels, int size, PixelFormat format)
    {
        var source = BitmapSource.Create(size, size, 96, 96, format, null, pixels, size * (format.BitsPerPixel / 8));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void PumpPendingWork() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private static void PumpPendingWorkUntil(Task task) { while (!task.IsCompleted) { PumpPendingWork(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); }

    private sealed class FocusProbeProvider(IntPtr hwnd, string name = "", bool hasHost = true) : IRawElementProviderSimple
    {
        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public IRawElementProviderSimple HostRawElementProvider => hasHost ? AutomationInteropProvider.HostProviderFromHandle(hwnd) : null!;
        public object? GetPatternProvider(int patternId) => null;
        public object? GetPropertyValue(int propertyId)
        {
            if (propertyId == AutomationElement.ClassNameProperty.Id) return "";
            if (propertyId == AutomationElement.NameProperty.Id) return name;
            if (propertyId == AutomationElement.NativeWindowHandleProperty.Id) return hwnd.ToInt32();
            if (propertyId == AutomationElement.ProcessIdProperty.Id) return Environment.ProcessId;
            if (propertyId == AutomationElement.ControlTypeProperty.Id) return ControlType.Custom.Id;
            if (propertyId == AutomationElement.BoundingRectangleProperty.Id) return new double[] { 0, 0, 10, 10 };
            return null;
        }
    }
}
