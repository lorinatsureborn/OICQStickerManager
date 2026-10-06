using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace OICQStickerManager.Services;

/// <summary>AI 视觉识别的服务商定义：OpenAI 兼容的 /chat/completions 一套协议覆盖全部常见厂商。</summary>
public sealed record AiProviderDef(
    string Id,
    string Name,
    string BaseUrl,
    string[] Models,
    string DefaultModel,
    string KeyPrefixHint,     // Key 前缀特征（供自动识别；空=无特征）
    string GuideUrl,          // 获取 Key 的控制台地址
    string Guide,             // 引导文案（怎么拿 Key、注意什么）
    bool NeedsKey = true);

/// <summary>一次识别请求的完整配置（已解析好 base url 与模型）。</summary>
public sealed record AiTagOptions(string ProviderId, string ProviderName, string ApiKey, string BaseUrl, string Model);

/// <summary>识别结果：标签建议 + 来源信息（进缓存的元数据）。</summary>
public sealed record AiTagResult(IReadOnlyList<string> Tags, string ProviderId, string Model, DateTime CreatedAt);

/// <summary>识别失败：Message 给用户看（引导怎么修），Detail 给日志看（完整现场）。</summary>
public class AiTagException : Exception
{
    public string Detail { get; }
    public AiTagException(string message, string detail, Exception? inner = null) : base(message, inner) => Detail = detail;
}

