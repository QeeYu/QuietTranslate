using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietTranslate;

internal sealed class SelectionTrigger : IDisposable
{
    private readonly Native.HookProc mouseProc, keyboardProc;
    private readonly Action<IntPtr, Native.Rect, string?> translate;
    private readonly Func<Settings> options;
    private readonly Func<bool> compatibility;
    private IntPtr hook, keyHook, dragWindow, lastWindow;
    private Native.Point down, lastUp;
    private Native.Rect originalWindow;
    private long lastUpTime;
    private bool dragScrolled;
    private CancellationTokenSource? pending;
    private SelectionButtonWindow? popup;
    internal SelectionButtonWindow? VisibleButton => popup;
    internal string State { get; private set; } = "idle";
    internal SelectionTrigger(Action<IntPtr, Native.Rect> translate) : this((w, r, _) => translate(w, r), () => new Settings(), () => false) { }
    internal SelectionTrigger(Action<IntPtr, Native.Rect, string?> translate, Func<Settings> options, Func<bool> compatibility)
    { this.translate = translate; this.options = options; this.compatibility = compatibility; mouseProc = Mouse; keyboardProc = Keyboard; }
    internal bool Enabled
    {
        get => hook != IntPtr.Zero;
        set
        {
            if (value == Enabled) return;
            if (!value) { Hide(); Native.UnhookWindowsHookEx(hook); if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook); hook = keyHook = dragWindow = IntPtr.Zero; return; }
            hook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("无法启用选词监听，仍可使用翻译快捷键。");
            keyHook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
        }
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x100 or 0x104)
        {
            int key = Marshal.ReadInt32(data);
            bool select = (Native.Down(0x10) && key is >= 0x21 and <= 0x28) || (Native.Down(0x11) && key == 0x41);
            if (select) { var window = Native.GetForegroundWindow(); var point = Native.Cursor(); Application.Current.Dispatcher.BeginInvoke(new Action(() => Queue(window, point, true))); }
            else if (key == 0x1B || (!Native.Down(0x11) && !Native.Down(0x12) && key is >= 0x20 and <= 0x5A)) Application.Current.Dispatcher.BeginInvoke(new Action(Hide));
        }
        return Native.CallNextHookEx(keyHook, code, message, data);
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int kind = message.ToInt32();
            if (kind is 0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E)
            {
                var point = Marshal.PtrToStructure<Native.Point>(data);
                if (kind is 0x20A or 0x20E && dragWindow != IntPtr.Zero && Native.Down(1) && Native.GetForegroundWindow() == dragWindow)
                {
                    // Wheel input during a held selection is part of the same gesture.
                    HidePopup(); dragScrolled = true; State = "drag scrolling";
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out uint clickedProcess);
                dragWindow = IntPtr.Zero; dragScrolled = false;
                if (clickedProcess != Environment.ProcessId)
                {
                    Hide();
                    if (kind == 0x201)
                    {
                        var foreground = Native.GetForegroundWindow(); Native.GetWindowThreadProcessId(foreground, out uint process);
                        var name = new System.Text.StringBuilder(80); Native.GetClassName(foreground, name, name.Capacity);
                        if (process != Environment.ProcessId && name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
                        { dragWindow = foreground; down = point; Native.GetWindowRect(foreground, out originalWindow); }
                    }
                }
            }
            else if (kind == 0x202 && dragWindow != IntPtr.Zero)
            {
                var window = dragWindow; dragWindow = IntPtr.Zero; var point = Marshal.PtrToStructure<Native.Point>(data);
                bool dragged = dragScrolled || Math.Abs(point.X - down.X) >= 6 || Math.Abs(point.Y - down.Y) >= 6;
                dragScrolled = false;
                bool doubled = window == lastWindow && Environment.TickCount64 - lastUpTime <= System.Windows.Forms.SystemInformation.DoubleClickTime && Math.Abs(point.X - lastUp.X) < 5 && Math.Abs(point.Y - lastUp.Y) < 5;
                lastUp = point; lastUpTime = Environment.TickCount64; lastWindow = window;
                Native.GetWindowRect(window, out var now);
                if ((dragged || doubled) && SameRect(now, originalWindow)) Queue(window, point, false);
                else State = $"released: drag={dragged}, sameWindowBounds={SameRect(now, originalWindow)}";
            }
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }
    private void Queue(IntPtr window, Native.Point point, bool keyboard)
    {
        State = "queued";
        pending?.Cancel(); pending?.Dispose(); pending = new CancellationTokenSource();
        _ = ShowAfterSelectionAsync(window, point, keyboard, pending.Token);
    }
    private async Task ShowAfterSelectionAsync(IntPtr window, Native.Point point, bool keyboard, CancellationToken ct)
    {
        try
        {
            await Task.Delay(keyboard ? 220 : 120, ct);
            if (!Enabled || Native.GetForegroundWindow() != window) return;
            Native.GetWindowThreadProcessId(window, out uint process); if (process == Environment.ProcessId) return;
            SelectionReader.Selection? selected = null;
            // Latest gesture retries while a cancelled probe/old button monitor is releasing UIA.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (!Enabled || Native.GetForegroundWindow() != window) return;
                selected = await SelectionReader.TryDirectAsync(window, ct);
                State = $"probe {attempt}: {SelectionReader.ProbeState}, selected={selected != null}";
                if (selected != null || SelectionReader.ProtectedWindow == window) break;
                await Task.Delay(85, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (SelectionReader.ProtectedWindow == window) return;
            bool guessed = selected == null;
            if (guessed && (keyboard || !compatibility())) return;
            var anchor = selected?.Bounds ?? new Native.Rect { Left = point.X, Right = point.X, Top = point.Y, Bottom = point.Y };
            if (Native.GetForegroundWindow() != window) return;
            if (popup != null && popup.SourceText == selected?.Text && SameRect(popup.Anchor, anchor)) return;
            popup?.Close();
            var pointerAnchor = new Native.Rect { Left = point.X, Right = point.X, Top = point.Y, Bottom = point.Y };
            var button = new SelectionButtonWindow(window, anchor, selected?.Text, options(), guessed, (text) => { Hide(); translate(window, pointerAnchor, text); }, point); popup = button;
            button.Closed += (_, _) => { if (ReferenceEquals(popup, button)) popup = null; };
            button.Show();
            State = "button shown";
        }
        catch (OperationCanceledException) { }
        catch { /* Inaccessible apps keep working; never simulate clipboard reads just to show a button. */ }
    }
    internal static bool SameRect(Native.Rect a, Native.Rect b) => a.Left == b.Left && a.Right == b.Right && a.Top == b.Top && a.Bottom == b.Bottom;
    private void HidePopup() { pending?.Cancel(); pending?.Dispose(); pending = null; popup?.Close(); popup = null; }
    internal void Hide() { dragWindow = IntPtr.Zero; dragScrolled = false; HidePopup(); }
    public void Dispose() { Enabled = false; Hide(); }
}

