using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QuietTranslate;

// An isolated Edge profile and synthetic local page; never operates a personal browser tab.
internal static class TableSelectionTest
{
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var tests = new List<object>(); int failed = 0, calls = 0;
        var cursor = Native.Cursor(); var previous = Native.GetForegroundWindow();
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe");
        string page = Path.Combine(Path.GetFullPath(output), "table.html");
        await File.WriteAllTextAsync(page, "<!doctype html><html lang=zh-CN translate=no><meta charset=utf-8><meta name=google content=notranslate><title>QuietTranslate table fixture</title><style>body{font:18px Arial;margin:24px}table{border-collapse:collapse;width:750px}td{border:1px solid #abc;padding:12px}tr{height:54px}p{height:48px}</style><p>本地合成表格，仅用于拖选测试</p><table>" + string.Concat(Enumerable.Range(1, 28).Select(i => $"<tr><td>Row {i:00}</td><td><span>Sample {i:00}: select across cells and lines safely.</span></td></tr>")) + "</table><p>Outside table region</p>");
        using var process = Process.Start(new ProcessStartInfo(edge) { UseShellExecute = false, ArgumentList = { "--no-first-run", "--inprivate", "--new-window", "--force-renderer-accessibility", "--user-data-dir=" + Path.Combine(Path.GetFullPath(output), "profile"), new Uri(page).AbsoluteUri, "--window-size=900,780", "--window-position=30,30" } })!;
        using var trigger = new SelectionTrigger((_, _, text) => { calls++; if (string.IsNullOrWhiteSpace(text)) throw new Exception("Empty table snapshot translated"); }, () => new Settings(), () => false);
        Window? focus = null; IntPtr source = IntPtr.Zero;
        async Task Test(string name, Func<Task> run)
        {
            try { trigger.Hide(); await run(); tests.Add(new { name, status = "PASS" }); }
            catch (Exception ex)
            {
                var active = Native.GetForegroundWindow(); var className = new System.Text.StringBuilder(80); Native.GetClassName(active, className, className.Capacity);
                failed++; tests.Add(new { name, status = "FAIL", error = ex.Message, state = trigger.State, probe = SelectionReader.ProbeState, sourceActive = active == source, activeClass = className.ToString() });
            }
        }
        try
        {
            for (int i = 0; i < 160; i++) { process.Refresh(); source = process.MainWindowHandle; if (source != IntPtr.Zero) break; await Task.Delay(50); }
            if (source == IntPtr.Zero) throw new Exception("Isolated Edge fixture did not open");
            Native.SetForegroundWindow(source); await Task.Delay(800); Native.TapKey(0x1B); await Task.Delay(200);
            var document = await Task.Run(() => AutomationElement.FromHandle(source).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
            if (document == null) throw new Exception("Fixture document unavailable");
            async Task<Rect> Cell(string name)
            {
                var r = await Task.Run(() => document.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))?.Current.BoundingRectangle);
                if (r == null || r.Value.IsEmpty) throw new Exception("Fixture cell not found: " + name);
                return r.Value;
            }
            var first = await Cell("Sample 01: select across cells and lines safely.");
            var fifth = await Cell("Sample 05: select across cells and lines safely.");
            var seventh = await Cell("Sample 07: select across cells and lines safely.");
            uint clipboard = Native.GetClipboardSequenceNumber(); trigger.Enabled = true;
            async Task Drag(Rect from, Rect to, bool moveAway = false)
            {
                var start = new Native.Point((int)from.Left + 2, (int)from.Top + 8);
                var end = new Native.Point((int)to.Left + 180, (int)to.Top + 8);
                Native.SetCursorPos(start.X, start.Y);
                Native.GetWindowThreadProcessId(Native.WindowFromPoint(start), out uint hit);
                Native.GetWindowThreadProcessId(source, out uint expected);
                if (hit != expected) throw new Exception("Fixture covered; refusing to drag another app");
                Native.mouse_event(2, 0, 0, 0, IntPtr.Zero);
                await DesktopProbe.UntilAsync(() => Native.Down(1), "Selection mouse-down not delivered");
                Native.SetCursorPos(end.X, end.Y); await Task.Delay(18);
                Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
                await DesktopProbe.UntilAsync(() => !Native.Down(1), "Selection mouse-up not delivered");
                if (moveAway) { await Task.Delay(15); Native.GetWindowRect(source, out var bounds); Native.SetCursorPos(bounds.Right - 30, bounds.Top + 16); }
            }
            async Task Button(string contains)
            {
                for (int i = 0; i < 100 && trigger.VisibleButton == null; i++) await Task.Delay(30);
                if (trigger.VisibleButton?.SourceText is not string text || string.IsNullOrWhiteSpace(text) || !text.Contains(contains)) throw new Exception("Latest table selection button missing or stale");
                if (Native.GetClipboardSequenceNumber() != clipboard) throw new Exception("Probing table changed clipboard");
            }
            await Test("First drag activates an unfocused browser and selects across rows", async () =>
            {
                focus = new Window { Title = "QuietTranslate selection focus fixture", Width = 180, Height = 90, Left = 950, Top = 60, Content = new TextBox { Text = "Synthetic focus" } };
                focus.Show(); focus.Activate(); await Task.Delay(120);
                if (Native.GetForegroundWindow() == source) throw new Exception("Inactive-window precondition failed");
                await Drag(first, fifth); await Button("Sample 03"); focus.Close(); focus = null;
            });
            await Test("Fast table selection survives moving pointer to browser chrome", async () =>
            {
                focus?.Close(); focus = null; Native.SetForegroundWindow(source); await Task.Delay(100);
                await Drag(first, seventh, true); await Button("Sample 05");
            });
            await Test("Rapid multi-row gestures show latest range and translate once", async () =>
            {
                for (int i = 0; i < 8; i++) { await Drag(i % 2 == 0 ? first : fifth, i % 2 == 0 ? fifth : seventh); await Task.Delay(20); }
                await Drag(fifth, seventh, true); await Button("");
                var expected = await Task.Run(() => ((TextPattern)document.GetCurrentPattern(TextPattern.Pattern)).GetSelection()[0].GetText(8001));
                if (trigger.VisibleButton!.SourceText != expected) throw new Exception("Button text differs from current browser selection");
                int before = calls;
                await HoverTest.ClickAsync((FrameworkElement)((Border)trigger.VisibleButton.Content).Child); await Task.Delay(100);
                if (calls != before + 1 || trigger.VisibleButton != null || Native.GetForegroundWindow() != source) throw new Exception("Click did not translate once and preserve focus");
            });
            await Test("Held table selection survives wheel scrolling and mouse release", async () =>
            {
                Native.TestChord(new Hotkey(2, 0x24)); await Task.Delay(100);
                Native.SetCursorPos((int)first.Left + 2, (int)first.Top + 8);
                Native.GetWindowThreadProcessId(Native.WindowFromPoint(Native.Cursor()), out uint hit);
                if (hit != process.Id) throw new Exception("Table fixture covered before wheel drag");
                Native.mouse_event(2, 0, 0, 0, IntPtr.Zero);
                await DesktopProbe.UntilAsync(() => Native.Down(1), "Wheel-drag mouse-down not delivered");
                Native.SetCursorPos((int)seventh.Left + 180, (int)seventh.Top + 8);
                for (int i = 0; i < 4; i++) { Native.mouse_event(0x0800, 0, 0, unchecked((uint)-120), IntPtr.Zero); await Task.Delay(40); }
                if (trigger.State != "drag scrolling") throw new Exception("Held selection wheel not observed");
                Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
                await DesktopProbe.UntilAsync(() => !Native.Down(1), "Wheel-drag mouse-up not delivered");
                await Button("Sample 03");
            });
        }
        catch (Exception ex) { failed++; tests.Add(new { name = "Browser fixture setup", status = "FAIL", error = ex.Message }); }
        finally
        {
            Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); trigger.Enabled = false; focus?.Close();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(previous);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "table-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, realCloudCalls = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
