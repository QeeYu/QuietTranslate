using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuietTranslate;

internal static class MotionTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Glyphs(Visual visual)
    {
        int Drawn(Drawing drawing) => drawing is GlyphRunDrawing glyph ? glyph.GlyphRun.GlyphIndices.Count : drawing is DrawingGroup group ? group.Children.Sum(Drawn) : 0;
        int count = VisualTreeHelper.GetDrawing(visual) is DrawingGroup drawing ? Drawn(drawing) : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++) if (VisualTreeHelper.GetChild(visual, i) is Visual child) count += Glyphs(child);
        return count;
    }
    internal static async Task<int> RunAsync(App app, string output)
    {
        Directory.CreateDirectory(output); var tests = new List<object>(); var observations = new Dictionary<string, object>(); var errors = new List<string>(); int failed = 0;
        var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow(); app.TestException = e => errors.Add(e.ToString());
        async Task Test(string name, Func<Task> action) { try { await action(); tests.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        var work = Native.WorkArea(cursor);
        string content = string.Concat(Enumerable.Repeat("Long translation 日本語と中文。Only visible lines should be rendered.\n", 500));
        try
        {
            await Test("Thirty-thousand-character translation draws only viewport lines during wheel animation", async () =>
            {
                var point = new Native.Point(work.Left + 300, work.Top + 130); Native.SetCursorPos(work.Left + 10, work.Top + 10);
                var w = new BubbleWindow(new Native.Rect { Left = point.X, Right = point.X, Top = point.Y, Bottom = point.Y }, IntPtr.Zero, .98);
                try
                {
                    w.SetText(content, "synthetic", true); w.Show(); await Task.Delay(500); w.UpdateLayout();
                    var view = w.View.Viewport; int glyphs = Glyphs(view);
                    observations["paint"] = new { inputCharacters = content.Length, drawnGlyphs = glyphs, renderTier = RenderCapability.Tier >> 16 };
                    var p = view.PointToScreen(new Point(view.ActualWidth / 2, 70)); Native.SetCursorPos((int)p.X, (int)p.Y);
                    var frames = new List<double>(); double previous = -1;
                    void Frame(object? sender, EventArgs e) { double time = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds; if (previous >= 0 && time > previous) frames.Add(time - previous); previous = time; }
                    var cpu = Process.GetCurrentProcess().TotalProcessorTime; var elapsed = Stopwatch.StartNew();
                    CompositionTarget.Rendering += Frame;
                    try { for (int i = 0; i < 45; i++) { SmoothScroll.Wheel(view, -30); await Task.Delay(16); } await Task.Delay(350); }
                    finally { CompositionTarget.Rendering -= Frame; }
                    double offset = view.VerticalOffset;
                    Check(offset > 200 && Native.GetForegroundWindow() == foreground, "native scroll missing or stole focus");
                    Check(Glyphs(view) < 2500, "viewport drawing grew with scroll distance");
                    w.SetText(content + "Appended while reading.", "synthetic"); await Task.Delay(180); Check(Math.Abs(view.VerticalOffset - offset) < 1, "append reset reading position");
                    observations["wheel"] = new { offset, durationMs = elapsed.ElapsedMilliseconds, cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds, frames = frames.Count, p95IntervalMs = frames.Count == 0 ? 0 : frames.OrderBy(x => x).ElementAt((int)((frames.Count - 1) * .95)), maxIntervalMs = frames.Count == 0 ? 0 : frames.Max() };
                    Native.mouse_event(0x0800, 0, 0, unchecked((uint)-120), IntPtr.Zero); await Task.Delay(230);
                    Check(view.VerticalOffset > offset + 10 && Native.GetForegroundWindow() == foreground, "native wheel hook did not scroll the visible viewport without activation");
                    Check(glyphs > 30 && glyphs < 2500, $"offscreen text still drawn: {glyphs} glyphs for {content.Length} characters");
                }
                finally { w.Close(); }
            });
            await Test("Bottom-edge streaming keeps one horizontal and vertical edge instead of changing sides", async () =>
            {
                Native.Point point = new(work.Left + (work.Right - work.Left) / 2, work.Bottom - 10);
                // Locate a deterministic candidate-switch case from the old placement scorer.
                for (int x = work.Left + 100; x < work.Right - 100; x += 60)
                {
                    var p = new Native.Point(x, work.Bottom - 10); var excluded = new Native.Rect { Left = p.X - 140, Right = p.X + 170, Top = p.Y - 82, Bottom = p.Y + 18 };
                    var small = PopupPlacement.Choose(p, work, new[] { excluded }, 118, 70); var large = PopupPlacement.Choose(p, work, new[] { excluded }, 380, 400);
                    if (Math.Abs(small.X - large.X) > 8 && Math.Abs(small.X + 118 - large.X - 380) > 8) { point = p; break; }
                }
                Native.SetCursorPos(point.X, point.Y);
                var w = new BubbleWindow(new Native.Rect { Left = point.X, Right = point.X, Top = point.Y, Bottom = point.Y }, IntPtr.Zero, .98);
                var rects = new List<Native.Rect>();
                try
                {
                    w.SetText("正在翻译…", "synthetic"); w.Show(); await Task.Delay(180);
                    foreach (int length in new[] { 16, 40, 85, 160, 300, 600, 1800, 6000, 18000, 31000 })
                    {
                        w.SetText(content[..Math.Min(length, content.Length)], "stream"); await Task.Delay(150);
                        Native.GetWindowRect(new WindowInteropHelper(w).Handle, out var r); rects.Add(r);
                        Check(r.Left >= work.Left + 7 && r.Right <= work.Right - 7 && r.Top >= work.Top + 7 && r.Bottom <= work.Bottom - 7, $"bubble outside work area: {r.Left},{r.Top},{r.Right},{r.Bottom}, work={work.Left},{work.Top},{work.Right},{work.Bottom}");
                    }
                    int leftRange = rects.Max(r => r.Left) - rects.Min(r => r.Left), rightRange = rects.Max(r => r.Right) - rects.Min(r => r.Right);
                    int topRange = rects.Max(r => r.Top) - rects.Min(r => r.Top), bottomRange = rects.Max(r => r.Bottom) - rects.Min(r => r.Bottom);
                    observations["edges"] = new { point.X, point.Y, leftRange, rightRange, topRange, bottomRange, rectangles = rects.Select(r => new { r.Left, r.Top, r.Right, r.Bottom }) };
                    Check(Math.Min(leftRange, rightRange) < 3 && Math.Min(topRange, bottomRange) < 3, $"placement changed sides: left={leftRange}, right={rightRange}, top={topRange}, bottom={bottomRange}");
                    var last = rects[^1]; w.SetText(content, "done", true); await Task.Delay(180);
                    Native.GetWindowRect(new WindowInteropHelper(w).Handle, out var after); Check(Math.Abs(after.Left - last.Left) < 3 && Math.Abs(after.Top - last.Top) < 3, "completion bounced");
                }
                finally { w.Close(); }
            });
        }
        finally { Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground); }
        await File.WriteAllTextAsync(Path.Combine(output, "motion-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, observations, errors, realCloudCalls = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 && errors.Count == 0 ? 0 : 1;
    }
}
