using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuietTranslate;

internal sealed class OcrService : IDisposable
{
    private readonly HttpClient client;
    private string baiduToken = "", baiduScope = "";
    private DateTimeOffset baiduExpiry;
    internal OcrService(HttpMessageHandler? handler = null) { client = ApiSupport.Client(handler); }
    internal static string SecretTarget(OcrProvider provider, ServiceSettings s) => Credentials.Target(Provider.CustomTranslation, s.Endpoint) + "/OCR/" + provider;
    internal static string RecognitionPrompt(ServiceSettings s) => string.IsNullOrWhiteSpace(s.Prompt) || s.Prompt == ServiceSettings.DefaultPrompt ? ServiceSettings.DefaultOcrPrompt : s.Prompt;
    internal async Task<string> RecognizeAsync(OcrProvider provider, ServiceSettings s, string key, Bitmap image, CancellationToken cancellation, string language = "auto")
    {
        if (provider == OcrProvider.Local && !ApiSupport.Endpoint(s.Endpoint).IsLoopback) throw new InvalidOperationException("本机 OCR 只允许回环地址；远程服务请选择自定义 OCR。");
        if (provider == OcrProvider.Windows) return await ScreenshotOcr.RecognizeAsync(image, cancellation, language);
        if (provider is OcrProvider.Baidu or OcrProvider.Tencent && s.AppId.Length == 0) s.AppId = Credentials.Get(SecretTarget(provider, s) + "/id");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(35)); var ct = deadline.Token;
        try
        {
            if (image.Width < 15 || image.Height < 15) throw new InvalidOperationException("识别区域过小，请框选完整文字行。");
            using var memory = new MemoryStream(); image.Save(memory, ImageFormat.Png);
            int max = provider == OcrProvider.OCRSpace ? 1024 * 1024 : 5 * 1024 * 1024;
            if (memory.Length > max) throw new InvalidOperationException($"截图超过此 OCR 的本程序上传上限（{max / 1024 / 1024} MB），请缩小框选范围。");
            string base64 = Convert.ToBase64String(memory.ToArray());
            using var request = new HttpRequestMessage(HttpMethod.Post, provider == OcrProvider.VisionAI ? Settings.CompletionEndpoint(Provider.CustomAI, s.Endpoint) : ApiSupport.Endpoint(s.Endpoint));
            if (key.Length == 0 && !s.AllowAnonymous) throw new InvalidOperationException("请在设置 → 截图 OCR 填写并保存当前服务的密钥。");
            switch (provider)
            {
                case OcrProvider.Local:
                    ApiSupport.Authenticate(request, s, key);
                    request.Content = ApiSupport.Json(new { image_base64 = base64, language }); break;
                case OcrProvider.OCRSpace:
                    request.Headers.Add("apikey", key);
                    request.Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string> { ["base64Image"] = "data:image/png;base64," + base64, ["language"] = language switch { "en" => "eng", "ja" => "jpn", "zh-Hans" => "chs", "zh-Hant" => "cht", "ko" => "kor", "fr" => "fre", "de" => "ger", "es" => "spa", _ => "auto" }, ["OCREngine"] = "2", ["isOverlayRequired"] = "false", ["scale"] = "true" }); break;
                case OcrProvider.Tencent:
                    ApiSupport.Tencent(request, JsonSerializer.Serialize(new { ImageBase64 = base64, LanguageType = language switch { "ja" => "jap", "ko" => "kor", "fr" => "fre", "de" => "ger", "es" => "spa", "zh-Hans" => "zh", "zh-Hant" => "zh_rare", "en" => "auto", _ => "mix" } }), s, key, "ocr", "GeneralBasicOCR", "2018-11-19"); break;
                case OcrProvider.Baidu:
                    if (s.AppId.Length == 0) throw new InvalidOperationException("百度 OCR 需要 API Key（应用标识）和 Secret Key（密钥）。");
                    // One short-lived authentication token; no source/result cache and no token persisted to disk.
                    string scope = ApiSupport.Hash(s.Endpoint + "\0" + s.AppId + "\0" + key);
                    if (scope != baiduScope || baiduExpiry <= DateTimeOffset.UtcNow)
                    {
                        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://aip.baidubce.com/oauth/2.0/token");
                        tokenRequest.Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = s.AppId, ["client_secret"] = key });
                        using var token = await ApiSupport.SendJson(client, tokenRequest, ct);
                        if (!token.RootElement.TryGetProperty("access_token", out var access)) throw new InvalidOperationException("百度 OCR 获取令牌失败，请检查应用标识和密钥。");
                        baiduToken = access.GetString()!; baiduScope = scope;
                        int seconds = token.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out int expiry) ? Math.Clamp(expiry - 60, 0, 300) : 300;
                        baiduExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
                    }
                    request.RequestUri = new Uri(request.RequestUri!.AbsoluteUri + "?access_token=" + Uri.EscapeDataString(baiduToken));
                    request.Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string> { ["image"] = base64, ["language_type"] = language switch { "en" => "ENG", "ja" => "JAP", "ko" => "KOR", "fr" => "FRE", "de" => "GER", "es" => "SPA", "zh-Hans" or "zh-Hant" => "CHN_ENG", _ => "auto_detect" }, ["detect_language"] = "true", ["detect_direction"] = "true" }); break;
                case OcrProvider.GoogleVision:
                    request.Headers.Add("X-goog-api-key", key);
                    request.Content = ApiSupport.Json(new { requests = new[] { new { image = new { content = base64 }, features = new[] { new { type = "DOCUMENT_TEXT_DETECTION" } }, imageContext = new { languageHints = language == "auto" ? Array.Empty<string>() : new[] { language } } } } }); break;
                case OcrProvider.VisionAI:
                    if (s.Model.Length == 0) throw new InvalidOperationException("请填写支持图片输入的模型名，纯文字模型不能用于 OCR。");
                    ApiSupport.Authenticate(request, s, key);
                    request.Content = ApiSupport.Json(new { model = s.Model, stream = false, messages = new object[] { new { role = "system", content = RecognitionPrompt(s) }, new { role = "user", content = new object[] { new { type = "text", text = "识别图片文字。" }, new { type = "image_url", image_url = new { url = "data:image/png;base64," + base64 } } } } } }); break;
                default:
                    ApiSupport.Authenticate(request, s, key);
                    request.Content = ApiSupport.Template(s, new() { ["{image_base64}"] = base64, ["{image_data_url}"] = "data:image/png;base64," + base64, ["{api_key}"] = key, ["{language}"] = language }); break;
            }
            using var doc = await ApiSupport.SendJson(client, request, ct); var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && ((root.TryGetProperty("IsErroredOnProcessing", out var failed) && failed.ValueKind == JsonValueKind.True) || root.TryGetProperty("error_code", out _) || root.TryGetProperty("error", out _) || (root.TryGetProperty("Response", out var response) && response.TryGetProperty("Error", out _)))) throw new InvalidOperationException("OCR 服务返回错误，请检查密钥、权限、图片格式和额度。");
            string result = ApiSupport.Extract(root, provider switch { OcrProvider.OCRSpace => "ParsedResults[].ParsedText", OcrProvider.Tencent => "Response.TextDetections[].DetectedText", OcrProvider.Baidu => "words_result[].words", OcrProvider.GoogleVision => "responses[0].fullTextAnnotation.text", OcrProvider.VisionAI => "choices[0].message.content", OcrProvider.Local => "text", _ => s.ResponsePath });
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("没有识别到文字。请缩小到清晰文字区域，或检查自定义返回路径。");
            if (result.Length > 8000) throw new InvalidOperationException("识别结果超过 8000 字，请缩小选区；未自动截断或发送译文请求。");
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new InvalidOperationException("OCR 识别超时，请检查网络后重试。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接 OCR 服务，请检查网络和地址。"); }
        catch (JsonException) { throw new InvalidOperationException("OCR 返回格式不兼容，请检查服务类型或返回路径。"); }
    }
    public void Dispose() { baiduToken = baiduScope = ""; client.Dispose(); }
}
