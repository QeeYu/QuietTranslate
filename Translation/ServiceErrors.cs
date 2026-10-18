using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuietTranslate;

internal enum ServiceFailure { Unknown, Authentication, Quota, RateLimit, Busy, InvalidRequest }

internal sealed class ServiceException : InvalidOperationException
{
    internal ServiceFailure Failure { get; }
    internal TimeSpan? RetryAfter { get; }
    internal ServiceException(string message, ServiceFailure failure, TimeSpan? retryAfter = null) : base(message)
    { Failure = failure; RetryAfter = retryAfter; }
}

internal static class ServiceErrors
{
    internal static async Task<ServiceException> FromHttpAsync(HttpResponseMessage response, CancellationToken ct, Provider? provider = null)
    {
        int status = (int)response.StatusCode;
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date) retryAfter = date - DateTimeOffset.UtcNow;
        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        // Error bodies are untrusted: bounded read, no remote messages, IDs or unknown codes in UI/logs.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var doc = await ApiSupport.ReadJson(response.Content, budget.Token, 16 * 1024).ConfigureAwait(false);
            if (FromJson(doc.RootElement, provider, status, retryAfter) is { } error) return error;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or InvalidOperationException) { }
        ct.ThrowIfCancellationRequested();
        return Create(status, null, provider, retryAfter);
    }

    internal static ServiceException? FromJson(JsonElement root, Provider? provider = null, int? status = null, TimeSpan? retryAfter = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        JsonElement error;
        string? code = null;
        if (root.TryGetProperty("error", out error))
        {
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var value)) code = Scalar(value);
                if (code == null && error.TryGetProperty("type", out value)) code = Scalar(value);
            }
        }
        else if (root.TryGetProperty("error_code", out error) || root.TryGetProperty("errorCode", out error))
        { code = Scalar(error); if (code == "0") return null; }
        else if (root.TryGetProperty("Response", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("Error", out error))
        { if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("Code", out var value)) code = Scalar(value); }
        else return null;
        return Create(status, code, provider, retryAfter);
    }

    private static string? Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;

    private static ServiceException Create(int? status, string? code, Provider? provider, TimeSpan? retryAfter)
    {
        (ServiceFailure failure, string hint) = status switch
        {
            401 or 403 => (ServiceFailure.Authentication, "认证或权限不足：检查 API Key、服务地址及区域。"),
            402 or 456 => (ServiceFailure.Quota, "额度不足，请检查服务余额或使用上限。"),
            404 => (ServiceFailure.InvalidRequest, "接口或模型不存在，请检查基础地址与模型名。"),
            429 => (ServiceFailure.RateLimit, "服务暂时拒绝请求：可能是限流、额度限制或平台繁忙，请稍后重试并检查平台用量。"),
            400 => (ServiceFailure.InvalidRequest, "请求不被服务接受；通用 AI 网关可尝试取消“发送关闭思考参数”。"),
            >= 500 => (ServiceFailure.Busy, "翻译服务暂时异常，请稍后重试。"),
            _ => (ServiceFailure.Unknown, "服务请求未完成，请检查权限、额度或稍后重试。")
        };
        (ServiceFailure, string)? known = code switch
        {
            "insufficient_quota" or "quota_exceeded" => (ServiceFailure.Quota, "服务额度不足或已达使用上限，请检查平台用量。"),
            "rate_limit_exceeded" => (ServiceFailure.RateLimit, "请求频率或 Token 用量达到限制，请降低频率后重试。"),
            "invalid_api_key" => (ServiceFailure.Authentication, "API Key 无效，请检查当前服务的密钥。"),
            "model_not_found" => (ServiceFailure.InvalidRequest, "模型不存在或没有访问权限，请检查模型名。"),
            "context_length_exceeded" => (ServiceFailure.InvalidRequest, "文本超过模型上下文限制，请缩短后重试。"),
            "server_overloaded" => (ServiceFailure.Busy, "模型服务当前繁忙，请稍后重试。"),
            _ => null
        };
        if (provider == Provider.GLM)
        {
            known ??= code switch
            {
                "1113" => (ServiceFailure.Quota, "智谱账户已欠费，请到平台检查账户状态。"),
                "1302" => (ServiceFailure.RateLimit, "智谱账户请求频率或并发达到限制，请降低频率后重试。"),
                "1305" => (ServiceFailure.Busy, "智谱模型当前访问量过大，请稍后重试。"),
                "1308" or "1310" => (ServiceFailure.Quota, "智谱账户已达到使用上限，请查看平台额度及重置时间。"),
                "1309" => (ServiceFailure.Quota, "智谱订阅套餐已到期，请检查套餐状态。"),
                "1311" => (ServiceFailure.Authentication, "当前智谱套餐没有此模型的访问权限。"),
                "1313" => (ServiceFailure.RateLimit, "智谱账户触发公平使用限制，请查看平台说明。"),
                "1315" => (ServiceFailure.Authentication, "此密钥仅限智谱企业编程套餐，请更换适用于当前接口的密钥。"),
                "1210" or "1214" => (ServiceFailure.InvalidRequest, "智谱请求参数不兼容，请检查模型及高级参数。"),
                "1211" => (ServiceFailure.InvalidRequest, "智谱模型不存在，请检查模型名称。"),
                "1220" => (ServiceFailure.Authentication, "当前密钥没有访问此智谱接口的权限。"),
                _ => null
            };
        }
        string detail = status.HasValue ? $"HTTP {status}" : "服务错误";
        if (known.HasValue) { (failure, hint) = known.Value; detail += " · " + code; }
        if (retryAfter > TimeSpan.Zero && failure is ServiceFailure.RateLimit or ServiceFailure.Busy)
            hint += $" 服务建议至少等待 {Math.Ceiling(retryAfter.Value.TotalSeconds):0} 秒。";
        return new ServiceException($"{hint}（{detail}）", failure, retryAfter);
    }
}
