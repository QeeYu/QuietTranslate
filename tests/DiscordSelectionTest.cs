using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuietTranslate;

// Only synthetic text. The renamed child apphost exercises the real Discord process policy.
internal static class DiscordSelectionTest
{
    private static void Focus(IntPtr window) => DesktopProbe.Focus(window);
    private const string Body = "UIA_METADATA_START timestamp 11:08\nFirst paragraph: 回复 / 更多 / 点击反应 are intentional words. 日本語 😀\nSecond paragraph: keep this selected sentence intact.\nUIA_METADATA_END reaction controls and hidden actions";
    private sealed record State(int StartX, int StartY, int EndX, int EndY, int AltX, int AltY, int SelectionStart, int SelectionLength, int MouseDowns, int MouseUps);
    private sealed class Field(string mode) : TextBox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, mode);
    }
    private sealed class Peer(TextBox owner, string mode) : TextBoxAutomationPeer(owner)
    {
        public override object GetPattern(PatternInterface pattern) => pattern == PatternInterface.Text && mode != "normal"
            ? new Ranges((ITextProvider)base.GetPattern(pattern), mode) : base.GetPattern(pattern);
    }
    private sealed class Ranges(ITextProvider inner, string mode) : ITextProvider
    {
        public ITextRangeProvider[] GetSelection() => mode == "missing" ? Array.Empty<ITextRangeProvider>() : new[] { inner.DocumentRange };
        public ITextRangeProvider DocumentRange => inner.DocumentRange;
        public SupportedTextSelection SupportedTextSelection => inner.SupportedTextSelection;
        public ITextRangeProvider[] GetVisibleRanges() => inner.GetVisibleRanges();
        public ITextRangeProvider RangeFromChild(IRawElementProviderSimple child) => inner.RangeFromChild(child);
        public ITextRangeProvider RangeFromPoint(Point point) => inner.RangeFromPoint(point);
    }
    internal static void OpenFixture(App app, string output, string mode)
    {
        Directory.CreateDirectory(output); int downs = 0, ups = 0;
        app.TestException = ex => { File.WriteAllText(Path.Combine(output, "fixture-error.txt"), ex.ToString()); app.Shutdown(3); };
        var field = new Field(mode) { Text = Body, FontSize = 18, AcceptsReturn = true, Margin = new Thickness(20), TextWrapping = TextWrapping.NoWrap };
        var window = new Window { Title = "QuietTranslate synthetic selection fixture", Content = field, Width = 810, Height = 350, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        void Write()
        {
            if (!field.IsLoaded) return;
            Point At(int index) { var rect = field.GetRectFromCharacterIndex(index); return field.PointToScreen(new Point(rect.Left + 1, rect.Top + rect.Height / 2)); }
            var start = At(Body.IndexOf("First", StringComparison.Ordinal)); var end = At(Body.IndexOf("\nUIA_METADATA_END", StringComparison.Ordinal)); var alt = At(Body.IndexOf("Second", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(output, "state.tmp"), JsonSerializer.Serialize(new State((int)start.X, (int)start.Y, (int)end.X, (int)end.Y, (int)alt.X, (int)alt.Y, field.SelectionStart, field.SelectionLength, downs, ups)));
            File.Move(Path.Combine(output, "state.tmp"), Path.Combine(output, "state.json"), true);
        }
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg is 0x201 or 0x203) field.Dispatcher.BeginInvoke(new Action(() => { downs++; Write(); }), DispatcherPriority.Background);
            if (msg == 0x202) field.Dispatcher.BeginInvoke(new Action(() => { ups++; Write(); }), DispatcherPriority.Background);
            return IntPtr.Zero;
        });
        var stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) }; stateTimer.Tick += (_, _) => Write();
        window.Loaded += (_, _) =>
        {
            field.Focus();
            if (mode != "missing") { int start = Body.IndexOf("First", StringComparison.Ordinal); field.Select(start, Body.IndexOf("\nUIA_METADATA_END", StringComparison.Ordinal) - start); }
            Write(); stateTimer.Start();
        };
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) }; safety.Tick += (_, _) => app.Shutdown(2);
        window.Closed += (_, _) => { safety.Stop(); stateTimer.Stop(); app.Shutdown(); }; window.Show(); DesktopProbe.ShowFixture(window); safety.Start();
    }
    private sealed class Handler(Func<string> expected) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() != expected()) throw new Exception("API received text outside the actual selection");
            Calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"合成译文，保持不变。\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") };
        }
    }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var tests = new List<object>(); int failed = 0;
        void Stage(string value) => File.WriteAllText(Path.Combine(output, "stage.txt"), value);
        var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow(); uint owned = 0;
        using var originalNative = ClipboardSnapshot.Capture();
        if (SelectionReader.OleGetClipboard(out var original) != 0) throw new Exception("Clipboard snapshot unavailable");
        try
        {
            const string sentinel = "Synthetic selection clipboard sentinel";
            if (!await ClipboardWriter.CopyAsync(sentinel)) throw new Exception("Could not seed synthetic clipboard"); owned = Native.GetClipboardSequenceNumber();
            string host = Path.Combine(Path.GetFullPath(output), "host"); Directory.CreateDirectory(host);
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(host, Path.GetFileName(file)), true);
            File.Copy(Environment.ProcessPath!, Path.Combine(host, "Discord.exe"), true);
            File.Copy(Environment.ProcessPath!, Path.Combine(host, "SelectionFixture.exe"), true);
            foreach (string mode in new[] { "missing", "enriched", "normal" })
            {
                Stage(mode + ": start");
                string folder = Path.Combine(Path.GetFullPath(output), mode); Directory.CreateDirectory(folder);
                using var process = Process.Start(new ProcessStartInfo(Path.Combine(host, mode == "normal" ? "SelectionFixture.exe" : "Discord.exe")) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "--discord-selection-fixture", folder, mode } })!;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); string expected = "";
                var handler = new Handler(() => expected); using var service = new TranslationService(handler); BubbleWindow? bubble = null;
                using var trigger = new SelectionTrigger((source, anchor, snapshot) => _ = Translate(source, anchor, snapshot), () => new Settings(), () => false);
                async Task Translate(IntPtr source, Native.Rect anchor, string? snapshot)
                {
                    try
                    {
                        Stage(mode + ": translating");
                        if ((mode == "normal") != (snapshot != null)) throw new Exception("Selection trust policy changed");
                        var selected = snapshot == null ? await SelectionReader.GetAsync(source, 900, CancellationToken.None) : new SelectionReader.Selection(snapshot, anchor, false);
                        if (selected.Text != expected || selected.UsedClipboard != (mode != "normal")) throw new Exception("Actual selection was changed or UIA metadata leaked");
                        await DesktopProbe.ReadExpectedAsync(sentinel);
                        string result = await service.TranslateAsync(Provider.OpenAI, new ServiceSettings(), "synthetic-key", selected.Text, _ => { }, CancellationToken.None);
                        bubble = new BubbleWindow(anchor, source, .92); bubble.SetText(result, "synthetic", true); bubble.Show();
                        if (bubble.Translation != "合成译文，保持不变。") throw new Exception("Translation result was altered"); done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                }
                try
                {
                    if (!await ClipboardWriter.CopyAsync(sentinel)) throw new Exception("Could not seed this isolated case");
                    owned = Native.GetClipboardSequenceNumber();
                    string statePath = Path.Combine(folder, "state.json");
                    await DesktopProbe.UntilAsync(() => File.Exists(statePath), "Fixture not ready");
                    State Read() { using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return JsonSerializer.Deserialize<State>(stream)!; }
                    var state = Read(); IntPtr source = IntPtr.Zero;
                    await DesktopProbe.UntilAsync(() => { process.Refresh(); source = process.MainWindowHandle; return source != IntPtr.Zero; }, "Fixture window not ready");
                    Native.SetWindowPos(source, new IntPtr(-1), 0, 0, 0, 0, 0x43); Focus(source); await Task.Delay(160); state = Read();
                    Native.SetCursorPos(state.StartX, state.StartY); await Task.Delay(50);
                    Native.GetWindowThreadProcessId(Native.WindowFromPoint(Native.Cursor()), out uint initialHit);
                    if (initialHit != process.Id) throw new Exception("Fixture covered before activation");
                    source = Native.GetAncestor(Native.WindowFromPoint(Native.Cursor()), 2);
                    if (SelectionReader.PreferClipboard(source) != (mode != "normal")) throw new Exception("Source process policy mismatch");
                    Native.SetWindowPos(source, new IntPtr(-1), 0, 0, 0, 0, 0x43); Focus(source);
                    // Desktop runners may still have an earlier click captured by their launcher.
                    // Establish focus before enabling the trigger; never retry the tested selection.
                    for (int activation = 0; mode == "missing" && activation < 3; activation++)
                    {
                        Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(50);
                        Native.SetCursorPos(state.StartX, state.StartY); await Task.Delay(50);
                        Native.GetWindowThreadProcessId(Native.WindowFromPoint(Native.Cursor()), out uint target);
                        if (target != process.Id) throw new Exception("Fixture covered during activation");
                        Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await Task.Delay(50); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(50);
                        Focus(source);
                        if (Native.GetForegroundWindow() == source && !Native.Down(1)) break;
                    }
                    await DesktopProbe.UntilAsync(() => Native.GetForegroundWindow() == source && !Native.Down(1), "Fixture not active or mouse held");
                    await Task.Delay(120); trigger.Enabled = true;
                    uint before = Native.GetClipboardSequenceNumber(); int gestures = mode == "missing" ? 13 : 0;
                    if (mode != "missing")
                    {
                        // The provider fixture preselects text. Queue only this probe so a running
                        // installed translator cannot also show its button over the same gesture.
                        typeof(SelectionTrigger).GetMethod("Queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(trigger, new object[] { source, new Native.Point(state.EndX, state.EndY), false });
                    }
                    for (int i = 0; i < gestures; i++)
                    {
                        Focus(source);
                        var start = i % 2 == 0 ? new Native.Point(state.StartX, state.StartY) : new Native.Point(state.AltX, state.AltY);
                        Native.SetCursorPos(start.X, start.Y); await Task.Delay(35);
                        Native.GetWindowThreadProcessId(Native.WindowFromPoint(Native.Cursor()), out uint hit);
                        if (hit != process.Id) throw new Exception("Fixture covered; refusing to drag another app");
                        Native.mouse_event(2, 0, 0, 0, IntPtr.Zero); await DesktopProbe.UntilAsync(() => Native.Down(1), "Mouse-down not delivered");
                        await Task.Delay(25);
                        Native.SetCursorPos((start.X + state.EndX) / 2, (start.Y + state.EndY) / 2); await Task.Delay(20);
                        Native.SetCursorPos(state.EndX, state.EndY); await Task.Delay(40); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero);
                        await DesktopProbe.UntilAsync(() => !Native.Down(1), "Mouse-up not delivered");
                        await Task.Delay(25);
                    }
                    var watch = Stopwatch.StartNew();
                    await Task.Delay(80);
                    await DesktopProbe.UntilAsync(() => Read().SelectionLength > 0, "Fixture selection not committed");
                    state = Read(); expected = Body.Substring(state.SelectionStart, state.SelectionLength);
                    if (expected.Length == 0 || expected.Contains("UIA_METADATA")) throw new Exception("Fixture did not select only message text");
                    await DesktopProbe.UntilAsync(() => trigger.VisibleButton is { IsVisible: true }, "Selection button missing: " + trigger.State);
                    long buttonMs = watch.ElapsedMilliseconds;
                    if (mode == "missing" && buttonMs > 1200) throw new Exception("Discord fallback too slow after rapid gestures");
                    await Task.Delay(800);
                    if (trigger.VisibleButton is not { IsVisible: true } || handler.Calls != 0 || Native.GetClipboardSequenceNumber() != before) throw new Exception("Probe dismissed the button, copied or sent a premature request");
                    await HoverTest.ClickAsync((FrameworkElement)((Border)trigger.VisibleButton.Content).Child);
                    await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (handler.Calls != 1 || Native.GetForegroundWindow() != source) throw new Exception("Click translated twice or changed focus"); bubble?.Close();
                    if (mode == "enriched")
                    {
                        Stage(mode + ": rich clipboard");
                        var hotkey = await SelectionReader.GetAsync(source, 900, CancellationToken.None);
                        if (!hotkey.UsedClipboard || hotkey.Text != expected) throw new Exception("Hotkey selection still contains UIA metadata");
                        var rich = new DataObject(); rich.SetText(sentinel); rich.SetData(DataFormats.Html, "<b>synthetic clipboard</b>");
                        Clipboard.SetDataObject(rich, false); owned = Native.GetClipboardSequenceNumber();
                        var richSelection = await SelectionReader.GetAsync(source, 900, CancellationToken.None);
                        if (richSelection.Text != expected || (Clipboard.GetData(DataFormats.Html) as string)?.Contains("synthetic clipboard") != true)
                            throw new Exception("Rich clipboard restoration changed text or HTML");
                    }
                    var restored = await DesktopProbe.ReadExpectedAsync(sentinel); owned = restored.sequence;
                    if (mode == "normal")
                    {
                        Stage(mode + ": ordinary copy cancels probing");
                        Native.TestChord(Hotkey.Parse("Ctrl+A"));
                        await DesktopProbe.UntilAsync(() => trigger.VisibleButton is { IsVisible: true }, "Keyboard selection button missing");
                        if (Native.GetClipboardSequenceNumber() != owned) throw new Exception("Background probe modified clipboard");
                        Native.TestChord(Hotkey.Parse("Ctrl+C"));
                        await DesktopProbe.UntilAsync(() => trigger.VisibleButton == null, "Ordinary copy left background probing active");
                        owned = (await DesktopProbe.ReadExpectedAsync(Body)).sequence;
                        if (!await ClipboardWriter.CopyAsync(sentinel)) throw new Exception("Synthetic reset failed");
                        owned = Native.GetClipboardSequenceNumber();
                        Native.TapKey(0x27); await Task.Delay(80); // Collapse selection without copying.
                        bool emptyRejected = false;
                        try { await SelectionReader.ReadAsync(source, 180, CancellationToken.None); }
                        catch (InvalidOperationException) { emptyRejected = true; }
                        if (!emptyRejected || ClipboardWriter.IsBusy) throw new Exception("Failed selection leaked its reservation");
                        owned = (await DesktopProbe.ReadExpectedAsync(sentinel)).sequence;
                    }
                    Stage(mode + ": passed");
                    tests.Add(new { name = mode, status = "PASS", gestures, buttonMs, originalTextPreserved = true, clipboardRestored = true, apiCalls = handler.Calls });
                }
                catch (Exception ex) { failed++; tests.Add(new { name = mode, status = "FAIL", error = ex.Message, state = trigger.State, clipboard = DesktopProbe.ClipboardHolder() }); }
                finally { trigger.Enabled = false; bubble?.Close(); Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
            try
            {
            if (!await ClipboardWriter.CopyAsync(sentinel)) throw new Exception("Could not seed native snapshot check");
            Stage("ownership check");
            owned = Native.GetClipboardSequenceNumber();
            ClipboardSnapshot? captured = null;
            for (int attempt = 0; attempt < 80 && captured == null; attempt++) { captured = ClipboardSnapshot.Capture(); if (captured == null) await Task.Delay(25); }
            using var prior = captured ?? throw new Exception("Native text snapshot unavailable");
            uint priorSequence = Native.GetClipboardSequenceNumber();
            const string newer = "Synthetic newer clipboard copy";
            if (!await ClipboardWriter.CopyAsync(newer)) throw new Exception("Could not seed newer clipboard copy");
            owned = Native.GetClipboardSequenceNumber();
            if (!prior.Restore(priorSequence)) throw new Exception("Clipboard ownership check failed");
            await DesktopProbe.ReadExpectedAsync(newer);
            tests.Add(new { name = "preserve-newer-copy", status = "PASS" });
            }
            catch (Exception ex) { failed++; tests.Add(new { name = "preserve-newer-copy", status = "FAIL", error = ex.Message, clipboard = DesktopProbe.ClipboardHolder() }); }
        }
        finally
        {
            Stage("restore original");
            if (owned != 0 && Native.GetClipboardSequenceNumber() == owned)
                for (int retry = 0; retry < 8; retry++)
                {
                    if (originalNative != null) { if (originalNative.Restore(owned)) break; }
                    else if (SelectionReader.OleSetClipboard(original) == 0) { if (original != null) SelectionReader.OleFlushClipboard(); break; }
                    await Task.Delay(25);
                }
            if (original != null && System.Runtime.InteropServices.Marshal.IsComObject(original)) System.Runtime.InteropServices.Marshal.ReleaseComObject(original);
            Native.mouse_event(4, 0, 0, 0, IntPtr.Zero); Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "discord-tests.json"), JsonSerializer.Serialize(new { passed = tests.Count - failed, failed, tests, realDiscordAccountUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
