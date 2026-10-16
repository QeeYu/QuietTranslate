using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace QuietTranslate;

internal static class ApiSupport
{
    internal static string LanguageName(string value) => value switch { "auto" => "自动识别", "zh-Hans" => "简体中文", "zh-Hant" => "繁体中文", "en" => "英语", "ja" => "日语", "ko" => "韩语", "fr" => "法语", "de" => "德语", "es" => "西班牙语", _ => value };
    internal static readonly string[] Languages = { "auto", "zh-Hans", "zh-Hant", "en", "ja", "ko", "fr", "de", "es" };
    internal static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    internal static HttpClient Client(HttpMessageHandler? handler = null) => new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(8), AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    internal static Uri Endpoint(string endpoint) => Settings.CompletionEndpoint(Provider.CustomTranslation, endpoint);
    internal static void Authenticate(HttpRequestMessage request, ServiceSettings s, string key)
    {
        if (key.Length == 0) { if (!s.AllowAnonymous) throw new InvalidOperationException("请填写并保存此服务的密钥；无需密钥的本机服务请勾选匿名访问。"); return; }
        if (string.IsNullOrWhiteSpace(s.KeyHeader))
        {
            if (!s.BodyTemplate.Contains("{api_key}", StringComparison.Ordinal)) throw new ArgumentException("密钥请求头留空时，请在自定义请求模板中使用 {api_key}。");
            return;
        }
        if (s.KeyHeader.IndexOfAny(new[] { '\r', '\n', ':' }) >= 0 || s.KeyPrefix.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("密钥请求头格式无效。");
        try { request.Headers.Add(s.KeyHeader.Trim(), s.KeyPrefix + key); }
        catch (FormatException) { throw new ArgumentException("密钥请求头或前缀格式无效，请检查设置。"); }
    }
    internal static HttpContent Template(ServiceSettings s, Dictionary<string, string> values)
    {
        var node = JsonNode.Parse(s.BodyTemplate) ?? throw new ArgumentException("请求模板须为 JSON 对象。");
        JsonNode? Expand(JsonNode? n)
        {
            if (n is JsonObject obj) { foreach (var name in obj.Select(p => p.Key).ToArray()) obj[name] = Expand(obj[name]); }
            else if (n is JsonArray array) { for (int i = 0; i < array.Count; i++) array[i] = Expand(array[i]); }
            else if (n is JsonValue v && v.TryGetValue<string>(out var text))
            {
                // One pass: user text containing a placeholder cannot inject another template variable.
                return JsonValue.Create(System.Text.RegularExpressions.Regex.Replace(text, @"\{[a-z_]+\}", m => values.TryGetValue(m.Value, out var replacement) ? replacement : m.Value));
            }
            return n;
        }
        node = Expand(node)!;
        if (s.FormEncoded)
        {
            if (node is not JsonObject obj) throw new ArgumentException("表单模板须为 JSON 对象，字段值为字符串或数字。");
            return new FormUrlEncodedContent(obj.Select(p => new KeyValuePair<string, string>(p.Key, p.Value is JsonValue v && v.TryGetValue<string>(out var text) ? text : p.Value?.ToJsonString() ?? "")));
        }
        return new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
    }
    // Dot paths support array indices and [] projection, e.g. words_result[].words.
    internal static string Extract(JsonElement root, string path)
    {
        var items = new List<JsonElement> { root };
        foreach (var part in path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<JsonElement>();
            foreach (var item in items)
            {
                if (part == "*") { if (item.ValueKind == JsonValueKind.Array) next.AddRange(item.EnumerateArray()); }
                else if (int.TryParse(part, out int index)) { if (item.ValueKind == JsonValueKind.Array && index >= 0 && index < item.GetArrayLength()) next.Add(item[index]); }
                else if (item.ValueKind == JsonValueKind.Array) { foreach (var child in item.EnumerateArray()) if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty(part, out var value)) next.Add(value); }
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(part, out var value)) next.Add(value);
            }
            items = next;
        }
        return string.Join("\n", items.SelectMany(i => i.ValueKind == JsonValueKind.Array ? i.EnumerateArray().ToArray() : new[] { i }).Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString())).Trim();
    }
    internal static async Task<JsonDocument> SendJson(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"服务请求失败（HTTP {(int)response.StatusCode}）：检查地址、权限、额度和网络。远端响应未记录。");
        return await ReadJson(response.Content, ct);
    }
    internal static async Task<JsonDocument> ReadJson(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidOperationException("服务返回内容过大。");
        using var stream = await content.ReadAsStreamAsync(ct);
        using var memory = new System.IO.MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) { if (memory.Length + count > 4 * 1024 * 1024) throw new InvalidOperationException("服务返回内容过大。"); await memory.WriteAsync(buffer.AsMemory(0, count), ct); }
        memory.Position = 0; return await JsonDocument.ParseAsync(memory, cancellationToken: ct);
    }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static byte[] Hmac(byte[] key, string text) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));
    internal static void Tencent(HttpRequestMessage request, string body, ServiceSettings s, string secret, string service, string action, string version, long? unixTime = null)
    {
        if (s.AppId.Length == 0 || secret.Length == 0) throw new InvalidOperationException("腾讯云需要 SecretId 和 SecretKey。");
        long timestamp = unixTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(); string date = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string host = request.RequestUri!.Authority;
        string canonical = "POST\n" + request.RequestUri.AbsolutePath + "\n\ncontent-type:application/json; charset=utf-8\nhost:" + host + "\n\ncontent-type;host\n" + Hash(body);
        string scope = date + "/" + service + "/tc3_request";
        string toSign = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Hash(canonical);
        byte[] dateKey = Hmac(Encoding.UTF8.GetBytes("TC3" + secret), date), serviceKey = Hmac(dateKey, service), signingKey = Hmac(serviceKey, "tc3_request");
        string signature = Convert.ToHexString(Hmac(signingKey, toSign)).ToLowerInvariant();
        request.Headers.TryAddWithoutValidation("Authorization", "TC3-HMAC-SHA256 Credential=" + s.AppId + "/" + scope + ", SignedHeaders=content-type;host, Signature=" + signature);
        request.Headers.Add("X-TC-Action", action); request.Headers.Add("X-TC-Version", version); request.Headers.Add("X-TC-Timestamp", timestamp.ToString());
        if (s.Region.Length > 0) request.Headers.Add("X-TC-Region", s.Region);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
    }
}
