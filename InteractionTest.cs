using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuietTranslate;

internal static class InteractionTest
{
    internal static void OpenFixture(App app)
    {
        var text = new TextBox { Text = string.Join("\n", Enumerable.Range(1, 85).Select(i => $"Line {i:00}: synthetic scrolling selection, no private content.")), FontSize = 17, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) };
        var window = new Window { Title = "QuietTranslate scrolling selection fixture", Width = 640, Height = 370, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = text, Topmost = true };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; timeout.Tick += (_, _) => window.Close();
        window.Closed += (_, _) => { timeout.Stop(); app.Shutdown(); }; window.Show(); window.Activate(); text.Focus(); timeout.Start();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task<int> RunAsync(App app, string output)
    {
        Directory.CreateDirectory(output); int failed = 0; var tests = new List<object>(); var errors = new List<string>(); var observations = new Dictionary<string, object>();
        var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow(); app.TestException = e => errors.Add(e.ToString());
        async Task Test(string name, Func<Task> run) { try { await run(); tests.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        try
        {
            await Test("Translation body has no status tooltip; compact action tips remain", () =>
            {
                var bubble = new BubbleWindow(default, IntPtr.Zero, .95);
                try { bubble.SetText("合成译文", "简体中文 · 123 ms · Esc 关闭", true); Check(bubble.ToolTip == null && bubble.View.ToolTip == null, "status tooltip remains on translation body"); Check(bubble.View.MoreButton.ToolTip != null, "action hint removed"); }
                finally { bubble.Close(); }
                return Task.CompletedTask;
            });
            await Test("Burst scrolling reverses immediately, respects fine deltas and has no queued tail", async () =>
            {
                var work = Native.WorkArea(cursor); var anchor = new Native.Rect { Left = work.Left + 350, Right = work.Left + 350, Top = work.Top + 150, Bottom = work.Top + 150 };
                var bubble = new BubbleWindow(anchor, IntPtr.Zero, .95);
                try
                {
                    Native.SetCursorPos(work.Left + 10, work.Top + 10); bubble.SetText(string.Concat(Enumerable.Repeat("用于验证连续滚轮和反向滚动的合成文字。\n", 160)), "synthetic", true); bubble.Show(); await Task.Delay(180);
                    var view = bubble.View.Viewport; view.ScrollToVerticalOffset(400); await Task.Delay(60);
                    for (int i = 0; i < 8; i++) SmoothScroll.Wheel(view, -120);
                    await Task.Delay(45); double beforeReverse = view.VerticalOffset;
                    SmoothScroll.Wheel(view, 120); await Task.Delay(65); double afterReverse = view.VerticalOffset;
                    Check(afterReverse < beforeReverse - 10, $"kept moving in old direction: {beforeReverse} -> {afterReverse}");
                    await Task.Delay(320); double settled = view.VerticalOffset; await Task.Delay(100); Check(Math.Abs(view.VerticalOffset - settled) < .1, "scroll tail continues after settling");
                    for (int i = 0; i < 4; i++) SmoothScroll.Wheel(view, -15);
                    await Task.Delay(340); double fine = view.VerticalOffset - settled; double expected = SystemParameters.WheelScrollLines * 18 * .5 * SmoothScroll.Speed;
                    Check(Math.Abs(fine - expected) < 1, $"fine wheel deltas lost: {fine}, expected {expected}");
                    observations["scroll"] = new { beforeReverse, afterReverse, settled, fine };
                }
                finally { bubble.Close(); }
            });
            await Test("Wheel while holding a long selection survives mouse-up and clicking translates once", async () =>
            {
                using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--scroll-selection-fixture") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
                int calls = 0; string? translated = null;
                using var trigger = new SelectionTrigger((_, _, text) => { calls++; translated = text; }, () => new Settings(), () => false);
                try
                {
                    IntPtr source = IntPtr.Zero;
                    for (int i = 0; i < 45; i++) { process.Refresh(); source = process.MainWindowHandle; if (source != IntPtr.Zero) break; await Task.Delay(40); }
                    Check(source != IntPtr.Zero, "external fixture not started"); await Task.Delay(120); Native.GetWindowRect(source, out var rect);
                    Native.SetCursorPos(rect.Left + 50, rect.Top + 70); await Task.Delay(40); Native.GetWindowThreadProcessId(Native.WindowFromPoint(Native.Cursor()), out uint hit);
                    Check(hit == process.Id, "external fixture covered; refusing to click another app");
                    Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(100); Check(Native.GetForegroundWindow() == source, "external fixture did not focus");
                    var element = AutomationElement.FromHandle(source).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    var bounds = element.Current.BoundingRectangle; trigger.Enabled = true; uint clipboard = Native.GetClipboardSequenceNumber();
                    async Task SelectWithWheel()
                    {
                        Native.TestChord(new Hotkey(2, 0x24)); await Task.Delay(100);
                        Native.SetCursorPos((int)bounds.Left + 12, (int)bounds.Top + 12); Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(60);
                        Native.SetCursorPos((int)bounds.Left + 310, (int)bounds.Top + 140); await Task.Delay(60);
                        for (int i = 0; i < 4; i++) { Native.mouse_event(0x0800, 0, 0, unchecked((uint)-120), IntPtr.Zero); await Task.Delay(35); }
                        Native.SetCursorPos((int)bounds.Left + 311, (int)bounds.Top + 145); await Task.Delay(50); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
                        for (int i = 0; i < 65 && trigger.VisibleButton == null; i++) await Task.Delay(40);
                        Check(trigger.VisibleButton is { IsVisible: true }, "scroll selection lost button: " + trigger.State + ", UIA=" + SelectionReader.ProbeState + ", leftHeld=" + Native.Down(1) + ", focused=" + (Native.GetForegroundWindow() == source));
                        Check(trigger.VisibleButton!.SourceText?.Length > 100, "fixture did not select a long range");
                    }
                    await SelectWithWheel();
                    Check(calls == 0 && Native.GetClipboardSequenceNumber() == clipboard, "wheel-selection caused copy or premature translation");
                    Native.mouse_event(0x0800, 0, 0, 120, IntPtr.Zero); await Task.Delay(350); Check(trigger.VisibleButton == null, "ordinary scrolling no longer dismisses old button");
                    await SelectWithWheel(); string snapshot = trigger.VisibleButton!.SourceText!;
                    await HoverTest.ClickAsync((FrameworkElement)((Border)trigger.VisibleButton.Content).Child); await Task.Delay(100);
                    Check(calls == 1 && translated == snapshot && Native.GetForegroundWindow() == source && trigger.VisibleButton == null, "click did not translate selection once, preserve focus and close button");
                    observations["selection"] = new { selectedCharacters = snapshot.Length, copyBeforeClick = false, requestsBeforeClick = 0, requestsAfterClick = calls };
                }
                finally { Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); trigger.Enabled = false; if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            });
        }
        finally { Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground); }
        await File.WriteAllTextAsync(Path.Combine(output, "interaction-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, errors, observations, realCloudCalls = false, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 && errors.Count == 0 ? 0 : 1;
    }
}
