using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class SelfTest
{
    private sealed class MockHandler : HttpMessageHandler
    {
        internal int Calls;
        internal bool BlockUntilCanceled;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; Started.TrySetResult(); if (BlockUntilCanceled) await Task.Delay(Timeout.Infinite, ct); return await Respond(request); }
    }
    private static HttpResponseMessage Response(string body, string type = "application/json", HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    private static async Task Reject(Func<Task> action, string label)
    { try { await action(); } catch (InvalidOperationException) { return; } throw new Exception(label); }
    public static async Task<int> RunAsync(string output)
    {
        var results = new List<object>(); var observations = new Dictionary<string, string>(); int failures = 0;
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); results.Add(new { name, status = "PASS" }); }
            catch (Exception ex) { failures++; results.Add(new { name, status = "FAIL", error = ex.GetType().Name + ": " + ex.Message }); }
        }
        await Test("Endpoint validation and completion paths", () =>
        {
            Check(Settings.CompletionEndpoint(Provider.OpenAI, "https://api.deepseek.com/v1/").AbsoluteUri == "https://api.deepseek.com/v1/chat/completions", "OpenAI path");
            Check(Settings.CompletionEndpoint(Provider.OpenAI, "https://api.deepseek.com/chat/completions").AbsolutePath == "/chat/completions", "full path");
            Check(Settings.CompletionEndpoint(Provider.Microsoft, "https://api.cognitive.microsofttranslator.com").Query == "?api-version=3.0&to=zh-Hans", "Microsoft path");
            Check(Settings.CompletionEndpoint(Provider.DeepL, "https://api-free.deepl.com").AbsolutePath == "/v2/translate", "DeepL path");
            foreach (string invalid in new[] { "http://example.com", "https://user:password@example.com", "https://example.com?key=secret", "file:///tmp/test" })
            { bool rejected = false; try { Settings.CompletionEndpoint(Provider.OpenAI, invalid); } catch (ArgumentException) { rejected = true; } Check(rejected, "unsafe URL accepted"); }
            Settings.CompletionEndpoint(Provider.OpenAI, "http://127.0.0.1:8000/v1"); return Task.CompletedTask;
        });
        await Test("Hotkey parsing and invalid input", () =>
        {
            Check(Hotkey.Parse("Ctrl+Alt+Q") == new Hotkey(3, 81), "Q hotkey"); Check(Hotkey.Parse("Ctrl+Shift+F8") == new Hotkey(6, 0x77), "F8 hotkey");
            Check(Hotkey.Parse("Ctrl+Alt+9") == new Hotkey(3, 57), "digit hotkey");
            Check(Hotkey.Format(6, 0x77) == "Ctrl+Shift+F8", "recorder formatting");
            foreach (string invalid in new[] { "Q", "Ctrl", "Ctrl+Q+W", "Ctrl+F13" }) { bool rejected = false; try { Hotkey.Parse(invalid); } catch (ArgumentException) { rejected = true; } Check(rejected, invalid); }
            return Task.CompletedTask;
        });
        await Test("Bubble right placement, edge fallback, negative monitor coordinates", () =>
        {
            var work = new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var point = BubbleWindow.Place(new Native.Rect { Left = 100, Top = 100, Right = 200 }, work, 360, 240); Check(point.X == 212 && point.Y == 100, "right side");
            point = BubbleWindow.Place(new Native.Rect { Left = 1700, Top = 1000, Right = 1800 }, work, 360, 240); Check(point.X == 1328 && point.Y == 832, "edge fallback");
            point = BubbleWindow.Place(new Native.Rect { Left = -1700, Top = -200, Right = -1600 }, new Native.Rect { Left = -1920, Top = -300, Right = 0, Bottom = 780 }, 360, 240); Check(point.X == -1588, "left monitor"); return Task.CompletedTask;
        });
        await Test("Bubble background alpha and RGB text color remain independent", () =>
        {
            Check(Settings.NormalizeTextColor(" #91bdf4 ") == "#91BDF4", "normalize RGB");
            Check(Settings.NormalizeTextColor("") == "", "automatic color");
            Check(Ui.Contrast(Colors.Black, Colors.White) > 20 && Ui.Contrast(Colors.White, Colors.White) == 1, "contrast hint calculation");
            foreach (var invalid in new[] { "red", "#FFF", "#00FFFFFF", "#GG0000", "# 12345" }) { bool rejected = false; try { Settings.NormalizeTextColor(invalid); } catch (ArgumentException) { rejected = true; } Check(rejected, "bad color accepted"); }
            var view = new BubbleView(.25, 18, "#91BDF4", true, () => { }, () => { });
            Check(view.BackgroundAlpha == 64 && view.TextColor == Ui.Color("#91BDF4") && view.TextColor.A == 255, "text should stay opaque");
            var saved = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { BubbleOpacity = .25, BubbleTextColor = "#91BDF4", SelectionButtonEnabled = true }));
            Check(saved!.BubbleTextColor == "#91BDF4" && saved.BubbleOpacity == .25 && saved.SelectionButtonEnabled, "preferences roundtrip"); return Task.CompletedTask;
        });
        await Test("OpenAI streaming and repeated inputs make new requests without result cache", async () =>
        {
            var handler = new MockHandler();
            handler.Respond = async request =>
            {
                Check(request.Headers.Authorization?.Scheme == "Bearer", "auth");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("model").GetString() == "deepseek-flash", "model");
                Check(body.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "thinking");
                return Response(": heartbeat\n\ndata: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hidden\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", "text/event-stream");
            };
            using var service = new TranslationService(handler); string shown = "";
            for (int i = 0; i < 2; i++) Check(await service.TranslateAsync(Provider.OpenAI, new(), "dummy", "Hello", s => shown = s, CancellationToken.None) == "你好，世界", "translation");
            Check(handler.Calls == 2 && shown == "你好，世界", "must not cache translated text");
        });
        await Test("Microsoft auto detection and simplified Chinese request", async () =>
        {
            var handler = new MockHandler { Respond = async request =>
            {
                Check(request.RequestUri!.Query == "?api-version=3.0&to=zh-Hans", "no from parameter");
                Check(request.Headers.Contains("Ocp-Apim-Subscription-Key") && request.Headers.Contains("Ocp-Apim-Subscription-Region"), "auth headers");
                Check((await request.Content!.ReadAsStringAsync()).Contains("Text"), "body");
                return Response("[{\"detectedLanguage\":{\"language\":\"ja\"},\"translations\":[{\"text\":\"你好\",\"to\":\"zh-Hans\"}]}]");
            } };
            using var service = new TranslationService(handler);
            Check(await service.TranslateAsync(Provider.Microsoft, new() { Endpoint = "https://api.cognitive.microsofttranslator.com", Region = "eastasia" }, "dummy", "こんにちは", _ => { }, CancellationToken.None) == "你好", "result");
        });
        await Test("DeepL auto detection and simplified Chinese request", async () =>
        {
            var handler = new MockHandler { Respond = async request =>
            {
                Check(request.Headers.Authorization?.Scheme == "DeepL-Auth-Key", "auth");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(!body.RootElement.TryGetProperty("source_lang", out _) && body.RootElement.GetProperty("target_lang").GetString() == "ZH-HANS", "auto source");
                return Response("{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"你好\"}]}");
            } };
            using var service = new TranslationService(handler);
            Check(await service.TranslateAsync(Provider.DeepL, new() { Endpoint = "https://api-free.deepl.com" }, "dummy", "Hello", _ => { }, CancellationToken.None) == "你好", "result");
        });
        await Test("Non-stream JSON fallback and generic AI without thinking extension", async () =>
        {
            var handler = new MockHandler { Respond = async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); Check(!body.RootElement.TryGetProperty("thinking", out _), "extension omitted");
                return Response("{\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}]}");
            } };
            using var service = new TranslationService(handler);
            Check(await service.TranslateAsync(Provider.OpenAI, new() { DisableThinking = false }, "dummy", "Hello", _ => { }, CancellationToken.None) == "你好", "fallback");
        });
        await Test("SSE multiline event, [DONE], incomplete stream and truncation", async () =>
        {
            async Task<string> Read(string value) { using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(value))); return await TranslationService.ReadStreamAsync(reader, _ => { }, CancellationToken.None); }
            Check(await Read("data: {\"choices\":\ndata: [{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n") == "ok", "multiline");
            await Reject(async () => { await Read("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"); }, "EOF must fail");
            await Reject(async () => { await Read("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"); }, "length must fail");
        });
        await Test("HTTP errors redact remote bodies; cancellation and length limits", async () =>
        {
            var handler = new MockHandler { Respond = _ => Task.FromResult(Response("secret-source-and-key", status: HttpStatusCode.Unauthorized)) };
            using var service = new TranslationService(handler);
            try { await service.TranslateAsync(Provider.OpenAI, new(), "dummy", "Hello", _ => { }, CancellationToken.None); throw new Exception("expected failure"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("401") && !ex.Message.Contains("secret"), "error privacy"); }
            await Reject(async () => { await service.TranslateAsync(Provider.OpenAI, new(), "dummy", new string('x', 8001), _ => { }, CancellationToken.None); }, "input cap");
            using var cts = new CancellationTokenSource(); cts.Cancel(); bool canceled = false;
            try { await service.TranslateAsync(Provider.OpenAI, new(), "dummy", "Hello", _ => { }, cts.Token); } catch (OperationCanceledException) { canceled = true; } Check(canceled, "cancel");
        });
        await Test("Credential Manager secret roundtrip and deletion, isolated target", () =>
        {
            string target = "QuietTranslate.SelfTest/" + Guid.NewGuid();
            try { Credentials.Set(target, "test-only-secret-测试"); Check(Credentials.Get(target) == "test-only-secret-测试", "roundtrip"); }
            finally { Credentials.Set(target, ""); }
            Check(Credentials.Get(target) == "", "delete");
            Check(Credentials.Target(Provider.OpenAI, "https://one.example") != Credentials.Target(Provider.OpenAI, "https://two.example"), "host binding"); return Task.CompletedTask;
        });
        await Test("Cancel an in-flight request without retrying or delivering late text", async () =>
        {
            var handler = new MockHandler { BlockUntilCanceled = true }; using var service = new TranslationService(handler); using var cts = new CancellationTokenSource();
            bool updated = false;
            var request = service.TranslateAsync(Provider.OpenAI, new(), "dummy", "synthetic pending text", _ => updated = true, cts.Token);
            await handler.Started.Task; cts.Cancel();
            try { await request; throw new Exception("Pending request was not canceled"); } catch (OperationCanceledException) { }
            Check(!updated && handler.Calls == 1, "No late text and no automatic retry");
        });
        await Test("Native hotkey registration and release", () =>
        {
            using var hotkeys = new Hotkeys(); hotkeys.Apply("Ctrl+Alt+Shift+F11", "Ctrl+Alt+Shift+F10");
            Check(!Hotkeys.Available(Hotkey.Parse("Ctrl+Alt+Shift+F11")), "conflict detection"); hotkeys.Suspend();
            Check(Hotkeys.Available(Hotkey.Parse("Ctrl+Alt+Shift+F11")), "suspend releases shortcut"); return Task.CompletedTask;
        });
        await Test("Bilingual OCR blocks missing language packs before recognition", async () =>
        {
            observations["ocrReadiness"] = ScreenshotOcr.ReadinessMessage;
            if (!ScreenshotOcr.IsReady)
            {
                await Reject(() => { ScreenshotOcr.RequireLanguages(); return Task.CompletedTask; }, "Missing language pack must block OCR");
                observations["ocrScope"] = "Missing English/Japanese packs: screenshot translation is blocked; no fallback text is sent to API."; return;
            }
            string sample = "Hello World";
            using var bitmap = new System.Drawing.Bitmap(600, 100);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Arial", 28)) { g.Clear(System.Drawing.Color.White); g.DrawString(sample, font, System.Drawing.Brushes.Black, 10, 15); }
            string text = await ScreenshotOcr.RecognizeAsync(bitmap, CancellationToken.None);
            observations["ocrSample"] = sample; observations["ocrActual"] = text;
            observations["ocrScope"] = "English sample only; does not verify Japanese accuracy.";
            Check(!string.IsNullOrWhiteSpace(text), "OCR returned no text");
        });
        string languages;
        try { languages = ScreenshotOcr.Languages; } catch (Exception ex) { languages = ex.GetType().Name; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), ocrLanguages = languages, observations, passed = results.Count - failures, failed = failures, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
    public static async Task<int> UiSmokeAsync(string output)
    {
        Directory.CreateDirectory(output);
        var settings = new SettingsWindow(new Settings { BubbleSmartColors = false }, _ => { }, false);
        var point = Native.Cursor(); var anchor = new Native.Rect { Left = point.X, Right = point.X, Top = point.Y, Bottom = point.Y };
        var bubble = new BubbleWindow(anchor, Native.GetForegroundWindow(), .92);
        try
        {
            settings.Show(); Native.SetForegroundWindow(new WindowInteropHelper(settings).Handle); await Task.Delay(220); Render(settings, Path.Combine(output, "settings-preview.png"));
            settings.ShowPage(1); await Task.Delay(50);
            var transparency = Find<System.Windows.Controls.Slider>(settings, "BubbleTransparency")!;
            var textColor = Find<System.Windows.Controls.TextBox>(settings, "BubbleColorHex")!;
            transparency.Value = 65; textColor.Text = "#91BDF4";
            var appearance = Find<BubbleView>(settings, "BubblePreview")!;
            bool appearancePreviewUpdates = appearance.BackgroundAlpha == 89 && appearance.TextColor == Ui.Color("#91BDF4"); Check(appearancePreviewUpdates, "live appearance preview");
            Render(settings, Path.Combine(output, "appearance-preview.png")); settings.ShowPage(0);
            var recorder = Find<System.Windows.Controls.TextBox>(settings, "SelectionShortcut");
            Check(recorder != null, "shortcut recorder exists"); recorder!.Focus(); await Task.Delay(40);
            Native.TestChord(Hotkey.Parse("Ctrl+Alt+9")); await Task.Delay(100);
            bool hotkeyRecorded = recorder.Text == "Ctrl+Alt+9"; Check(hotkeyRecorded, "shortcut recording: " + recorder.Text);
            recorder.Focus(); await Task.Delay(80); Native.TapKey(0x1B); await Task.Delay(120);
            bool recorderEscRestores = recorder.Text == "Ctrl+Alt+9"; Check(recorderEscRestores, "recorder Esc restore");
            bool occupiedShortcutCaptured;
            using (var other = new Hotkeys())
            {
                other.Apply("Ctrl+Alt+8", "Ctrl+Alt+0"); bool triggeredOther = false; other.Pressed += _ => triggeredOther = true;
                Native.SetForegroundWindow(new WindowInteropHelper(settings).Handle); System.Windows.Input.Keyboard.Focus(recorder); await Task.Delay(100); Check(recorder.IsKeyboardFocused, "recorder focused before occupied shortcut test"); Native.TestChord(Hotkey.Parse("Ctrl+Alt+8"));
                for (int retry = 0; retry < 30 && (recorder.Text != "Ctrl+Alt+8" || !Find<System.Windows.Controls.TextBlock>(settings, "ShortcutNotice")!.Text.Contains("占用")); retry++) await Task.Delay(30);
                occupiedShortcutCaptured = recorder.Text == "Ctrl+Alt+8" && !triggeredOther && Find<System.Windows.Controls.TextBlock>(settings, "ShortcutNotice")!.Text.Contains("占用");
                Check(occupiedShortcutCaptured, "occupied shortcut must be captured, reported, and not trigger its owner: " + recorder.Text + ", " + Find<System.Windows.Controls.TextBlock>(settings, "ShortcutNotice")!.Text + ", ownerTriggered=" + triggeredOther);
            }
            var source = Native.GetForegroundWindow(); bubble.Close();
            bubble = new BubbleWindow(anchor, source, .92, dark: false); bubble.SetText("今晚八点见！我已经把资料发到频道里了。\n如果你需要帮助，随时告诉我。", "自动识别 → 简体中文 · 演示文字");
            bubble.Show(); await Task.Delay(180); Render(bubble, Path.Combine(output, "bubble-preview.png"));
            Native.GetWindowRect(new WindowInteropHelper(bubble).Handle, out var rect);
            bool preserved = Native.GetForegroundWindow() == source;
            Native.TapKey(0x1B); await Task.Delay(160); bool escClosed = !bubble.IsVisible;
            bool dismissedTextCleared = bubble.Translation.Length == 0; bubble.SetText("late completion", "late");
            Check(dismissedTextCleared && bubble.Translation.Length == 0, "closed bubble must drop text and ignore late updates");
            bubble.Close();
            bubble = new BubbleWindow(anchor, Native.GetForegroundWindow(), .92); bubble.SetText("点击外部关闭测试", "测试"); bubble.Show(); await Task.Delay(100);
            Native.GetWindowRect(new WindowInteropHelper(settings).Handle, out var settingsRect);
            var previousCursor = Native.Cursor();
            try
            {
                Native.SetCursorPos(settingsRect.Left + 40, settingsRect.Top + 70);
                Native.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Native.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
                await Task.Delay(150);
            }
            finally { Native.SetCursorPos(previousCursor.X, previousCursor.Y); }
            bool outsideClosed = !bubble.IsVisible;
            bubble.Close();
            bubble = new BubbleWindow(anchor, Native.GetForegroundWindow(), .92); bubble.SetText("切换窗口测试", "测试"); bubble.Show();
            Settings? savedLook = null;
            var darkSettings = new SettingsWindow(new Settings { SelectionHotkey = "Ctrl+Alt+Shift+F9", ScreenshotHotkey = "Ctrl+Alt+Shift+F10" }, value => savedLook = value, true);
            bool focusChangeDismisses = false;
            try
            {
                darkSettings.Show(); Native.SetForegroundWindow(new WindowInteropHelper(darkSettings).Handle); await Task.Delay(240); Check(Native.GetForegroundWindow() == new WindowInteropHelper(darkSettings).Handle, "Synthetic focus target activated"); focusChangeDismisses = !bubble.IsVisible; Check(focusChangeDismisses, "Foreground change must dismiss bubble"); Render(darkSettings, Path.Combine(output, "settings-dark-preview.png"));
                darkSettings.ShowPage(1); Find<System.Windows.Controls.Slider>(darkSettings, "BubbleTransparency")!.Value = 35; Find<System.Windows.Controls.TextBox>(darkSettings, "BubbleColorHex")!.Text = "#FFD69A"; Find<System.Windows.Controls.Slider>(darkSettings, "BubbleFontSize")!.Value = 18;
                await Task.Delay(50); Render(darkSettings, Path.Combine(output, "appearance-dark-preview.png"));
                bubble = new BubbleWindow(anchor, Native.GetForegroundWindow(), .65, 18, dark: true, textColor: "#FFD69A"); bubble.SetText("今晚八点见！我已经把资料发到频道里了。\n如果你需要帮助，随时告诉我。", "演示文字");
                bubble.Show(); await Task.Delay(100); Render(bubble, Path.Combine(output, "bubble-dark-preview.png")); bubble.Close();
                Find<System.Windows.Controls.Button>(darkSettings, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(savedLook?.BubbleTextColor == "#FFD69A" && Math.Abs(savedLook.BubbleOpacity - .65) < .001 && savedLook.BubbleFontSize == 18, "UI saves appearance preferences");
            }
            finally { darkSettings.Close(); }
            object clipboardAndCapture = await DesktopPathsAsync();
            object selectionButton = await SelectionButtonPathAsync(output);
            await File.WriteAllTextAsync(Path.Combine(output, "ui-smoke.json"), JsonSerializer.Serialize(new { settingsRendered = true, darkThemeRendered = true, appearancePreviewUpdates, appearancePreferencesSaved = true, hotkeyRecorded, occupiedShortcutCaptured, recorderEscRestores, dismissedTextCleared, focusChangeDismisses, bubbleRendered = true, bubblePreservesFocus = preserved, escClosesBubble = escClosed, outsideClickClosesBubble = outsideClosed, selectionButton, desktopPaths = clipboardAndCapture, bubbleBounds = new { rect.Left, rect.Top, rect.Right, rect.Bottom } }, new JsonSerializerOptions { WriteIndented = true }));
            return preserved && escClosed && outsideClosed ? 0 : 1;
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "ui-smoke-error.txt"), ex.ToString()); return 1; }
        finally { bubble.Close(); settings.Close(); }
    }
    private static async Task<object> DesktopPathsAsync()
    {
        if (SelectionReader.OleGetClipboard(out var original) != 0) throw new Exception("Could not snapshot original clipboard; desktop clipboard test not run.");
        var oldForeground = Native.GetForegroundWindow(); var oldCursor = Native.Cursor();
        var text = new System.Windows.Controls.TextBox { Text = "Hello Discord selection", FontSize = 24, Margin = new Thickness(24), AcceptsReturn = true };
        var fixture = new Window { Title = "QuietTranslate synthetic desktop test", Content = text, Width = 640, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        bool restoredOriginal = false;
        try
        {
            fixture.Show(); fixture.Activate(); text.Focus(); text.SelectAll(); await Task.Delay(150);
            var handle = new WindowInteropHelper(fixture).Handle;
            Check(Native.GetForegroundWindow() == handle, "Test fixture did not receive focus");
            System.Windows.Input.Keyboard.Focus(text); Native.TestChord(Hotkey.Parse("Ctrl+A")); await Task.Delay(120);
            Check(text.SelectedText == "Hello Discord selection", "native selection fixture retained selected text");
            var data = new DataObject(); data.SetText("clipboard-sentinel"); data.SetData(DataFormats.Html, "<b>clipboard-sentinel</b>");
            bool set = false;
            for (int i = 0; i < 8; i++)
            {
                try { Clipboard.SetDataObject(data, false); set = true; break; }
                catch (System.Runtime.InteropServices.COMException) { await Task.Delay(30); }
            }
            Check(set, "Clipboard is busy");
            // Warm the provider once, then verify the fast UI Automation path without clipboard mutation.
            await SelectionReader.TryDirectAsync(handle, CancellationToken.None); await Task.Delay(100);
            uint sequence = Native.GetClipboardSequenceNumber();
            var directTime = System.Diagnostics.Stopwatch.StartNew();
            var direct = await SelectionReader.TryDirectAsync(handle, CancellationToken.None);
            for (int attempt = 0; attempt < 3 && direct == null; attempt++) { await Task.Delay(100); direct = await SelectionReader.TryDirectAsync(handle, CancellationToken.None); }
            directTime.Stop();
            Check(direct?.Text == "Hello Discord selection" && !direct.UsedClipboard, "Direct selected text read: " + SelectionReader.ProbeState + ", got " + (direct?.Text ?? "null"));
            Check(Native.GetClipboardSequenceNumber() == sequence, "Direct read must not touch clipboard");
            string selected = await SelectionReader.ReadAsync(handle, 900, CancellationToken.None);
            Check(selected == "Hello Discord selection", "SendInput selection text");
            Check(Clipboard.GetText() == "clipboard-sentinel", "Original clipboard text restored");
            Check((Clipboard.GetData(DataFormats.Html) as string)?.Contains("clipboard-sentinel") == true, "Original clipboard HTML restored");
            text.Select(0, 0); bool emptyRejected = false;
            try { await SelectionReader.ReadAsync(handle, 300, CancellationToken.None); } catch (InvalidOperationException) { emptyRejected = true; }
            Check(emptyRejected && Clipboard.GetText() == "clipboard-sentinel", "No-selection must not translate stale clipboard");
            Native.GetWindowRect(handle, out var r);
            var captureTask = CaptureWindow.SelectAsync(CancellationToken.None);
            await Task.Delay(180);
            Native.SetCursorPos(r.Left + 50, r.Top + 90); Native.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(60); Native.SetCursorPos(r.Left + 350, r.Top + 170); await Task.Delay(80);
            Native.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
            if (await Task.WhenAny(captureTask, Task.Delay(1500)) != captureTask) { Native.TapKey(0x1B); throw new Exception("Screenshot selection timed out"); }
            using var capture = await captureTask;
            Check(capture != null && Math.Abs(capture.Bitmap.Width - 300) <= 3 && Math.Abs(capture.Bitmap.Height - 80) <= 3, "Screen crop pixel dimensions");
            return new { selectedTextRead = true, directReadWithoutClipboard = true, directReadMilliseconds = directTime.ElapsedMilliseconds, clipboardFallbackRestored = true, clipboardTextRestored = true, clipboardHtmlRestored = true, emptySelectionRejected = true, screenshotDragAndCrop = true };
        }
        finally
        {
            Native.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
            for (int i = 0; i < 8; i++)
            {
                if (SelectionReader.OleSetClipboard(original) == 0) { restoredOriginal = original == null || SelectionReader.OleFlushClipboard() == 0; break; }
                await Task.Delay(20);
            }
            if (original != null && System.Runtime.InteropServices.Marshal.IsComObject(original)) System.Runtime.InteropServices.Marshal.ReleaseComObject(original);
            fixture.Close(); Native.SetCursorPos(oldCursor.X, oldCursor.Y); Native.SetForegroundWindow(oldForeground);
            if (!restoredOriginal) throw new Exception("Desktop test could not restore original clipboard.");
        }
    }
    internal static void OpenSelectionFixture(App app)
    {
        var text = new System.Windows.Controls.TextBox { Text = "Hello, select this sentence to translate.", FontSize = 24, Margin = new Thickness(24), AcceptsReturn = true };
        var window = new Window { Title = "QuietTranslate synthetic selection fixture", Width = 680, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = text };
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(18) };
        timeout.Tick += (_, _) => window.Close();
        window.Closed += (_, _) => { timeout.Stop(); app.Shutdown(); };
        window.Show(); window.Activate(); text.Focus(); timeout.Start();
    }
    private static async Task<object> SelectionButtonPathAsync(string output)
    {
        var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow(); BubbleWindow? resultBubble = null;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--selection-fixture") { UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MockHandler { Respond = async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Length > 0, "selected text reaches translator");
            return Response("{\"choices\":[{\"message\":{\"content\":\"这是划词翻译的演示结果。\"},\"finish_reason\":\"stop\"}]}");
        } };
        using var service = new TranslationService(handler);
        bool focusPreserved = false;
        async Task Translate(IntPtr source, Native.Rect anchor, string? snapshot)
        {
            try
            {
                focusPreserved = Native.GetForegroundWindow() == source;
                Check(snapshot != null, "confirmed selection must be passed with the button session");
                var selection = new SelectionReader.Selection(snapshot!, anchor, false);
                resultBubble = new BubbleWindow(selection.Bounds ?? anchor, source, .6, textColor: "#87D8D8", dark: true);
                resultBubble.Show();
                using var updates = new StreamedText(resultBubble.Dispatcher, value => resultBubble.SetText(value, "合成接口测试"));
                var translated = await service.TranslateAsync(Provider.OpenAI, new(), "dummy", selection.Text, updates.Report, CancellationToken.None);
                updates.Dispose(); resultBubble.SetText(translated, "合成接口测试", completed: true);
                completed.TrySetResult(translated);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        }
        using var trigger = new SelectionTrigger((source, anchor, text) => _ = Translate(source, anchor, text), () => new Settings(), () => false);
        try
        {
            IntPtr source = IntPtr.Zero;
            for (int i = 0; i < 40; i++) { process.Refresh(); source = process.MainWindowHandle; if (source != IntPtr.Zero) break; await Task.Delay(50); }
            Check(source != IntPtr.Zero, "external synthetic window launched"); Native.SetForegroundWindow(source); await Task.Delay(100);
            Check(Native.GetForegroundWindow() == source, "external synthetic window focused");
            trigger.Enabled = true; uint clipboard = Native.GetClipboardSequenceNumber(); Native.GetWindowRect(source, out var rect);
            // Cancel/coalesce rapid gestures before the UIA provider has completed earlier reads.
            for (int gesture = 0; gesture < 12; gesture++)
            {
                Native.SetCursorPos(rect.Left + 35, rect.Top + 74); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(8);
                Native.SetCursorPos(rect.Left + 130 + gesture * 17, rect.Top + 74); await Task.Delay(8); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(18);
            }
            Native.SetCursorPos(rect.Left + 35, rect.Top + 74); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(50);
            Native.SetCursorPos(rect.Left + 370, rect.Top + 74); await Task.Delay(70); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
            for (int i = 0; i < 65 && trigger.VisibleButton == null; i++) await Task.Delay(40);
            Check(trigger.VisibleButton is { IsVisible: true }, "selection button appears after external drag: " + trigger.State + ", UIA=" + SelectionReader.ProbeState + ", foreground=" + (Native.GetForegroundWindow() == source));
            Check(handler.Calls == 0 && Native.GetClipboardSequenceNumber() == clipboard, "selection alone must not translate or copy");
            var initialButton = trigger.VisibleButton!; await Task.Delay(8300); Check(trigger.VisibleButton is { IsVisible: true }, "valid selection must survive old eight-second timeout: " + initialButton.DismissReason);
            Render(trigger.VisibleButton!, Path.Combine(output, "selection-button-preview.png"));
            Native.GetWindowRect(new WindowInteropHelper(trigger.VisibleButton).Handle, out var buttonRect);
            Native.SetCursorPos((buttonRect.Left + buttonRect.Right) / 2, (buttonRect.Top + buttonRect.Bottom) / 2); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
            if (await Task.WhenAny(completed.Task, Task.Delay(2500)) != completed.Task) throw new Exception("selection button click did not complete translation");
            Check(await completed.Task == "这是划词翻译的演示结果。" && handler.Calls == 1 && focusPreserved, "click translates once without taking focus");
            await Task.Delay(100); Render(resultBubble!, Path.Combine(output, "selection-result-preview.png"));
            resultBubble!.Close(); Check(resultBubble.Translation.Length == 0, "selected translation released on close");
            Native.SetForegroundWindow(source); Native.SetCursorPos(rect.Left + 100, rect.Top + 74);
            for (int i = 0; i < 2; i++) { Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(80); }
            for (int i = 0; i < 25 && trigger.VisibleButton == null; i++) await Task.Delay(40);
            Check(trigger.VisibleButton is { IsVisible: true } && handler.Calls == 1, "double click selects word and shows button without another request");
            Native.TapKey(0x1B); await Task.Delay(180); Check(trigger.VisibleButton == null, "Esc dismisses selection button");
            Native.SetForegroundWindow(source); Native.SetCursorPos(rect.Left + 35, rect.Top + 150); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); Native.SetCursorPos(rect.Left + 220, rect.Top + 150); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(450); Check(trigger.VisibleButton == null, "blank drag must not create a guessed normal-mode button");
            trigger.Enabled = false; Check(trigger.VisibleButton == null, "disabling selection buttons removes popup");
            return new { rapidGestureCount = 12, latestSelectionRecoversAfterRapidGestures = true, externalProcessDragShowsButton = true, doubleClickShowsButton = true, escDismissesButton = true, validSelectionSurvivesEightSeconds = true, blankDragDoesNotShowButton = true, noNetworkBeforeClick = true, noClipboardBeforeClick = true, clickTranslatesOnceWithMockApi = true, originalFocusPreserved = true, dismissClearsText = true, disablingRemovesPopup = true };
        }
        finally
        {
            trigger.Dispose(); resultBubble?.Close(); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(1000)) process.Kill(); }
            Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground);
        }
    }
    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T item && item.Name == name) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Find<T>(VisualTreeHelper.GetChild(root, i), name); if (found != null) return found; }
        return null;
    }
}

