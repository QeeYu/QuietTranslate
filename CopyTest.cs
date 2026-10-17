using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuietTranslate;

internal static class CopyTest
{
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    private static async Task Until(Func<bool> condition) { for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(25); Check(condition(), "timed out waiting for copy result"); }
    private static async Task<Task> HoldAsync(int ms)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            bool open = false;
            try { for (int i = 0; i < 40 && !(open = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(20); ready.TrySetResult(open); if (open) Thread.Sleep(ms); }
            finally { if (open) CloseClipboard(); released.TrySetResult(); }
        }) { IsBackground = true }.Start();
        Check(await ready.Task, "could not acquire synthetic clipboard lock"); return released.Task;
    }
    internal static async Task<int> RunAsync(App app, string output, bool baselineOnly = false)
    {
        Directory.CreateDirectory(output); var tests = new List<object>(); var observations = new Dictionary<string, object>(); var errors = new List<string>(); int failed = 0;
        var foreground = Native.GetForegroundWindow(); var cursor = Native.Cursor(); uint owned = 0; app.TestException = e => errors.Add(e.GetType().Name);
        if (SelectionReader.OleGetClipboard(out var original) != 0) throw new Exception("Clipboard snapshot unavailable");
        void Remember(string expected) { uint before = Native.GetClipboardSequenceNumber(); Check(Clipboard.GetText() == expected, "copied text mismatch"); uint after = Native.GetClipboardSequenceNumber(); if (before == after) owned = after; }
        async Task Test(string name, Func<Task> action) { try { await action(); tests.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; tests.Add(new { name, status = "FAIL", error = ex.Message }); } }
        var work = Native.WorkArea(cursor); var anchor = new Native.Rect { Left = work.Left + 250, Right = work.Left + 250, Top = work.Top + 130, Bottom = work.Top + 130 };
        try
        {
            await Test("Bubble toolbar and menu copy do not block the UI while clipboard is locked", async () =>
            {
                var w = new BubbleWindow(anchor, IntPtr.Zero, .92);
                string text = string.Concat(Enumerable.Repeat("合成 Unicode 日本語 😀 clipboard\n", 1000));
                try
                {
                    w.SetText(text, "synthetic", true); w.Show(); await Task.Delay(240);
                    var copy = Buttons(w.View).First(b => (b.ToolTip as string) == "复制译文");
                    foreach (bool menu in new[] { false, true })
                    {
                        var released = await HoldAsync(450); var watch = Stopwatch.StartNew();
                        if (menu) ((MenuItem)w.View.ActionsMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); else copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        long handler = watch.ElapsedMilliseconds; await Task.Delay(60); bool uiRanBeforeRelease = !released.IsCompleted;
                        observations[menu ? "bubbleMenu" : "bubbleButton"] = new { handlerMs = handler, uiRanBeforeRelease };
                        await released; await Until(() => !ClipboardWriter.IsBusy); await Task.Delay(80);
                        observations[menu ? "bubbleMenuResult" : "bubbleButtonResult"] = new { tooltip = copy.ToolTip as string, busy = ClipboardWriter.IsBusy, visible = w.IsVisible, reason = w.DismissReason };
                        Check((copy.ToolTip as string) == "已复制", "copy not successful"); Remember(text);
                        Check(handler < 100, "copy stalled UI during clipboard contention: " + handler + "ms");
                        Check(w.IsVisible && Native.GetForegroundWindow() == foreground, "copy closed bubble or changed foreground focus");
                    }
                }
                finally { w.Close(); }
            });
            if (!baselineOnly)
            {
                await Test("Manual input/output buttons and selected-text copy use the responsive path", async () =>
                {
                    var options = new Settings { AutoTranslateDelay = 2000, ManualSource = "en", ManualTarget = "en" };
                    var w = new ManualWindow(() => options, (_, _, _) => { });
                    try
                    {
                        w.Show(); w.Input.Text = "Synthetic 日本語 😀 copy sample"; await w.TranslateNowAsync();
                        foreach (string name in new[] { "CopyInput", "CopyOutput", "selection" })
                        {
                            var released = await HoldAsync(350); var watch = Stopwatch.StartNew(); string expected;
                            if (name == "selection") { w.Input.Select(0, 9); expected = w.Input.SelectedText; ApplicationCommands.Copy.Execute(null, w.Input); }
                            else { expected = name == "CopyInput" ? w.Input.Text : w.Output.Text; HoverTest.Find<Button>(w, name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                            long handler = watch.ElapsedMilliseconds; await Task.Delay(50); bool responsive = !released.IsCompleted; await released;
                            await Until(() => !ClipboardWriter.IsBusy); await Task.Delay(30); Remember(expected); observations["manual-" + name] = new { handlerMs = handler, responsive }; Check(handler < 100, "manual copy blocked UI");
                            Check(HoverTest.Find<TextBlock>(w, "ManualStatus")!.Text.StartsWith("已复制"), "copy feedback missing");
                        }
                    }
                    finally { w.Close(); }
                });
                await Test("Busy failure is honest, retry works, and closing during copy is safe", async () =>
                {
                    var w = new BubbleWindow(anchor, IntPtr.Zero, .92); w.SetText("Synthetic retry", "synthetic", true); w.Show(); await Task.Delay(180);
                    try
                    {
                        var copy = HoverTest.Find<Button>(w, "BubbleCopy")!; var released = await HoldAsync(1400);
                        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !ClipboardWriter.IsBusy); Check((copy.ToolTip as string)?.Contains("被占用") == true, "failed copy reported success");
                        await released; copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => (copy.ToolTip as string) == "已复制"); Remember("Synthetic retry");
                        released = await HoldAsync(350); copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); w.Close(); await released; await Until(() => !ClipboardWriter.IsBusy); await Task.Delay(40); Remember("Synthetic retry");
                        Check(!w.IsVisible && errors.Count == 0, "late copy callback reopened or touched closed window");
                    }
                    finally { w.Close(); }
                });
                await Test("Repeated launches reuse the primary; close-to-tray and tray exit leave no process", async () =>
                {
                    for (int pass = 0; pass < 3; pass++)
                    {
                        string token = Guid.NewGuid().ToString("N"), folder = Path.Combine(output, "instance-" + pass); Directory.CreateDirectory(folder);
                        Process Launch() => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "--instance-fixture", token, folder } })!;
                        using var primary = Launch();
                        try
                        {
                            await Until(() => File.Exists(Path.Combine(folder, "ready.json"))); using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "ready.json")));
                            Check(ready.RootElement.GetProperty("inputStack").GetString() == "PointerLogic", "legacy WISP input stack still active");
                            using var closeWindow = EventWaitHandle.OpenExisting("Local\\QuietTranslate.TestClose." + token); closeWindow.Set(); await Until(() => File.Exists(Path.Combine(folder, "closed.json"))); Check(!primary.HasExited, "closing settings should keep tray running");
                            using (var duplicate = Launch()) { Check(await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ContinueWith(t => t.IsCompletedSuccessfully), "duplicate launch did not exit promptly"); Check(duplicate.ExitCode == 0, "duplicate launch failed"); }
                            await Task.Delay(140); primary.Refresh(); Check(!primary.HasExited && primary.MainWindowHandle != IntPtr.Zero, "duplicate launch did not reopen primary settings");
                            using var exit = EventWaitHandle.OpenExisting("Local\\QuietTranslate.TestExit." + token); exit.Set();
                            await primary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); Check(primary.ExitCode == 0, "tray exit failed");
                        }
                        finally { if (!primary.HasExited) { primary.Kill(); await primary.WaitForExitAsync(); } }
                    }
                    observations["instanceCycles"] = 3;
                });
            }
        }
        finally
        {
            if (owned != 0 && Native.GetClipboardSequenceNumber() == owned)
                for (int i = 0; i < 8; i++) { if (SelectionReader.OleSetClipboard(original) == 0) { SelectionReader.OleFlushClipboard(); break; } await Task.Delay(25); }
            Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "copy-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, observations, errors, realCloudCalls = false, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 && errors.Count == 0 ? 0 : 1;
    }
    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button b) yield return b;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Buttons(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
