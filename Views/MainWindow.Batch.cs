using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OICQStickerManager.Models;
using OICQStickerManager.ViewModels;

namespace OICQStickerManager.Views;

public partial class MainWindow
{
    private void EnterBatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        CloseGifPreview();
        vm.EnterBatchManagement();
    }

    private void ExitBatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ExitBatchManagement();
    }

    private void BatchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectAllBatchStickers();
    }

    private void BatchClear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ClearBatchSelection();
    }

    private void Gallery_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBatchManaging: true } vm) return;
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(StickersList, source) is not ListBoxItem item
            || item.DataContext is not StickerModel sticker) return;
        if (e.ChangedButton == MouseButton.Left)
        {
            item.Focus();
            if (e.ClickCount == 1) vm.ToggleBatchSticker(sticker);
            e.Handled = true; // 拦截发送命令和 ListBox 的默认单选。
        }
    }

    private void GalleryTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is MainViewModel { IsBatchManaging: true }) e.Handled = true;
    }

    private async void BatchEditTags_Click(object sender, RoutedEventArgs e) => await OpenBatchSelectionEditorAsync();

    private async Task<bool> OpenBatchSelectionEditorAsync()
    {
        if (DataContext is not MainViewModel { IsBatchManaging: true } vm) return false;
        var selected = vm.GetBatchSelection();
        if (selected.Count == 0) return false;
        List<StickerModel> stickers;
        BatchToolbar.IsEnabled = false;
        try { stickers = await vm.PrepareBatchTagEditingAsync(selected); }
        catch (Exception ex)
        {
            await ShowAlertAsync("无法编辑标签", ex.Message, "知道了", showCancel: false);
            return false;
        }
        finally { BatchToolbar.IsEnabled = true; }
        if (stickers.Count == 0)
        {
            await ShowAlertAsync("导入失败", "所选 QQ 图片未能复制到图库，原缓存已保留。", "知道了", showCancel: false);
            return false;
        }
        ShowBatchTagEditor(stickers, "批量编辑标签",
            $"已选 {stickers.Count} 张图片；下方为共有标签。增删会应用到整批，各图片独有标签保留。" +
            "AI 会逐张识别并合并候选，点选的建议也会添加到整批。" +
            (stickers.Count < selected.Count ? "重复图片已合并，无法导入的图片已跳过。" : ""));
        return true;
    }

    private async void BatchAi_Click(object sender, RoutedEventArgs e)
    {
        if (await OpenBatchSelectionEditorAsync()) await RunEditorAiAsync(forceRefresh: false);
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsBatchManaging: true } vm) return;
        var stickers = vm.GetBatchSelection();
        if (stickers.Count == 0) return;
        var library = stickers.Where(s => s is not QqStickerModel).ToList();
        var qq = stickers.OfType<QqStickerModel>().ToList();
        var title = library.Count == 0 ? "清理 QQ 缓存残留" : "删除所选图片";
        var detail = library.Count > 0 ? $"将从图库和磁盘中删除 {library.Count} 张图库图片，无法恢复。" : "";
        if (qq.Count > 0) detail += $"\n将核验所选 {qq.Count} 张 QQ 图片，只删除已取消收藏的缓存残留；仍在收藏或无法确认的图片会保留。\n缓存清理不会删除已导入图库的副本。";
        if (!await ShowAlertAsync(title, detail.Trim(), library.Count == 0 ? "清理" : "删除", destructive: true)) return;
        BatchToolbar.IsEnabled = false;
        CloseGifPreview();
        string? resultMessage = null;
        Exception? failure = null;
        try
        {
            if (library.Count > 0) await vm.DeleteStickersAsync(library);
            if (qq.Count > 0)
            {
                var result = await vm.DeleteQqOrphansAsync(qq);
                vm.StatusText = $"QQ 缓存：已清理 {result.Deleted} 张，保留 {result.Preserved} 张，未删除 {result.Failed} 张";
                resultMessage = $"已清理 {result.Deleted} 张缓存残留。\n保留 {result.Preserved} 张仍在收藏或无法确认的图片。" +
                    (result.Failed > 0 ? $"\n{result.Failed} 张文件被占用或无权限，未能删除。" : "");
            }
        }
        catch (Exception ex) { failure = ex; vm.StatusText = $"删除异常: {ex.Message}"; }
        finally { BatchToolbar.IsEnabled = true; }
        // 操作已结束就解除禁用；结果提示的关闭不再控制工具栏的生命周期。
        if (failure != null) await ShowAlertAsync("删除未完成", failure.Message, "知道了", showCancel: false);
        else if (resultMessage != null) await ShowAlertAsync("QQ 缓存清理结果", resultMessage, "知道了", showCancel: false);
    }

    private void ShowBatchTagEditor(List<StickerModel> stickers, string title, string subtitle)
    {
        IEnumerable<string> common = stickers[0].Tags;
        foreach (var s in stickers.Skip(1)) common = common.Intersect(s.Tags);
        _batchOriginalTags = common.Distinct().ToList();
        _editingTags = new List<string>(_batchOriginalTags);
        _pendingStickers = stickers;
        _batchAdjustMode = true;
        _suggestedTag = null;
        TagInputBox.Text = "";
        TagEditorTitle.Text = title;
        TagEditorSubtitle.Text = subtitle;
        SetTagEditorPreview(stickers[0]);
        ClearAiSuggestions();
        RefreshEditorUI();
        ShowOverlay(TagEditorOverlay, TagEditorSheet, TagEditorSheetScale, 0.94);
        TagInputBox.Focus();
    }
}
