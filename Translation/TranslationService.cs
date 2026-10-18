using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Security.Cryptography;

namespace QuietTranslate;

public sealed class TranslationService : IDisposable
{
    private readonly HttpClient client;
    public TranslationService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8), AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> TranslateAsync(Provider provider, ServiceSettings settings, string key, string text,
        Action<string> update, CancellationToken cancellation, string target = "zh-Hans", string source = "auto")
    {
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("没有取得文字。请先选中文字，或改用截图翻译。");
        if (text.Length > 8000) throw new InvalidOperationException("一次最多翻译 8000 字，请缩小选区。");
        if (provider == Provider.Tencent && text.Length >= 6000) throw new InvalidOperationException("腾讯翻译单次须少于 6000 字符，请缩小选区；未自动截断。");
        if (provider == Provider.Baidu && Encoding.UTF8.GetByteCount(text) > 6000) throw new InvalidOperationException("百度翻译单次文本超过本程序的 6000 UTF-8 字节上限，请缩小选区；未自动截断。");
        if (string.IsNullOrWhiteSpace(key) && !settings.AllowAnonymous) throw new InvalidOperationException("请打开设置，填写当前服务的密钥。");
        var endpoint = Settings.CompletionEndpoint(provider, settings.Endpoint);
        if (provider is Provider.Baidu or Provider.Tencent or Provider.Youdao && settings.AppId.Length == 0) settings.AppId = Credentials.Get(Credentials.Target(provider, settings) + "/id");
        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (Settings.IsAI(provider))
        {
            ApiSupport.Authenticate(request, settings, key);
            string prompt = BuildPrompt(settings, text, target, source);
            // Hunyuan-MT's documented format puts the instruction and source in one user turn.
            var messages = IsHunyuanMT(settings.Model)
                ? new[] { new { role = "user", content = prompt + "\n\n" + text } }
                : new[] { new { role = "system", content = prompt }, new { role = "user", content = text } };
            var body = new Dictionary<string, object>
            {
                ["model"] = settings.Model,
                ["messages"] = messages,
                ["stream"] = true, ["max_tokens"] = 4096
            };
            if (settings.DisableThinking)
            {
                if (provider is Provider.OpenAI or Provider.Doubao or Provider.GLM) body["thinking"] = new { type = "disabled" };
                else if (provider is Provider.Qwen or Provider.SiliconFlow) body["enable_thinking"] = false;
            }
            request.Content = Json(body);
        }
        else if (provider == Provider.Microsoft)
        {
            request.RequestUri = new Uri(endpoint.GetLeftPart(UriPartial.Path) + "?api-version=3.0&to=" + Uri.EscapeDataString(target) + (source == "auto" ? "" : "&from=" + Uri.EscapeDataString(source)));
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            if (!string.IsNullOrWhiteSpace(settings.Region)) request.Headers.Add("Ocp-Apim-Subscription-Region", settings.Region.Trim());
            request.Content = Json(new[] { new { Text = text } }); // Omit `from`: server-side auto detection.
        }
        else if (provider == Provider.DeepL)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
            var body = new Dictionary<string, object> { ["text"] = new[] { text }, ["target_lang"] = target == "zh-Hans" ? "ZH-HANS" : target == "zh-Hant" ? "ZH-HANT" : target.ToUpperInvariant() };
            if (source != "auto") body["source_lang"] = source.StartsWith("zh") ? "ZH" : source.ToUpperInvariant();
            request.Content = Json(body);
        }
        else if (provider == Provider.Google)
        {
            request.Headers.Add("X-goog-api-key", key);
            var body = new Dictionary<string, object> { ["q"] = text, ["target"] = target == "zh-Hans" ? "zh-CN" : target == "zh-Hant" ? "zh-TW" : target, ["format"] = "text" };
            if (source != "auto") body["source"] = source.StartsWith("zh") ? "zh" : source;
            request.Content = Json(body);
        }
        else if (provider == Provider.Tencent)
        {
            string code(string x) => x switch { "zh-Hans" => "zh", "zh-Hant" => "zh-TW", _ => x };
            ApiSupport.Tencent(request, JsonSerializer.Serialize(new { SourceText = text, Source = code(source), Target = code(target), ProjectId = 0 }), settings, key, "tmt", "TextTranslate", "2018-03-21");
        }
        else if (provider is Provider.Baidu or Provider.Youdao)
        {
            if (settings.AppId.Length == 0) throw new InvalidOperationException("请填写应用 ID。");
            string code(string x) => x == "zh-Hans" ? (provider == Provider.Youdao ? "zh-CHS" : "zh") : x == "zh-Hant" ? (provider == Provider.Youdao ? "zh-CHT" : "cht") : provider == Provider.Baidu ? x switch { "ja" => "jp", "ko" => "kor", "fr" => "fra", "es" => "spa", _ => x } : x;
            string salt = Guid.NewGuid().ToString("N"), time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var fields = new Dictionary<string, string> { ["q"] = text, ["from"] = code(source), ["to"] = code(target), ["salt"] = salt };
            if (provider == Provider.Baidu)
            { fields["appid"] = settings.AppId; fields["sign"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(settings.AppId + text + salt + key))).ToLowerInvariant(); }
            else
            {
                var chars = text.EnumerateRunes().Select(r => r.ToString()).ToArray();
                string input = chars.Length <= 20 ? text : string.Concat(chars.Take(10)) + chars.Length + string.Concat(chars.TakeLast(10));
                fields["appKey"] = settings.AppId; fields["signType"] = "v3"; fields["curtime"] = time; fields["sign"] = ApiSupport.Hash(settings.AppId + input + salt + time + key);
            }
            request.Content = new FormUrlEncodedContent(fields);
        }
        else if (provider == Provider.LibreTranslate)
        {
            request.Content = Json(new { q = text, source = source.StartsWith("zh") ? "zh" : source, target = target.StartsWith("zh") ? "zh" : target, format = "text", api_key = key });
        }
        else
        {
            ApiSupport.Authenticate(request, settings, key);
            request.Content = ApiSupport.Template(settings, new() { ["{text}"] = text, ["{source}"] = source, ["{target}"] = target, ["{api_key}"] = key });
        }
        TranslationRequests.Turn? turn = null;
        try
        {
            turn = await TranslationRequests.EnterAsync(endpoint, key, ct).ConfigureAwait(false);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await ServiceErrors.FromHttpAsync(response, ct, provider).ConfigureAwait(false);
            string result;
            if (Settings.IsAI(provider) && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                result = await ReadStreamAsync(reader, update, ct, provider).ConfigureAwait(false);
            }
            else
            {
                using var doc = await ApiSupport.ReadJson(response.Content, ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (ServiceErrors.FromJson(root, provider) is { } error) throw error;
                if (Settings.IsAI(provider) && root.GetProperty("choices")[0].TryGetProperty("finish_reason", out var finish) &&
                    finish.ValueKind == JsonValueKind.String && finish.GetString() != "stop")
                    throw new InvalidOperationException("译文未完整返回（长度限制或内容限制），请缩短文字后重试。");
                result = provider switch
                {
                    Provider.Microsoft => root[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "",
                    Provider.DeepL => root.GetProperty("translations")[0].GetProperty("text").GetString() ?? "",
                    Provider.Google => WebUtility.HtmlDecode(ApiSupport.Extract(root, "data.translations[].translatedText")),
                    Provider.Baidu => ApiSupport.Extract(root, "trans_result[].dst"),
                    Provider.Youdao => ApiSupport.Extract(root, "translation"),
                    Provider.Tencent => ApiSupport.Extract(root, "Response.TargetText"),
                    Provider.LibreTranslate => ApiSupport.Extract(root, "translatedText"),
                    Provider.CustomTranslation => ApiSupport.Extract(root, settings.ResponsePath),
                    _ => ReadContent(root.GetProperty("choices")[0].GetProperty("message"))
                };
                update(result);
            }
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("服务未返回译文；请确认模型支持此接口。");
            return result;
        }
        catch (ServiceException ex) { turn?.BackOff(ex); throw; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new InvalidOperationException("翻译等待或请求超时（30 秒），请稍后重试或检查网络。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("连接翻译服务失败，请检查网络、代理和 API 地址。"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        { throw new InvalidOperationException("服务返回格式不兼容，请检查服务类型和地址。"); }
        finally { turn?.Dispose(); }
    }

    internal static bool IsWord(string text)
    {
        text = text.Trim();
        int limit = text.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u4e00' and <= '\u9fff') ? 8 : 48;
        return text.Length <= limit && System.Text.RegularExpressions.Regex.IsMatch(text, @"^[\p{L}\p{M}]+(?:[-'’][\p{L}\p{M}]+)*$");
    }
    internal static string BuildPrompt(ServiceSettings settings, string text, string target, string source)
    {
        string prompt = string.IsNullOrWhiteSpace(settings.Prompt) ? ServiceSettings.DefaultPrompt : settings.Prompt;
        bool translationOnly = IsHunyuanMT(settings.Model);
        if (translationOnly && prompt == ServiceSettings.DefaultPrompt) prompt = ServiceSettings.TranslationOnlyPrompt;
        prompt = prompt.Replace("{target_language}", ApiSupport.LanguageName(target)).Replace("{source_language}", ApiSupport.LanguageName(source));
        if (!translationOnly && settings.WordMeanings && IsWord(text)) prompt += "\n当前输入是孤立单词或短词。若有常见歧义，最多列出三个常见的目标语言释义，可简写词性。不要虚构上下文；语种或含义无法确定时简短说明。专有名词或代码应保留。";
        return prompt;
    }
    private static bool IsHunyuanMT(string model) => model.Trim().Split('/')[^1].Equals("Hunyuan-MT-7B", StringComparison.OrdinalIgnoreCase)
        || model.Trim().Split('/')[^1].Equals("Hunyuan-MT-7B-fp8", StringComparison.OrdinalIgnoreCase);

    internal static async Task<string> ReadStreamAsync(StreamReader reader, Action<string> update, CancellationToken ct, Provider? provider = null)
    {
        var result = new StringBuilder();
        var eventData = new StringBuilder();
        bool finished = false;
        void Consume()
        {
            string data = eventData.ToString().Trim(); eventData.Clear();
            if (data.Length == 0) return;
            if (data == "[DONE]") { finished = true; return; }
            using var doc = JsonDocument.Parse(data);
            if (ServiceErrors.FromJson(doc.RootElement, provider) is { } error) throw error;
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                string part = ReadContent(delta);
                if (part.Length > 0) { result.Append(part); update(result.ToString()); }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                if (reason.GetString() != "stop") throw new InvalidOperationException("译文未完整返回（长度限制或内容限制），请缩短文字后重试。");
                finished = true;
            }
        }
        while (!finished)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) { Consume(); break; }
            if (line.Length == 0) Consume();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            { if (eventData.Length > 0) eventData.Append('\n'); eventData.Append(line.AsSpan(5).TrimStart()); }
            if (result.Length > 40000 || eventData.Length > 262144) throw new InvalidOperationException("服务响应过长，已停止。");
        }
        if (!finished) throw new InvalidOperationException("翻译连接中断，译文可能不完整，请重试。");
        return result.ToString();
    }
    private static string ReadContent(JsonElement element) =>
        element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    public void Dispose() => client.Dispose();
}
