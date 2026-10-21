using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace QuietTranslate;

internal sealed class TrayMenu : IDisposable
{
    internal ContextMenu Menu { get; } = new() { Placement = PlacementMode.MousePoint, MinWidth = 274, StaysOpen = false };
    private readonly MenuItem manual, capture, pause;
    private readonly MenuItem? speech;
    private readonly Native.HookProc mouseProc;
    private IntPtr mouseHook;
    private readonly TextBlock state = new() { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
    private string baseState = "轻翼已就绪";
    internal TrayMenu(Action input, Action screenshot, Action image, Action togglePause, Action settings, Action exit, Action? toggleSpeech = null, Action? unlockSpeech = null)
    {
        Ui.Apply(Menu);
        var brand = new StackPanel { Margin = new Thickness(2, 4, 8, 6) };
        brand.Children.Add(new TextBlock { Text = "轻译", FontSize = 15, FontWeight = FontWeights.SemiBold }); Ui.Bind(state, TextBlock.ForegroundProperty, "Muted"); brand.Children.Add(state);
        Menu.Items.Add(new MenuItem { Header = brand, IsEnabled = false, Icon = new Image { Source = AppIcons.Image, Width = 24, Height = 24 }, Opacity = 1 });
        Menu.Items.Add(new Separator());
        manual = Add("输入翻译", "\uE8D2", input);
        capture = Add("截图翻译", "\uE722", screenshot);
        Add("识别图片…", "\uEB9F", image);
        if (toggleSpeech != null)
        {
            speech = Add("开启实时语音翻译", "\uE720", toggleSpeech);
            if (unlockSpeech != null) Add("解锁语音字幕", "\uE785", unlockSpeech);
        }
        Menu.Items.Add(new Separator());
        pause = Add("暂停翻译", "\uE769", togglePause);
        Add("设置", "\uE713", settings);
        Menu.Items.Add(new Separator()); Add("退出轻译", "\uE8BB", exit);
        mouseProc = OnMouse;
        Menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(Menu) is HwndSource source) Native.SetForegroundWindow(source.Handle);
            if (mouseHook == IntPtr.Zero) mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
        };
        Menu.Closed += (_, _) => RemoveHook();
    }
    private MenuItem Add(string text, string glyph, Action action)
    {
        var item = new MenuItem { Header = text, Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center } };
        item.Click += (_, _) => { Menu.IsOpen = false; action(); }; Menu.Items.Add(item); return item;
    }
    internal void Refresh(Settings settings, bool paused, bool blocked)
    {
        Ui.Apply(Menu);
        state.Text = baseState = paused ? "轻翼休息中 · 已手动暂停" : blocked ? "当前应用划词已自动暂停" : "轻翼已就绪";
        pause.Header = paused ? "恢复翻译" : "暂停翻译"; ((TextBlock)pause.Icon).Text = paused ? "\uE768" : "\uE769";
        pause.InputGestureText = settings.PauseHotkey; manual.InputGestureText = settings.ManualHotkey; capture.InputGestureText = settings.ScreenshotHotkey;
    }
    internal void RefreshSpeech(bool enabled, SpeechOptions options)
    {
        if (speech == null) return;
        state.Text = baseState + (enabled ? " · 语音已开启" : " · 语音关闭");
        speech.Header = enabled ? "关闭实时语音翻译并清空记录" : "开启实时语音翻译"; speech.InputGestureText = options.Hotkey;
    }
    internal void Show() { Menu.IsOpen = false; Menu.IsOpen = true; }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B && PresentationSource.FromVisual(Menu) is HwndSource source)
        {
            Native.GetWindowRect(source.Handle, out var bounds); var p = Marshal.PtrToStructure<Native.Point>(data);
            if (p.X < bounds.Left || p.X >= bounds.Right || p.Y < bounds.Top || p.Y >= bounds.Bottom) Menu.Dispatcher.BeginInvoke(new Action(() => Menu.IsOpen = false));
        }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }
    private void RemoveHook() { if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; } }
    public void Dispose() { Menu.IsOpen = false; RemoveHook(); }
}
