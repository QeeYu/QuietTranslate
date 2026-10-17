using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

internal static class UpgradeTest
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private const string Chunk = "这是一段逐步返回的合成译文。";
    private sealed class PacedStream : Stream
    {
        private readonly Queue<byte[]> packets = new(Enumerable.Range(0, 60).Select(_ => Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Chunk } } } }) + "\n\n")).Append(Encoding.UTF8.GetBytes("data: [DONE]\n\n")));
        private byte[] current = Array.Empty<byte>(); private int position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position == current.Length)
            {
                if (packets.Count == 0) return 0;
                await Task.Delay(12, ct).ConfigureAwait(false); current = packets.Dequeue(); position = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - position); current.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class PacedContent : HttpContent
    {
        internal PacedContent() { Headers.ContentType = new MediaTypeHeaderValue("text/event-stream"); }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new PacedStream());
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class PacedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { await Task.Delay(10, ct); return new(HttpStatusCode.OK) { Content = new PacedContent() }; }
    }
    internal static async Task<int> RunAsync(App app, string output)
    {
        Directory.CreateDirectory(output); int failed = 0; var tests = new List<object>(); var errors = new List<string>();
        var oldTheme = Ui.Theme; var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow(); app.TestException = e => errors.Add(e.ToString());
        async Task Test(string name, Func<Task> run) { try { await run(); tests.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        try
        {
            await Test("Theme preference round-trips; missing value defaults to system", () =>
            {
                Check(JsonSerializer.Deserialize<Settings>("{}")!.Theme == ThemeMode.System, "old settings default");
                foreach (var mode in Enum.GetValues<ThemeMode>()) Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Theme = mode }))!.Theme == mode, "theme persistence");
                Check(Ui.ResolveTheme(ThemeMode.Dark) && !Ui.ResolveTheme(ThemeMode.Light), "explicit theme follows OS"); return Task.CompletedTask;
            });
            await Test("Theme previews retain edits, update native caption and save without restart", async () =>
            {
                Ui.Theme = ThemeMode.Light; Settings? saved = null; var w = new SettingsWindow(new Settings { Theme = ThemeMode.Light }, value => saved = value);
                try
                {
                    w.Show(); w.Activate(); await Task.Delay(100); w.ShowPage(1);
                    var color = HoverTest.Find<TextBox>(w, "BubbleColorHex")!; color.Text = "#CBAADA";
                    int dictionaries = w.Resources.MergedDictionaries.Count;
                    HoverTest.Find<Button>(w, "ThemeDark")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                    Check(w.Resources["UiThemeDark"] is true && color.Text == "#CBAADA", "draft lost or not dark");
                    var handle = new WindowInteropHelper(w).Handle;
                    if (Environment.OSVersion.Version.Build >= 22000)
                    {
                        Check(Ui.ApplyCaption(w) == 0, "Windows rejected caption colors");
                    }
                    Render(w, Path.Combine(output, "settings-dark.png"));
                    for (int i = 0; i < 6; i++) { HoverTest.Find<Button>(w, "ThemeLight")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); HoverTest.Find<Button>(w, "ThemeDark")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    Check(w.Resources.MergedDictionaries.Count == dictionaries, "styles accumulate on theme changes");
                    HoverTest.Find<Button>(w, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(saved?.Theme == ThemeMode.Dark && saved.BubbleTextColor == "#CBAADA", "theme and draft not saved");
                    HoverTest.Find<Button>(w, "ThemeLight")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Render(w, Path.Combine(output, "settings-light.png"));
                }
                finally { w.Close(); }
            });
            await Test("Background token bursts coalesce and disposed updates never overwrite completion", async () =>
            {
                int calls = 0; string text = ""; bool wrongThread = false;
                using var updates = new StreamedText(app.Dispatcher, value => { calls++; wrongThread |= !app.Dispatcher.CheckAccess(); text = value; });
                await Task.Run(() => { for (int i = 0; i < 10000; i++) updates.Report(i.ToString()); }); await Task.Delay(100);
                Check(text == "9999" && calls < 10 && !wrongThread, $"coalescing text={text}, calls={calls}, wrongThread={wrongThread}");
                updates.Dispose(); text = "final"; await Task.Run(() => updates.Report("stale")); await Task.Delay(80); Check(text == "final", "late update replaced final");
            });
            await Test("Paced SSE appears before completion; editing cancels stale manual updates", async () =>
            {
                Ui.Theme = ThemeMode.Dark; var s = new Settings { Theme = ThemeMode.Dark, AutoTranslateDelay = 2000 }; using var api = new TranslationService(new PacedHandler());
                var w = new ManualWindow(() => s, (_, _, _) => { }, api, (_, _) => "synthetic");
                try
                {
                    w.Show(); w.Input.Text = "Synthetic long source, no real cloud requests.";
                    var request = w.TranslateNowAsync(); await Task.Delay(250);
                    Check(!request.IsCompleted && w.Output.Text.Length > 0 && w.Output.Text.Length < Chunk.Length * 60, "stream stalls until completion");
                    Render(w, Path.Combine(output, "manual-streaming.png")); await request;
                    Check(w.Output.Text == string.Concat(Enumerable.Repeat(Chunk, 60)), "final stream lost content");
                    w.Input.Text = "Another synthetic request"; request = w.TranslateNowAsync(); await Task.Delay(120); w.Input.Text = "User changed the source";
                    string stopped = w.Output.Text; await request; await Task.Delay(100); Check(w.Output.Text == stopped, "canceled stream continued updating");
                    Ui.Theme = ThemeMode.Light; w.RefreshOptions(); Check(w.Resources["UiThemeDark"] is false, "manual did not update live theme");
                }
                finally { w.Close(); }
            });
            await Test("Hovering a short bubble does not freeze incoming long text", async () => Check(await GrowthRepro.RunAsync(Path.Combine(output, "growth")) == 0, "growth regression"));
            await Test("Native wheel over inactive bubble is smooth, bounded and preserves source focus", async () =>
            {
                Ui.Theme = ThemeMode.Dark;
                var owner = new Window { Width = 240, Height = 100, Content = new TextBox { Text = "Synthetic source" } }; BubbleWindow? bubble = null;
                try
                {
                    owner.Show(); owner.Activate(); var handle = new WindowInteropHelper(owner).Handle; Native.SetForegroundWindow(handle); await Task.Delay(100);
                    var source = Native.GetForegroundWindow();
                    var work = Native.WorkArea(cursor); var anchor = new Native.Rect { Left = work.Left + 320, Right = work.Left + 320, Top = work.Top + 150, Bottom = work.Top + 150 };
                    bubble = new BubbleWindow(anchor, source, .99); bubble.ApplyAppearance(new Settings { Theme = ThemeMode.Dark }); bubble.SetText(string.Concat(Enumerable.Repeat("合成滚动文字，仅用于检查鼠标滚轮与滚动位置。\n", 150)), "synthetic", true); bubble.Show(); await Task.Delay(180);
                    var scroll = bubble.View.Viewport; Check(scroll.ScrollableHeight > 200, "missing overflow"); var p = scroll.PointToScreen(new Point(scroll.ActualWidth / 2, 80)); Native.SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(80);
                    Native.mouse_event(0x0800, 0, 0, unchecked((uint)-120), IntPtr.Zero); await Task.Delay(45); double middle = scroll.VerticalOffset; await Task.Delay(150); double end = scroll.VerticalOffset;
                    Check(middle > 0 && end > middle && end < 300, $"wheel not smooth: middle={middle}, end={end}"); Check(Native.GetForegroundWindow() == source, "wheel stole source focus");
                    await Task.Delay(180); end = scroll.VerticalOffset;
                    bubble.SetText(bubble.Translation + "追加的新译文。", "stream"); await Task.Delay(180); Check(Math.Abs(scroll.VerticalOffset - end) < 1, "append moved reading position");
                    SmoothScroll.Wheel(scroll, -30000); await Task.Delay(340); Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "bottom bound");
                    SmoothScroll.Wheel(scroll, 30000); await Task.Delay(340); Check(scroll.VerticalOffset < 1, "top bound"); Render(bubble, Path.Combine(output, "bubble-dark.png"));
                }
                finally { bubble?.Close(); owner.Close(); }
            });
            await Test("Tray popup matches both themes, shows shortcuts and closes on outside click", async () =>
            {
                using var menu = new TrayMenu(() => { }, () => { }, () => { }, () => { }, () => { }, () => { });
                foreach (bool dark in new[] { false, true })
                {
                    Ui.Theme = dark ? ThemeMode.Dark : ThemeMode.Light; menu.Refresh(new Settings(), dark, dark);
                    var work = Native.WorkArea(cursor); Native.SetCursorPos(work.Left + 400, work.Top + 280); menu.Show(); await Task.Delay(150);
                    Check(menu.Menu.IsOpen && menu.Menu.ActualWidth >= 274 && menu.Menu.Resources["UiThemeDark"] is bool b && b == dark, "tray popup theme");
                    Render(menu.Menu, Path.Combine(output, dark ? "tray-dark.png" : "tray-light.png"));
                    Native.SetCursorPos(work.Left + 10, work.Top + 10); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(120); Check(!menu.Menu.IsOpen, "tray stays open outside");
                }
            });
        }
        finally { Ui.Theme = oldTheme; Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground); }
        await File.WriteAllTextAsync(Path.Combine(output, "upgrade-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, errors, realCloudCalls = false, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 && errors.Count == 0 ? 0 : 1;
    }
    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
}
