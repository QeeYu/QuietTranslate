using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace QuietTranslate;

internal static class PopupPlacement
{
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    internal static Native.Point Near(Native.Point pointer, Native.Rect selection, int width, int height, IntPtr source, int offsetX = 0, int offsetY = 0)
    {
        var work = Native.WorkArea(pointer);
        var exclusions = new List<Native.Rect> {
            // Browser selection tools are frequently drawn inside the browser's own HWND.
            new() { Left = pointer.X - 140, Right = pointer.X + 170, Top = pointer.Y - 82, Bottom = pointer.Y + 18 }, selection
        };
        Native.GetWindowThreadProcessId(source, out uint sourceProcess);
        EnumWindows((window, _) =>
        {
            if (window == source || !IsWindowVisible(window)) return true;
            Native.GetWindowThreadProcessId(window, out uint process); if (process == Environment.ProcessId) return true;
            var name = new StringBuilder(80); Native.GetClassName(window, name, name.Capacity);
            bool popup = name.ToString() == "#32768" || (process == sourceProcess && (Native.GetWindowLongPtr(window, -16).ToInt64() & 0x80000000L) != 0);
            if (popup && Native.GetWindowRect(window, out var r) && r.Right - r.Left < 900 && r.Bottom - r.Top < 500 && Math.Abs(r.Left - pointer.X) < 650 && Math.Abs(r.Top - pointer.Y) < 400) exclusions.Add(r);
            return true;
        }, IntPtr.Zero);
        return Choose(pointer, work, exclusions, width, height, offsetX, offsetY);
    }
    internal static Native.Point Choose(Native.Point p, Native.Rect work, IReadOnlyList<Native.Rect> exclusions, int width, int height, int offsetX = 0, int offsetY = 0)
    {
        var candidates = new[] { new Native.Point(p.X + 24, p.Y + 28), new(p.X + 184, p.Y - height / 2), new(p.X - width - 24, p.Y + 28), new(p.X + 24, p.Y - height - 96), new(p.X - width - 184, p.Y - height / 2) };
        Native.Point best = default; double score = double.MaxValue;
        foreach (var candidate in candidates)
        {
            int x = Math.Clamp(candidate.X + offsetX, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
            int y = Math.Clamp(candidate.Y + offsetY, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
            double value = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));
            foreach (var r in exclusions) value += 100 * (double)Math.Max(0, Math.Min(x + width + 6, r.Right) - Math.Max(x - 6, r.Left)) * Math.Max(0, Math.Min(y + height + 6, r.Bottom) - Math.Max(y - 6, r.Top));
            if (value < score) { score = value; best = new(x, y); }
        }
        return best;
    }
}
