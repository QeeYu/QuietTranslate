using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class BubbleTest
{
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); int failed = 0; var tests = new List<object>(); var sizes = new List<object>();
        async Task Test(string name, Func<Task> run) { try { await run(); tests.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        await Test("Smart text works with opposite themes, low contrast accents and mid-gray surfaces", () =>
        {
            foreach (string surface in new[] { "#FFFFFF", "#202630", "#E8F3F1", "#787878", "#A0A0A0" })
            foreach (string requested in new[] { "", surface, "#91BDF4", "#FFD69A" })
            {
                var v = new BubbleView(.92, 16, "", false, () => { }, () => { }); v.ApplyAppearance(new Settings { BubbleBackgroundColor = surface, BubbleTextColor = requested });
                Check(Ui.Contrast(v.TextColor, v.SurfaceColor) >= 4.49, surface + " / " + requested);
                if (requested.Length > 0) { v.ApplyAppearance(new Settings { BubbleBackgroundColor = surface, BubbleTextColor = requested, BubbleSmartColors = false }); Check(v.TextColor == Ui.Color(requested), "manual color changed"); }
            }
            var button = new Settings { ButtonBackground = "#FFFFF0", ButtonForeground = "#FFFFFF" }; Check(Ui.Contrast(SelectionButtonWindow.TextColor(button), Ui.Color(button.ButtonBackground)) >= 4.49, "button contrast"); return Task.CompletedTask;
        });
        await Test("Palette preserves opacity, selected surface and invisible outer border", () =>
        {
            var v = new BubbleView(1, 16, "", true, () => { }, () => { }); v.ApplyAppearance(new Settings { BubbleBackgroundColor = "#123456", BubbleOpacity = .25, BubbleBorderOpacity = -1 }); Check(v.SurfaceColor.R == 0x12 && v.BackgroundAlpha == 64 && ((SolidColorBrush)v.BorderBrush).Color.A == 64, "palette replaced selected alpha");
            v.ApplyAppearance(new Settings { BubbleOpacity = 0, BubbleBorderOpacity = -1, BubbleBorderVisible = false }); Check(v.BackgroundAlpha == 0 && ((SolidColorBrush)v.BorderBrush).Color.A == 0 && v.BorderThickness.Left == 0, "transparent outline"); return Task.CompletedTask;
        });
        await Test("Glyph measurement distinguishes equal-length wide and narrow letters", () =>
        {
            var v = new BubbleView(.92, 16, "", false, () => { }, () => { }); v.Text = new string('W', 18); double wide = v.FittingWidth(380); v.Text = new string('i', 18); double narrow = v.FittingWidth(380); Check(wide > narrow + 65 && narrow < 170 && wide <= 380, $"wide={wide}, narrow={narrow}");
            v.Text = "你好"; Check(v.FittingWidth(380) <= 120, "word too wide"); v.Text = "\n🙂 Hello 日本語\n"; Check(double.IsFinite(v.FittingWidth(160)) && v.FittingWidth(160) <= 160, "emoji/newline"); return Task.CompletedTask;
        });
        await Test("Pointer placement avoids selection tool zone and stays inside monitor work area", () =>
        {
            var work = new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var zone = new Native.Rect { Left = 460, Top = 318, Right = 770, Bottom = 418 };
            var p = PopupPlacement.Choose(new(600, 400), work, new[] { zone }, 250, 140);
            Check(p.X >= zone.Right + 6 || p.X + 256 <= zone.Left || p.Y >= zone.Bottom + 6 || p.Y + 146 <= zone.Top, "selection tool overlap");
            foreach (var cursor in new[] { new Native.Point(1, 1), new(1919, 1), new(1, 1079), new(1919, 1079) }) { p = PopupPlacement.Choose(cursor, work, Array.Empty<Native.Rect>(), 380, 400); Check(p.X >= 8 && p.Y >= 8 && p.X + 380 <= 1912 && p.Y + 400 <= 1072, "monitor bounds"); }
            return Task.CompletedTask;
        });
        var oldCursor = Native.Cursor(); var oldForeground = Native.GetForegroundWindow();
        var safe = Native.WorkArea(oldCursor); Native.SetCursorPos(safe.Left + 10, safe.Top + 10); await Task.Delay(400);
        var owner = new Window { Title = "QuietTranslate synthetic bubble owner", Width = 260, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new TextBox { Text = "Synthetic content only" } };
        BubbleWindow? bubble = null;
        try
        {
            owner.Show(); var handle = new WindowInteropHelper(owner).Handle; Native.SetForegroundWindow(handle); await Task.Delay(120);
            var work = Native.WorkArea(Native.Cursor()); var anchor = new Native.Rect { Left = work.Left + 300, Right = work.Left + 380, Top = work.Top + 180, Bottom = work.Top + 200 };
            foreach (var sample in new[] { ("word", "你好"), ("sentence", "收到，我马上过去。"), ("paragraph", "今晚八点见，资料已发到频道里。需要帮助的话，随时告诉我。我们可以先确认内容，再一起讨论下一步的安排。"), ("long", string.Concat(Enumerable.Repeat("这是用于验证自动换行和滚动的合成文字。", 100))) })
            {
                await Test("Live sizing and bounds: " + sample.Item1, async () =>
                {
                    Native.SetForegroundWindow(handle); bubble = new BubbleWindow(anchor, IntPtr.Zero, .94); bubble.ApplyAppearance(new()); bubble.SetText(sample.Item2, "synthetic", completed: true); bubble.Show(); await Task.Delay(220);
                    Check(bubble.IsVisible && bubble.ActualWidth <= 381 && bubble.ActualHeight <= 401, $"unbounded size: {bubble.DismissReason}, visible={bubble.IsVisible}, {bubble.ActualWidth}x{bubble.ActualHeight}"); Check(Math.Abs(bubble.ActualWidth - bubble.View.FittingWidth(380)) < 3, "live size differs from measured text");
                    if (sample.Item1 == "word") Check(bubble.ActualWidth <= 125 && bubble.ActualHeight < 95, "short translation not compact");
                    if (sample.Item1 == "sentence") Check(bubble.ActualWidth < 260 && bubble.ActualHeight < 100, "sentence whitespace");
                    sizes.Add(new { sample = sample.Item1, width = bubble.ActualWidth, height = bubble.ActualHeight }); Render(bubble, Path.Combine(output, sample.Item1 + ".png")); bubble.Close(); bubble = null;
                });
                bubble?.Close(); bubble = null;
            }
            await Test("Stream grows in steps and final result can shrink to exact glyph width", async () =>
            {
                Native.SetForegroundWindow(handle); bubble = new BubbleWindow(anchor, IntPtr.Zero, .94); bubble.SetText("正在翻译…", "synthetic"); bubble.Show(); await Task.Delay(120);
                bubble.SetText("这是一个比较长的中间结果，用来验证窗口宽度变化。", "stream"); await Task.Delay(190); double wide = bubble.Width;
                bubble.SetText("完成", "done", completed: true); await Task.Delay(190); Check(bubble.Width < wide && bubble.Width <= 125, "final shrink"); bubble.Close(); bubble = null;
            });
            await Test("Header dragging preserves source focus and pins position across result updates", async () =>
            {
                owner.Activate(); Native.SetForegroundWindow(handle); await Task.Delay(120); var dragSource = Native.GetForegroundWindow();
                var a = new Native.Rect { Left = work.Left + 440, Right = work.Left + 440, Top = work.Top + 260, Bottom = work.Top + 260 };
                bubble = new BubbleWindow(a, dragSource, .95); bubble.SetText("你好", "synthetic", completed: true); bubble.Show(); await Task.Delay(200);
                Check(bubble.IsVisible, "drag fixture closed: " + bubble.DismissReason);
                Native.GetWindowRect(new WindowInteropHelper(bubble).Handle, out var before);
                var grip = bubble.View.DragHandle; var p = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
                Native.SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(80); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(60);
                Native.SetCursorPos((int)p.X + 90, (int)p.Y + 35); await Task.Delay(70); Native.SetCursorPos((int)p.X + 140, (int)p.Y + 70); await Task.Delay(70); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(120);
                Native.GetWindowRect(new WindowInteropHelper(bubble).Handle, out var moved);
                Check(bubble.IsVisible && bubble.PositionedByUser && Math.Abs(moved.Left - before.Left - 140) < 4 && Math.Abs(moved.Top - before.Top - 70) < 4, $"drag did not move: pinned={bubble.PositionedByUser}, delta={moved.Left-before.Left},{moved.Top-before.Top}");
                Check(Native.GetForegroundWindow() == dragSource, "drag stole source focus");
                Native.SetCursorPos(work.Left + 10, work.Top + 10); bubble.SetText("拖动后，翻译结果更新仍保持本次的位置，不会重新跳回鼠标附近。", "synthetic", completed: true); await Task.Delay(240);
                Native.GetWindowRect(new WindowInteropHelper(bubble).Handle, out var after); Check(Math.Abs(after.Left - moved.Left) < 2 && Math.Abs(after.Top - moved.Top) < 2, "updated translation jumped back");
                Render(bubble, Path.Combine(output, "dragged-bubble.png")); bubble.Close(); bubble = null;
            });
            await Test("Top-right more menu, native copy, context action, close and focus preservation", async () =>
            {
                if (SelectionReader.OleGetClipboard(out var saved) != 0) throw new Exception("Clipboard snapshot unavailable"); uint copied = 0;
                try
                {
                    owner.Activate(); Native.SetForegroundWindow(handle); await Task.Delay(100); var source = Native.GetForegroundWindow(); bubble = new BubbleWindow(anchor, source, .94); bool context = false; bubble.SetContext("在翻译栏编辑原文…", () => context = true); bubble.SetText("菜单复制测试", "synthetic", completed: true); bubble.Show(); await Task.Delay(170);
                    Check(bubble.IsVisible, "menu fixture already closed: " + bubble.DismissReason);
                    await Click(bubble.View.MoreButton); await Task.Delay(160); Check(bubble.IsVisible && bubble.View.IsMenuOpen, "menu click dismissed bubble: " + bubble.DismissReason + ", visible=" + bubble.IsVisible + ", menu=" + bubble.View.IsMenuOpen);
                    var menu = bubble.View.ActionsMenu; await Click((MenuItem)menu.Items[0]); await Task.Delay(160); copied = Native.GetClipboardSequenceNumber(); Check(Clipboard.GetText() == "菜单复制测试" && bubble.IsVisible && !menu.IsOpen, $"copy action or menu exclusion: reason={bubble.DismissReason}, visible={bubble.IsVisible}, open={menu.IsOpen}, copied={Clipboard.GetText() == "菜单复制测试"}");
                    Check(Native.GetForegroundWindow() == source, "copy stole original focus");
                    await Click(bubble.View.MoreButton); await Task.Delay(220); Check(bubble.IsVisible && menu.IsOpen, "second menu open: " + bubble.DismissReason + ", visible=" + bubble.IsVisible + ", open=" + menu.IsOpen); await Click((MenuItem)menu.Items[1]); await Task.Delay(220); Check(context && bubble.IsVisible, $"context action: invoked={context}, visible={bubble.IsVisible}, open={menu.IsOpen}, reason={bubble.DismissReason}");
                    await Click(bubble.View.MoreButton); await Task.Delay(220); Check(menu.IsOpen, "third menu open"); await Click((MenuItem)menu.Items[3]); await Task.Delay(160); Check(!bubble.IsVisible && bubble.Translation.Length == 0, "close menu action"); bubble = null;
                }
                finally
                {
                    bubble?.Close(); bubble = null;
                    if (copied != 0 && Native.GetClipboardSequenceNumber() == copied) { for (int i = 0; i < 8; i++) { if (SelectionReader.OleSetClipboard(saved) == 0) { SelectionReader.OleFlushClipboard(); break; } await Task.Delay(20); } }
                    if (saved != null && System.Runtime.InteropServices.Marshal.IsComObject(saved)) System.Runtime.InteropServices.Marshal.ReleaseComObject(saved);
                }
            });
            await Test("Settings preview uses the same adaptive measurements and smart toggle", async () =>
            {
                Settings? applied = null; var w = new SettingsWindow(new Settings(), s => applied = s, false);
                try { w.Show(); w.ShowPage(1); await Task.Delay(160); var smart = Find<CheckBox>(w, "BubbleSmartColors")!; var fg = Find<TextBox>(w, "BubbleColorHex")!; var bg = Find<TextBox>(w, "BubbleBackgroundHex")!; var preview = Find<BubbleView>(w, "BubblePreview")!;
                    bg.Text = "#202630"; fg.Text = "#202630"; Check(preview.ColorAdjusted && Ui.Contrast(preview.TextColor, preview.SurfaceColor) >= 4.49, "smart preview"); smart.IsChecked = false; Check(preview.TextColor == Ui.Color("#202630"), "manual preview"); smart.IsChecked = true;
                    Find<Button>(w, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(applied?.BubbleSmartColors == true && applied.BubbleBackgroundColor == "#202630", "smart preference save"); Render(w, Path.Combine(output, "settings.png")); }
                finally { w.Close(); }
            });
            await Test("Light/dark smart palette gallery renders", async () =>
            {
                var grid = new Grid { Background = new SolidColorBrush(Ui.Color("#EEF2F5")), Margin = new Thickness(0) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new());
                var samples = new[] { ("短词 · 贴合内容", "你好", "#FAFBFD", ""), ("深色背景 · 自动浅字", "收到，我马上过去。", "#202630", ""), ("段落 · 自然换行", "今晚八点见，资料已发到频道里。需要帮助的话，随时告诉我。我们可以先确认内容，再一起讨论下一步的安排。", "#FAFBFD", ""), ("雾青 · 自动协调", "资料已发送，记得查看。", "#E8F3F1", ""), ("自选蓝色 · 智能调整可读性", "The meeting starts at eight.", "#FFFFFF", "#91BDF4"), ("右上角 · 紧凑操作", "复制、更多和关闭，随气泡尺寸协调排列。", "#202630", "#FFD69A") };
                for (int i = 0; i < samples.Length; i++) { var sample = samples[i]; var panel = new StackPanel { Margin = new Thickness(24, 16, 18, 12) }; panel.Children.Add(new TextBlock { Text = sample.Item1, Foreground = new SolidColorBrush(Ui.Color("#556473")), FontSize = 13, Margin = new Thickness(0, 0, 0, 12) }); var v = new BubbleView(.96, 16, "", false, () => { }, () => { }); v.ApplyAppearance(new Settings { BubbleBackgroundColor = sample.Item3, BubbleTextColor = sample.Item4, BubbleOpacity = .96 }); v.Text = sample.Item2; v.Width = v.FittingWidth(365); v.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(v); Grid.SetColumn(panel, i % 2); Grid.SetRow(panel, i / 2); grid.Children.Add(panel); }
                var w = new Window { Width = 900, Height = 680, Title = "轻译气泡预览 · 合成文字", Content = grid, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                try { w.Show(); await Task.Delay(180); Render(w, Path.Combine(output, "bubble-gallery.png")); } finally { w.Close(); }
            });
        }
        finally { bubble?.Close(); owner.Close(); Native.SetCursorPos(oldCursor.X, oldCursor.Y); Native.SetForegroundWindow(oldForeground); }
        await File.WriteAllTextAsync(Path.Combine(output, "bubble-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, sizes, realProviderCalls = false }, new JsonSerializerOptions { WriteIndented = true })); return failed == 0 ? 0 : 1;
    }
    private static async Task Click(FrameworkElement element)
    {
        element.UpdateLayout(); var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)); Native.SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(100);
        element.UpdateLayout(); p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)); Native.SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(40);
        var cursor = Native.Cursor(); Check(Math.Abs(cursor.X - p.X) < 3 && Math.Abs(cursor.Y - p.Y) < 3, $"Pointer moved externally before synthetic click: expected {p}, got {cursor.X},{cursor.Y}");
        if (PresentationSource.FromVisual(element) is HwndSource source) Check(Native.WindowFromPoint(cursor) == source.Handle, $"Click target covered: point={cursor.X},{cursor.Y}, expected HWND={source.Handle}, actual={Native.WindowFromPoint(cursor)}");
        Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(60); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
    }
    private static void Render(Window w, string path) { w.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(w); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); png.Save(stream); }
    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement { if (root is T element && element.Name == name) return element; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Find<T>(VisualTreeHelper.GetChild(root, i), name); if (found != null) return found; } return null; }
}

