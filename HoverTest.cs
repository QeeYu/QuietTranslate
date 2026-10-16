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

namespace QuietTranslate;

internal static class HoverTest
{
    internal static async Task<int> RunAsync(App app, string output)
    {
        Directory.CreateDirectory(output); var errors = new List<string>(); var results = new List<object>();
        var oldCursor = Native.Cursor(); var oldForeground = Native.GetForegroundWindow();
        app.TestException = e => errors.Add(e.ToString());
        var root = new StackPanel { Margin = new Thickness(20) };
        var a = new Button { Content = "悬停打开长提示", ToolTip = "这是一段合成的长提示，用于验证换行、连续打开与关闭，不含任何真实用户输入。This is synthetic tooltip content only, repeated for wrapping and reopening." };
        var b = new Button { Content = "第二个提示", ToolTip = "继续悬停和关闭，检查是否产生后续空引用错误。", Margin = new Thickness(0, 20, 0, 0) };
        foreach (var button in new[] { a, b }) { ToolTipService.SetInitialShowDelay(button, 80); ToolTipService.SetBetweenShowDelay(button, 0); root.Children.Add(button); }
        var owner = new Window { Title = "轻译 · 合成提示框回归", Width = 420, Height = 220, Content = root, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen }; Ui.Apply(owner);
        int opened = 0; a.ToolTipOpening += (_, _) => opened++; b.ToolTipOpening += (_, _) => opened++;
        try
        {
            owner.Show(); owner.Activate(); Native.SetForegroundWindow(new WindowInteropHelper(owner).Handle); await Task.Delay(150);
            for (int pass = 0; pass < 4; pass++)
            {
                var button = pass % 2 == 0 ? a : b; var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                Native.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(650);
                Native.SetCursorPos((int)point.X - 100, (int)point.Y + 60); await Task.Delay(150);
            }
            results.Add(new { name = "Native tooltip hover/reopen/close does not raise dispatcher errors", status = errors.Count == 0 && opened > 0 ? "PASS" : "FAIL", opened });
            if (opened == 0) errors.Add("Native hover did not open a tooltip; not counted as verified.");
            var tip = new ToolTip { Content = a.ToolTip, PlacementTarget = a, Placement = PlacementMode.Bottom }; Ui.Apply(tip);
            try { tip.IsOpen = true; await Task.Delay(120); tip.ApplyTemplate(); tip.UpdateLayout(); if (tip.ActualWidth > 301 || tip.ActualHeight < 40) throw new Exception("Long tooltip did not wrap"); results.Add(new { name = "Tooltip template expands and long content wraps at runtime", status = "PASS", width = tip.ActualWidth, height = tip.ActualHeight }); }
            catch (Exception ex) { errors.Add(ex.ToString()); results.Add(new { name = "Tooltip template expands at runtime", status = "FAIL" }); }
            finally { tip.IsOpen = false; }
            var rich = new TextBlock { Text = "自定义内容 · 保留控件", FontWeight = FontWeights.Bold };
            var custom = new ToolTip { Content = rich, PlacementTarget = b }; Ui.Apply(custom);
            try { custom.IsOpen = true; await Task.Delay(100); custom.UpdateLayout(); if (VisualTreeHelper.GetParent(rich) == null) throw new Exception("Rich tooltip content was flattened to text"); results.Add(new { name = "Tooltip retains custom element content", status = "PASS" }); }
            catch (Exception ex) { errors.Add(ex.ToString()); }
            finally { custom.IsOpen = false; }
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        finally { owner.Close(); await Task.Delay(150); Native.SetCursorPos(oldCursor.X, oldCursor.Y); Native.SetForegroundWindow(oldForeground); app.TestException = null; }
        await File.WriteAllTextAsync(Path.Combine(output, "hover-tests.json"), JsonSerializer.Serialize(new { passed = errors.Count == 0, errors, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        return errors.Count == 0 ? 0 : 1;
    }
    internal static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement { if (root is T element && element.Name == name) return element; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Find<T>(VisualTreeHelper.GetChild(root, i), name); if (found != null) return found; } return null; }
    internal static async Task ClickAsync(FrameworkElement element)
    {
        element.UpdateLayout(); var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)); Native.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(60);
        Native.GetWindowThreadProcessId(Native.WindowFromPoint(Native.Cursor()), out uint process); if (process != Environment.ProcessId) throw new Exception("Test target is covered by another process");
        Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
    }
}
