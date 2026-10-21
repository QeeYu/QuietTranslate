using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QuietTranslate;

internal sealed class SpeechConnectionException(string message, bool retry) : Exception(message)
{
    internal bool Retry { get; } = retry;
}

internal sealed class GeminiLive
{
    private readonly SpeechOptions options;
    private readonly string secret;
    private readonly Func<ChannelWriter<byte[]>, CancellationToken, Task> capture;
    internal GeminiLive(SpeechOptions options, string secret, Func<ChannelWriter<byte[]>, CancellationToken, Task> capture)
    { this.options = options.Copy(); this.secret = secret; this.capture = capture; }

    internal static object Setup(SpeechOptions options) => new
    {
        setup = new
        {
            model = options.Model.StartsWith("models/", StringComparison.Ordinal) ? options.Model : "models/" + options.Model,
            generationConfig = new { responseModalities = new[] { "AUDIO" }, translationConfig = new { targetLanguageCode = options.Target, echoTargetLanguage = options.EchoTarget } },
            inputAudioTranscription = new { }, outputAudioTranscription = new { }
        }
    };

    internal async Task RunAsync(Action<string, string, bool> text, Action<string> status, Action<long> sent, Action<long> usage, CancellationToken ct, Action<string, bool>? input = null)
    {
        using var socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var transport = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(8) });
        Uri address = options.Address();
        if (options.Authentication == 0) address = new UriBuilder(address) { Query = "key=" + Uri.EscapeDataString(secret) }.Uri;
        else socket.Options.SetRequestHeader(options.Authentication == 1 ? "x-goog-api-key" : "Authorization", options.Authentication == 1 ? secret : "Bearer " + secret);
        using var abort = ct.Register(socket.Abort);
        using (var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(address, transport, setupDeadline.Token).ConfigureAwait(false);
            await SendAsync(socket, Setup(options), setupDeadline.Token).ConfigureAwait(false);
            bool ready = false;
            while (!ready)
            {
                using var message = await ReceiveAsync(socket, setupDeadline.Token).ConfigureAwait(false);
                CheckError(message.RootElement);
                ready = message.RootElement.TryGetProperty("setupComplete", out _);
            }
        }
        ct.ThrowIfCancellationRequested(); status("正在采集 · 等待语音");
        // Ten 100ms frames: stall protection fails rather than silently translating stale audio.
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = connection.Token;
        Task recording = capture(frames.Writer, token);
        Task sending = SendAudioAsync(socket, frames.Reader, sent, token);
        Task receiving = ReadTextAsync(socket, text, usage, token, input);
        try
        {
            Task first = await Task.WhenAny(recording, sending, receiving).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new SpeechConnectionException("语音连接已结束。", true);
        }
        finally
        {
            connection.Cancel(); socket.Abort(); frames.Writer.TryComplete();
            try { await Task.WhenAll(recording, sending, receiving).ConfigureAwait(false); } catch { }
            while (frames.Reader.TryRead(out var frame)) Array.Clear(frame);
        }
    }
    private static async Task SendAudioAsync(ClientWebSocket socket, ChannelReader<byte[]> frames, Action<long> sent, CancellationToken ct)
    {
        await foreach (var frame in frames.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try { await SendAsync(socket, new { realtimeInput = new { audio = new { data = Convert.ToBase64String(frame), mimeType = "audio/pcm;rate=16000" } } }, ct).ConfigureAwait(false); sent(frame.Length); }
            finally { Array.Clear(frame); }
        }
    }
    private static async Task ReadTextAsync(ClientWebSocket socket, Action<string, string, bool> update, Action<long> usage, CancellationToken ct, Action<string, bool>? input)
    {
        var original = new StringBuilder(); var translated = new StringBuilder(); bool finalized = false;
        while (true)
        {
            using var message = await ReceiveAsync(socket, ct).ConfigureAwait(false); var root = message.RootElement; CheckError(root);
            if (root.TryGetProperty("usageMetadata", out var metrics) && metrics.TryGetProperty("totalTokenCount", out var count) && count.TryGetInt64(out long number) && number >= 0) usage(number);
            if (root.TryGetProperty("goAway", out _)) throw new SpeechConnectionException("服务端即将结束连接，正在重新连接。", true);
            if (!root.TryGetProperty("serverContent", out var content)) continue;
            if (content.TryGetProperty("interimInputTranscription", out var interim) && interim.TryGetProperty("text", out var draft)) input?.Invoke(draft.GetString() ?? "", false);
            if (content.TryGetProperty("inputTranscription", out var source) && source.TryGetProperty("text", out var sourceText))
            {
                // Input transcription is an independent stream; show it as contemporaneous,
                // not as a guaranteed word/sentence alignment with translated speech.
                string recognized = sourceText.GetString() ?? ""; original.Append(recognized); input?.Invoke(recognized, true);
                if (original.Length > 16000) original.Remove(0, original.Length - 16000);
            }
            if (content.TryGetProperty("outputTranscription", out var target) && target.TryGetProperty("text", out var targetText))
            {
                if (finalized) { translated.Clear(); finalized = false; }
                translated.Append(targetText.GetString());
                if (translated.Length > 16000 || original.Length > 16000) throw new SpeechConnectionException("服务返回的字幕段落过长，请重新开启功能。", false);
                if (translated.Length > 0) update(original.ToString(), translated.ToString(), false);
            }
            if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
            { original.Clear(); translated.Clear(); finalized = false; update("", "", false); continue; }
            bool complete = (content.TryGetProperty("generationComplete", out var generated) && generated.ValueKind == JsonValueKind.True) ||
                (content.TryGetProperty("turnComplete", out var done) && done.ValueKind == JsonValueKind.True);
            if (complete && !finalized && translated.Length > 0)
            { update(original.ToString(), translated.ToString(), true); original.Clear(); finalized = true; }
        }
    }
    private static void CheckError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return;
        int code = error.TryGetProperty("code", out var value) && value.TryGetInt32(out int n) ? n : 0;
        throw new SpeechConnectionException(code switch { 401 or 403 => "语音服务鉴权失败或没有模型权限，请检查密钥。", 429 => "语音服务限流或额度不足。", 400 or 404 => "语音接口、模型或参数不被服务支持。", _ => "语音服务返回错误，请稍后重试。" }, code is 429 or >= 500);
    }
    private static async Task SendAsync(ClientWebSocket socket, object message, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { Array.Clear(bytes); }
    }
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[8192]; using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) throw new SpeechConnectionException("语音服务连接已断开。", received.CloseStatus is WebSocketCloseStatus.NormalClosure or WebSocketCloseStatus.EndpointUnavailable);
            // Gemini may deliver its UTF-8 JSON in either text or binary frames.
            // Binary here is the envelope, not raw PCM; keep parsing/size checks.
            if (received.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary)) throw new SpeechConnectionException("语音服务返回了不兼容的消息。", false);
            if (message.Length + received.Count > 1024 * 1024) throw new SpeechConnectionException("语音服务消息过大。", false);
            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                message.Position = 0;
                try { return await JsonDocument.ParseAsync(message, cancellationToken: ct).ConfigureAwait(false); }
                catch (JsonException) { throw new SpeechConnectionException("语音服务返回的消息不是有效 JSON，请检查是否为 Gemini Live 兼容接口。", false); }
            }
        }
    }
}
