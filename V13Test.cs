using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class V13Test
{
    private sealed class Mock : HttpMessageHandler
    {
        internal int Calls;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _) => Task.FromResult(Response("{}"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; ct.ThrowIfCancellationRequested(); return Respond(request, ct); }
    }
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> action) { try { await action(); results.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        await Test("Custom templates escape source text and never recursively expand user placeholders", async () =>
        {
            var s = new ServiceSettings { BodyTemplate = "{\"q\":\"{text}\",\"lang\":\"{target}\",\"nested\":[\"{text}\"]}" };
            string source = "\"\\\n{api_key} 日本語 😀";
            using var content = ApiSupport.Template(s, new() { ["{text}"] = source, ["{target}"] = "ja", ["{api_key}"] = "SECRET" });
            using var doc = JsonDocument.Parse(await content.ReadAsStringAsync()); Check(doc.RootElement.GetProperty("q").GetString() == source && doc.RootElement.GetProperty("nested")[0].GetString() == source, "unsafe or recursive expansion");
            s.FormEncoded = true; using var form = ApiSupport.Template(s, new() { ["{text}"] = "a&b=中", ["{target}"] = "ja" }); Check((await form.ReadAsStringAsync()).Contains("a%26b%3D"), "form escaping");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com"); s.KeyHeader = ""; s.BodyTemplate = "{\"key\":\"{api_key}\"}"; ApiSupport.Authenticate(request, s, "test-key"); Check(!request.Headers.Contains("Authorization"), "body-only auth");
        });
        await Test("Response mapping handles indexed and projected arrays", () => { using var doc = JsonDocument.Parse("{\"a\":[{\"text\":\"甲\"},{\"text\":\"乙\"}]}"); Check(ApiSupport.Extract(doc.RootElement, "a[].text") == "甲\n乙" && ApiSupport.Extract(doc.RootElement, "a[1].text") == "乙", "JSON path"); return Task.CompletedTask; });
        await Test("Editable prompt substitutes target and source; single words offer alternatives", () => { var s = new ServiceSettings { Prompt = "译为{target_language}；源语{source_language}" }; string p = TranslationService.BuildPrompt(s, "bank", "ja", "en"); Check(p.Contains("日语") && p.Contains("英语") && p.Contains("三个") && !p.Contains("{target_language}"), "prompt"); Check(!TranslationService.BuildPrompt(s, "hello world", "en", "auto").Contains("三个"), "sentences shouldn't get word mode"); return Task.CompletedTask; });
        foreach (Provider p in Enum.GetValues<Provider>())
        {
            await Test("Translation request/response: " + p, async () =>
            {
                var s = Settings.Defaults()[p]; s.Model = "synthetic-model"; s.AppId = "synthetic-id";
                if (p == Provider.CustomTranslation) { s.Endpoint = "https://custom.example/translate"; s.BodyTemplate = "{\"q\":\"{text}\",\"to\":\"{target}\"}"; s.ResponsePath = "data.text"; }
                var mock = new Mock { Respond = async (r, ct) =>
                {
                    string body = await r.Content!.ReadAsStringAsync(ct); Check(r.Method == HttpMethod.Post, "method");
                    if (Settings.IsAI(p)) { using var doc = JsonDocument.Parse(body); Check(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("日语"), "dynamic target"); if (p != Provider.OpenAI) Check(!doc.RootElement.TryGetProperty("thinking", out _), "unexpected extension"); return Response("{\"choices\":[{\"message\":{\"content\":\"翻译\"},\"finish_reason\":\"stop\"}]}"); }
                    if (p == Provider.Tencent) { Check(r.Headers.Contains("X-TC-Action") && r.Headers.GetValues("Authorization").Single().Contains("TC3-HMAC") && body.Contains("\"Target\":\"ja\""), "Tencent signature and current language code"); return Response("{\"Response\":{\"TargetText\":\"翻译\"}}"); }
                    if (p == Provider.Baidu || p == Provider.Youdao) { Check(body.Contains("sign=") && body.Contains(p == Provider.Baidu ? "to=jp" : "to=ja"), "signed target"); return Response(p == Provider.Baidu ? "{\"trans_result\":[{\"dst\":\"翻译\"}]}" : "{\"errorCode\":\"0\",\"translation\":[\"翻译\"]}"); }
                    return Response(p switch { Provider.Microsoft => "[{\"translations\":[{\"text\":\"翻译\"}]}]", Provider.DeepL => "{\"translations\":[{\"text\":\"翻译\"}]}", Provider.Google => "{\"data\":{\"translations\":[{\"translatedText\":\"翻译\"}]}}", Provider.LibreTranslate => "{\"translatedText\":\"翻译\"}", _ => "{\"data\":{\"text\":\"翻译\"}}" });
                } };
                using var api = new TranslationService(mock); Check(await api.TranslateAsync(p, s, "synthetic-secret", "Hello world", _ => { }, CancellationToken.None, "ja", "en") == "翻译" && mock.Calls == 1, "translation result");
            });
        }
        foreach (OcrProvider p in Enum.GetValues<OcrProvider>().Where(p => p != OcrProvider.Windows))
        {
            await Test("Cloud/custom OCR protocol: " + p, async () =>
            {
                var s = Settings.OcrDefaults()[p]; s.AppId = "synthetic-id"; s.Model = "vision-model";
                var mock = new Mock { Respond = async (r, ct) =>
                {
                    var body = await r.Content!.ReadAsStringAsync(ct);
                    if (r.RequestUri!.AbsolutePath.Contains("oauth")) return Response("{\"access_token\":\"synthetic-token\"}");
                    if (p == OcrProvider.OCRSpace) Check(body.Contains("language=auto") && body.Contains("OCREngine=2"), "OCR auto");
                    if (p == OcrProvider.Tencent) Check(body.Contains("mix") && r.Headers.Contains("Authorization"), "Tencent OCR auto");
                    if (p == OcrProvider.Baidu) Check(body.Contains("auto_detect") && r.RequestUri.Query.Contains("synthetic-token"), "Baidu auto/auth");
                    if (p == OcrProvider.VisionAI) Check(body.Contains("image_url"), "vision payload");
                    if (p == OcrProvider.Local) { using var local = JsonDocument.Parse(body); Check(r.RequestUri.IsLoopback && local.RootElement.GetProperty("image_base64").GetString()!.Length > 0 && local.RootElement.GetProperty("language").GetString() == "auto", "local OCR fixed protocol"); }
                    return Response(p switch { OcrProvider.OCRSpace => "{\"IsErroredOnProcessing\":false,\"ParsedResults\":[{\"ParsedText\":\"こんにちは Hello\"}]}", OcrProvider.Tencent => "{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"こんにちは Hello\"}]}}", OcrProvider.Baidu => "{\"words_result\":[{\"words\":\"こんにちは Hello\"}]}", OcrProvider.GoogleVision => "{\"responses\":[{\"fullTextAnnotation\":{\"text\":\"こんにちは Hello\"}}]}", OcrProvider.VisionAI => "{\"choices\":[{\"message\":{\"content\":\"こんにちは Hello\"}}]}", _ => "{\"text\":\"こんにちは Hello\"}" });
                } };
                using var image = new System.Drawing.Bitmap(120, 40); using var api = new OcrService(mock); Check(await api.RecognizeAsync(p, s, "synthetic-secret", image, CancellationToken.None) == "こんにちは Hello", "OCR result");
            });
        }
        await Test("Provider business errors are redacted and never retried", async () => { var mock = new Mock { Respond = (_, _) => Task.FromResult(Response("{\"error_code\":\"54003\",\"error_msg\":\"PRIVATE_SOURCE_SECRET\"}")) }; using var api = new TranslationService(mock); bool rejected = false; try { await api.TranslateAsync(Provider.Baidu, new() { AppId = "id", Endpoint = "https://example.com" }, "key", "source", _ => { }, CancellationToken.None); } catch (InvalidOperationException ex) { rejected = !ex.Message.Contains("PRIVATE"); } Check(rejected && mock.Calls == 1, "business error privacy"); });
        await Test("Bubble background and border opacity; no outer line when disabled", () => { var view = new BubbleView(.5, 16, "#FFFFFF", false, () => { }, () => { }); view.ApplyAppearance(new Settings { BubbleOpacity = 0, BubbleBackgroundColor = "#123456", BubbleBorderOpacity = -1 }); Check(view.BackgroundAlpha == 0 && ((SolidColorBrush)view.BorderBrush).Color.A == 0, "outer border remains opaque"); view.ApplyAppearance(new Settings { BubbleOpacity = .4, BubbleBorderOpacity = .2, BubbleBorderColor = "#ABCDEF", BubbleBorderVisible = false }); Check(((SolidColorBrush)view.BorderBrush).Color.A == 51 && view.BorderThickness.Left == 0, "independent border"); return Task.CompletedTask; });
        await Test("Named configurations keep independent credential scopes; JSON has no keys", () => { var a = new ServiceSettings { Endpoint = "https://same.example/v1", CredentialId = "a", AppId = "PRIVATE_IDENTIFIER" }; var b = new ServiceSettings { Endpoint = a.Endpoint, CredentialId = "b" }; Check(Credentials.Target(Provider.CustomAI, a) != Credentials.Target(Provider.CustomAI, b), "profile keys shared"); var s = new Settings(); s.SavedServices["test"] = new() { Provider = Provider.CustomAI, Settings = a }; string json = JsonSerializer.Serialize(s); Check(!json.Contains("PRIVATE_IDENTIFIER"), "identifier persisted as plaintext"); var restored = JsonSerializer.Deserialize<Settings>(json)!; Check(restored.SavedServices["test"].Settings.CredentialId == "a", "profile persistence"); return Task.CompletedTask; });
        await Test("Application rules match process names; Pause works without modifiers", () => { var s = new Settings(); s.AppRules.Add(new() { ProcessName = "game", Mode = AppMode.Paused, SourceLanguage = "ja" }); Check(App.FindRule(s, "GAME.exe")?.Mode == AppMode.Paused && App.FindRule(s, "discord") == null, "rule matching"); Check(Hotkey.Parse("Pause") == new Hotkey(0, 0x13) && Hotkey.Format(0, 0x13) == "Pause", "Pause"); return Task.CompletedTask; });
        await Test("Native total pause releases translation keys and keeps resume/manual", () => { using var keys = new Hotkeys(); var s = new Settings { SelectionHotkey = "Ctrl+Alt+Shift+7", ScreenshotHotkey = "Ctrl+Alt+Shift+8", PauseHotkey = "Ctrl+Alt+Shift+9", ManualHotkey = "Ctrl+Alt+Shift+0" }; keys.Apply(s, false); Check(!Hotkeys.Available(Hotkey.Parse(s.SelectionHotkey)), "registration"); keys.Apply(s, true); Check(Hotkeys.Available(Hotkey.Parse(s.SelectionHotkey)) && !Hotkeys.Available(Hotkey.Parse(s.PauseHotkey)) && !Hotkeys.Available(Hotkey.Parse(s.ManualHotkey)), "pause policy"); keys.Apply(s, true, true); Check(Hotkeys.Available(Hotkey.Parse(s.ManualHotkey)), "full pause"); return Task.CompletedTask; });
        await Test("Settings save applies without closing; all six pages render", async () =>
        {
            Settings? saved = null; var w = new SettingsWindow(new(), s => saved = s, false);
            try { w.Show(); await Task.Delay(150); for (int i = 0; i < 6; i++) { w.ShowPage(i); await Task.Delay(30); Render(w, Path.Combine(output, "settings-" + i + ".png")); } w.ShowPage(1); Find<Slider>(w, "BubbleTransparency")!.Value = 60; Find<TextBox>(w, "BubbleBackgroundHex")!.Text = "#18323B"; Find<Button>(w, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(saved?.BubbleBackgroundColor == "#18323B" && Math.Abs(saved.BubbleOpacity - .4) < .001 && w.IsVisible, "save/apply"); }
            finally { w.Close(); }
        });
        await Test("Manual translation debounces typing, invalidates stale text, survives focus loss and clears on close", async () =>
        {
            var s = new Settings { AutoTranslateDelay = 300 }; var mock = new Mock { Respond = async (r, ct) => { using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)); string source = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!; await Task.Delay(source == "old" ? 400 : 10, ct); return Response(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = source + "-translated" }, finish_reason = "stop" } } })); } };
            using var api = new TranslationService(mock); var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic-secret");
            try
            {
                w.Show(); w.Input.Text = "a"; await Task.Delay(100); w.Input.Text = "ab"; await Task.Delay(100); w.Input.Text = "final"; await Task.Delay(450); Check(mock.Calls == 1 && w.Output.Text == "final-translated", "typing debounce");
                w.Input.Text = "old"; var old = w.TranslateNowAsync(); await Task.Delay(40); w.Input.Text = "new"; await Task.Delay(550); await old; Check(w.Output.Text == "new-translated", "stale response overwrote current");
                var other = new Window { Width = 200, Height = 120, Content = "Synthetic focus target" }; other.Show(); await Task.Delay(100); Check(w.IsVisible, "outside focus closes manual bar"); other.Close(); Render(w, Path.Combine(output, "manual.png"));
                int before = mock.Calls; await w.TranslateNowAsync(); Check(mock.Calls == before, "unchanged request repeated"); w.Close(); Check(w.Input.Text.Length == 0 && w.Output.Text.Length == 0, "closed contents retained");
            }
            finally { w.Close(); }
        });
        await File.WriteAllTextAsync(Path.Combine(output, "v13-tests.json"), JsonSerializer.Serialize(new { os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), passed = results.Count - failed, failed, tests = results, realProviderCalls = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
    private static void Render(Window w, string file) { w.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(w); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(file); encoder.Save(stream); }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement { if (root is T element && element.Name == name) return element; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Find<T>(VisualTreeHelper.GetChild(root, i), name); if (found != null) return found; } return null; }
}