/// <summary>
/// AI 视觉模型标签建议服务。用户自备 API Key（设置页配置），
/// 协议统一走各厂商的 OpenAI 兼容接口（Anthropic/Gemini 均提供兼容层，无需单独适配）。
/// 失败必须可解释：所有异常按"用户能做什么"转译，完整现场落 asuka-aitag.log。
/// </summary>
public class AiTagService
{
    public static string LogFilePath => Path.Combine(Path.GetTempPath(), "asuka-aitag.log");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public AiTagService(HttpMessageHandler? handler = null)
    {
        _ownsHttp = handler == null;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    // ———— 服务商注册表 ————

    public static readonly AiProviderDef[] Providers =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1",
            ["gpt-4o-mini", "gpt-4o", "gpt-5-mini", "gpt-5"], "gpt-4o-mini", "sk-",
            "https://platform.openai.com/api-keys",
            "在 OpenAI 平台创建 API Key。gpt-4o-mini 带原生视觉且最便宜，推荐从它开始。", true),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1",
            ["deepseek-flash"], "deepseek-flash", "",
            "https://platform.deepseek.com/api_keys",
            "在 DeepSeek 开放平台创建 API Key。deepseek-flash（V4.1）带原生视觉；注意 v4-pro 是纯文本模型，不要选。", true),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4",
            ["glm-4v-flash", "glm-4.5v", "glm-4v-plus"], "glm-4v-flash", "",
            "https://open.bigmodel.cn/usercenter/apikeys",
            "在智谱开放平台创建 API Key。glm-4v-flash 是免费的视觉模型，白嫖首选。", true),
        new("moonshot", "Kimi 月之暗面", "https://api.moonshot.cn/v1",
            ["moonshot-v1-8k-vision-preview", "moonshot-v1-32k-vision-preview"], "moonshot-v1-8k-vision-preview", "sk-",
            "https://platform.moonshot.cn/console/api-keys",
            "在月之暗面平台创建 API Key，注意必须选带 vision 的模型（列表已预置）。", true),
        new("qwen", "阿里通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ["qwen-vl-plus", "qwen-vl-max", "qwen2.5-vl-72b-instruct"], "qwen-vl-plus", "sk-",
            "https://bailian.console.aliyun.com/?apiKey=1",
            "在阿里云百炼平台创建 API Key（DashScope 专属 Key），qwen-vl-plus 性价比高。", true),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1",
            ["Qwen/Qwen2.5-VL-32B-Instruct", "Qwen/Qwen2.5-VL-7B-Instruct", "deepseek-ai/deepseek-vl2", "THUDM/GLM-4.1V-9B-Thinking"],
            "Qwen/Qwen2.5-VL-32B-Instruct", "sk-",
            "https://cloud.siliconflow.cn/account/ak",
            "在硅基流动创建 API Key。聚合平台，开源视觉模型多，部分小模型免费。", true),
        new("doubao", "豆包·火山方舟", "https://ark.cn-beijing.volces.com/api/v3",
            ["doubao-1.5-vision-lite-250315", "doubao-1.5-vision-pro-250328"], "doubao-1.5-vision-lite-250315", "",
            "https://console.volcengine.com/ark/region:ark-cn-beijing/apiKey",
            "在火山方舟控制台创建 API Key 并开通视觉模型；模型框也可填 ep- 开头的接入点 ID。", true),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai",
            ["gemini-2.5-flash", "gemini-2.0-flash", "gemini-2.5-pro"], "gemini-2.5-flash", "AIza",
            "https://aistudio.google.com/apikey",
            "在 Google AI Studio 获取 API Key（免费额度充足）。国内网络需自行解决连通性。", true),
        new("anthropic", "Claude", "https://api.anthropic.com/v1",
            ["claude-sonnet-4-5", "claude-opus-4-1", "claude-3-5-haiku-latest"], "claude-sonnet-4-5", "sk-ant-",
            "https://console.anthropic.com/settings/keys",
            "在 Anthropic 控制台创建 API Key（走官方 OpenAI 兼容层，无需额外配置）。", true),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1",
            ["google/gemini-2.0-flash-001", "qwen/qwen2.5-vl-72b-instruct", "qwen/qwen2.5-vl-32b-instruct:free"],
            "google/gemini-2.0-flash-001", "sk-or-",
            "https://openrouter.ai/settings/keys",
            "在 OpenRouter 创建 API Key。一个 Key 调用各家模型，带 :free 后缀的免费。", true),
        new("ollama", "本地模型 Ollama", "http://localhost:11434/v1",
            ["qwen2.5vl:7b", "llama3.2-vision:11b", "gemma3:4b"], "qwen2.5vl:7b", "",
            "https://ollama.com",
            "本机运行 Ollama 并先 ollama pull 拉取视觉模型（如 qwen2.5vl）。无需 API Key，不联网。", false),
        new("custom", "自定义接口", "",
            [], "", "",
            "",
            "填任意 OpenAI 兼容的 /chat/completions 接口地址（新版 OneAPI、中转站等）。", true),
    ];

    public static AiProviderDef? FindProvider(string? id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>根据 API Key 前缀自动识别服务商；识别不出返回 null（需用户手动选）。
    /// 只有"独占"前缀参与识别（sk-ant-/sk-or-/AIza…，按长度降序防 sk- 抢跑）；
    /// 各家通用的裸 sk- 无法唯一区分，一律判 null 交用户手选——猜错只会换来 401 迷惑。</summary>
    public static string? DetectProviderId(string apiKey)
    {
        var key = apiKey.Trim();
        if (key.Length == 0) return null;
        foreach (var p in Providers.Where(p => p.NeedsKey && p.KeyPrefixHint.Length > 3)
                     .OrderByDescending(p => p.KeyPrefixHint.Length))
            if (key.StartsWith(p.KeyPrefixHint, StringComparison.OrdinalIgnoreCase))
                return p.Id;
        // 智谱 Key 形如 {32位hex}.{16位字母数字}
        if (key.Length > 33 && key[32] == '.'
            && key.Take(32).All(char.IsAsciiHexDigit)) return "zhipu";
        return null;
    }

    /// <summary>把用户配置解析成一次请求所需的完整参数；解析失败抛 AiTagException（Message 面向用户）。</summary>
    public static AiTagOptions BuildOptions(string providerId, string apiKey, string model, string baseUrl)
    {
        apiKey = apiKey.Trim();
        var def = FindProvider(providerId) ?? throw new AiTagException(
            "服务商配置无效，请到 设置 → AI 识别 重新选择。",
            $"unknown provider id: {providerId}");

        if (def.NeedsKey && apiKey.Length == 0)
            throw new AiTagException(
                $"还没有配置 {def.Name} 的 API Key，请到 设置 → AI 识别 填入。",
                "api key empty");

        string url;
        if (def.Id == "custom")
        {
            baseUrl = baseUrl.Trim();
            if (baseUrl.Length == 0)
                throw new AiTagException(
                    "自定义接口还没有填接口地址，请到 设置 → AI 识别 填入（以 /v1 结尾的 OpenAI 兼容地址）。",
                    "custom base url empty");
            url = baseUrl.TrimEnd('/');
        }
        else
        {
            url = def.BaseUrl.TrimEnd('/');
        }

        var modelId = model.Trim();
        if (modelId.Length == 0)
        {
            modelId = def.DefaultModel;
            if (modelId.Length == 0)
                throw new AiTagException(
                    "还没有填写模型名，请到 设置 → AI 识别 选择或输入一个支持视觉（看图）的模型。",
                    "model empty");
        }
        return new AiTagOptions(def.Id, def.Name, apiKey, url, modelId);
    }

    // ———— 识别主流程 ————

    /// <summary>识别一张图片并返回标签建议。只做网络与解析，缓存由调用方（缓存层）负责。</summary>
    public async Task<AiTagResult> SuggestTagsAsync(string imagePath, AiTagOptions opt,
        IReadOnlyList<string> existingTags, CancellationToken ct = default)
    {
        var (dataUrl, ext, bytes) = await Task.Run(() => BuildImageDataUrl(imagePath));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (content, status, body) = await PostChatAsync(opt, dataUrl, existingTags, ct);
            var tags = ParseTags(content, opt, body);
            Log($"OK {opt.ProviderName} model={opt.Model} img={bytes}B({ext}) {sw.ElapsedMilliseconds}ms tags=[{string.Join(",", tags)}]");
            return new AiTagResult(tags, opt.ProviderId, opt.Model, DateTime.Now);
        }
        catch (AiTagException ex)
        {
            Log($"FAIL {opt.ProviderName} model={opt.Model} img={bytes}B({ext}) {sw.ElapsedMilliseconds}ms :: {ex.Detail}");
            throw;
        }
        catch (Exception ex)
        {
            Log($"FAIL {opt.ProviderName} model={opt.Model} img={bytes}B({ext}) {sw.ElapsedMilliseconds}ms :: UNEXPECTED {ex}");
            throw new AiTagException($"识别失败：{ex.Message}", $"unexpected {ex.GetType().Name}: {ex}", ex);
        }
    }

    private async Task<(string content, int status, string body)> PostChatAsync(
        AiTagOptions opt, string dataUrl, IReadOnlyList<string> existingTags, CancellationToken ct)
    {
        var tagHint = existingTags.Count > 0
            ? "优先从这些已有标签里复用语义相符的：" + string.Join("、", existingTags.Take(120)) + "\n"
            : "";
        var payload = new
        {
            model = opt.Model,
            max_tokens = 300,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text =
                            "你是表情包整理助手。请看这张表情图片，给出 3~6 个适合做搜索关键词的中文标签。\n" +
                            tagHint +
                            "要求：\n" +
                            "- 只输出一个 JSON 字符串数组，格式如 [\"金馆长\",\"大笑\",\"搞笑\"]，不要输出任何其他文字或解释\n" +
                            "- 每个标签 1~6 个字；涵盖画面主体（人物/动物/物品）、情绪或动作，以及文字梗的核心词\n" +
                            "- 如果是知名角色/IP/表情包系列，给出它的通用名称"
                        },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            },
        };
        var json = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, opt.BaseUrl + "/chat/completions");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (opt.ApiKey.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
        // Anthropic 兼容层要求的额外头（其余服务商忽略）
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        int status;
        string body;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiTagException("请求超时（60 秒无响应）。请检查网络，或稍后重试。", "http timeout 60s");
        }
        catch (HttpRequestException ex)
        {
            throw new AiTagException(
                "网络请求失败，无法连接到服务商接口。请检查网络连通性（部分境外服务需要代理）。",
                $"http request failed: {ex.Message}");
        }

        if (status < 200 || status >= 300)
            throw ExplainHttpError(status, body, opt);

        // OpenAI 兼容响应：choices[0].message.content；缺结构说明网关返回了非预期内容
        string? content = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception)
        {
            throw new AiTagException(
                "接口返回了无法理解的内容（不是标准的对话响应）。若使用中转/代理服务，请确认其完整支持 chat/completions 接口。",
                $"bad response shape, status={status}, body={Clip(body, 500)}");
        }
        if (string.IsNullOrWhiteSpace(content))
            throw new AiTagException(
                "模型返回了空内容，请重试；若持续出现，换一个视觉模型（见设置页预设列表）。",
                $"empty content, status={status}, finish body={Clip(body, 300)}");
        return (content, status, body);
    }

    /// <summary>把 HTTP 错误码转译成"用户下一步该做什么"。</summary>
    private static AiTagException ExplainHttpError(int status, string body, AiTagOptions opt)
    {
        var serverMsg = ExtractServerErrorMessage(body);
        var detail = $"http {status} model={opt.Model} body={Clip(body, 500)}";
        var suffix = serverMsg.Length > 0 ? $"\n接口说明：{Clip(serverMsg, 160)}" : "";
        return status switch
        {
            401 or 403 => new AiTagException(
                $"API Key 无效或没有权限（HTTP {status}）。请到设置页核对 Key 是否正确、是否已开通对应服务。{suffix}", detail),
            404 => new AiTagException(
                $"接口或模型不存在（HTTP 404）。请检查模型名是否拼写正确——必须是该服务商的支持视觉的模型，中转用户请核对接口地址。{suffix}", detail),
            429 => new AiTagException(
                $"请求太频繁或额度用完（HTTP 429）。稍等片刻再试，或到服务商控制台查看额度/账单。{suffix}", detail),
            400 => new AiTagException(
                $"请求被拒绝（HTTP 400），常见原因是该模型不支持图片输入（不是视觉模型）。请换用设置页预设里的视觉模型。{suffix}", detail),
            _ => new AiTagException(
                $"服务商返回错误（HTTP {status}）。{suffix}", detail),
        };
    }

    private static string ExtractServerErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
                if (err.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("message", out var m2)) return m2.GetString() ?? "";
        }
        catch { }
        return "";
    }

    // ———— 响应解析 ————

    /// <summary>从模型输出里抠出 JSON 字符串数组（容忍 markdown 代码块包裹），清洗为合法标签。</summary>
    public static List<string> ParseTags(string content, AiTagOptions opt, string rawBody = "")
    {
        var text = content.Trim();
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            throw new AiTagException(
                "模型没有按约定返回标签列表，请重试；若反复出现，请在设置页换一个模型。",
                $"unparseable content={Clip(content, 300)} body={Clip(rawBody, 200)}");
        List<string>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<string>>(text[start..(end + 1)], JsonOpts);
        }
        catch (Exception ex)
        {
            throw new AiTagException(
                "模型返回的标签列表无法解析，请重试；若反复出现，请在设置页换一个模型。",
                $"json parse failed: {ex.Message} content={Clip(content, 300)}", ex);
        }
        var tags = (list ?? new List<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0 && t.Length <= 20)
            .Select(t => t.Replace('\n', ' ').Replace('\r', ' '))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToList();
        if (tags.Count == 0)
            throw new AiTagException(
                "模型没有给出有效标签，请重试；若反复出现，请在设置页换一个模型。",
                $"empty tag list content={Clip(content, 300)}");
        return tags;
    }

    // ———— 图片编码 ————

    /// <summary>把库内图片转成 data URL：魔数定真实格式；GIF/超大图统一转 PNG（取首帧/降采样），
    /// 规避部分服务商对 gif/bmp 的挑剔与请求体积上限。</summary>
    public static (string DataUrl, string Ext, int Bytes) BuildImageDataUrl(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new AiTagException(
                "表情图片文件不存在（可能已被删除或移动）。", $"file missing: {imagePath}");

        var kind = ImageSniffer.Detect(imagePath);
        var bytes = File.ReadAllBytes(imagePath);

        // 超过 5MB 重新编码（部分服务商对请求体积有 4~10MB 上限）
        if (kind is ImageKind.Jpeg or ImageKind.Png && bytes.Length <= 5 * 1024 * 1024)
        {
            var mime = kind == ImageKind.Jpeg ? "image/jpeg" : "image/png";
            var ext = kind == ImageKind.Jpeg ? ".jpg" : ".png";
            return (ToDataUrl(mime, bytes), ext, bytes.Length);
        }

        // GIF（含动图，取首帧）/ BMP / WebP / 超大图 → WPF 解码 + PNG 编码
        try
        {
            var png = PngEncode(imagePath, bytes.Length > 2 * 1024 * 1024 ? 1024 : (int?)null);
            return (ToDataUrl("image/png", png), ".png", png.Length);
        }
        catch (Exception ex)
        {
            throw new AiTagException(
                "图片无法解码（文件可能损坏）。", $"decode failed kind={kind}: {ex.Message}", ex);
        }
    }

    private static byte[] PngEncode(string path, int? decodePixelWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth is int w) bmp.DecodePixelWidth = w;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string ToDataUrl(string mime, byte[] bytes) =>
        "data:" + mime + ";base64," + Convert.ToBase64String(bytes);

    // ———— 日志 ————

    private static readonly object LogLock = new();
    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
                File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {MaskKey(message)}\n");
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>日志脱敏：任何 sk-xxxx 之类的 Key 片段只留前 8 字符。</summary>
    public static string MaskKey(string message)
    {
        // Bearer 后、或 sk-/AIza 开头的连续 token 打码
        var parts = message.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            var isKey = (p.StartsWith("sk-", StringComparison.Ordinal)
                         || p.StartsWith("AIza", StringComparison.Ordinal)
                         || p.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                        && p.Length > 12;
            if (isKey) parts[i] = p[..8] + "***";
        }
        return string.Join(' ', parts);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
