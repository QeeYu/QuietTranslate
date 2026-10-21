using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class SpeechPolishTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, string message)
    { for (int i = 0; i < 120 && !condition(); i++) await Task.Delay(25); Check(condition(), message); }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.Message }); }
        }
        foreach (bool binary in new[] { false, true })
            await Test((binary ? "Binary" : "Text") + " JSON setup, fragmented captions, audio and finalization", async () =>
            {
                await using var server = new SpeechTest.Server(binary: binary);
                var session = new LiveSession(Path.Combine(output, binary ? "binary" : "text"), SpeechTest.SyntheticCapture);
                try
                {
                    await session.EnableAsync(new SpeechOptions { Endpoint = server.Address.AbsoluteUri }, "synthetic-key");
                    await Until(() => session.Records == 1, "No final subtitle: " + session.Status);
                    await Task.Delay(100); Check(session.Records == 1 && session.Translation == "译文完整" && session.Usage == 321, "Fragmented caption lost or duplicated");
                    Check(server.Errors.IsEmpty && server.Frames > 0, "Capture/protocol mismatch");
                }
                finally { await session.DisableAsync(); }
            });
        await Test("Malformed binary is rejected safely before capture", async () =>
        {
            await using var server = new SpeechTest.Server(binary: true, malformed: true); int captures = 0;
            var session = new LiveSession(Path.Combine(output, "malformed"), (o, frames, level, token) => { captures++; return SpeechTest.SyntheticCapture(o, frames, level, token); });
            try
            {
                await session.EnableAsync(new SpeechOptions { Endpoint = server.Address.AbsoluteUri }, "synthetic-key");
                await Until(() => session.Status.Contains("有效 JSON"), "Invalid data was not reported safely");
                await Task.Delay(120); Check(captures == 0 && server.Connections == 1, "Invalid service caused capture or retry");
            }
            finally { await session.DisableAsync(); }
        });
        await Test("Overlay opacity, themes, hide/double-click, settings sync and invisible resizing", async () =>
        {
            await using var server = new SpeechTest.Server(binary: true);
            var options = new SpeechOptions { Endpoint = server.Address.AbsoluteUri, ControlsHidden = false }; var session = new LiveSession(Path.Combine(output, "ui"), SpeechTest.SyntheticCapture);
            SpeechWindow? view = null; SettingsWindow? settings = null; SpeechOptions? saved = null; Settings? settingsSaved = null; int saves = 0;
            try
            {
                await session.EnableAsync(options, "synthetic-key"); await Until(() => session.Records == 1, "UI fixture failed");
                settings = new SettingsWindow(new Settings { Speech = options.Copy() }, value => settingsSaved = value); settings.Show(); settings.ShowPage(6);
                view = new SpeechWindow(session, () => { }, () => { }, _ => Task.CompletedTask, value => { saved = value.Copy(); saves++; settings.SyncSpeechAppearance(value); });
                view.Show(); view.Left = 100; view.Top = 100; view.Activate(); await Task.Delay(180);
                Check(view.ResizeMode == ResizeMode.CanResize, "Visible system resize grip remained");
                var h = new WindowInteropHelper(view).Handle;
                Check(HoverTest.Find<Thumb>(view, "SpeechResize22") != null, "Invisible resize corner missing");
                HoverTest.Find<Button>(view, "SpeechOpacityButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
                var popup = (Popup)typeof(SpeechWindow).GetField("appearance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
                var slider = HoverTest.Find<Slider>((FrameworkElement)popup.Child, "SpeechOpacitySlider")!;
                int beforeSlider = saves; slider.Value = 55; Check(Math.Abs(session.Options!.Opacity - .55) < .001, "Opacity did not preview immediately");
                Check(saves == beforeSlider, "Slider wrote settings for every step"); popup.IsOpen = false; await Task.Delay(30);
                Check(saved?.Opacity == .55 && saves >= beforeSlider + 1, $"Opacity not saved when editor closed: open={popup.IsOpen}, saved={saved?.Opacity}, current={session.Options!.Opacity}, saves={saves}, before={beforeSlider}");
                HoverTest.Find<Button>(view, "SpeechThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saved?.Background == "#F6F8FB" && saved.Foreground == "#202D3A" && saved.Opacity == .55, "Light theme lost contrast or opacity"); Capture(view, Path.Combine(output, "subtitles-light.png"));
                HoverTest.Find<Button>(view, "SpeechThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saved?.Background == "#17212B" && saved.Foreground == "#F5F7FA", "Dark theme failed"); Capture(view, Path.Combine(output, "subtitles-dark.png"));
                HoverTest.Find<Button>(view, "SpeechHideControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var header = HoverTest.Find<DockPanel>(view, "SpeechDrag")!; Check(header.Visibility == Visibility.Collapsed && saved?.ControlsHidden == true, "Toolbar did not hide/save"); Capture(view, Path.Combine(output, "subtitles-hidden.png"));
                var clicks = new List<string>(); view.PreviewMouseLeftButtonDown += (_, e) => clicks.Add($"{e.ClickCount}:{e.OriginalSource.GetType().Name}");
                var pointer = Native.Cursor(); var point = view.PointToScreen(new Point(85, 85)); Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x43); Native.SetForegroundWindow(h); Native.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(60);
                try
                {
                    Check(Native.GetAncestor(Native.WindowFromPoint(Native.Cursor()), 2) == h, $"Native click target is another window; expected {h}, actual {Native.GetAncestor(Native.WindowFromPoint(Native.Cursor()), 2)}, cursor {Native.Cursor().X},{Native.Cursor().Y}, point {point.X},{point.Y}");
                    Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(25); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(60);
                    Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(25); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
                    await Until(() => header.Visibility == Visibility.Visible, "Double click did not restore toolbar: " + string.Join(",", clicks));
                }
                finally { Native.SetCursorPos(pointer.X, pointer.Y); }
                view.SetLocked(true); Check(header.Visibility == Visibility.Collapsed, "Lock left controls active"); view.SetLocked(false); Check(header.Visibility == Visibility.Visible, "Unlock did not restore controls");
                async Task Drag(Point from, Point to)
                {
                    var previousPointer = Native.Cursor(); Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x43); Native.SetCursorPos((int)from.X, (int)from.Y); await Task.Delay(80);
                    var release = Task.Run(async () => { await Task.Delay(150); Native.SetCursorPos((int)from.X + 6, (int)from.Y + 6); await Task.Delay(80); Native.SetCursorPos((int)to.X, (int)to.Y); await Task.Delay(100); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); });
                    try { Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await release; await Task.Delay(100); }
                    finally { Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); Native.SetCursorPos(previousPointer.X, previousPointer.Y); }
                }
                double left = view.Left, top = view.Top; var start = view.PointToScreen(new Point(300, 22));
                await Drag(start, new Point(start.X - 28, start.Y - 20));
                Check(view.Left < left - 10 && view.Top < top - 10, "Empty part of the top bar did not drag the window");
                double width = view.ActualWidth, height = view.ActualHeight; var edge = view.PointToScreen(new Point(width - 3, height - 3));
                await Drag(edge, new Point(edge.X + 35, edge.Y + 25));
                Check(view.ActualWidth > width + 10 && view.ActualHeight > height + 10, $"Invisible corner did not resize: {width}x{height} -> {view.ActualWidth}x{view.ActualHeight}");
                view.Width = 420; view.UpdateLayout(); Check(header.ActualWidth >= 350 && header.ActualHeight < 60, "Narrow toolbar clipped or wrapped");
                HoverTest.Find<Button>(settings, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(settingsSaved?.Speech.Opacity == .55 && settingsSaved.Speech.Background == "#17212B" && !settingsSaved.Speech.ControlsHidden, "Open settings overwrote quick appearance");
                Check(server.Connections == 1 && session.Records == 1 && server.Errors.IsEmpty, "Appearance reconnected or lost history");
            }
            finally { view?.Close(); settings?.Close(); await session.DisableAsync(); }
        });
        await File.WriteAllTextAsync(Path.Combine(output, "speech-polish.json"), JsonSerializer.Serialize(new { passed = results.Count - failed, failed, results, realCloudCalls = false, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}

