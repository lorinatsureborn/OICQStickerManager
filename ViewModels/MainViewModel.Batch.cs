using OICQStickerManager.Models;
using System.IO;

namespace OICQStickerManager.ViewModels;

public partial class MainViewModel
{
    private readonly HashSet<StickerModel> _batchSelection = new();
    private bool _isBatchManaging;
    public bool IsBatchManaging => _isBatchManaging;
    public int BatchSelectedCount => _batchSelection.Count;
    public bool HasBatchSelection => IsBatchManaging && BatchSelectedCount > 0;
    public string BatchSelectionText => $"已选 {BatchSelectedCount} 张";
    public string BatchSelectAllText => CurrentBatchItems().Any() && CurrentBatchItems().All(_batchSelection.Contains)
        ? "取消本页全选" : "全选本页";
    public bool HasBatchQqSelection => _batchSelection.Any(s => s is QqStickerModel);
    public string BatchEditTagsText => HasBatchQqSelection ? "导入并编辑" : "编辑标签";
    public string BatchAiText => HasBatchQqSelection ? "导入并 AI 建议" : "AI 建议标签";
    public string BatchDeleteText => _batchSelection.Count > 0 && _batchSelection.All(s => s is QqStickerModel)
        ? "清理缓存残留" : "删除图片";

    private IEnumerable<StickerModel> CurrentBatchItems() => IsQqTabActive
        ? CurrentQqService?.Mirror.Cast<StickerModel>() ?? Enumerable.Empty<StickerModel>()
        : _stickersView.Cast<StickerModel>();

    private bool OwnsBatchItem(StickerModel sticker) => sticker is QqStickerModel qq
        ? _qqServices.TryGetValue(qq.Uin, out var service) && service.Mirror.Contains(qq)
        : Stickers.Contains(sticker);

    public void EnterBatchManagement()
    {
        if (IsBatchManaging) return;
        _isBatchManaging = true;
        OnPropertyChanged(nameof(IsBatchManaging));
        NotifyBatchSelection();
    }

    public void ExitBatchManagement()
    {
        _isBatchManaging = false;
        ClearBatchSelection();
        OnPropertyChanged(nameof(IsBatchManaging));
    }

    public void ToggleBatchSticker(StickerModel sticker)
    {
        if (!IsBatchManaging || !OwnsBatchItem(sticker)) return;
        if (!_batchSelection.Add(sticker)) _batchSelection.Remove(sticker);
        sticker.IsBatchSelected = _batchSelection.Contains(sticker);
        NotifyBatchSelection();
    }

    /// <summary>只选择当前筛选结果，包含尚未滚动到、尚未生成控件的图片。</summary>
    public void SelectAllBatchStickers()
    {
        if (!IsBatchManaging) return;
        PruneBatchSelection();
        var visible = CurrentBatchItems().ToArray();
        var clear = visible.Length > 0 && visible.All(_batchSelection.Contains);
        foreach (var sticker in visible)
        {
            if (clear) _batchSelection.Remove(sticker);
            else _batchSelection.Add(sticker);
            sticker.IsBatchSelected = !clear;
        }
        NotifyBatchSelection();
    }

    public List<StickerModel> GetBatchSelection()
    {
        PruneBatchSelection();
        return _batchSelection.ToList();
    }

    public void RefreshGalleryTags() => _stickersView.Refresh();

    public async Task<List<StickerModel>> PrepareBatchTagEditingAsync(IEnumerable<StickerModel> stickers)
    {
        var snapshot = stickers.Where(OwnsBatchItem).ToArray();
        var qq = snapshot.OfType<QqStickerModel>().ToArray();
        if (qq.Length > 0) await ImportQqCoreAsync(qq, raiseCompleted: false);
        return snapshot.Select(s => s is QqStickerModel
                ? Stickers.FirstOrDefault(copy => string.Equals(copy.Md5, s.Md5, StringComparison.OrdinalIgnoreCase)) : s)
            .Where(s => s != null && Stickers.Contains(s)).Cast<StickerModel>().Distinct().ToList();
    }

    public async Task<(int Deleted, int Preserved, int Failed)> DeleteQqOrphansAsync(IEnumerable<QqStickerModel> stickers)
    {
        var snapshot = stickers.Distinct().ToArray();
        var candidates = snapshot.Where(IsOwnedQqCache).ToArray();
        var deep = candidates.Length > 0 ? await GetOrCreateDeepSyncAsync() : null;
        var confirmed = deep == null ? new List<QqStickerModel>() : await deep.ConfirmOrphansAsync(candidates);
        int deleted = 0, failed = 0;
        foreach (var sticker in confirmed)
        {
            // 核验期间账号可能解绑、文件可能移位；只删除所属账号 Ori 目录中的单个文件。
            if (!IsOwnedQqCache(sticker)) continue;
            try
            {
                await Task.Run(() => File.Delete(sticker.FullPath));
                if (_qqServices.TryGetValue(sticker.Uin, out var service)) service.Mirror.Remove(sticker);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        UpdateCounts();
        return (deleted, snapshot.Length - deleted - failed, failed);
    }

    private bool IsOwnedQqCache(QqStickerModel sticker)
    {
        if (!OwnsBatchItem(sticker) || !_qqServices.TryGetValue(sticker.Uin, out var service)) return false;
        try
        {
            return string.Equals(Path.GetFileNameWithoutExtension(sticker.FullPath), sticker.Md5, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(sticker.FullPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(service.OriDir)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void ClearBatchSelection()
    {
        foreach (var sticker in _batchSelection) sticker.IsBatchSelected = false;
        _batchSelection.Clear();
        NotifyBatchSelection();
    }

    private void PruneBatchSelection()
    {
        if (_batchSelection.Count > 0)
        {
            var owned = Stickers.Concat<StickerModel>(_qqServices.Values.SelectMany(s => s.Mirror)).ToHashSet();
            foreach (var sticker in _batchSelection.Where(s => !owned.Contains(s)).ToArray())
            {
                _batchSelection.Remove(sticker);
                sticker.IsBatchSelected = false;
                // QQ 目录重扫会替换模型对象；同账号、同路径的图片仍是同一个选择。
                if (sticker is QqStickerModel qq && _qqServices.TryGetValue(qq.Uin, out var service))
                {
                    var replacement = service.Mirror.FirstOrDefault(s => string.Equals(s.FullPath, qq.FullPath, StringComparison.OrdinalIgnoreCase));
                    if (replacement != null) { _batchSelection.Add(replacement); replacement.IsBatchSelected = true; }
                }
            }
        }
        NotifyBatchSelection();
    }

    private void NotifyBatchSelection()
    {
        OnPropertyChanged(nameof(BatchSelectedCount));
        OnPropertyChanged(nameof(BatchSelectionText));
        OnPropertyChanged(nameof(HasBatchSelection));
        OnPropertyChanged(nameof(BatchSelectAllText));
        OnPropertyChanged(nameof(HasBatchQqSelection));
        OnPropertyChanged(nameof(BatchEditTagsText));
        OnPropertyChanged(nameof(BatchAiText));
        OnPropertyChanged(nameof(BatchDeleteText));
    }
}
