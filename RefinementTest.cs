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

internal static class RefinementTest
{
    private sealed class Mock : HttpMessageHandler
    {
        internal int Calls;
        internal bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string text = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            if (text == "blocking") await Task.Delay(350, ct);
            if (Fail) throw new HttpRequestException("synthetic connection failure");
            string result = text.Contains("meeting") ? "今晚八点见，资料已经发到频道里。\n需要帮助的话，随时告诉我。" : "译文：" + text;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = result }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json") };
        }
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> run) { try { await run(); results.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        await Test("Semantic palettes retain readable labels, body text and accent controls", () =>
        {
            foreach (bool dark in new[] { false, true })
            {
                var target = new Border(); Ui.Apply(target, dark); Color C(string key) => ((SolidColorBrush)target.Resources["Ui" + key]).Color;
                foreach (string surface in new[] { "Surface", "Input", "AccentSoft" }) foreach (string ink in new[] { "Text", "Muted", "Accent", "Warning" }) Check(Ui.Contrast(C(ink), C(surface)) >= 4.49, dark + ": " + ink + "/" + surface + "=" + Ui.Contrast(C(ink), C(surface)));
                Check(Ui.Contrast(C("Accent"), C("OnAccent")) >= 4.49, "primary control text");
            }
            return Task.CompletedTask;
        });
        foreach (bool dark in new[] { false, true })
        await Test("Manual UI, state and compact bounds: " + (dark ? "dark" : "light"), async () =>
        {
            var s = new Settings { AutoTranslateDelay = 2000 }; var mock = new Mock(); using var api = new TranslationService(mock);
            var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic", dark);
            try
            {
                w.Show(); await Task.Delay(100); Render(w, Path.Combine(output, dark ? "manual-dark-empty.png" : "manual-light-empty.png"));
                w.Input.Text = "The meeting starts at eight. I've sent the notes to the channel. Let me know if you need help."; await w.TranslateNowAsync(); await Task.Delay(120);
                Check(w.ActualHeight <= 360 && w.ActualWidth <= 440 && w.Output.Text.StartsWith("今晚"), "compact completed layout");
                Check(Find<Button>(w, "CopyOutput")!.IsEnabled && Find<TextBlock>(w, "ManualStatus")!.Text.Contains("已完成"), "visible completion state");
                Render(w, Path.Combine(output, dark ? "manual-dark.png" : "manual-light.png"));
                w.Input.Text = string.Concat(Enumerable.Repeat("long paragraph with English and 日本語。", 160)); await w.TranslateNowAsync(); await Task.Delay(120);
                Check(w.Input.ActualHeight <= 191 && w.Output.ActualHeight <= 246 && w.ActualHeight <= w.MaxHeight, "long text bounds");
                Render(w, Path.Combine(output, dark ? "manual-dark-long.png" : "manual-light-long.png"));
            }
            finally { w.Close(); }
        });
        await Test("Editing away and back, cancellation and identical input remain retryable", async () =>
        {
            var s = new Settings { AutoTranslateDelay = 2000 }; var mock = new Mock(); using var api = new TranslationService(mock); var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic");
            try
            {
                w.Show(); w.Input.Text = "A"; await w.TranslateNowAsync(); int first = mock.Calls;
                w.Input.Text = "B"; w.Input.Text = "A"; await w.TranslateNowAsync(); Check(mock.Calls == first + 1 && w.Output.Text == "译文：A" && Find<Button>(w, "CopyOutput")!.IsEnabled, "return-to-old-input was blocked");
                await w.TranslateNowAsync(); Check(mock.Calls == first + 1, "completed identical input repeated");
                w.Input.Text = "blocking"; var request = w.TranslateNowAsync(); await Task.Delay(40); Check((string)Find<Button>(w, "TranslateNow")!.Content == "取消", "working action");
                w.CancelTranslation(); await request; int before = mock.Calls; await w.TranslateNowAsync(); Check(mock.Calls == before + 1 && w.Output.Text == "译文：blocking", "cancel retry");
                w.CancelTranslation(); await w.TranslateNowAsync(); Check(mock.Calls == before + 2, "retry after completed cancellation");
            }
            finally { w.Close(); }
        });
        await Test("Language swap is explicit; identical languages avoid a network call", async () =>
        {
            var s = new Settings { AutoTranslateDelay = 2000 }; var mock = new Mock(); using var api = new TranslationService(mock); var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic");
            try
            {
                w.Show(); Check(!Find<Button>(w, "SwapLanguages")!.IsEnabled, "auto source guessed during swap");
                var source = Find<Button>(w, "SourceLanguage")!; ((MenuItem)source.ContextMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(Find<Button>(w, "SwapLanguages")!.IsEnabled, "explicit source cannot swap"); Find<Button>(w, "SwapLanguages")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((string)source.Content == ApiSupport.LanguageName("zh-Hans") && (string)Find<Button>(w, "TargetLanguage")!.Content == ApiSupport.LanguageName("en"), "language swap");
                ((MenuItem)Find<Button>(w, "TargetLanguage")!.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                w.Input.Text = "保留原文"; await w.TranslateNowAsync(); Check(mock.Calls == 0 && w.Output.Text == "保留原文", "same-language request billed");
            }
            finally { w.Close(); }
        });
        await Test("Errors are visible separately from copyable translations; settings update live", async () =>
        {
            var s = new Settings { AutoTranslateDelay = 2000 }; var mock = new Mock { Fail = true }; using var api = new TranslationService(mock); var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic");
            try
            {
                w.Show(); w.Input.Text = "test failure"; await w.TranslateNowAsync(); await Task.Delay(80);
                Check(w.Output.Text.Length == 0 && !Find<Button>(w, "CopyOutput")!.IsEnabled && Find<TextBlock>(w, "ManualStatus")!.Text.Contains("未完成"), "error became a translation"); Render(w, Path.Combine(output, "manual-error.png"));
                s.ManualProvider = Provider.CustomAI; s.ManualSource = "ja"; s.ManualTarget = "en"; w.RefreshOptions();
                Check(Find<Button>(w, "ManualService")!.ToolTip.ToString()!.Contains("自定义") && (string)Find<Button>(w, "SourceLanguage")!.Content == ApiSupport.LanguageName("ja") && (string)Find<Button>(w, "TargetLanguage")!.Content == ApiSupport.LanguageName("en"), "options did not update");
            }
            finally { w.Close(); }
        });
        await Test("Paused manual panel never starts a new request until resumed", async () =>
        {
            var s = new Settings { AutoTranslateDelay = 300 }; var mock = new Mock(); using var api = new TranslationService(mock); var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic");
            try
            {
                w.Show(); w.SetPaused(true); w.Input.Text = "paused text"; await Task.Delay(450); await w.TranslateNowAsync();
                Check(mock.Calls == 0 && !Find<Button>(w, "TranslateNow")!.IsEnabled && Find<TextBlock>(w, "ManualStatus")!.Text.Contains("暂停"), "paused request escaped");
                w.SetPaused(false); await w.TranslateNowAsync(); Check(mock.Calls == 1 && w.Output.Text.Contains("paused text"), "resume did not recover");
            }
            finally { w.Close(); }
        });
        await Test("Normal and pause icons preserve the same identity at tray sizes", () =>
        {
            var grid = new Grid { Background = Brushes.White }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
            for (int col = 0; col < 2; col++)
            {
                var stack = new StackPanel { Margin = new Thickness(24) }; stack.Children.Add(new TextBlock { Text = col == 0 ? "运行中" : "已暂停 · 同一图标的变化形态", FontSize = 14, Margin = new Thickness(0, 0, 0, 16), Foreground = Brushes.Black });
                foreach (int size in new[] { 16, 24, 32, 64, 128 }) { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) }; row.Children.Add(new Image { Source = col == 0 ? AppIcons.Image : AppIcons.PausedImage, Width = size, Height = size }); row.Children.Add(new TextBlock { Text = size + " px", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black }); stack.Children.Add(row); }
                Grid.SetColumn(stack, col); grid.Children.Add(stack);
            }
            grid.Measure(new Size(650, 500)); grid.Arrange(new Rect(0, 0, 650, 500)); RenderElement(grid, 650, 500, Path.Combine(output, "icons.png")); return Task.CompletedTask;
        });
        await File.WriteAllTextAsync(Path.Combine(output, "refinement-tests.json"), JsonSerializer.Serialize(new { passed = results.Count - failed, failed, tests = results, realCloudCalls = false }, new JsonSerializerOptions { WriteIndented = true })); return failed == 0 ? 0 : 1;
    }
    private static void Render(Window w, string path) { w.UpdateLayout(); RenderElement(w, (int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), path); }
    private static void RenderElement(Visual element, int width, int height, string path) { var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file); }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement { if (root is T element && element.Name == name) return element; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Find<T>(VisualTreeHelper.GetChild(root, i), name); if (found != null) return found; } return null; }
}
