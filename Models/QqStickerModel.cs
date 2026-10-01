using System.Text.Json.Serialization;

namespace OICQStickerManager.Models;

/// <summary>
/// QQ 收藏表情镜像条目（personal_emoji\Ori 里一个文件的视图）。
/// 继承 StickerModel 以复用格子模板与发送链路，但绝不进入图库 Stickers 集合、不进 stickers.json。
/// Ori 文件名 = 内容大写 MD5（实测验证），IsGif 以文件头魔数为准（1/3 缓存文件后缀是错的）。
/// </summary>
public class QqStickerModel : StickerModel
{
    /// <summary>镜像所属账号（qq:&lt;uin&gt; 选项卡的归属）。</summary>
    public string Uin { get; set; } = "";

    /// <summary>QQ 收藏标记（快捷面板混排的来源角标）。</summary>
    [JsonIgnore]
    public override bool IsQqItem => true;

    /// <summary>
    /// 搜索命中标记（QQ 页分桶排序用，见 MainViewModel.ApplyQqSearchBuckets）：
    /// 命中的排前、未命中的排后，桶内各按时序+频率。UI 辅助态，不落盘。
    /// </summary>
    [JsonIgnore]
    public bool SearchHit { get; set; }

    /// <summary>Thumb 目录下的静态缩略图（gif 没有），绑定对话框预览用。</summary>
    [JsonIgnore]
    public string? ThumbPath { get; set; }

    private bool _isGifReal;

    /// <summary>按魔数嗅探的真实动图判定（Ori 后缀不可信，不能用基类的扩展名判定）。</summary>
    [JsonIgnore]
    public override bool IsGif => _isGifReal;

    public void SetRealKind(OICQStickerManager.Services.ImageKind kind)
    {
        _isGifReal = kind == OICQStickerManager.Services.ImageKind.Gif;
        OnPropertyChanged(nameof(IsGif));
    }

    [JsonIgnore]
    public DateTime FileMtime { get; set; } = DateTime.MinValue;

    private bool _isImported;
    /// <summary>已导入图库角标（按 Md5 与图库比对）。</summary>
    [JsonIgnore]
    public bool IsImported
    {
        get => _isImported;
        set { if (_isImported != value) { _isImported = value; OnPropertyChanged(); } }
    }

    private bool _isOrphaned;
    /// <summary>深度同步判定：QQ 已取消收藏、此处为缓存残留（淡色角标 + 悬浮解释）。</summary>
    [JsonIgnore]
    public bool IsOrphaned
    {
        get => _isOrphaned;
        set { if (_isOrphaned != value) { _isOrphaned = value; OnPropertyChanged(); OnPropertyChanged(nameof(OrphanToolTip)); } }
    }

    [JsonIgnore]
    public string OrphanToolTip => "QQ 中已取消收藏，这里是缓存残留；可在图库中继续管理或忽略";

    // Md5 基类可空（旧数据惰性补算），镜像条目取文件名时总是有值，这里兜底防解引用
    [JsonIgnore]
    public override string DisplayName
    {
        get
        {
            var md5 = Md5 ?? string.Empty;
            return md5.Length == 0 ? "QQ收藏表情（未知）" : $"QQ收藏表情 {md5[..Math.Min(6, md5.Length)]}";
        }
    }

    public override string ToString() => DisplayName;
}
