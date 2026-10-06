namespace OICQStickerManager.Models;

/// <summary>
/// 一份已验证的 AI 服务商配置档案（用户输入 Key → 测试通过 → 命名保存）。
/// 档案是唯一持久事实源；激活档案的内容在启动/切换时写入工作配置（VM 的 AiTag* 属性）。
/// </summary>
public class AiKeyProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户起的别名（如「智谱主力」「白嫖小号」）。</summary>
    public string Name { get; set; } = "";

    /// <summary>服务商 id（AiTagService.Providers 的 Id）。</summary>
    public string ProviderId { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>自定义服务商的接口地址（其余服务商为空）。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>该档案当前选用的模型（空 = 服务商默认）。</summary>
    public string Model { get; set; } = "";

    /// <summary>思考强度档位（空 = 服务商默认；合法值见 AiProviderDef.EffortLevels）。</summary>
    public string Effort { get; set; } = "";

    /// <summary>最近一次「检测可用视觉模型」的结果（点芯片选用，可重新检测）。</summary>
    public List<string> DetectedModels { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最近一次测试通过时间（建档时必过，重测后刷新）。</summary>
    public DateTime? VerifiedAt { get; set; }
}
