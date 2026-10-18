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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class OcrSettingsTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T found && found.Name == name) return found;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) { try { return Find<T>(node, name); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(name);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal string ExpectedPrompt = "";
        internal VisionThinkingMode ExpectedThinking;
        internal bool Reject;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = body.RootElement;
            Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "Vision endpoint changed");
            Check(request.Headers.TryGetValues("X-Synthetic-Key", out var values) && string.Join("", values) == "Test synthetic-key", "Custom authentication ignored");
            Check(root.GetProperty("messages")[0].GetProperty("content").GetString() == ExpectedPrompt, "Edited OCR prompt not sent");
            Check(root.GetProperty("model").GetString() == "synthetic-vision" && !root.GetProperty("stream").GetBoolean(), "Vision parameters changed");
            Check(root.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"), "Image missing");
            bool thinking = root.TryGetProperty("thinking", out var type), enable = root.TryGetProperty("enable_thinking", out var flag), template = root.TryGetProperty("chat_template_kwargs", out var kwargs);
            Check(thinking == (ExpectedThinking == VisionThinkingMode.ThinkingTypeDisabled) && enable == (ExpectedThinking == VisionThinkingMode.EnableThinkingFalse) && template == (ExpectedThinking == VisionThinkingMode.ChatTemplateDisabled), "Thinking format leaks into another mode");
            if (thinking) Check(type.GetProperty("type").GetString() == "disabled", "Thinking was enabled");
            if (enable) Check(flag.ValueKind == JsonValueKind.False, "Wrong enable_thinking type");
            if (template) Check(kwargs.GetProperty("enable_thinking").ValueKind == JsonValueKind.False, "Wrong chat template flag");
            Calls++;
            if (Reject) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"synthetic unsupported parameter\"}}", Encoding.UTF8, "application/json") };
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Synthetic OCR 日本語\"}}]}", Encoding.UTF8, "application/json") };
        }
    }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> run)
        {
            try { await run(); results.Add(new { name, status = "PASS" }); }
            catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); }
        }
        foreach (bool dark in new[] { false, true })
        await Test("OCR controls, switching and persistence: " + dark, async () =>
        {
            var settings = new Settings { OcrProvider = OcrProvider.VisionAI };
            settings.OcrServices[OcrProvider.VisionAI].Endpoint = "https://ocr.example.invalid/v1";
            settings.OcrServices[OcrProvider.VisionAI].Prompt = ServiceSettings.DefaultPrompt; // Existing releases saved the translation default here.
            var editor = new ServiceEditor(settings, true, new());
            var window = new Window { Content = new ScrollViewer { Content = editor }, Width = 730, Height = 850, ShowActivated = false };
            Ui.Apply(window, dark); Ui.Bind(window, Control.BackgroundProperty, "Bg");
            try
            {
                window.Show(); var prompts = Find<Expander>(editor, "OcrPromptOptions"); var custom = Find<Expander>(editor, "OcrCustomOptions");
                prompts.IsExpanded = custom.IsExpanded = true; await Task.Delay(60); window.UpdateLayout();
                var prompt = Find<TextBox>(editor, "OcrPrompt"); var header = Find<TextBox>(editor, "OcrKeyHeader"); var prefix = Find<TextBox>(editor, "OcrKeyPrefix");
                Check(prompts.IsVisible && prompt.IsVisible && prompt.ActualHeight >= 80 && prompt.Text == ServiceSettings.DefaultOcrPrompt, "AI OCR prompt is empty or hidden");
                Check(custom.IsVisible && header.IsVisible && prefix.IsVisible && header.Text == "Authorization" && prefix.Text == "Bearer ", "AI OCR authentication is empty");
                Check(!Find<TextBox>(editor, "OcrBodyTemplate").IsVisible, "Unused AI JSON template shown");
                var mode = Find<ComboBox>(editor, "OcrThinking");
                Check(mode.IsVisible && mode.SelectedIndex == 0 && mode.Items.Count == 4, "Thinking defaults changed or control hidden");
                mode.SelectedIndex = (int)VisionThinkingMode.EnableThinkingFalse;
                prompt.Text = "合成自定义识别指令，保留换行，只输出原文。";
                var choice = Find<ComboBox>(editor, "OcrChoice"); choice.SelectedIndex = (int)OcrProvider.Windows;
                Check(prompts.Visibility == Visibility.Collapsed && custom.Visibility == Visibility.Collapsed, "Empty expanders remain for local OCR");
                choice.SelectedIndex = (int)OcrProvider.Custom; window.UpdateLayout();
                Check(custom.IsVisible && Find<TextBox>(editor, "OcrBodyTemplate").IsVisible && prompts.Visibility == Visibility.Collapsed, "Custom OCR fields hidden");
                choice.SelectedIndex = (int)OcrProvider.OCRSpace;
                Check(custom.Visibility == Visibility.Collapsed && prompts.Visibility == Visibility.Collapsed, "Built-in OCR shows empty options");
                choice.SelectedIndex = (int)OcrProvider.VisionAI; editor.Commit(); window.UpdateLayout();
                var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
                Check(prompt.Text == restored.OcrServices[OcrProvider.VisionAI].Prompt && prompt.Text.StartsWith("合成"), "Prompt lost on switch or save/reload");
                Check(mode.SelectedIndex == (int)VisionThinkingMode.EnableThinkingFalse && restored.OcrServices[OcrProvider.VisionAI].VisionThinking == VisionThinkingMode.EnableThinkingFalse, "Thinking selection lost on switch or save/reload");
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, dark ? "ocr-dark.png" : "ocr-light.png")); png.Save(file);
            }
            finally { window.Close(); }
        });
        await Test("Translation editor retains provider-specific controls", () =>
        {
            var editor = new ServiceEditor(new Settings(), false, new()); var choice = Find<ComboBox>(editor, "ServiceChoice");
            var prompts = Find<Expander>(editor, "ServicePromptOptions"); var custom = Find<Expander>(editor, "ServiceCustomOptions");
            Check(prompts.Visibility == Visibility.Visible && custom.Visibility == Visibility.Visible, "Translation AI options hidden");
            choice.SelectedIndex = (int)Provider.Microsoft; Check(prompts.Visibility == Visibility.Collapsed && custom.Visibility == Visibility.Collapsed, "Built-in translator has empty options");
            choice.SelectedIndex = (int)Provider.CustomTranslation; Check(custom.Visibility == Visibility.Visible, "Custom translation options hidden");
            return Task.CompletedTask;
        });
        await Test("Vision request uses edited prompt, authentication and legacy fallback", async () =>
        {
            var handler = new Handler(); using var service = new OcrService(handler); using var image = new System.Drawing.Bitmap(32, 32);
            var config = new ServiceSettings { Endpoint = "https://ocr.example.invalid/v1", Model = "synthetic-vision", KeyHeader = "X-Synthetic-Key", KeyPrefix = "Test ", Prompt = "Synthetic OCR instruction" };
            foreach (string prompt in new[] { "Synthetic OCR instruction", "", ServiceSettings.DefaultPrompt })
            {
                config.Prompt = prompt; handler.ExpectedPrompt = prompt == "Synthetic OCR instruction" ? prompt : ServiceSettings.DefaultOcrPrompt;
                Check(await service.RecognizeAsync(OcrProvider.VisionAI, config, "synthetic-key", image, CancellationToken.None) == "Synthetic OCR 日本語", "Recognition text changed");
            }
            Check(handler.Calls == 3, "Unexpected request count");
        });
        await Test("Thinking modes send one explicit format and preserve old defaults", async () =>
        {
            Check(JsonSerializer.Deserialize<ServiceSettings>("{}")!.VisionThinking == VisionThinkingMode.Default, "Old configs enable thinking parameters");
            var handler = new Handler { ExpectedPrompt = ServiceSettings.DefaultOcrPrompt }; using var service = new OcrService(handler); using var image = new System.Drawing.Bitmap(32, 32);
            var config = new ServiceSettings { Endpoint = "https://ocr.example.invalid/v1", Model = "synthetic-vision", KeyHeader = "X-Synthetic-Key", KeyPrefix = "Test " };
            foreach (var mode in new[] { VisionThinkingMode.Default, VisionThinkingMode.ThinkingTypeDisabled, VisionThinkingMode.EnableThinkingFalse, VisionThinkingMode.ChatTemplateDisabled, (VisionThinkingMode)999 })
            {
                config.VisionThinking = mode; handler.ExpectedThinking = Enum.IsDefined(mode) ? mode : VisionThinkingMode.Default;
                Check(await service.RecognizeAsync(OcrProvider.VisionAI, config, "synthetic-key", image, CancellationToken.None) == "Synthetic OCR 日本語", "Thinking option changed OCR output");
            }
            handler.Reject = true; handler.ExpectedThinking = config.VisionThinking = VisionThinkingMode.ThinkingTypeDisabled;
            int before = handler.Calls; bool rejected = false;
            try { await service.RecognizeAsync(OcrProvider.VisionAI, config, "synthetic-key", image, CancellationToken.None); }
            catch (ServiceException) { rejected = true; }
            Check(rejected && handler.Calls == before + 1 && config.VisionThinking == VisionThinkingMode.ThinkingTypeDisabled, "Rejected parameters were silently retried or configuration changed");
        });
        await File.WriteAllTextAsync(Path.Combine(output, "ocr-settings.json"), JsonSerializer.Serialize(new { passed = results.Count - failed, failed, tests = results, realCloudCalls = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
