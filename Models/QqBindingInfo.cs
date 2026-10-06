namespace OICQStickerManager.Models;

/// <summary>一条 QQ 账号绑定（持久化在 config.json 的 QqBindings）。</summary>
public class QqBindingInfo
{
    public string Uin { get; set; } = "";

    /// <summary>显示别名（选项卡「QQ（别名）」），默认取 uin 尾号，可由用户批量改名。</summary>
    public string Alias { get; set; } = "";

    public DateTime BoundAt { get; set; } = DateTime.Now;

    public string AccountDirectory { get; set; } = "";
}
