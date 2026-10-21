using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace QuietTranslate;

internal static class SpeechTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, string message, int count = 120)
    { for (int i = 0; i < count && !condition(); i++) await Task.Delay(25); Check(condition(), message); }

    internal sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly Task accept;
        internal readonly ConcurrentQueue<string> Targets = new();
        internal readonly ConcurrentQueue<string> Errors = new();
        internal int Connections, Frames;
        internal volatile bool ReplacedKeySeen;
        internal int ErrorCode;
        private readonly WebSocketMessageType envelope;
        private readonly bool malformed;
        private readonly bool scripted;
        private readonly TaskCompletionSource<WebSocket> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Uri Address { get; }
        internal Server(int errorCode = 0, bool binary = false, bool malformed = false, bool scripted = false)
        {
            envelope = binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text; this.malformed = malformed; this.scripted = scripted;
            ErrorCode = errorCode; listener.Start(); Address = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live");
            accept = Task.Run(async () =>
            {
                try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(Handle(client)); } }
                catch (OperationCanceledException) { }
            });
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Interlocked.Increment(ref Connections); var stream = client.GetStream(); var header = new StringBuilder();
                    byte[] single = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(single, stop.Token) == 0) return;
                        header.Append((char)single[0]); if (header.Length > 16384) throw new Exception("Oversized synthetic handshake");
                    }
                    if (header.ToString().Split("\r\n")[0].Contains("key=synthetic-replaced-speech-key", StringComparison.Ordinal)) ReplacedKeySeen = true;
                    string key = header.ToString().Split("\r\n").First(s => s.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                    string accepted = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accepted + "\r\n\r\n"), stop.Token);
                    using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
                    using var setup = JsonDocument.Parse(await Read(socket)); var config = setup.RootElement.GetProperty("setup");
                    Check(config.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString() == "AUDIO", "Expected audio generation with transcript, not invented text-only mode");
                    Check(config.TryGetProperty("inputAudioTranscription", out _) && config.TryGetProperty("outputAudioTranscription", out _), "Transcription fields must be at setup level");
                    Check(!config.TryGetProperty("systemInstruction", out _) && !config.TryGetProperty("tools", out _), "Translation-only model must not receive agent instructions");
                    string target = config.GetProperty("generationConfig").GetProperty("translationConfig").GetProperty("targetLanguageCode").GetString()!; Targets.Enqueue(target);
                    if (ErrorCode != 0) { await Send(socket, JsonSerializer.Serialize(new { error = new { code = ErrorCode, message = "untrusted secret must not be exposed" } })); return; }
                    if (malformed) { await Send(socket, "not JSON"); return; }
                    await Send(socket, "{\"setupComplete\":{}}");
                    using var frame = JsonDocument.Parse(await Read(socket));
                    var audio = frame.RootElement.GetProperty("realtimeInput").GetProperty("audio");
                    Check(audio.GetProperty("mimeType").GetString() == "audio/pcm;rate=16000", "Wrong PCM format");
                    Check(Convert.FromBase64String(audio.GetProperty("data").GetString()!).Length == 3200, "Expected 100ms PCM frame"); Interlocked.Increment(ref Frames);
                    if (!scripted)
                    {
                    await Send(socket, "{\"serverContent\":{\"inputTranscription\":{\"text\":\"Synthetic input\"},\"outputTranscription\":{\"text\":\"译文\"},\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm\",\"data\":\"AAAA\"}}]}}}");
                    byte[] split = Encoding.UTF8.GetBytes("{\"serverContent\":{\"outputTranscription\":{\"text\":\"完整\"},\"generationComplete\":true},\"usageMetadata\":{\"totalTokenCount\":321}}");
                    await socket.SendAsync(split.AsMemory(0, 31), envelope, false, stop.Token);
                    await socket.SendAsync(split.AsMemory(31), envelope, true, stop.Token);
                    await Send(socket, "{\"serverContent\":{\"turnComplete\":true}}");
                    }
                    ready.TrySetResult(socket);
                    while (!stop.IsCancellationRequested) { using var more = JsonDocument.Parse(await Read(socket)); Interlocked.Increment(ref Frames); }
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) { }
                catch (Exception ex) { Errors.Enqueue(ex.Message); }
            }
        }
        private async Task Send(WebSocket socket, string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), envelope, true, stop.Token);
        internal async Task Caption(string delta, bool complete = false)
        {
            var socket = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Send(socket, JsonSerializer.Serialize(new { serverContent = new { outputTranscription = new { text = delta }, turnComplete = complete } }));
        }
        internal async Task Source(string text, bool interim = false)
        {
            var socket = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var fields = new Dictionary<string, object> { [interim ? "interimInputTranscription" : "inputTranscription"] = new { text } };
            await Send(socket, JsonSerializer.Serialize(new { serverContent = fields }));
        }
        private async Task<string> Read(WebSocket socket)
        {
            using var data = new MemoryStream(); byte[] buffer = new byte[8192];
            while (true) { var read = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token); if (read.MessageType == WebSocketMessageType.Close) throw new IOException(); data.Write(buffer, 0, read.Count); if (read.EndOfMessage) return Encoding.UTF8.GetString(data.ToArray()); }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await accept; try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3)); } catch { } stop.Dispose(); }
    }
    internal static async Task SyntheticCapture(SpeechOptions options, ChannelWriter<byte[]> frames, Action<double> level, CancellationToken ct)
    {
        while (true) { ct.ThrowIfCancellationRequested(); if (!frames.TryWrite(new byte[3200])) throw new Exception("Synthetic audio queue full"); level(.3); await Task.Delay(100, ct); }
    }
    internal static async Task<int> RunAsync(App app, string output, bool audioOnly = false)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> run)
        {
            File.WriteAllText(Path.Combine(output, "stage.txt"), name);
            try { await run(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.GetType().Name + ": " + ex.Message }); }
        }
        if (!audioOnly)
        {
            await Test("Settings have no persisted on-state or key; endpoint and shortcut validation", () =>
            {
                var options = new SpeechOptions(); options.Validate(); string json = JsonSerializer.Serialize(options);
                Check(!json.Contains("Enabled") && !json.Contains("ApiKey") && options.Target == "zh-Hans", "Unsafe defaults");
                foreach (string endpoint in new[] { "ws://example.com/live", "wss://example.com/live?key=bad", "wss://user:password@example.com/live", "https://example.com/live" })
                { options.Endpoint = endpoint; bool rejected = false; try { options.Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected, "Unsafe endpoint accepted"); }
                return Task.CompletedTask;
            });
            await Test("History encrypted at rest, paged, bounded, clearable and cleaned after abandonment", () =>
            {
                string root = Path.Combine(output, "encrypted"); SessionHistory.Cleanup(root);
                using (var history = new SessionHistory(root, 1600))
                {
                    var record = new SpeechRecord(DateTimeOffset.Now, "synthetic", "ja", "SYNTHETIC_PRIVATE_SOURCE", "SYNTHETIC_PRIVATE_TRANSLATION");
                    Check(history.Append(record), "Append failed");
                    using (var encrypted = new FileStream(Directory.GetFiles(root, "*.qth").Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var bytes = new MemoryStream()) { encrypted.CopyTo(bytes); Check(!Encoding.UTF8.GetString(bytes.ToArray()).Contains("PRIVATE"), "Plaintext leaked to disk"); }
                    Check(history.ReadPage(0).Single() == record, "Decryption/page failed");
                    for (int i = 0; i < 10 && !history.Full; i++) history.Append(record);
                    Check(history.Full && history.Bytes <= 1600, "History limit not enforced"); history.Clear(); Check(history.Count == 0 && history.Bytes == 0 && !history.Full, "Clear failed");
                }
                File.WriteAllBytes(Path.Combine(root, Guid.NewGuid().ToString("N") + ".qth"), new byte[12]);
                SessionHistory.Cleanup(root); Check(!Directory.GetFiles(root).Any(), "Startup cleanup failed"); return Task.CompletedTask;
            });
            await Test("A blocked history deletion leaves a marker and startup cleanup retries", () =>
            {
                string root = Path.Combine(output, "locked-history"); SessionHistory.Cleanup(root);
                var history = new SessionHistory(root); history.Append(new(DateTimeOffset.Now, "synthetic", "en", "source", "target"));
                using (var held = new FileStream(Directory.GetFiles(root, "*.qth").Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bool rejected = false; try { history.Dispose(); } catch (IOException) { rejected = true; }
                    Check(rejected && File.Exists(Path.Combine(root, "cleanup.pending")), "Deletion failure was hidden");
                }
                SessionHistory.Cleanup(root); Check(Directory.GetFiles(root).Length == 0, "Retry did not remove residual record"); return Task.CompletedTask;
            });
            await Test("An occupied speech shortcut preserves the pre-existing translation shortcuts", () =>
            {
                using var holder = new Hotkeys(); holder.Apply("Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F10");
                using var keys = new Hotkeys(); var options = new Settings { SelectionHotkey = "Ctrl+Alt+Shift+1", ScreenshotHotkey = "Ctrl+Alt+Shift+2", PauseHotkey = "Ctrl+Alt+Shift+3", ManualHotkey = "Ctrl+Alt+Shift+4", Speech = new SpeechOptions { Hotkey = "Ctrl+Alt+Shift+F9" } };
                keys.Apply(options, false); Check(keys.SpeechUnavailable && !Hotkeys.Available(Hotkey.Parse(options.SelectionHotkey)), "Speech shortcut disabled original translation keys"); return Task.CompletedTask;
            });
            await Test("Gemini fragments, same-frame audio and captions, language switch and disable cleanup", async () =>
            {
                await using var server = new Server(); var options = new SpeechOptions { Endpoint = server.Address.AbsoluteUri };
                var session = new LiveSession(Path.Combine(output, "session"), SyntheticCapture);
                try
                {
                    Check(!session.Enabled, "Default on"); await session.EnableAsync(options, "synthetic-key");
                    await Until(() => session.Records == 1, "Final transcript did not arrive");
                    Check(session.Translation == "译文完整" && session.Usage == 321, "Transcript/audio/usage processing failed");
                    await Task.Delay(120); Check(session.Records == 1, "generationComplete and turnComplete duplicated records");
                    var first = (await session.ReadPageAsync(0)).Single(); Check(first.Target == "zh-Hans" && first.Context > 0 && first.Original.Length == 0 && (await session.ReadPageAsync(0, true)).Single().Original == "Synthetic input", "Independent source or target record lost");
                    options.Target = "ja"; await session.ReconfigureAsync(options, "synthetic-key"); await Until(() => session.Records == 2, "Language switch lost session history");
                    Check(server.Targets.SequenceEqual(new[] { "zh-Hans", "ja" }), "Wrong language routing");
                    // Capture a synthetic UI preview; no user audio/config/cloud account is involved.
                    var overlay = new SpeechWindow(session, () => { }, () => { }, _ => Task.CompletedTask); overlay.Show(); await Task.Delay(160);
                    try
                    {
                        overlay.UpdateLayout(); var image = new RenderTargetBitmap((int)overlay.ActualWidth, (int)overlay.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(overlay);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, "speech-overlay.png")); encoder.Save(file);
                        overlay.SetLocked(true); overlay.SetLocked(false);
                    }
                    finally { overlay.Close(); }
                    await session.DisableAsync(); int frames = server.Frames; await Task.Delay(250);
                    Check(!session.Enabled && session.Records == 0 && session.Translation.Length == 0 && !Directory.GetFiles(Path.Combine(output, "session"), "*.qth").Any(), "Disable retained records/captions");
                    Check(server.Frames == frames && server.Errors.IsEmpty, "Audio continued or protocol mismatch");
                    await session.EnableAsync(options, "synthetic-key"); await Until(() => session.Records == 1, "New session failed"); Check(session.Records == 1, "Prior history restored");
                }
                finally { await session.DisableAsync(); }
            });
            await Test("Off during startup prevents capture; rejected service never receives audio", async () =>
            {
                await using var server = new Server(403); int captures = 0;
                Task Capture(SpeechOptions o, ChannelWriter<byte[]> w, Action<double> l, CancellationToken c) { captures++; return SyntheticCapture(o, w, l, c); }
                var session = new LiveSession(Path.Combine(output, "cancelled"), Capture); var options = new SpeechOptions { Endpoint = server.Address.AbsoluteUri };
                try
                {
                    Task enabling = session.EnableAsync(options, "synthetic-key"); Task disabling = session.DisableAsync(); await Task.WhenAll(enabling, disabling);
                    Check(!session.Enabled && captures == 0, "Startup resurrected capture after off");
                    await session.EnableAsync(options, "synthetic-key"); await Until(() => session.Status.Contains("鉴权"), "Service error missing");
                    int connections = server.Connections; await Task.Delay(250); Check(server.Connections == connections && captures == 0, "Rejected service retried or captured audio");
                    Check(!session.Status.Contains("untrusted"), "Remote error leaked");
                }
                finally { await session.DisableAsync(); }
            });
            await Test("Reconnect capped at three; off cancels backoff and clears history", async () =>
            {
                await using var server = new Server(503); var options = new SpeechOptions { Endpoint = server.Address.AbsoluteUri };
                var session = new LiveSession(Path.Combine(output, "retry"), SyntheticCapture);
                try
                {
                    await session.EnableAsync(options, "synthetic-key"); await Until(() => server.Connections == 4 && session.Status.Contains("已停止"), "Retry bound failed", 450);
                    await Task.Delay(200); Check(server.Connections == 4, "Unbounded retries"); await session.DisableAsync();
                    await session.EnableAsync(options, "synthetic-key"); await Until(() => session.Status.Contains("重连"), "Backoff missing");
                    await session.DisableAsync(); int connections = server.Connections; await Task.Delay(1100); Check(server.Connections == connections && !session.Enabled, "Retry restarted after off");
                }
                finally { await session.DisableAsync(); }
            });
            await Test("Settings save credentials without enabling capture and show the saved-key state", async () =>
            {
                var options = new Settings(); options.Speech.Endpoint = "ws://127.0.0.1:1/synthetic-" + Guid.NewGuid().ToString("N");
                string target = options.Speech.SecretTarget; Settings? saved = null; int toggles = 0;
                var window = new SettingsWindow(options, value => saved = value, toggleSpeech: () => toggles++);
                try
                {
                    window.Show(); window.ShowPage(6); await Task.Delay(200);
                    Check(HoverTest.Find<PasswordBox>(window, "SpeechApiKey")!.Password.Length == 0, "Saved secret exposed");
                    HoverTest.Find<PasswordBox>(window, "SpeechApiKey")!.Password = "synthetic-speech-key";
                    HoverTest.Find<ComboBox>(window, "SpeechTarget")!.SelectedIndex = Array.IndexOf(SpeechOptions.Languages, "ja");
                    HoverTest.Find<Button>(window, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(saved?.Speech.Target == "ja" && Credentials.Exists(target) && toggles == 0, "Save lost options or started capture");
                    Check(HoverTest.Find<TextBlock>(window, "SpeechKeyStatus")!.Text.Contains("密钥已保存") && HoverTest.Find<PasswordBox>(window, "SpeechApiKey")!.Password.Length == 0, "Credential badge/reset failed");
                    Check(!JsonSerializer.Serialize(saved).Contains("synthetic-speech-key"), "Secret in settings JSON");
                    HoverTest.Find<Button>(window, "ToggleSpeech")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(toggles == 1, "Explicit master switch unavailable");
                }
                finally { window.Close(); Credentials.Set(target, ""); }
            });
            await Test("Application shortcut is a master switch; pause and close delete records without automatic resume", async () => await app.CheckSpeechSwitchAsync(output));
        }
        else
        {
            await Test("Native per-process capture receives only the fixture's generated tone as 16kHz PCM", async () =>
            {
                Check(AudioCapture.SupportsProcess, "Process capture unavailable on this Windows");
                using var self = Process.GetCurrentProcess(); using var playback = new WasapiOut(AudioClientShareMode.Shared, 100);
                var tone = new SignalGenerator(48000, 2) { Frequency = 660, Gain = .02, Type = SignalGeneratorType.Sin };
                playback.Init(tone.ToWaveProvider16());
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4)); int frames = 0, nonzero = 0;
                Task capturing = AudioCapture.RunAsync(new SpeechOptions { CaptureProcess = true, ProcessId = self.Id, ProcessName = self.ProcessName, ProcessStarted = self.StartTime.ToUniversalTime().Ticks }, frame =>
                { Check(frame.Length == 3200, "Wrong native frame size"); Interlocked.Increment(ref frames); if (frame.Any(b => b != 0)) Interlocked.Increment(ref nonzero); Array.Clear(frame); }, _ => { }, stop.Token);
                try { playback.Play(); await Task.Delay(1500); stop.Cancel(); try { await capturing; } catch (OperationCanceledException) { } Check(frames > 0 && nonzero > 0, "No synthetic process audio captured"); }
                finally { stop.Cancel(); playback.Stop(); try { await capturing; } catch (OperationCanceledException) { } }
            });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "speech-tests.json"), JsonSerializer.Serialize(new { passed = results.Count - failed, failed, results, realCloudCalls = false, syntheticAudioOnly = true }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}

