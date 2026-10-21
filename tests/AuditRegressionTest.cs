using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuietTranslate;

internal static class AuditRegressionTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T item && item.Name == name) return item;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) { try { return Find<T>(node, name); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(name);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<string>> Respond = _ => Task.FromResult("{}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await Respond(request), Encoding.UTF8, "application/json") };
    }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failures = 0;
        async Task Test(string name, Func<Task> run)
        { try { await run(); results.Add(new { name, passed = true }); } catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message }); } }
        await Test("Defaults, legacy preferences and test persistence cannot overwrite the real profile", () =>
        {
            string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietTranslate", "settings.json");
            Check(!Path.GetFullPath(Settings.FilePath).Equals(real, StringComparison.OrdinalIgnoreCase) && Credentials.Prefix.StartsWith("QuietTranslate.Tests/", StringComparison.Ordinal), "Test host is using the real profile");
            var defaults = new Settings(); Check(defaults.SelectionButtonEnabled && !defaults.Speech.ControlsHidden, "Requested defaults missing");
            defaults.Save(); Check(File.Exists(Settings.FilePath) && Settings.Load().SelectionButtonEnabled, "Isolated persistence failed");
            File.WriteAllText(Settings.FilePath, "{\"Speech\":{}}"); Check(!Settings.Load().Speech.ControlsHidden, "Legacy missing preference enabled movie mode");
            File.WriteAllText(Settings.FilePath, "{\"Speech\":{\"ControlsHidden\":true}}"); Check(Settings.Load().Speech.ControlsHidden, "Explicit movie preference overwritten");
            bool blocked = false;
            try { Credentials.Set("QuietTranslate/Reserved-Isolation-Check", "synthetic-must-not-be-written"); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "Test host can write production credentials"); return Task.CompletedTask;
        });
        await Test("Unchecking key deletion after a failed save preserves the stored key", () =>
        {
            var settings = new Settings(); string target = settings.Speech.SecretTarget; int attempts = 0;
            Credentials.Set(target, "synthetic-preserved-key");
            var window = new SettingsWindow(settings, _ => { if (++attempts == 1) throw new IOException("synthetic save failure"); });
            try
            {
                var delete = Find<CheckBox>(window, "SpeechDeleteKey"); var save = Find<Button>(window, "SaveSettings");
                delete.IsChecked = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Credentials.Get(target) == "synthetic-preserved-key", "Failed save deleted key");
                delete.IsChecked = false; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(attempts == 2 && Credentials.Get(target) == "synthetic-preserved-key", "Stale deletion intent deleted the saved key");
            }
            finally { window.Close(); Credentials.Set(target, ""); }
            return Task.CompletedTask;
        });
        await Test("Caption reflow preserves unread characters instead of obsolete line indices", () =>
        {
            var board = new CueRows(); var cue = new SpeechCue(1, "", "0123456789abcdefghijklmnopqrst");
            Check(board.Layout(new[] { cue }, 0, 5, true, (s, _) => s.Length <= 5, 5).Length == 3, "Narrow layout missing");
            var rows = board.Layout(new[] { cue }, .1, 5, true, (s, _) => s.Length <= 10, 10);
            Check(string.Concat(rows.Select(r => r.Text)) == cue.Translation[15..], "Wider layout hid text or resurrected consumed text");
            rows = board.Layout(new[] { cue }, .2, 5, true, (s, _) => s.Length <= 20, 20);
            Check(rows.Length == 1 && rows[0].Text == cue.Translation[15..], "Smaller font lost remaining text");
            board.InvalidateMeasurements();
            rows = board.Layout(new[] { cue }, .3, 5, true, (s, _) => s.Length <= 10, 10);
            Check(string.Concat(rows.Select(r => r.Text)) == cue.Translation[15..], "Font family/weight reset consumed progress");
            return Task.CompletedTask;
        });
        await Test("Settings failure and partial credential failure restore all affected keys", () =>
        {
            var store = new Dictionary<string, string> { ["one"] = "old-one", ["two"] = "old-two" };
            var changes = new Dictionary<string, string> { ["one"] = "new-one", ["two"] = "" };
            bool failed = false;
            try { Credentials.SaveChanges(changes, () => throw new IOException("synthetic settings failure"), name => store.GetValueOrDefault(name, ""), (name, value) => store[name] = value); }
            catch (IOException) { failed = true; }
            Check(failed && store["one"] == "old-one" && store["two"] == "old-two", "Settings failure persisted a key change");
            failed = false; bool injected = false;
            try { Credentials.SaveChanges(changes, () => throw new Exception("Commit should not run"), name => store[name], (name, value) => { store[name] = value; if (name == "two" && !injected) { injected = true; throw new IOException("synthetic credential failure"); } }); }
            catch (IOException) { failed = true; }
            Check(failed && store["one"] == "old-one" && store["two"] == "old-two", "Partial write was not rolled back");
            Credentials.SaveChanges(changes, () => { }, name => store[name], (name, value) => store[name] = value);
            Check(store["one"] == "new-one" && store["two"] == "", "Successful commit lost keys");
            return Task.CompletedTask;
        });
        await Test("Full encrypted history skips processing until explicitly cleared", () =>
        {
            using var history = new SessionHistory(Path.Combine(output, "quota"), 1024);
            var record = new SpeechRecord(DateTimeOffset.Now, "synthetic", "zh-Hans", "", "测试字幕");
            for (int i = 0; i < 20 && !history.Full; i++) history.Append(record);
            Check(history.Full, "Quota was not reached"); long bytes = history.Bytes;
            Check(history.Upsert(record with { Translation = new string('长', 150000) }) == -1 && history.Bytes == bytes, "Full history still processed a record");
            history.Clear(); Check(!history.Full && history.Append(record), "Clear did not re-enable recording"); return Task.CompletedTask;
        });
        await Test("Vision OCR rejects incomplete results and sends the configured output budget", async () =>
        {
            foreach (string reason in new[] { "length", "content_filter", "stop" })
            {
                var handler = new Handler { Respond = async request =>
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Check(body.RootElement.GetProperty("max_tokens").GetInt32() == 8192, "OCR output budget ignored");
                    return JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "Synthetic OCR" }, finish_reason = reason } } });
                } };
                using var ocr = new OcrService(handler); using var image = new System.Drawing.Bitmap(16, 16);
                bool rejected = false;
                try { await ocr.RecognizeAsync(OcrProvider.VisionAI, new ServiceSettings { Endpoint = "https://example.invalid/v1", Model = "synthetic-vision", MaxOutputTokens = 8192 }, "synthetic-key", image, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected == (reason != "stop"), "Incomplete OCR accepted, or complete OCR rejected");
            }
        });
        await Test("Text AI uses the selected output budget without splitting or retrying", async () =>
        {
            int calls = 0; var handler = new Handler { Respond = async request =>
            {
                calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("max_tokens").GetInt32() == 8192, "Translation output budget ignored");
                return "{\"choices\":[{\"message\":{\"content\":\"synthetic translation\"},\"finish_reason\":\"stop\"}]}";
            } };
            using var translator = new TranslationService(handler);
            await translator.TranslateAsync(Provider.CustomAI, new ServiceSettings { Endpoint = "https://example.invalid/v1", Model = "synthetic", MaxOutputTokens = 8192 }, "synthetic-key", "Hello", _ => { }, CancellationToken.None);
            Check(calls == 1, "Translation sent extra requests");
        });
        await Test("Late model list cannot cross provider or endpoint boundaries", async () =>
        {
            foreach (bool switchProvider in new[] { true, false })
            {
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler { Respond = _ => reply.Task };
            var settings = new Settings(); settings.Current.Endpoint = "https://models.example.invalid/v1"; settings.Current.AllowAnonymous = true;
            var editor = new ServiceEditor(settings, false, new(), handler);
            var read = Find<Button>(editor, "ReadServiceModels"); read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (switchProvider) Find<ComboBox>(editor, "ServiceChoice").SelectedIndex = (int)Provider.Qwen;
            else Find<TextBox>(editor, "ServiceEndpoint").Text = "https://example.invalid/new-gateway";
            reply.SetResult("{\"data\":[{\"id\":\"wrong-provider-model\"}]}");
            for (int i = 0; i < 100 && !read.IsEnabled; i++) await Task.Delay(10);
            Check(read.IsEnabled && Find<ComboBox>(editor, "ServiceModels").Items.Count == 0, "Stale provider models shown");
            }
        });
        await Test("An unresponsive clipboard owner is rejected without blocking or modifying the clipboard", async () =>
        {
            var ready = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                IntPtr window = DesktopProbe.ClipboardWindow(); ready.TrySetResult(window);
                try { release.Wait(3000); } finally { DesktopProbe.DestroyWindow(window); done.TrySetResult(); }
            }) { IsBackground = true }; worker.Start();
            IntPtr owner = await ready.Task; uint before = Native.GetClipboardSequenceNumber();
            var watch = System.Diagnostics.Stopwatch.StartNew(); bool rejected = false;
            try
            {
                await (Task)typeof(SelectionReader).GetMethod("EnsureResponsiveAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { owner, CancellationToken.None })!;
            }
            catch (InvalidOperationException) { rejected = true; }
            finally { release.Set(); await done.Task; }
            Check(owner != IntPtr.Zero && rejected && watch.ElapsedMilliseconds < 600 && Native.GetClipboardSequenceNumber() == before, "Unresponsive owner was not bounded or clipboard changed");
        });
        await Test("Overlay movement preserves edited font and colors while untouched fields synchronize", () =>
        {
            var options = new Settings(); var window = new SettingsWindow(options, _ => { });
            try
            {
                Find<TextBox>(window, "SpeechFontSize").Text = "32";
                Find<ComboBox>(window, "SpeechFontWeight").SelectedIndex = 0;
                Find<TextBox>(window, "SpeechBackground").Text = "#123456";
                var changed = options.Speech.Copy(); changed.WindowLeft = 200; changed.Opacity = .35;
                window.SyncSpeechAppearance(changed); changed.WindowLeft = 220; window.SyncSpeechAppearance(changed);
                Check(Find<TextBox>(window, "SpeechFontSize").Text == "32" && Find<ComboBox>(window, "SpeechFontWeight").SelectedIndex == 0, "Edited font overwritten");
                Check(Find<TextBox>(window, "SpeechBackground").Text == "#123456", "Edited color overwritten");
                Check(Find<TextBox>(window, "SpeechOpacity").Text == "35", "Untouched opacity failed to synchronize");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
        await File.WriteAllTextAsync(Path.Combine(output, "audit-regression.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, results, realCloudCalls = false, userKeysWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
