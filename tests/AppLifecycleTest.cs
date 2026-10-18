using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace QuietTranslate;

public sealed partial class App
{
    private Action<ManualWindow>? manualShownForTest;
    internal async Task<int> RunLifecycleAsync(string output)
    {
        Directory.CreateDirectory(output); var errors = new List<string>(); var results = new List<object>(); int failed = 0;
        TestException = e => errors.Add(e.ToString());
        var cursor = Native.Cursor(); var foreground = Native.GetForegroundWindow();
        async Task Test(string name, Func<Task> action) { try { await action(); results.Add(new { name, status = "PASS" }); } catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); } }
        var isolated = new Settings { SelectionButtonEnabled = false, ManualTopmost = true, SelectionHotkey = "Ctrl+Alt+Shift+7", ScreenshotHotkey = "Ctrl+Alt+Shift+8", PauseHotkey = "Ctrl+Alt+Shift+9", ManualHotkey = "Ctrl+Alt+Shift+0" };
        string scope = Guid.NewGuid().ToString("N");
        foreach (var config in isolated.Services.Values) { config.Endpoint = "https://example.invalid/" + scope; config.CredentialId = scope; config.Model = "synthetic"; }
        foreach (var config in isolated.OcrServices.Values) config.Endpoint = "https://example.invalid/" + scope;
        try
        {
            Start(isolated, ".LifecycleTest." + scope); settingsWindow?.Close();
            await Test("Open, native close and reopen the real manual controller repeatedly", async () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    OpenManual(); var window = manual ?? throw new Exception("manual reference missing after open"); await Task.Delay(100);
                    if (i == 1) { window.WindowState = WindowState.Minimized; OpenManual(); if (manual != window || window.WindowState != WindowState.Normal) throw new Exception("minimized panel not restored"); }
                    var close = HoverTest.Find<Button>(window, "ManualClose") ?? throw new Exception("close control missing");
                    ToolTipService.SetInitialShowDelay(close, 50);
                    var point = close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2)); Native.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(600);
                    await HoverTest.ClickAsync(close); await Task.Delay(120);
                    if (manual != null || window.IsVisible) throw new Exception("closed manual window still retained");
                }
            });
            manual?.Close();
            await Test("Closing during Show does not dereference a cleared manual field", async () =>
            {
                manualShownForTest = window => window.Close();
                try { OpenManual(); if (manual != null) throw new Exception("closed panel retained during Show"); }
                finally { manualShownForTest = null; }
                OpenManual(); if (manual is not { IsVisible: true }) throw new Exception("panel failed to recover after reentrant close"); manual.Close(); await Task.Delay(60);
            });
            await Test("Closing with a service popup open clears it and allows a fresh panel", async () =>
            {
                OpenManual(); var window = manual!; await Task.Delay(100); var service = HoverTest.Find<Button>(window, "ManualService")!;
                service.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
                window.Close(); await Task.Delay(120); if (service.ContextMenu.IsOpen || manual != null) throw new Exception("service menu survived close");
                OpenManual(); if (manual is not { IsVisible: true }) throw new Exception("fresh panel missing"); manual.Close();
            });
            await Test("Custom manual shortcut toggles open and closed; Esc closes and clears content", async () =>
            {
                Native.TestChord(Hotkey.Parse(isolated.ManualHotkey)); await Task.Delay(180); var first = manual ?? throw new Exception("manual hotkey did not open panel");
                first.Input.Text = "Synthetic content to clear on close";
                Native.TestChord(Hotkey.Parse(isolated.ManualHotkey)); await Task.Delay(120); if (manual != null || first.IsVisible || first.Input.Text.Length > 0) throw new Exception("manual shortcut did not close and clear");
                Native.TestChord(Hotkey.Parse(isolated.ManualHotkey)); await Task.Delay(180); var reopened = manual ?? throw new Exception("manual hotkey did not reopen panel");
                Native.TapKey(0x1B); await Task.Delay(140); if (manual != null || reopened.IsVisible) throw new Exception("Esc did not close manual panel");
            });
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        finally { manualShownForTest = null; manual?.Close(); settingsWindow?.Close(); await Task.Delay(150); Native.SetCursorPos(cursor.X, cursor.Y); Native.SetForegroundWindow(foreground); }
        await File.WriteAllTextAsync(Path.Combine(output, "lifecycle-tests.json"), JsonSerializer.Serialize(new { passed = failed == 0 && errors.Count == 0, tests = results, errors, simulatedServices = true, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 && errors.Count == 0 ? 0 : 1;
    }
}
