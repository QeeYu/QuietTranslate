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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

// Documentation uses real controls and a local mock response, never stored credentials.
internal static class DocsCapture
{
    private sealed class DemoHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; await Task.Delay(150, ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "今晚八点见，资料已经发到频道里。\n需要帮助的话，随时告诉我。" }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json") };
        }
    }
    internal static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Capturing client content alone omits the Window's themed background.
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Window.GetWindow(element)?.Background ?? Brushes.Transparent, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        image.Render(background); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    internal static async Task<int> RunAsync(App app, string output)
    {
        Directory.CreateDirectory(output); var frames = new List<object>(); var errors = new List<string>(); var oldTheme = Ui.Theme;
        app.TestException = e => errors.Add(e.ToString());
        try
        {
            var settings = new Settings { Theme = ThemeMode.Light, AutoTranslateDelay = 800 };
            var w = new SettingsWindow(settings, _ => { });
            try
            {
                w.Show(); await Task.Delay(120); Render((FrameworkElement)w.Content, Path.Combine(output, "settings-shortcuts.png"));
                w.ShowPage(2); await Task.Delay(60); Render((FrameworkElement)w.Content, Path.Combine(output, "settings-service.png"));
                w.ShowPage(1); await Task.Delay(60); Render((FrameworkElement)w.Content, Path.Combine(output, "appearance-light.png"));
                HoverTest.Find<Button>(w, "ThemeDark")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60); Render((FrameworkElement)w.Content, Path.Combine(output, "appearance-dark.png"));
            }
            finally { w.Close(); }
            Ui.Theme = ThemeMode.Light; var handler = new DemoHandler(); using var service = new TranslationService(handler);
            var manual = new ManualWindow(() => settings, (_, _, _) => { }, service, (_, _) => "synthetic-docs-key");
            try
            {
                manual.Show(); await Task.Delay(80);
                void Frame(string caption, int duration)
                {
                    string name = $"manual-{frames.Count:000}.png"; Render(manual, Path.Combine(output, name)); frames.Add(new { file = name, caption, duration });
                }
                Frame("打开输入翻译栏 · Alt + T", 1000);
                string source = "The meeting starts at eight. I've sent the notes to the channel. Let me know if you need help.";
                for (int n = 8; n < source.Length; n += 8) { manual.Input.Text = source[..n]; await Task.Delay(35); Frame("输入文字 · 停顿后自动翻译", 75); }
                manual.Input.Text = source; await Task.Delay(80); Frame("停止输入 · 等待自动翻译", 850);
                await Task.Delay(1100); if (manual.Output.Text.Length == 0 || handler.Calls != 1) throw new Exception("Documentation mock translation did not finish exactly once");
                Frame("译文完成 · 可复制或更换语言", 1800); Render(manual, Path.Combine(output, "manual-light.png"));
                Ui.Theme = ThemeMode.Dark; manual.RefreshOptions(); await Task.Delay(1100); Render(manual, Path.Combine(output, "manual-dark.png"));
            }
            finally { manual.Close(); }
            foreach (bool dark in new[] { false, true })
            {
                Ui.Theme = dark ? ThemeMode.Dark : ThemeMode.Light;
                var bubble = new BubbleView(.98, 16, "", dark, () => { }, () => { }); bubble.ApplyAppearance(new Settings { Theme = Ui.Theme, BubbleOpacity = .98 }); bubble.Text = "今晚八点见，资料已经发到频道里。\n需要帮助的话，随时告诉我。"; bubble.Width = bubble.FittingWidth(380);
                var host = new Window { Content = bubble, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false };
                try { host.Show(); await Task.Delay(80); Render(bubble, Path.Combine(output, dark ? "bubble-dark.png" : "bubble-light.png")); } finally { host.Close(); }
            }
            var flow = new StackPanel { Margin = new Thickness(22) };
            flow.Children.Add(new TextBlock { Text = "The meeting starts at eight.", FontSize = 20, Margin = new Thickness(0, 0, 0, 16) });
            var sourceBox = new TextBox { Text = "I've sent the notes to the channel.", IsReadOnly = true, FontSize = 17, Padding = new Thickness(10), Height = 52 }; flow.Children.Add(sourceBox);
            var sourceHost = new Window { Title = "本地示例原文", Width = 520, Height = 170, Content = flow };
            try { Ui.Apply(sourceHost, false); sourceHost.Show(); sourceBox.Focus(); sourceBox.SelectAll(); await Task.Delay(100); Render((FrameworkElement)sourceHost.Content, Path.Combine(output, "selected-source.png")); } finally { sourceHost.Close(); }
            await File.WriteAllTextAsync(Path.Combine(output, "frames.json"), JsonSerializer.Serialize(new { mockService = true, realCloudCalls = false, frames, errors }, new JsonSerializerOptions { WriteIndented = true }));
            return errors.Count == 0 ? 0 : 1;
        }
        finally { Ui.Theme = oldTheme; }
    }
}
