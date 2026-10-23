using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;
using OICQStickerManager.Views;

internal static class BatchManagementTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Batch select all respects search and toggles the complete filtered set", FilteredSelection),
        ("Batch selection survives filters and tabs but drops removed images", SelectionLifetime),
        ("Batch management selects across library and QQ account tabs", QqBoundary),
        ("Batch selection survives QQ mirror object replacement", MirrorReplacement),
        ("QQ batch cleanup reads once and preserves favorites and unowned paths", VerifiedCacheCleanup),
        ("QQ batch cleanup preserves files when the index is unavailable or changing", UnavailableCacheIndex),
        ("QQ batch cleanup reports locked files without hiding them", LockedCacheCleanup),
        ("QQ batch cleanup rechecks account ownership after index reads", UnboundDuringCleanup),
        ("Mixed batch tag editing imports QQ images and merges existing copies", MixedTagEditing),
        ("QQ preview uses its valid thumbnail when the original cannot decode", ThumbnailFallback),
        ("Batch select all includes images without generated UI containers", LargeSelection),
        ("Batch selection never persists in sticker metadata", SelectionIsTransient),
        ("Batch click commands select without activating the send path", ClickCommands),
    ];
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MainViewModel Create(int count = 3)
    {
        var vm = LibraryTests.Create(Program.NewDirectory());
        Program.RunAsync(vm.Initialization);
        for (var i = 0; i < count; i++) vm.Stickers.Add(new StickerModel { Tags = [i < 2 ? "共同" : "其他", $"独有{i}"] });
        vm.UpdateTabTags(); Pump();
        return vm;
    }
    private static void Pump() => Program.RunAsync(Task.Delay(30));
    private static void FilteredSelection()
    {
        var vm = Create();
        vm.SearchText = "共同"; Pump();
        vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        Program.Require(vm.BatchSelectedCount == 2 && !vm.Stickers[2].IsBatchSelected, "select all included a hidden image");
        vm.SelectAllBatchStickers();
        Program.Require(vm.BatchSelectedCount == 0 && vm.Stickers.All(s => !s.IsBatchSelected), "cancel all left selection flags behind");
        vm.ToggleBatchSticker(vm.Stickers[0]); vm.SelectAllBatchStickers();
        Program.Require(vm.GetBatchSelection().Count == 2, "select all did not fill a partial selection");
    }
    private static void SelectionLifetime()
    {
        var vm = Create(); vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        vm.SearchText = "共同"; Pump();
        Program.Require(vm.BatchSelectedCount == 3 && vm.Stickers[2].IsBatchSelected, "filtering cleared selected images");
        var removed = vm.Stickers[0]; vm.Stickers.Remove(removed); Pump();
        Program.Require(vm.BatchSelectedCount == 2 && !removed.IsBatchSelected, "removed image remained selected");
        vm.ExitBatchManagement(); vm.EnterBatchManagement();
        Program.Require(!vm.HasBatchSelection && vm.Stickers.All(s => !s.IsBatchSelected), "re-entry restored stale selections");
        vm.SelectAllBatchStickers(); vm.SelectedTab = "共同";
        Program.Require(vm.IsBatchManaging && vm.BatchSelectedCount == 1, "changing the tag page cleared the batch selection");
    }
    private static void QqBoundary()
    {
        var vm = Create();
        var service = AddQqService(vm);
        var qq = new QqStickerModel { Uin = service.Uin, Md5 = new string('A', 32), FullPath = Path.Combine(service.OriDir, "cache.png") };
        service.Mirror.Add(qq);
        vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        vm.SelectedTab = "qq:synthetic";
        Program.Require(vm.IsBatchManaging && vm.BatchSelectedCount == 3, "QQ tab cleared library selections or exited batch mode");
        vm.SelectAllBatchStickers();
        Program.Require(vm.BatchSelectedCount == 4 && qq.IsBatchSelected, "QQ select all did not add its mirror images");
        vm.SelectAllBatchStickers();
        Program.Require(vm.BatchSelectedCount == 3 && !qq.IsBatchSelected, "QQ cancel all cleared selections from other tabs");
        vm.ToggleBatchSticker(qq); vm.SelectedTab = "最近";
        Program.Require(vm.GetBatchSelection().Count == 4 && qq.IsBatchSelected, "switching away from QQ lost its selection");
        vm.ToggleBatchSticker(new QqStickerModel { Uin = service.Uin });
        Program.Require(vm.BatchSelectedCount == 4, "unowned QQ item was accepted");
    }
    internal static QqEmojiService AddQqService(MainViewModel vm)
    {
        var service = new QqEmojiService("synthetic", Program.NewDirectory(), action => action());
        var services = (Dictionary<string, QqEmojiService>)typeof(MainViewModel).GetField("_qqServices", Private)!.GetValue(vm)!;
        services.Add(service.Uin, service);
        return service;
    }
    private static void MirrorReplacement()
    {
        var vm = Create(); var service = AddQqService(vm);
        var first = new QqStickerModel { Uin = service.Uin, Md5 = new string('B', 32), FullPath = Path.Combine(service.OriDir, "cache.png") };
        service.Mirror.Add(first); vm.SelectedTab = "qq:synthetic"; vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        service.Mirror.Clear();
        var replacement = new QqStickerModel { Uin = first.Uin, Md5 = first.Md5, FullPath = first.FullPath };
        service.Mirror.Add(replacement);
        var selected = vm.GetBatchSelection();
        Program.Require(selected.Count == 1 && ReferenceEquals(selected[0], replacement) && replacement.IsBatchSelected && !first.IsBatchSelected,
            "mirror rescan lost selection or kept the retired UI model");
    }
    private static QqStickerModel AddCache(QqEmojiService service, char key, string? path = null)
    {
        var hash = new string(key, 32);
        path ??= Path.Combine(service.OriDir, hash + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
        var sticker = new QqStickerModel { Uin = service.Uin, Md5 = hash, FullPath = path };
        service.Mirror.Add(sticker);
        return sticker;
    }
    private static void SetCacheIndex(MainViewModel vm, QqEmojiService service, Func<Task<(HashSet<string>?, bool)>> read)
    {
        var sync = new QqDeepSyncService("synthetic", _ => service, _ => Task.FromResult(false), _ => read());
        typeof(MainViewModel).GetField("_deepSync", Private)!.SetValue(vm, sync);
    }
    private static void VerifiedCacheCleanup()
    {
        var vm = Create(0); var service = AddQqService(vm);
        var first = AddCache(service, 'A'); var second = AddCache(service, 'B');
        var active = AddCache(service, 'C'); active.IsOrphaned = true;
        var unknown = AddCache(service, 'D'); unknown.Md5 = "invalid";
        var outside = AddCache(service, 'E', Path.Combine(Program.NewDirectory(), "outside.png"));
        var unowned = new QqStickerModel { Uin = service.Uin, Md5 = new string('F', 32), FullPath = outside.FullPath };
        var libraryCopy = new StickerModel { Md5 = first.Md5, FullPath = Path.Combine(Program.NewDirectory(), "library.png") };
        File.Copy(first.FullPath, libraryCopy.FullPath); vm.Stickers.Add(libraryCopy);
        int reads = 0;
        SetCacheIndex(vm, service, () => { reads++; return Task.FromResult<(HashSet<string>?, bool)>((new HashSet<string> { active.Md5! }, false)); });
        vm.EnterBatchManagement(); vm.ToggleBatchSticker(first); vm.ToggleBatchSticker(libraryCopy);
        var result = RunCleanup(vm, new[] { first, second, active, unknown, outside, unowned });
        Program.Require(reads == 1 && result == (2, 4, 0), "cleanup did not verify one account snapshot or misreported outcomes");
        Program.Require(!File.Exists(first.FullPath) && !File.Exists(second.FullPath) && File.Exists(active.FullPath)
            && File.Exists(unknown.FullPath) && File.Exists(outside.FullPath) && File.Exists(libraryCopy.FullPath), "cleanup deleted a favorite, unknown path or library copy");
        Program.Require(!active.IsOrphaned && vm.BatchSelectedCount == 1 && libraryCopy.IsBatchSelected, "cleanup retained stale selection or refavorite flags");
    }
    private static void UnavailableCacheIndex()
    {
        foreach (var deferred in new[] { false, true })
        {
            var vm = Create(0); var service = AddQqService(vm); var item = AddCache(service, 'A'); item.IsOrphaned = true;
            SetCacheIndex(vm, service, () => Task.FromResult<(HashSet<string>?, bool)>((null, deferred)));
            var result = RunCleanup(vm, new[] { item });
            Program.Require(result == (0, 1, 0) && File.Exists(item.FullPath) && service.Mirror.Contains(item), "unavailable or changing index was treated as an empty favorite list");
        }
    }
    private static void LockedCacheCleanup()
    {
        var vm = Create(0); var service = AddQqService(vm); var item = AddCache(service, 'A');
        SetCacheIndex(vm, service, () => Task.FromResult<(HashSet<string>?, bool)>((new HashSet<string>(), false)));
        using var locked = File.Open(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = RunCleanup(vm, new[] { item });
        Program.Require(result == (0, 0, 1) && File.Exists(item.FullPath) && service.Mirror.Contains(item), "locked cache was hidden or reported as deleted");
    }
    private static void UnboundDuringCleanup()
    {
        var vm = Create(0); using var service = AddQqService(vm); var item = AddCache(service, 'A');
        SetCacheIndex(vm, service, async () =>
        {
            await Task.Delay(20);
            ((Dictionary<string, QqEmojiService>)typeof(MainViewModel).GetField("_qqServices", Private)!.GetValue(vm)!).Remove(service.Uin);
            return (new HashSet<string>(), false);
        });
        var result = RunCleanup(vm, new[] { item });
        Program.Require(result == (0, 1, 0) && File.Exists(item.FullPath), "cache cleanup continued after its account was unbound");
    }
    private static (int Deleted, int Preserved, int Failed) RunCleanup(MainViewModel vm, IEnumerable<QqStickerModel> images)
    {
        (int, int, int) result = default;
        Program.RunAsync(Run()); return result;
        async Task Run() { result = await vm.DeleteQqOrphansAsync(images); }
    }
    private static void MixedTagEditing()
    {
        var vm = Create(0); var service = AddQqService(vm);
        var first = AddCache(service, 'A'); var second = AddCache(service, 'B');
        var existing = new StickerModel { Md5 = first.Md5, Tags = ["保留"], FullPath = Path.Combine(Program.NewDirectory(), "existing.png") };
        File.Copy(first.FullPath, existing.FullPath); vm.Stickers.Add(existing);
        List<StickerModel>? prepared = null;
        Program.RunAsync(Run());
        async Task Run() { prepared = await vm.PrepareBatchTagEditingAsync(new StickerModel[] { existing, first, second }); }
        Program.Require(prepared!.Count == 2 && prepared.Contains(existing) && existing.Tags.Contains("保留")
            && vm.Stickers.All(s => s is not QqStickerModel) && File.Exists(first.FullPath) && File.Exists(second.FullPath), "mixed editing lost tags, duplicated an existing image or consumed a QQ cache");
        Program.RunAsync(Run());
        Program.Require(vm.Stickers.Count == 2, "reopening mixed editing duplicated imported QQ images");
    }
    private static void ThumbnailFallback()
    {
        var vm = Create(0); var service = AddQqService(vm); var image = AddCache(service, 'A');
        var thumbnail = Path.Combine(service.OriDir, "thumbnail.png"); File.Copy(image.FullPath, thumbnail);
        image.ThumbPath = thumbnail;
        Program.Require(image.ImageSource?.UriSource.LocalPath == image.FullPath, "QQ thumbnail replaced a decodable original");
        // 使用新 URI，避免 WIC 复用上一步已成功解码的原图缓存。
        var originalPath = Path.Combine(service.OriDir, "undecodable.jpg");
        File.WriteAllBytes(originalPath, new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0, 20 });
        image.FullPath = originalPath;
        var preview = image.ImageSource;
        Program.Require(preview?.UriSource.LocalPath == thumbnail && preview.IsFrozen && ReferenceEquals(preview, image.ImageSource)
            && image.FullPath == originalPath, "an undecodable QQ original did not use/cache its thumbnail or its send/delete path changed");
        File.Delete(image.FullPath); image.FullPath = originalPath;
        Program.Require(image.ImageSource?.UriSource.LocalPath == thumbnail, "a missing original prevented its valid thumbnail preview");
        File.Delete(thumbnail); image.FullPath = originalPath;
        Program.Require(image.ImageSource == null, "missing original and thumbnail did not degrade safely");
    }
    private static void LargeSelection()
    {
        var vm = Create(1200); vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        Program.Require(vm.BatchSelectedCount == 1200 && vm.Stickers.All(s => s.IsBatchSelected), "off-screen images were not selected");
    }
    private static void SelectionIsTransient()
    {
        var vm = Create(); vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        var serialized = JsonSerializer.Serialize(vm.Stickers);
        Program.Require(!serialized.Contains("IsBatchSelected"), "selection leaked into serialized models");
        Program.RunAsync(vm.SaveDatabaseAsync());
        var path = (string)typeof(MainViewModel).GetProperty("DbPath", Private)!.GetValue(vm)!;
        Program.Require(!File.ReadAllText(path).Contains("IsBatchSelected"), "selection leaked into the library database");
    }
    private static void ClickCommands()
    {
        var vm = Create(); var sticker = vm.Stickers[0]; vm.EnterBatchManagement();
        foreach (var mode in new[] { SendMode.SingleClick, SendMode.DoubleClick })
        {
            vm.CurrentSendMode = mode;
            Program.RunAsync((Task)typeof(MainViewModel).GetMethod("HandleStickerClickAsync", Private)!.Invoke(vm, [sticker])!);
            var selected = sticker.IsBatchSelected;
            Program.RunAsync((Task)typeof(MainViewModel).GetMethod("HandleStickerDoubleClickAsync", Private)!.Invoke(vm, [sticker])!);
            Program.Require(sticker.IsBatchSelected == selected && sticker.UseCount == 0, "batch double click activated sending or changed selection");
        }
    }

    // Reuse the real hidden WPF window and its isolated profile from AiUiTests.
    internal static void VerifyWindow(MainWindow window, MainViewModel vm, string root)
    {
        vm.Stickers.Clear();
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        for (var i = 0; i < 6; i++)
        {
            var path = Path.Combine(root, "Library", $"batch-{i}.png");
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)(70 + i * 25), 140, 180)), null, new Rect(0, 0, 96, 96), 12, 12);
                dc.DrawEllipse(Brushes.White, null, new Point(32, 38), 7, 9);
                dc.DrawEllipse(Brushes.White, null, new Point(64, 38), 7, 9);
                dc.DrawEllipse(Brushes.White, null, new Point(48, 66), 14, 5);
            }
            var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            vm.Stickers.Add(new StickerModel { FullPath = path, Md5 = $"batch-ui-{i}", Tags = ["共同", $"独有{i}"] });
        }
        vm.UpdateTabTags(); Pump();
        Click(window, "EnterBatchButton");
        Program.Require(vm.IsBatchManaging && !Element(window, "BatchEditTagsButton").IsEnabled, "empty batch actions are enabled");
        Click(window, "BatchSelectAllButton"); Pump();
        Program.Require(vm.BatchSelectedCount == 6 && Element(window, "BatchEditTagsButton").IsEnabled, "toolbar did not update after select all");
        Layout(window, 760, 480);
        var list = (ListBox)Element(window, "StickersList");
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var presenter = Find<ContentPresenter>(first)!;
        var outline = (Border)list.ItemTemplate.FindName("BatchOutline", presenter);
        var check = (Border)list.ItemTemplate.FindName("BatchCheck", presenter);
        Program.Require(outline.BorderThickness.Left == 4 && outline.Visibility == Visibility.Visible && check.Visibility == Visibility.Visible,
            "selected image lacks the thick outline or checkmark");
        var firstSticker = (StickerModel)first.DataContext;
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        outline.RaiseEvent(click); Pump();
        Program.Require(click.Handled && !firstSticker.IsBatchSelected, "gallery click did not toggle batch selection or suppress sending");
        Program.Require(outline.Visibility == Visibility.Collapsed && check.Visibility == Visibility.Collapsed, "deselected tile retained selection visuals");
        vm.ToggleBatchSticker(firstSticker);
        Click(window, "BatchEditTagsButton");
        var tags = (List<string>)typeof(MainWindow).GetField("_editingTags", Private)!.GetValue(window)!;
        Program.Require(tags.SequenceEqual(new[] { "共同" }), "editor did not start from the shared tags");
        tags.Clear(); tags.Add("新增");
        Invoke(window, "SaveTag_Click", window, new RoutedEventArgs());
        WaitUntil(() => vm.Stickers.All(s => s.Tags.Contains("新增")));
        Program.Require(vm.Stickers.Select((s, i) => s.Tags.Contains($"独有{i}") && !s.Tags.Contains("共同")).All(x => x),
            "batch edit lost individual tags or failed to remove the common tag");
        Pump(); Program.RunAsync(Task.Delay(220));
        Click(window, "BatchEditTagsButton");
        tags = (List<string>)typeof(MainWindow).GetField("_editingTags", Private)!.GetValue(window)!;
        tags.Add("取消不保存"); Invoke(window, "CloseTagEditor_Click", window, new RoutedEventArgs());
        Program.Require(vm.Stickers.All(s => !s.Tags.Contains("取消不保存")), "cancelling the editor changed tags");
        Program.RunAsync(Task.Delay(220));

        // Real production batch runner with synthetic HTTP responses: cap, cache, partial failure, cancellation.
        using var handler = new BatchHandler();
        vm.AiTag.Dispose();
        typeof(MainViewModel).GetField("<AiTag>k__BackingField", Private)!.SetValue(vm, new AiTagService(handler));
        vm.AiTagProvider = "openai"; vm.AiTagApiKey = "sk-proj-batch-synthetic";
        Click(window, "BatchEditTagsButton");
        var pending = (Task)Invoke(window, "RunEditorAiAsync", true)!;
        Program.RunAsync(pending);
        Program.Require(handler.Calls == 6 && handler.Maximum == 2, $"batch AI made {handler.Calls} requests at concurrency {handler.Maximum}");
        Program.Require(((TextBlock)Element(window, "AiSuggestMetaText")).Text.Contains("5/6"), "partial failure was not visible");
        Program.Require(vm.Stickers.All(s => !s.Tags.Contains("候选")), "AI wrote tags before the user accepted them");
        var before = handler.Calls;
        pending = (Task)Invoke(window, "RunEditorAiAsync", false)!;
        Invoke(window, "AlertConfirm_Click", window, new RoutedEventArgs());
        Program.RunAsync(pending);
        Program.Require(handler.Calls == before + 1, "successful images did not reuse their cached suggestions");
        handler.Delay = 5000;
        before = handler.Calls;
        pending = (Task)Invoke(window, "RunEditorAiAsync", true)!;
        WaitUntil(() => handler.Calls >= before + 2);
        Invoke(window, "CloseTagEditor_Click", window, new RoutedEventArgs());
        Program.RunAsync(pending);
        Program.Require(handler.Calls == before + 2 && handler.Active == 0, "closing the editor left requests running or started queued images");
        Program.RunAsync(Task.Delay(220));

        // Exercise the destructive action only against this temporary fixture library.
        var keep = vm.Stickers[^1]; vm.ToggleBatchSticker(keep);
        Click(window, "BatchDeleteButton");
        Program.Require(vm.Stickers.Count == 6, "batch deletion bypassed confirmation");
        Invoke(window, "CancelAlert"); Program.RunAsync(Task.Delay(220));
        Program.Require(vm.Stickers.Count == 6, "cancelled deletion removed images");
        Click(window, "BatchDeleteButton"); Invoke(window, "AlertConfirm_Click", window, new RoutedEventArgs());
        WaitUntil(() => vm.Stickers.Count == 1 && Element(window, "BatchToolbar").IsEnabled);
        Program.Require(vm.Stickers[0] == keep && File.Exists(keep.FullPath) && Directory.GetFiles(Path.Combine(root, "Library")).Length == 1,
            "batch deletion removed the unselected image or left selected files behind");

        // Save a reviewable rendering at the minimum supported window size.
        vm.ExitBatchManagement(); vm.EnterBatchManagement(); vm.SelectAllBatchStickers();
        Layout(window, 760, 480); Program.RunAsync(Task.Delay(300));
        var screenshot = Environment.GetEnvironmentVariable("ASUKA_BATCH_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshot))
        {
            Layout(window, 760, 480);
            var bitmap = new RenderTargetBitmap(760, 480, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render((Visual)window.Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(screenshot); encoder.Save(output);
        }
        VerifyQqWindow(window, vm, keep);
        vm.ExitBatchManagement();
        Console.WriteLine("PASS Batch WPF toolbar, visuals, tag save/cancel, delete confirmation, AI concurrency/cache/failure/cancellation");
    }
    private static void VerifyQqWindow(MainWindow window, MainViewModel vm, StickerModel keep)
    {
        var service = AddQqService(vm);
        var active = AddCache(service, 'A'); active.IsOrphaned = false;
        var orphan = AddCache(service, 'B'); orphan.IsOrphaned = true;
        File.Copy(keep.FullPath, active.FullPath, overwrite: true);
        File.Copy(keep.FullPath, orphan.FullPath, overwrite: true);
        SetCacheIndex(vm, service, () => Task.FromResult<(HashSet<string>?, bool)>((new HashSet<string> { active.Md5! }, false)));
        vm.TabTags.Insert(1, new TabItemModel("qq:synthetic", "QQ（测试）", isQq: true));
        vm.SelectedTab = "qq:synthetic"; Pump();
        Program.Require(vm.IsBatchManaging && keep.IsBatchSelected, "QQ page exited batch mode or lost another page's selection");
        vm.ExitBatchManagement(); Layout(window, 760, 480);
        Program.Require(Element(window, "EnterBatchButton").Visibility == Visibility.Visible
            && ((FrameworkElement)Element(window, "EnterBatchButton").Parent).Visibility == Visibility.Visible,
            "QQ page hid the batch management entry");
        Click(window, "EnterBatchButton"); Click(window, "BatchSelectAllButton"); Pump(); Layout(window, 760, 480);
        var list = (ListBox)Element(window, "StickersList");
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var presenter = Find<ContentPresenter>(first)!;
        var outline = (Border)list.ItemTemplate.FindName("BatchOutline", presenter);
        var check = (Border)list.ItemTemplate.FindName("BatchCheck", presenter);
        Program.Require(outline.Visibility == Visibility.Visible && outline.BorderThickness.Left == 4 && check.Visibility == Visibility.Visible,
            "QQ selected images lack outline/checkmark visuals");
        var orphanItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(orphan);
        var orphanPresenter = Find<ContentPresenter>(orphanItem)!;
        var orphanBadge = (Border)list.ItemTemplate.FindName("OrphanBadge", orphanPresenter);
        var statusLabel = (TextBlock)list.ItemTemplate.FindName("FavoriteStatusLabel", orphanPresenter);
        Program.Require(orphanBadge.Visibility == Visibility.Visible && statusLabel.Text == "缓存残留"
            && ((TextBlock)Element(window, "StickerCountLabel")).Text.Contains("缓存残留 1"),
            "the QQ tile or footer concealed its verified cache-remnant classification");
        orphan.IsOrphaned = false; Pump();
        Program.Require(orphanBadge.Visibility == Visibility.Collapsed, "a re-favorited QQ image retained its cache-remnant badge");
        orphan.IsOrphaned = true; Pump();
        var screenshot = Environment.GetEnvironmentVariable("ASUKA_BATCH_QQ_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshot))
        {
            Program.RunAsync(Task.Delay(300)); Layout(window, 760, 480);
            var bitmap = new RenderTargetBitmap(760, 480, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)window.Content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(screenshot); encoder.Save(output);
        }
        vm.SelectedTab = "最近"; Pump();
        Program.Require(vm.BatchSelectedCount == 2 && ((Button)Element(window, "BatchDeleteButton")).Content.ToString() == "清理缓存残留",
            "batch actions depend on the current tag instead of the selected images");
        Click(window, "BatchDeleteButton");
        Program.Require(service.Mirror.Count == 2, "QQ batch cleanup bypassed confirmation");
        Invoke(window, "AlertConfirm_Click", window, new RoutedEventArgs());
        WaitUntil(() => service.Mirror.Count == 1 && ((TextBlock)Element(window, "AlertTitle")).Text == "QQ 缓存清理结果");
        Program.RunAsync(Task.Delay(350));
        Program.Require(Element(window, "AlertOverlay").Visibility == Visibility.Visible,
            "the confirmation dialog's closing animation hid the new cleanup result and stranded the batch toolbar");
        Program.Require(Element(window, "BatchDoneButton").IsEnabled,
            "cleanup finished but the batch toolbar still depends on dismissing its result dialog");
        Program.Require(File.Exists(active.FullPath) && !File.Exists(orphan.FullPath) && File.Exists(keep.FullPath),
            "QQ batch UI deleted a favorite/library copy or retained its confirmed orphan");
        Invoke(window, "AlertConfirm_Click", window, new RoutedEventArgs()); Pump();
        Click(window, "BatchClearButton");
        Program.Require(vm.BatchSelectedCount == 0 && !active.IsBatchSelected, "clear all did not reset selections on another page");

        // The live failure: an undecodable image remains a favorite, so verification finishes without deleting a file.
        File.WriteAllBytes(active.FullPath, new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0, 20 });
        active.ThumbPath = keep.FullPath; active.FullPath = active.FullPath;
        vm.ToggleBatchSticker(active);
        Click(window, "BatchDeleteButton"); Invoke(window, "AlertConfirm_Click", window, new RoutedEventArgs());
        WaitUntil(() => ((TextBlock)Element(window, "AlertTitle")).Text == "QQ 缓存清理结果");
        Program.RunAsync(Task.Delay(350));
        Program.Require(Element(window, "AlertOverlay").Visibility == Visibility.Visible && Element(window, "BatchDoneButton").IsEnabled
            && ((TextBlock)Element(window, "AlertMessage")).Text.Contains("已清理 0 张") && File.Exists(active.FullPath),
            "zero-deletion result hid itself or stranded the batch toolbar");
        Invoke(window, "CancelAlert"); Program.RunAsync(Task.Delay(200));
        Click(window, "BatchDoneButton");
        Program.Require(!vm.IsBatchManaging && Element(window, "EnterBatchButton").Visibility == Visibility.Visible,
            "batch management could not exit after the zero-deletion result was dismissed");
        Console.WriteLine("PASS Cleanup result survives closing animations, unlocks the toolbar and allows batch exit after zero deletions");
        Console.WriteLine("PASS QQ WPF entry, selection visuals, cross-tab actions, verified cache cleanup and global clear");
    }
    private static FrameworkElement Element(MainWindow window, string name) => (FrameworkElement)window.FindName(name);
    private static object? Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static void Click(MainWindow window, string name) => ((Button)Element(window, name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(MainWindow window, double width, double height)
    {
        window.Width = width; window.Height = height;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
    }
    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T found) return found; if (Find<T>(child) is T nested) return nested; }
        return null;
    }
    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline) Program.RunAsync(Task.Delay(10));
        Program.Require(condition(), "batch UI operation did not complete");
    }
    private sealed class BatchHandler : HttpMessageHandler
    {
        internal int Calls, Active, Maximum, Delay = 60;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active); Maximum = Math.Max(Maximum, active);
            try
            {
                await Task.Delay(Delay, ct);
                if (call == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("synthetic failure") };
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"[\\\"候选\\\",\\\"开心\\\"]\"}}]}") };
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
