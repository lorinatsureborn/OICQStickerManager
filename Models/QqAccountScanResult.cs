namespace OICQStickerManager.Models;

/// <summary>扫描本机 Tencent Files 得到的一个 QQ 账号概况（绑定对话框与启动询问用）。</summary>
public class QqAccountScanResult
{
    public string Uin { get; set; } = "";

    /// <summary>personal_emoji\Ori 目录；该账号从未收藏过表情时为空串。</summary>
    public string OriDir { get; set; } = "";

    public int StickerCount { get; set; }

    /// <summary>热文件（nt_msg.db-wal / mmkv / log）最新修改时间，用于猜测"当前在玩的号"；无信号时为 MinValue。</summary>
    public DateTime LastActive { get; set; } = DateTime.MinValue;

    public bool HasEmoji => StickerCount > 0;
}
