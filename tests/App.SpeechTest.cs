using System;
using System.IO;
using System.Threading.Tasks;

namespace QuietTranslate;

public sealed partial class App
{
    internal async Task CheckSpeechSwitchAsync(string output)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static async Task Until(Func<bool> value, string message) { for (int i = 0; i < 120 && !value(); i++) await Task.Delay(25); Check(value(), message); }
        await using var server = new SpeechTest.Server();
        var options = new Settings
        {
            SelectionButtonEnabled = false, SelectionHotkey = "Ctrl+Alt+Shift+7", ScreenshotHotkey = "Ctrl+Alt+Shift+8", PauseHotkey = "Ctrl+Alt+Shift+9", ManualHotkey = "Ctrl+Alt+Shift+0",
            Speech = new SpeechOptions { Endpoint = server.Address.AbsoluteUri, Hotkey = "Ctrl+Alt+Shift+F6" }
        };
        string secretTarget = options.Speech.SecretTarget; string root = Path.Combine(output, "app-session");
        bool leaveForExitCleanup = false;
        Credentials.Set(secretTarget, "synthetic-speech-key");
        try
        {
            Start(options, ".SpeechTest." + Guid.NewGuid().ToString("N")); await speechStartupCleanup;
            speech = new LiveSession(root, SpeechTest.SyntheticCapture);
            Check(!speech.Enabled && !Directory.Exists(root), "App startup created audio/history");
            settingsWindow!.ShowPage(6); HoverTest.Find<System.Windows.Controls.Button>(settingsWindow, "Page6")!.Focus(); await Task.Delay(100);
            int pressed = 0; hotkeys!.Pressed += id => pressed = id;
            Native.TestChord(Hotkey.Parse(options.Speech.Hotkey));
            try { await Until(() => speech.Enabled && speech.Records == 1 && speechWindow != null, "Master on shortcut failed"); }
            catch
            {
                File.WriteAllText(Path.Combine(output, "app-switch-state.json"), System.Text.Json.JsonSerializer.Serialize(new { pressed, recording, blocked, paused, speechStarting, speechClosing, enabled = speech.Enabled, records = speech.Records, status = speech.Status, connections = server.Connections, errors = server.Errors.ToArray() })); throw;
            }
            using (var self = System.Diagnostics.Process.GetCurrentProcess()) options.AppRules.Add(new AppRule { ProcessName = Path.GetFileNameWithoutExtension(self.ProcessName), Mode = AppMode.Paused });
            settingsWindow.Activate(); await Task.Delay(100); RefreshState(true); Check(blocked && speech.Enabled, "Foreground exclusion did not match, or stopped explicitly enabled speech");
            TogglePause(); await Until(() => !speechClosing && !speech.Enabled && speech.Records == 0, "Total pause inside an excluded app failed");
            TogglePause(); Check(!speech.Enabled, "Resume inside excluded app restarted speech");
            Check(Directory.GetFiles(root, "*.qth").Length == 0, "Master off kept file");
            options.AppRules.Clear(); RefreshState(true);
            await ToggleSpeechAsync(); await Until(() => speech.Records == 1, "Second use failed");
            int connections = server.Connections;
            Credentials.Set(secretTarget, "synthetic-replaced-speech-key");
            await ApplySpeechOptionsAsync(options.Speech.Copy(), keyChanged: true);
            await Until(() => server.Connections > connections && server.ReplacedKeySeen && speech.Records >= 1, "Key-only change did not reconnect with the saved replacement key");
            TogglePause(); await Until(() => !speechClosing && !speech.Enabled && speech.Records == 0, "Total pause did not clear speech");
            TogglePause(); await Task.Delay(100); Check(!speech.Enabled, "Resume restarted speech automatically");
            await ToggleSpeechAsync(); await Until(() => speech.Records == 1 && speechWindow != null, "Third use failed");
            speechWindow!.Close(); await Until(() => !speechClosing && !speech.Enabled && speech.Records == 0, "Closing subtitle left background capture active");
            Check(Directory.GetFiles(root, "*.qth").Length == 0 && server.Errors.IsEmpty, "Closed feature retained records or protocol mismatch");
            await ToggleSpeechAsync(); await Until(() => speech.Records == 1, "Exit cleanup fixture could not start");
            leaveForExitCleanup = true; // The runner verifies files after App.Exit invokes production Cleanup.
        }
        finally { if (!leaveForExitCleanup) await StopSpeechAsync(); settingsWindow?.Close(); Credentials.Set(secretTarget, ""); }
    }
}

