using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace QuietTranslate;

public sealed partial class App
{
    internal static string InputStackForTest() => typeof(InputManager).Assembly.GetType("System.Windows.Input.StylusLogic")?.GetProperty("CurrentStylusLogic", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)?.GetType().Name ?? "unavailable";
    internal void OpenInstanceFixture(string token, string output)
    {
        Directory.CreateDirectory(output);
        var options = new Settings { SelectionButtonEnabled = false, OcrProvider = OcrProvider.Windows, SelectionHotkey = "Ctrl+Alt+Shift+7", ScreenshotHotkey = "Ctrl+Alt+Shift+8", PauseHotkey = "Ctrl+Alt+Shift+9", ManualHotkey = "Ctrl+Alt+Shift+0" };
        foreach (var config in options.Services.Values) { config.Endpoint = "https://example.invalid/" + token; config.CredentialId = token; }
        Start(options, ".InstanceTest." + token);
        var closeWindow = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\QuietTranslate.TestClose." + token);
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\QuietTranslate.TestExit." + token);
        var closeRegistration = ThreadPool.RegisterWaitForSingleObject(closeWindow, (_, _) => Dispatcher.BeginInvoke(new Action(() => { settingsWindow?.Close(); File.WriteAllText(Path.Combine(output, "closed.json"), "{\"background\":true}"); })), null, Timeout.Infinite, false);
        var exitRegistration = ThreadPool.RegisterWaitForSingleObject(exit, (_, _) => Dispatcher.BeginInvoke(new Action(() => ((MenuItem)trayMenu!.Menu.Items[trayMenu.Menu.Items.Count - 1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)))), null, Timeout.Infinite, false);
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) }; safety.Tick += (_, _) => Shutdown(2); safety.Start();
        Exit += (_, _) => { closeRegistration.Unregister(null); exitRegistration.Unregister(null); closeWindow.Dispose(); exit.Dispose(); safety.Stop(); };
        settingsWindow!.ContentRendered += (_, _) => File.WriteAllText(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId, inputStack = InputStackForTest() }));
    }
}
