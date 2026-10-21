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
    internal static string ExternalSample => string.Concat(Enumerable.Repeat("Synthetic clipboard interoperability 日本語 中文 😀\n", 32));
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    private static async Task Until(Func<bool> condition) { for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(25); Check(condition(), "timed out waiting for copy result"); }
    private static async Task<Task> HoldAsync(int ms, Task? release = null)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            bool open = false;
            var owner = DesktopProbe.ClipboardWindow();
            try { if (owner != IntPtr.Zero) for (int i = 0; i < 40 && !(open = OpenClipboard(owner)); i++) Thread.Sleep(20); ready.TrySetResult(open); if (open) { if (release == null) Thread.Sleep(ms); else release.Wait(ms); } }
            finally { if (open) CloseClipboard(); if (owner != IntPtr.Zero) DesktopProbe.DestroyWindow(owner); released.TrySetResult(); }
        }) { IsBackground = true }.Start();
        Check(await ready.Task, "could not acquire synthetic clipboard lock"); return released.Task;
    }
    internal static async Task<int> RunAsync(App app, string output, bool baselineOnly = false)
    {
        Directory.CreateDirectory(output); var tests = new List<object>(); var observations = new Dictionary<string, object>(); var errors = new List<string>(); int failed = 0;
        var foreground = Native.GetForegroundWindow(); var cursor = Native.Cursor(); uint owned = 0; app.TestException = e => errors.Add(e.GetType().Name);
        if (SelectionReader.OleGetClipboard(out var original) != 0) throw new Exception("Clipboard snapshot unavailable");
        async Task Remember(string expected, Action? blocked = null)
        {
            var read = await DesktopProbe.ReadExpectedAsync(expected, blocked); owned = read.sequence;
            observations["readback-" + observations.Count] = new { read.attempts };
        }
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
                        await released; await Until(() => !ClipboardWriter.IsBusy && (copy.ToolTip as string) != "正在复制…");
                        observations[menu ? "bubbleMenuResult" : "bubbleButtonResult"] = new { tooltip = copy.ToolTip as string, busy = ClipboardWriter.IsBusy, visible = w.IsVisible, reason = w.DismissReason };
                        Check((copy.ToolTip as string) == "已复制", "copy not successful"); await Remember(text);
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
                            await Until(() => !ClipboardWriter.IsBusy); await Task.Delay(30); await Remember(expected); observations["manual-" + name] = new { handlerMs = handler, responsive }; Check(handler < 100, "manual copy blocked UI");
                            Check(HoverTest.Find<TextBlock>(w, "ManualStatus")!.Text.StartsWith("已复制"), "copy feedback missing");
                        }
                    }
                    finally { w.Close(); }
                });
                await Test("Settings fields copy through commands, keyboard and menus without blocking", async () =>
                {
                    var settings = new Settings(); string scope = Guid.NewGuid().ToString("N");
                    foreach (var s in settings.Services.Values.Concat(settings.OcrServices.Values)) { s.Endpoint = "https://example.invalid/copy-" + scope; s.CredentialId = scope; }
                    int saves = 0; var w = new SettingsWindow(settings, _ => saves++) { Topmost = true };
                    try
                    {
                        w.Show(); w.Activate(); await Task.Delay(150);
                        HoverTest.Find<Button>(w, "ServiceTab")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
                        foreach (string name in new[] { "ServiceEndpoint", "ServiceModel", "ServicePrompt" })
                        foreach (string mode in new[] { "command", "keyboard", "menu" })
                        {
                            if (name == "ServicePrompt") { HoverTest.Find<Expander>(w, "ServicePromptOptions")!.IsExpanded = true; await Task.Delay(50); }
                            var field = HoverTest.Find<TextBox>(w, name)!; field.SelectAll(); field.BringIntoView(); field.Focus(); await Task.Delay(70);
                            string expected = field.SelectedText; MenuItem? item = null; ContextMenu? menu = null;
                            if (mode == "menu")
                            {
                                menu = new ContextMenu { PlacementTarget = field }; item = new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = field }; menu.Items.Add(item); menu.IsOpen = true; await Task.Delay(70);
                            }
                            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            var released = await HoldAsync(3000, releaseGate.Task); var watch = Stopwatch.StartNew();
                            try
                            {
                                if (mode == "command") ApplicationCommands.Copy.Execute(null, field);
                                else if (mode == "keyboard") Native.TestChord(new Hotkey(2, 0x43));
                                else await HoverTest.ClickAsync(item!);
                                long injectionMs = watch.ElapsedMilliseconds;
                                await Task.Delay(60); long responseMs = watch.ElapsedMilliseconds;
                                bool responsive = !released.IsCompleted;
                                observations[name + "-" + mode] = new { responseMs, injectionMs, responsive };
                                Check(responseMs < (mode == "command" ? 160 : 260) && responsive, "settings copy blocked UI: " + name + "/" + mode + ", " + responseMs + " ms");
                            }
                            finally { releaseGate.TrySetResult(); await released; }
                            await Until(() => !ClipboardWriter.IsBusy); await Task.Delay(30); await Remember(expected);
                            Check(HoverTest.Find<TextBlock>(w, "SettingsNotice")!.Text == "已复制", "settings copy feedback missing");
                            if (menu != null) menu.IsOpen = false;
                        }
                        var endpoint = HoverTest.Find<TextBox>(w, "ServiceEndpoint")!;
                        var release = await HoldAsync(1400); ApplicationCommands.Copy.Execute(null, endpoint);
                        await Until(() => !ClipboardWriter.IsBusy && HoverTest.Find<TextBlock>(w, "SettingsNotice")!.Text != "正在复制…");
                        Check(HoverTest.Find<TextBlock>(w, "SettingsNotice")!.Text.Contains("被占用"), "settings copy failure reported success"); await release;
                        release = await HoldAsync(300); ApplicationCommands.Copy.Execute(null, endpoint); w.Close(); await release; await Until(() => !ClipboardWriter.IsBusy); await Remember(endpoint.SelectedText);
                        Check(saves == 0 && errors.Count == 0, "copy saved settings or accessed closed UI");
                    }
                    finally { w.Close(); }
                });
                await Test("Busy failure is honest, retry works, and closing during copy is safe", async () =>
                {
                    var w = new BubbleWindow(anchor, IntPtr.Zero, .92); w.SetText("Synthetic retry", "synthetic", true); w.Show(); await Task.Delay(180);
                    try
                    {
                        var copy = HoverTest.Find<Button>(w, "BubbleCopy")!; var released = await HoldAsync(1400);
                        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !ClipboardWriter.IsBusy && (copy.ToolTip as string) != "正在复制…"); Check((copy.ToolTip as string)?.Contains("被占用") == true, "failed copy reported success");
                        await released; copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => (copy.ToolTip as string) == "已复制"); await Remember("Synthetic retry");
                        released = await HoldAsync(350); copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); w.Close(); await released; await Until(() => !ClipboardWriter.IsBusy); await Task.Delay(40); await Remember("Synthetic retry");
                        Check(!w.IsVisible && errors.Count == 0, "late copy callback reopened or touched closed window");
                    }
                    finally { w.Close(); }
                });
                await Test("Readback waits for a known holder; wrong clipboard values still fail", async () =>
                {
                    Check(await ClipboardWriter.CopyAsync("Synthetic readback"), "initial write failed");
                    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var released = await HoldAsync(3000, release.Task);
                    var read = Remember("Synthetic readback", () => blocked.TrySetResult());
                    try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2)); observations["controlledReadbackContention"] = true; }
                    finally { release.TrySetResult(); await released; }
                    await read;
                    bool wrongFailed = false;
                    try { await Remember("Synthetic wrong value"); } catch (Exception ex) when (ex.Message == "copied text mismatch") { wrongFailed = true; }
                    Check(wrongFailed, "readback retries must not hide wrong output");
                    Check(await ClipboardWriter.CopyAsync(ExternalSample), "external reader sample write failed"); await Remember(ExternalSample);
                    for (int reader = 0; reader < 3; reader++)
                    {
                        string report = Path.Combine(output, "external-wpf-reader-" + reader + ".json");
                        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "--clipboard-read-check", report } })!;
                        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(child.ExitCode == 0, "independent WPF clipboard reader failed"); }
                        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
                    }
                    observations["independentWpfReaders"] = 3;
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
