using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class GrowthRepro
{
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var cursor = Native.Cursor(); BubbleWindow? bubble = null;
        double before = 0, during = 0; bool hovered = false; string? error = null;
        try
        {
            var work = Native.WorkArea(cursor);
            Native.SetCursorPos(work.Left + 10, work.Top + 10);
            var anchor = new Native.Rect { Left = work.Left + 250, Right = work.Left + 250, Top = work.Top + 180, Bottom = work.Top + 180 };
            bubble = new BubbleWindow(anchor, IntPtr.Zero, .98); bubble.SetText("短句", "synthetic"); bubble.Show(); await Task.Delay(180);
            before = bubble.ActualHeight;
            var p = bubble.View.DragHandle.PointToScreen(new Point(8, 10)); Native.SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(80);
            hovered = bubble.View.IsMouseOver;
            for (int i = 1; i <= 5; i++) { bubble.SetText(string.Concat(Enumerable.Repeat("这是长段翻译逐步到达的合成文字，用于检查鼠标停在气泡上时是否正常展开。", i * 3)), "stream"); await Task.Delay(160); }
            during = bubble.ActualHeight;
            if (!hovered || during < before + 100) throw new Exception($"hovered={hovered}, before={before}, during={during}");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (bubble != null)
            {
                bubble.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(bubble.ActualWidth), (int)Math.Ceiling(bubble.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(bubble);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(Path.Combine(output, "hover-growth.png"))) encoder.Save(file); bubble.Close();
            }
            Native.SetCursorPos(cursor.X, cursor.Y);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "hover-growth.json"), JsonSerializer.Serialize(new { passed = error == null, hovered, beforeHeight = before, duringHeight = during, error }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
    }
}
