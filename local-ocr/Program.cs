using System.Drawing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuietTranslate;

// Optional process; never launched by the translator. No text/image files or request logs.
int port = 17321;
if (args.Length > 0 && (!int.TryParse(args[0], out port) || port < 1024 || port > 65535)) { Console.Error.WriteLine("端口须为 1024–65535。"); return 2; }
string token = Environment.GetEnvironmentVariable("QUIETTRANSLATE_OCR_TOKEN") ?? "";
using var stop = new CancellationTokenSource();
using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/");
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
try { listener.Start(); }
catch (HttpListenerException) { Console.Error.WriteLine("无法监听回环端口，请检查端口是否被占用或受系统策略限制。无需关闭防火墙。可换端口重试。"); return 3; }
Console.WriteLine($"轻译本机 OCR：http://127.0.0.1:{port}/ocr · Ctrl+C 退出");
Console.WriteLine(ScreenshotOcr.ReadinessMessage);
while (!stop.IsCancellationRequested)
{
    HttpListenerContext context;
    try { context = await listener.GetContextAsync(); }
    catch (Exception) when (stop.IsCancellationRequested) { break; }
    try
    {
        var request = context.Request;
        // No CORS. Prevent browser pages from using this local service as an OCR relay.
        if (request.Headers["Origin"] != null || request.Url?.Host != "127.0.0.1") { await Reply(403, new { error = "不接受网页跨域请求。" }); continue; }
        if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/health") { await Reply(200, new { ready = ScreenshotOcr.IsReady, languages = ScreenshotOcr.Languages }); continue; }
        if (request.HttpMethod != "POST" || request.Url.AbsolutePath != "/ocr") { await Reply(404, new { error = "使用 POST /ocr。" }); continue; }
        if (token.Length > 0 && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers["Authorization"] ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token)))) { await Reply(401, new { error = "本机服务密钥不正确。" }); continue; }
        if (request.ContentType?.Split(';')[0].Trim() != "application/json") { await Reply(415, new { error = "需要 application/json。" }); continue; }
        const int limit = 7 * 1024 * 1024;
        if (request.ContentLength64 > limit) { await Reply(413, new { error = "请求超过 7 MB。" }); continue; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var memory = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await request.InputStream.ReadAsync(buffer, deadline.Token)) > 0) { if (memory.Length + count > limit) throw new InvalidDataException(); await memory.WriteAsync(buffer.AsMemory(0, count), deadline.Token); }
        memory.Position = 0; using var json = await JsonDocument.ParseAsync(memory, cancellationToken: deadline.Token);
        var root = json.RootElement; string base64 = root.GetProperty("image_base64").GetString() ?? "";
        string language = root.TryGetProperty("language", out var lang) ? lang.GetString() ?? "auto" : "auto";
        using var bytes = new MemoryStream(Convert.FromBase64String(base64)); using var image = Image.FromStream(bytes);
        if (image.Width < 15 || image.Height < 15 || (long)image.Width * image.Height > 16_000_000) throw new InvalidDataException();
        using var bitmap = new Bitmap(image); string text = await ScreenshotOcr.RecognizeAsync(bitmap, deadline.Token, language);
        if (text.Length > 8000) { await Reply(413, new { error = "识别文字超过 8000 字，请缩小图片。" }); continue; }
        await Reply(200, new { text });
    }
    catch (OperationCanceledException) { await Reply(408, new { error = "读取或识别超时。" }); }
    catch (InvalidOperationException ex) { await Reply(422, new { error = ex.Message }); }
    catch { await Reply(400, new { error = "图片或 JSON 无效；需 image_base64 字段和可选 language 字段。" }); }
    finally { context.Response.Close(); }
    async Task Reply(int status, object value)
    {
        try { byte[] body = JsonSerializer.SerializeToUtf8Bytes(value); context.Response.StatusCode = status; context.Response.ContentType = "application/json; charset=utf-8"; context.Response.ContentLength64 = body.Length; context.Response.Headers["Cache-Control"] = "no-store"; await context.Response.OutputStream.WriteAsync(body); }
        catch (IOException) { } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
    }
}
return 0;
