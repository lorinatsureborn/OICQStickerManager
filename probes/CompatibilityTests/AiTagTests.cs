using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OICQStickerManager.Services;

internal static class AiTagTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("OpenAI project keys select OpenAI automatically", () => Detect("  sk-proj-synthetic-key  ", "openai")),
        ("OpenAI service account keys select OpenAI automatically", () => Detect("sk-svcacct-synthetic-key", "openai")),
        ("Claude keys select Claude before the generic sk prefix", () => Detect("sk-ant-api03-synthetic-key", "anthropic")),
        ("Generic sk keys require explicit provider selection", () => Detect("sk-synthetic-key", null)),
        ("OpenAI GPT-5 tagging uses the supported completion budget", () => Program.RunAsync(TagRequest("openai", "gpt-5-mini"))),
        ("OpenAI o-series tagging uses the supported completion budget", () => Program.RunAsync(TagRequest("openai", "o3"))),
        ("Claude compatibility tagging carries valid authentication and image content", () => Program.RunAsync(TagRequest("anthropic", "claude-haiku-4-5"))),
        ("OpenAI vision discovery uses the same supported request budget", () => Program.RunAsync(OpenAiDiscovery())),
        ("Claude models follow cursors and use advertised image capability", () => Program.RunAsync(ClaudePagination())),
        ("Model listing preserves rate-limit errors", () => Program.RunAsync(ModelError())),
        ("Cancelling tagging preserves cancellation", () => Program.RunAsync(CancelTagging())),
        ("Empty model content receives the promised format retry", () => Program.RunAsync(EmptyContentRetry())),
        ("Malformed tag text receives one format retry", () => Program.RunAsync(FormatRetry())),
        ("JSON-wrapped credentials are masked in diagnostics", MaskJsonKeys),
        ("A changed active model survives profile reload", PersistModel),
        ("Replacing a verified key invalidates the draft", InvalidateKey),
        ("Replacing a key detects its new provider and clears the old model", ReplaceProviderKey),
        ("Changing provider clears incompatible model and effort", SwitchProvider),
        ("Changing a custom endpoint detaches the verified profile", ChangeEndpoint),
        ("AI cache uses the selected data directory", CacheDirectory),
        ("AI cache flush waits for an already queued write", CacheFlush),
        ("Vision probe carries a decodable PNG image", ProbeImageDecodes),
    ];

    private static void Detect(string key, string? expected) =>
        Program.Require(AiTagService.DetectProviderId(key) == expected, "wrong provider inferred from key prefix");

    private static string TestImage()
    {
        var path = Path.Combine(Program.NewDirectory(), "sticker.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1cAAAAASUVORK5CYII="));
        return path;
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Tags(string content = "[\"小鸟\",\"开心\",\"可爱\"]") =>
        Reply(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));

    private static async Task TagRequest(string provider, string model)
    {
        using var handler = new Handler(req =>
        {
            Program.Require(req.RequestUri!.AbsolutePath == "/v1/chat/completions", "wrong chat endpoint");
            Program.Require(req.Headers.Authorization?.ToString() == "Bearer synthetic-api-key", "wrong bearer credential");
            if (provider == "anthropic")
                Program.Require(req.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "Claude version header missing");
            using var json = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var payload = json.RootElement;
            Program.Require(payload.GetProperty("model").GetString() == model, "selected model not sent");
            if (provider == "openai")
                Program.Require(payload.TryGetProperty("max_completion_tokens", out _) && !payload.TryGetProperty("max_tokens", out _), "OpenAI rejects the legacy max_tokens field for reasoning models");
            Program.Require(payload.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"), "image data missing");
            return Tags();
        });
        using var service = new ServiceScope(handler);
        var result = await service.Value.SuggestTagsAsync(TestImage(), AiTagService.BuildOptions(provider, " synthetic-api-key ", model, ""), []);
        Program.Require(result.Tags.SequenceEqual(new[] { "小鸟", "开心", "可爱" }), "tag response lost");
    }

    private static async Task OpenAiDiscovery()
    {
        using var handler = new Handler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                Program.Require(req.RequestUri!.PathAndQuery == "/v1/models", "OpenAI received another provider's pagination parameters");
                return Reply("{\"object\":\"list\",\"data\":[{\"id\":\"gpt-5-mini\"},{\"id\":\"gpt-image-1\"},{\"id\":\"gpt-realtime\"}]}");
            }
            using var json = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Program.Require(json.RootElement.GetProperty("model").GetString() == "gpt-5-mini", "non-chat model was probed");
            return json.RootElement.TryGetProperty("max_completion_tokens", out _)
                ? Tags("yes") : Reply("{\"error\":{\"message\":\"Use max_completion_tokens\"}}", HttpStatusCode.BadRequest);
        });
        using var service = new ServiceScope(handler);
        var models = await service.Value.ListVisionModelsAsync(AiTagService.BuildOptions("openai", "synthetic-api-key", "", ""));
        Program.Require(models.SequenceEqual(new[] { "gpt-5-mini" }), "supported reasoning vision model was dropped");
    }

    private static async Task ClaudePagination()
    {
        using var handler = new Handler(req =>
        {
            Program.Require(req.Method == HttpMethod.Get, "advertised image capability should avoid paid probing");
            Program.Require(req.Headers.GetValues("x-api-key").Single() == "synthetic-api-key", "Claude models credential missing");
            var second = req.RequestUri!.Query.Contains("after_id=claude-sonnet-5-5");
            var id = second ? "claude-haiku-4-5-20251001" : "claude-sonnet-5-5";
            return Reply(JsonSerializer.Serialize(new
            {
                data = new[] { new { type = "model", id, created_at = "2026-09-28T00:00:00Z", display_name = id, capabilities = new { image_input = new { supported = true } } } },
                has_more = !second, first_id = id, last_id = id,
            }));
        });
        using var service = new ServiceScope(handler);
        var models = await service.Value.ListVisionModelsAsync(AiTagService.BuildOptions("anthropic", "synthetic-api-key", "", ""));
        Program.Require(models.SequenceEqual(new[] { "claude-sonnet-5-5", "claude-haiku-4-5-20251001" }), "later model pages were lost");
    }

    private static async Task ModelError()
    {
        using var handler = new Handler(_ => Reply("{\"error\":{\"message\":\"rate limit\"}}", HttpStatusCode.TooManyRequests));
        using var service = new ServiceScope(handler);
        try { await service.Value.ListVisionModelsAsync(AiTagService.BuildOptions("openai", "synthetic-api-key", "", "")); }
        catch (AiTagException ex) { Program.Require(ex.Message.Contains("429"), "rate limit was mislabeled as missing models endpoint"); return; }
        throw new InvalidOperationException("HTTP 429 was ignored");
    }

    private static async Task CancelTagging()
    {
        using var handler = new Handler(_ => Tags());
        using var service = new ServiceScope(handler);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await service.Value.SuggestTagsAsync(TestImage(), AiTagService.BuildOptions("openai", "synthetic-api-key", "", ""), [], cts.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("explicit cancellation was not preserved");
    }

    private static async Task EmptyContentRetry()
    {
        var calls = 0;
        using var handler = new Handler(_ => ++calls == 1 ? Tags("") : Tags());
        using var service = new ServiceScope(handler);
        var result = await service.Value.SuggestTagsAsync(TestImage(), AiTagService.BuildOptions("openai", "synthetic-api-key", "", ""), []);
        Program.Require(calls == 2 && result.Tags.Contains("小鸟"), "empty-content format drift never retried");
    }

    private static async Task FormatRetry()
    {
        var calls = 0;
        using var handler = new Handler(_ => ++calls == 1 ? Tags("This is a bird.") : Tags());
        using var service = new ServiceScope(handler);
        var result = await service.Value.SuggestTagsAsync(TestImage(), AiTagService.BuildOptions("openai", "synthetic-api-key", "", ""), []);
        Program.Require(calls == 2 && result.Tags.Contains("小鸟"), "non-JSON answer did not retry once");
    }

    private static void MaskJsonKeys()
    {
        foreach (var secret in new[] { "sk-proj-synthetic-secret", "sk-ant-api03-synthetic-secret", "AIzaSyntheticSecretValue", "0123456789abcdef0123456789abcdef.syntheticSecret" })
        {
            var masked = AiTagService.MaskKey("{\"error\":\"Invalid key: " + secret + "\"}");
            Program.Require(!masked.Contains(secret), "credential leaked through JSON punctuation");
        }
        Program.Require(!AiTagService.MaskKey("Authorization: Bearer arbitrarySyntheticSecret").Contains("arbitrarySyntheticSecret"), "generic bearer credential leaked");
    }

    private static OICQStickerManager.ViewModels.MainViewModel NewVm()
    {
        var vm = LibraryTests.Create(Program.NewDirectory());
        Program.RunAsync(vm.Initialization);
        vm.AiTagProvider = "openai"; vm.AiTagApiKey = "sk-proj-synthetic-key";
        vm.MarkAiDraftVerified();
        return vm;
    }

    private static void PersistModel()
    {
        using var vm = NewVm();
        var profile = vm.SaveCurrentAsProfile("test", []);
        vm.AiTagModel = "gpt-5-mini";
        vm.FlushPendingConfigSave();
        using var restored = LibraryTests.Create(Path.GetDirectoryName((string)typeof(OICQStickerManager.ViewModels.MainViewModel).GetField("_configPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!)!);
        Program.RunAsync(restored.Initialization);
        Program.Require(restored.AiTagModel == "gpt-5-mini" && profile.Model == "gpt-5-mini", "active model choice was never written back to its profile");
    }

    private static void InvalidateKey()
    {
        using var vm = NewVm(); vm.AiTagApiKey = "sk-proj-another-synthetic-key";
        Program.Require(!vm.AiDraftVerified, "another key inherited the previous key's successful verification");
    }

    private static void ReplaceProviderKey()
    {
        using var vm = NewVm(); vm.AiTagModel = "gpt-5-mini"; vm.AiTagApiKey = "sk-ant-api03-synthetic-key";
        Program.Require(vm.BuildAiTagOptions().ProviderId == "anthropic" && vm.AiTagModel == "", "Claude key kept OpenAI's endpoint or model");
    }

    private static void SwitchProvider()
    {
        using var vm = NewVm(); vm.AiTagModel = "gpt-5-mini"; vm.AiTagEffort = "minimal"; vm.AiTagProvider = "anthropic";
        Program.Require(vm.AiTagModel == "" && vm.AiTagEffort == "" && !vm.AiDraftVerified, "old model, effort or verification survived the provider change");
    }

    private static void ChangeEndpoint()
    {
        using var vm = NewVm(); vm.AiTagProvider = "custom"; vm.AiTagBaseUrl = "https://one.invalid/v1"; vm.AiTagModel = "vision-model";
        vm.MarkAiDraftVerified(); vm.SaveCurrentAsProfile("custom", []); vm.AiTagBaseUrl = "https://two.invalid/v1";
        Program.Require(vm.AiActiveProfileId == "" && !vm.AiDraftVerified, "another endpoint inherited a verified profile");
    }

    private static void CacheDirectory() => WithCacheRoot(root =>
        Program.Require(AiTagCache.FilePath == Path.Combine(root, "ai-tag-cache.json"), "AI cache escaped the isolated data directory"));

    private static void CacheFlush() => WithCacheRoot(root =>
    {
        Program.Require(AiTagCache.FilePath == Path.Combine(root, "ai-tag-cache.json"), "refusing to write to the real user cache");
        var gate = (SemaphoreSlim)typeof(AiTagCache).GetField("WriteLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        gate.Wait();
        Task? flush = null;
        try
        {
            AiTagCache.Put("md5:synthetic", ["小鸟"], "openai", "gpt-4o-mini");
            flush = AiTagCache.FlushAsync();
            Program.Require(!flush.IsCompleted, "flush ignored the existing pending write");
        }
        finally { gate.Release(); if (flush != null) Program.RunAsync(flush); }
        Program.Require(File.ReadAllText(AiTagCache.FilePath).Contains("md5:synthetic"), "queued cache write did not complete");
    });

    private static void WithCacheRoot(Action<string> action)
    {
        var previous = Environment.GetEnvironmentVariable("ASUKA_DATA_DIR");
        try { var root = Program.NewDirectory(); Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", root); AiTagCache.ResetForTests(); action(root); }
        finally { AiTagCache.ResetForTests(); Environment.SetEnvironmentVariable("ASUKA_DATA_DIR", previous); }
    }

    private static void ProbeImageDecodes()
    {
        var dataUrl = (string)typeof(AiTagService).GetField("ProbePngDataUrl", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        using var stream = new MemoryStream(Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]));
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        Program.Require(frame.PixelWidth == 8 && frame.PixelHeight == 8, "probe image dimensions were invalid");
        frame.CopyPixels(new byte[8 * 8 * 4], 8 * 4, 0);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }

    private sealed class ServiceScope(HttpMessageHandler handler) : IDisposable
    {
        internal AiTagService Value { get; } = new(handler);
        public void Dispose() => Value.Dispose();
    }
}
