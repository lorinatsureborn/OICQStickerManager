using OICQStickerManager.Models;

namespace OICQStickerManager.ViewModels;

/// <summary>绑定对话框里一个账号行的可绑定状态（每行勾选 + 是否已绑定）。</summary>
public class QqAccountRow : ViewModelBase
{
    public QqAccountRow(QqAccountScanResult scan, bool isBound)
    {
        Scan = scan;
        IsBound = isBound;
    }

    public QqAccountScanResult Scan { get; }

    public string Uin => Scan.Uin;

    public bool IsBound { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public string Meta
    {
        get
        {
            var parts = new List<string>
            {
                Scan.HasEmoji ? $"{Scan.StickerCount} 个收藏表情" : "暂无收藏表情",
            };
            if (Scan.LastActive != DateTime.MinValue) parts.Add("最近活跃：" + FormatLastActive(Scan.LastActive));
            if (IsBound) parts.Add("已绑定");
            return string.Join(" · ", parts);
        }
    }

    public static string FormatLastActive(DateTime time) =>
        time == DateTime.MinValue ? "未知"
        : time.Date == DateTime.Today ? $"今天 {time:HH:mm}"
        : time.ToString("yyyy-MM-dd HH:mm");
}
