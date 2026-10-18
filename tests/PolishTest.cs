using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuietTranslate;

internal static class PolishTest
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    internal static async Task<int> RunAsync(App app, string output)
    {
        Directory.CreateDirectory(output); var tests = new List<object>(); var errors = new List<string>(); var observations = new Dictionary<string, object>();
        int failed = 0; var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow(); app.TestException = e => errors.Add(e.ToString());
        async Task Test(string name, Func<Task> action) { try { await action(); tests.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.Message }); } }
        try
        {
            await Test("Spaced wheel detents move through physical pixels instead of short multi-line jumps", async () =>
            {
                var work = Native.WorkArea(cursor); var a = new Native.Rect { Left = work.Left + 250, Right = work.Left + 250, Top = work.Top + 140, Bottom = work.Top + 140 };
                var w = new BubbleWindow(a, IntPtr.Zero, .92);
                try
                {
                    w.SetText(string.Concat(Enumerable.Repeat("合成段落，用于验证像素滚动。English 日本語。\n", 160)), "synthetic", true); w.Show(); await Task.Delay(220);
                    var view = w.View.Viewport;
                    for (int i = 0; i < 30 && view.ScrollableHeight <= 0; i++) await Task.Delay(30);
                    double scale = VisualTreeHelper.GetDpi(view).DpiScaleY; var offsets = new List<double>();
                    for (int i = 0; i < 30; i++)
                    {
                        if (i % 8 == 0) SmoothScroll.Wheel(view, -120);
                        await Task.Delay(16); offsets.Add(view.VerticalOffset);
                    }
                    observations["wheel"] = new { scale, offsets, view.ScrollableHeight, view.ExtentHeight, view.ViewportHeight, lines = SystemParameters.WheelScrollLines };
                    Check(offsets.Any(x => x > 1), "wheel did not move");
                    Check(offsets.All(x => Math.Abs(x * scale - Math.Round(x * scale)) < .01), "intermediate text offsets use fractional physical pixels");
                    Check(offsets[7] - offsets[4] >= 1 / scale, "each wheel detent finishes too abruptly before the next input");
                    Check(offsets.Zip(offsets.Skip(1)).All(p => p.Second >= p.First), "same-direction input reversed");
                    await Task.Delay(350); double settled = view.VerticalOffset; await Task.Delay(100); Check(Math.Abs(view.VerticalOffset - settled) < .01, "idle scrolling did not stop");
                    DocsCapture.Render(w.View, Path.Combine(output, "crisp-scroll.png"));
                }
                finally { w.Close(); }
            });
            await Test("Transparent text keeps fixed display hinting and preserves tiny wheel increments", async () =>
            {
                var view = new BubbleView(.92, 16, "", false, () => { }, () => { }); view.Text = string.Concat(Enumerable.Repeat("小增量滚动验证。日本語とEnglish。\n", 100)); view.Width = 330; view.SetHeightLimit(300);
                var w = new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowActivated = false };
                try
                {
                    w.Show(); await Task.Delay(180);
                    var text = FindText(view);
                    observations["text"] = new { hint = text == null ? "missing" : TextOptions.GetTextHintingMode(text).ToString(), format = text == null ? "missing" : TextOptions.GetTextFormattingMode(text).ToString(), render = text == null ? "missing" : TextOptions.GetTextRenderingMode(text).ToString(), view.Viewport.ScrollableHeight };
                    Check(text != null && TextOptions.GetTextHintingMode(text) == TextHintingMode.Fixed && TextOptions.GetTextFormattingMode(text) == TextFormattingMode.Display && TextOptions.GetTextRenderingMode(text) == TextRenderingMode.Grayscale, "transparent text uses animated/ideal rendering");
                    Check(text!.CacheMode == null && view.CacheMode == null && view.UseLayoutRounding, "text is cached or layout is not pixel aligned");
                    for (int i = 0; i < 4; i++) { SmoothScroll.Wheel(view.Viewport, -1); await Task.Delay(340); }
                    Check(view.Viewport.VerticalOffset >= .7 && view.Viewport.VerticalOffset < 3, "tiny deltas were discarded or treated as whole notches");
                    observations["tinyWheelOffset"] = view.Viewport.VerticalOffset;
                }
                finally { w.Close(); }
            });
            await Test("AI and ordinary translation visibly distinguish saved, pending and endpoint-specific credentials", () => CredentialsAsync(false));
            await Test("OCR saved/deletion status and scroll-speed preferences apply without exposing credentials", () => CredentialsAsync(true));
        }
        finally { Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground); }
        await File.WriteAllTextAsync(Path.Combine(output, "polish-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, observations, errors, realCloudCalls = false, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 && errors.Count == 0 ? 0 : 1;

        async Task CredentialsAsync(bool ocr)
        {
            string scope = Guid.NewGuid().ToString("N"), secret = "synthetic-only-credential-" + scope;
            var s = new Settings { Provider = Provider.CustomAI, OcrProvider = OcrProvider.OCRSpace };
            foreach (var pair in s.Services) { pair.Value.Endpoint = "https://" + scope + ".example.invalid/" + pair.Key; pair.Value.CredentialId = scope; pair.Value.Model = "synthetic"; }
            foreach (var pair in s.OcrServices) pair.Value.Endpoint = "https://" + scope + ".example.invalid/ocr/" + pair.Key;
            var cleanup = new[] { Credentials.Target(Provider.CustomAI, s.Services[Provider.CustomAI]), Credentials.Target(Provider.Microsoft, s.Services[Provider.Microsoft]), OcrService.SecretTarget(s.OcrProvider, s.OcrServices[s.OcrProvider]) };
            Settings? stored = null; string json = ""; var w = new SettingsWindow(s, value => { stored = value; json = JsonSerializer.Serialize(value); });
            string prefix = ocr ? "Ocr" : "Service";
            T Find<T>(string name) where T : FrameworkElement => HoverTest.Find<T>(w, name) ?? throw new Exception("missing " + name);
            string Status() => Find<TextBlock>(prefix + "KeyStatus").Text;
            void Save() => Find<Button>("SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                w.Show(); w.ShowPage(ocr ? 3 : 2); await Task.Delay(100); Check(Status().Contains("未配置"), "empty credentials are not clearly labelled");
                Find<PasswordBox>(prefix + "Key").Password = secret; Check(Status().Contains("待保存"), "typed key incorrectly marked saved");
                Save(); Check(stored != null && Status().Contains("已保存") && Find<PasswordBox>(prefix + "Key").Password.Length == 0, "save did not confirm success while keeping key hidden");
                Check(!json.Contains(secret) && !Status().Contains(scope), "secret or credential fragment leaked to JSON/status");
                string original = Find<TextBox>(prefix + "Endpoint").Text; Find<TextBox>(prefix + "Endpoint").Text += "/different"; Check(Status().Contains("未配置"), "changed endpoint claims previous key is saved");
                Find<TextBox>(prefix + "Endpoint").Text = original; Check(Status().Contains("已保存"), "returning to saved address lost state");
                if (!ocr)
                {
                    Find<ComboBox>("ServiceChoice").SelectedIndex = (int)Provider.Microsoft; Check(Status().Contains("未配置"), "provider reused another provider's saved status");
                    Find<PasswordBox>("ServiceKey").Password = secret; Save(); Check(Status().Contains("已保存"), "ordinary translator saved state missing");
                    Find<ComboBox>("ServiceChoice").SelectedIndex = (int)Provider.CustomAI; Check(Status().Contains("已保存"), "AI saved state missing after switching back");
                }
                else
                {
                    Find<Button>("ForgetOcrKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(Status().Contains("待删除") && Credentials.Exists(cleanup[2]), "deletion happened before applying or status is misleading");
                    Save(); Check(Status().Contains("未配置") && !Credentials.Exists(cleanup[2]), "deleted OCR key still shown as saved");
                    Find<Slider>("WheelScrollSpeed").Value = 40; Save(); Check(json.Contains("\"WheelScrollSpeed\":0.4"), "scroll-speed preference was not saved");
                }
                DocsCapture.Render((FrameworkElement)w.Content, Path.Combine(output, ocr ? "ocr-key-state.png" : "saved-key-state.png"));
                w.Close(); w = new SettingsWindow(stored!, _ => { }); w.Show(); w.ShowPage(ocr ? 3 : 2); await Task.Delay(80);
                Check(Status().Contains(ocr ? "未配置" : "已保存") && Find<PasswordBox>(prefix + "Key").Password.Length == 0, "reopening lost status or revealed a key");
            }
            finally { w.Close(); foreach (string target in cleanup) { Credentials.Set(target, ""); Credentials.Set(target + "/id", ""); } }
        }
    }
    private static TextBox? FindText(DependencyObject parent)
    {
        if (parent is TextBox text) return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindText(VisualTreeHelper.GetChild(parent, i)) is TextBox found) return found;
        return null;
    }
}
