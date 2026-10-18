using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace QuietTranslate;

internal sealed record Capture(Bitmap Bitmap, Native.Rect Bounds) : IDisposable { public void Dispose() => Bitmap.Dispose(); }

internal sealed class CaptureWindow : Window
{
    private readonly System.Windows.Shapes.Rectangle selection = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(25, 255, 255, 255)) };
    private readonly Canvas canvas = new();
    private Point? start;
    private readonly Rectangle screen;
    public Action<Native.Rect?>? Selected;
    public CaptureWindow(Bitmap bitmap, Rectangle screenBounds)
    {
        screen = screenBounds;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Topmost = true; Background = Brushes.Black; Cursor = Cursors.Cross;
        var grid = new Grid();
        grid.Children.Add(new Image { Source = ToSource(bitmap), Stretch = Stretch.Fill });
        grid.Children.Add(new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0, 0, 0)), IsHitTestVisible = false });
        grid.Children.Add(canvas); canvas.Children.Add(selection);
        grid.Children.Add(new TextBlock { Text = "拖动框选 · Esc / 右键取消", Foreground = Brushes.White, Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 25, 30, 40)), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
        Content = grid;
        SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), screen.X, screen.Y, screen.Width, screen.Height, 0x0040);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Selected?.Invoke(null); } };
        MouseRightButtonDown += (_, _) => Selected?.Invoke(null);
        MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); CaptureMouse(); Draw(start.Value); };
        MouseMove += (_, e) => { if (start.HasValue) Draw(e.GetPosition(canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!start.HasValue) return;
            Point a = PointToScreen(start.Value), b = PointToScreen(e.GetPosition(canvas));
            ReleaseMouseCapture(); start = null;
            var rect = new Native.Rect { Left = Math.Max(screen.Left, (int)Math.Min(a.X, b.X)), Top = Math.Max(screen.Top, (int)Math.Min(a.Y, b.Y)), Right = Math.Min(screen.Right, (int)Math.Max(a.X, b.X)), Bottom = Math.Min(screen.Bottom, (int)Math.Max(a.Y, b.Y)) };
            if (rect.Right - rect.Left >= 5 && rect.Bottom - rect.Top >= 5) Selected?.Invoke(rect);
        };
    }
    private void Draw(Point p)
    {
        if (!start.HasValue) return;
        Canvas.SetLeft(selection, Math.Min(start.Value.X, p.X)); Canvas.SetTop(selection, Math.Min(start.Value.Y, p.Y));
        selection.Width = Math.Abs(start.Value.X - p.X); selection.Height = Math.Abs(start.Value.Y - p.Y);
    }
    internal static BitmapSource ToSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    public static async Task<Capture?> SelectAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource<Capture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<CaptureWindow>(); var bitmaps = new List<Bitmap>();
        var foreground = Native.GetForegroundWindow();
        try
        {
            // Freeze all screens before showing any overlay; images stay in RAM and never touch the clipboard.
            foreach (var monitor in System.Windows.Forms.Screen.AllScreens)
            {
                var b = monitor.Bounds;
                var image = new Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                bitmaps.Add(image);
                using (var g = Graphics.FromImage(image)) g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                var window = new CaptureWindow(image, b);
                window.Selected = rect =>
                {
                    if (done.Task.IsCompleted) return;
                    if (!rect.HasValue) { done.TrySetResult(null); return; }
                    var r = rect.Value;
                    var crop = image.Clone(new Rectangle(r.Left - b.Left, r.Top - b.Top, r.Right - r.Left, r.Bottom - r.Top), image.PixelFormat);
                    if (!done.TrySetResult(new Capture(crop, r))) crop.Dispose();
                };
                windows.Add(window);
            }
            foreach (var window in windows) window.Show();
            var cursor = Native.Cursor();
            windows.FirstOrDefault(w => { Native.GetWindowRect(new WindowInteropHelper(w).Handle, out var r); return cursor.X >= r.Left && cursor.X < r.Right && cursor.Y >= r.Top && cursor.Y < r.Bottom; })?.Activate();
            using var registration = ct.Register(() => done.TrySetCanceled(ct));
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint process);
                if (process != Environment.ProcessId) done.TrySetResult(null);
            };
            timer.Start();
            try { return await done.Task; }
            finally { timer.Stop(); }
        }
        finally
        {
            bool ours = windows.Any(w => new WindowInteropHelper(w).Handle == Native.GetForegroundWindow());
            foreach (var window in windows) window.Close();
            foreach (var bitmap in bitmaps) bitmap.Dispose();
            if (ours && foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
        }
    }
}