internal sealed class SelectionButtonWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly CancellationTokenSource lifetime = new();
    internal string? SourceText { get; private set; }
    internal Native.Rect Anchor { get; }
    private bool checking, closed;
    internal string DismissReason { get; private set; } = "external";
    internal SelectionButtonWindow(IntPtr source, Native.Rect anchor, string? text, Settings s, bool guessed, Action<string?> click, Native.Point? releasePoint = null)
    {
        SourceText = text; Anchor = anchor; Native.GetWindowRect(source, out var sourceRect);
        Title = "轻译 · 点击翻译选中文字"; Width = Height = Math.Clamp(s.ButtonSize, 28, 56); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; Ui.Apply(this);
        var border = CreateFace(s); var button = new Button { Content = s.ButtonLabel, Focusable = false, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = Math.Max(13, Width * .4), FontWeight = FontWeights.SemiBold, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = new SolidColorBrush(TextColor(s)), BorderThickness = new Thickness(0), ToolTip = guessed ? "兼容选区 · 点击后复制并翻译" : "点击翻译选中文字 · 未发送网络请求" };
        if (s.ButtonShadow) { Width += 12; Height += 12; border.Margin = new Thickness(6); }
        border.Child = button; Content = border;
        button.Click += (_, _) => { button.IsEnabled = false; string? snapshot = SourceText; click(snapshot); };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, new IntPtr(Native.GetWindowLongPtr(handle, -20).ToInt64() | 0x08000080));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x21) { handled = true; return new IntPtr(3); }
                if (msg == 0x0312 && w.ToInt32() == 92) { handled = true; Dispatcher.BeginInvoke(new Action(Close)); }
                return IntPtr.Zero;
            });
            Native.RegisterHotKey(handle, 92, 0x4000, 0x1B);
        };
        long active = Environment.TickCount64, lastCheck = 0;
        Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle; Native.GetWindowRect(handle, out var rect);
            var pointer = releasePoint ?? Native.Cursor();
            var a = s.ButtonPosition == 2 ? new Native.Rect { Left = pointer.X, Right = pointer.X, Top = pointer.Y, Bottom = pointer.Y } : anchor;
            if (s.ButtonPosition == 1) a.Top -= (rect.Bottom - rect.Top) / 2;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            a.Left += (int)((s.ButtonOffsetX - 12) * scale); a.Right += (int)((s.ButtonOffsetX - 12) * scale); a.Top += (int)(s.ButtonOffsetY * scale);
            var position = s.ButtonPosition == 2 ? PopupPlacement.Near(pointer, anchor, rect.Right - rect.Left, rect.Bottom - rect.Top, source, (int)((s.ButtonOffsetX - 12) * scale), (int)(s.ButtonOffsetY * scale)) : BubbleWindow.Place(a, Native.WorkArea(new Native.Point(anchor.Left, anchor.Top)), rect.Right - rect.Left, rect.Bottom - rect.Top);
            Native.SetWindowPos(handle, new IntPtr(-1), position.X, position.Y, 0, 0, 0x0011); timer.Start();
        };
        timer.Tick += async (_, _) =>
        {
            if (Native.GetForegroundWindow() != source || Native.Down(0x1B)) { DismissReason = "focus/escape"; Close(); return; }
            Native.GetWindowRect(source, out var now); if (!SelectionTrigger.SameRect(sourceRect, now)) { DismissReason = "source moved"; Close(); return; }
            var p = Native.Cursor(); var a = Anchor;
            bool nearby = IsMouseOver || (p.X >= a.Left - 12 && p.X <= a.Right + 80 && p.Y >= a.Top - 28 && p.Y <= a.Bottom + 60);
            if (nearby) active = Environment.TickCount64;
            if (guessed && Environment.TickCount64 - active > Math.Clamp(s.CompatibilityTimeout, 3, 60) * 1000) { DismissReason = "compatibility timeout"; Close(); return; }
            if (guessed || checking || IsMouseOver || Environment.TickCount64 - lastCheck < 700) return;
            checking = true; lastCheck = Environment.TickCount64;
            try
            {
                var current = await SelectionReader.TryDirectAsync(source, lifetime.Token);
                if (closed) return;
                // A timed-out/unavailable accessibility provider does not mean the selection was cleared.
                // Outside clicks, typing, scrolling and focus changes already end the session reliably.
                if (current != null && current.Text != SourceText) { DismissReason = "selection changed"; Close(); }
            }
            catch (OperationCanceledException) { }
            catch { /* Keep the confirmed session if the accessibility provider temporarily stops responding. */ }
            finally { checking = false; }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); timer.Stop(); Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 92); SourceText = null; Content = null; };
    }
    internal static Color TextColor(Settings s) => s.BubbleSmartColors ? BubbleView.ReadableText(Ui.Color(s.ButtonForeground), Ui.Color(s.ButtonBackground)) : Ui.Color(s.ButtonForeground);
    internal static Border CreateFace(Settings s) => new() { Background = BubbleView.Alpha(Ui.Color(s.ButtonBackground), s.ButtonOpacity), BorderBrush = BubbleView.Alpha(Ui.Color(s.ButtonBorderColor), s.ButtonOpacity), BorderThickness = new Thickness(s.ButtonBorderVisible ? 1 : 0), CornerRadius = new CornerRadius(Math.Clamp(s.ButtonRadius, 0, s.ButtonSize / 2)), Effect = s.ButtonShadow ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = .15 } : null };
}
