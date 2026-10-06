using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using OICQStickerManager.ViewModels; // 引用你写的基类

namespace OICQStickerManager.Models;

public class StickerModel : ViewModelBase // 💡 继承基类
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>文件内容 MD5（大写 hex）：去重与"QQ 页已导入"对账键；新入库文件同时以其命名。旧数据惰性补算。</summary>
    public string? Md5 { get; set; }

    private string _fullPath = string.Empty;
    public string FullPath
    {
        get => _fullPath;
        set
        {
            _fullPath = value;
            _imageSource = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(IsGif));
        }
    }

    [JsonIgnore]
    public virtual bool IsGif => string.Equals(System.IO.Path.GetExtension(FullPath), ".gif", StringComparison.OrdinalIgnoreCase); // 动图角标与悬浮预览用

    private List<string> _tags = new();
    public List<string> Tags
    {
        get => _tags;
        set
        {
            value ??= new List<string>();
            // 同引用或同内容都不发通知：启动装载/镜像对账会批量重建列表，无差别通知是全列表绑定刷新
            if (ReferenceEquals(_tags, value)) return;
            if (value is List<string> incoming && _tags.SequenceEqual(incoming)) { _tags = incoming; return; }
            _tags = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>页脚悬浮等展示场景用的有效标签：图库条目=自身 Tags；
    /// QQ 镜像覆写为"自身标签优先，否则借图库同款副本的标签"（见 QqStickerModel）。</summary>
    [JsonIgnore]
    public virtual List<string> EffectiveTags => Tags;

    // 最近活跃时间 = 最近一次使用；从未使用时以入库时间兜底（语义见 StickerRanking）
    private DateTime _lastUsedTime = DateTime.MinValue;
    public DateTime LastUsedTime
    {
        get => _lastUsedTime;
        set
        {
            if (_lastUsedTime == value) return; // 批量赋值（镜像对账/装载）同值不刷
            _lastUsedTime = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RankScore));
        }
    }

    // 累计发送次数：热度频次因子的输入，每次发送 +1；旧数据缺省为 0
    private int _useCount;
    public int UseCount
    {
        get => _useCount;
        set
        {
            if (_useCount == value) return;
            _useCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RankScore));
        }
    }

    /// <summary>排序热度分（频次 × 新近度，公式见 StickerRanking）：图库/标签/快捷面板共用的排序键，不落盘。</summary>
    [JsonIgnore]
    public double RankScore => StickerRanking.Score(_useCount, _lastUsedTime);

    // 缩略图解码宽度：显示区域 120 DIP，256px 给 200% DPI 留足余量
    private const int ThumbnailWidth = 256;

    private BitmapImage? _imageSource;

    [JsonIgnore]
    public BitmapImage? ImageSource
    {
        get
        {
            // 缓存解码结果：绑定会反复求值，不能每次都重新解码
            if (_imageSource != null) return _imageSource;
            if (string.IsNullOrEmpty(FullPath) || !System.IO.File.Exists(FullPath))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // 按缩略图尺寸解码，内存从每张数 MB 降到 ~260KB
                bitmap.DecodePixelWidth = ThumbnailWidth;
                bitmap.UriSource = new Uri(FullPath);
                bitmap.EndInit();
                bitmap.Freeze();
                _imageSource = bitmap;
                return bitmap;
            }
            catch
            {
                return null; // 防止损坏的图片导致闪退
            }
        }
    }

    /// <summary>QQ 收藏镜像条目标记：快捷面板混排时给来源角标用（图库条目恒为 false）。</summary>
    [JsonIgnore]
    public virtual bool IsQqItem => false;

    [JsonIgnore]
    public virtual string DisplayName => Tags.Count > 0 ? string.Join("、", Tags) : "未命名表情"; // 无障碍/界面显示用

    // ItemAutomationPeer 取名称的标准来源，让读屏软件能区分每个表情
    public override string ToString() => DisplayName;
}
