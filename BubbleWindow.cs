using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietTranslate;

internal sealed class BubbleWindow : Window
{
    private readonly BubbleView view;
    private readonly DispatcherTimer focusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly Native.HookProc mouseProc;
    private IntPtr mouseHook;
    private readonly IntPtr sourceWindow;
    private readonly Native.Rect anchor;
    private readonly Native.Point pointer;
    private string pending = "";
    private bool dirty, closed;
    public event Action? Dismissed;
    public string Translation => pending;
    internal void ApplyAppearance(Settings s) { view.SetTheme(Ui.IsDark); view.ApplyAppearance(s); ResizeToText(true); }
    internal void SetContext(string label, Action action) => view.SetContext(label, action);
    internal BubbleView View => view;
    internal string DismissReason { get; private set; } = "closed";
    private bool resizePending, finalSize;
    private long lastResize;
    private bool dragging, positionedByUser;
    private Native.Point dragStart, dragOrigin, pinnedPosition;
    internal bool PositionedByUser => positionedByUser;
    public BubbleWindow(Native.Rect anchorBounds, IntPtr foreground, double opacity, double fontSize = 16, bool? dark = null, string textColor = "")
    {
        anchor = anchorBounds; sourceWindow = foreground;
        pointer = anchorBounds.Left == anchorBounds.Right ? new(anchorBounds.Left, anchorBounds.Top) : Native.Cursor();
        Title = "轻译 · QuietTranslate"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Width = 108; SizeToContent = SizeToContent.Height; MaxHeight = 400;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        view = new BubbleView(opacity, fontSize, textColor, dark, Close, () =>
        { try { if (!string.IsNullOrEmpty(pending)) { Clipboard.SetText(pending); view!.Notify("已复制"); } } catch (COMException) { view!.Notify("剪贴板被占用"); } });
        Content = view;
        view.DragHandle.MouseLeftButtonDown += (_, e) =>
        {
            if (view.IsMenuOpen) view.ActionsMenu.IsOpen = false;
            Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
            dragStart = Native.Cursor(); dragOrigin = new(r.Left, r.Top); dragging = true;
            view.DragHandle.CaptureMouse(); e.Handled = true;
        };
        view.DragHandle.MouseMove += (_, _) => { if (dragging) MoveDrag(Native.Cursor()); };
        view.DragHandle.MouseLeftButtonUp += (_, e) => { EndDrag(); e.Handled = true; };
        view.DragHandle.LostMouseCapture += (_, _) => dragging = false;
        // The bubble owns outside-click dismissal; a second WPF capture would close the
        // menu on the same mouse release that opens it in a NOACTIVATE window.
        view.ActionsMenu.StaysOpen = true;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, new IntPtr(Native.GetWindowLongPtr(handle, -20).ToInt64() | 0x08000080)); // NOACTIVATE | TOOLWINDOW
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message == 0x21) { handled = true; return new IntPtr(3); }
                if (message == 0x0312 && w.ToInt32() == 91) { handled = true; Dispatcher.BeginInvoke(new Action(Close)); }
                return IntPtr.Zero;
            });
            Native.RegisterHotKey(handle, 91, 0x4000, 0x1B);
        };
        ContentRendered += (_, _) => { FlushRender(); ResizeToText(true); Position(); }; SizeChanged += (_, _) => { if (IsVisible) Position(); };
        view.MouseLeave += (_, _) => resizePending = true;
        view.ActionsMenu.Closed += (_, _) => resizePending = true;
        view.ActionsMenu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(view.ActionsMenu) is HwndSource popup)
            {
                Native.SetWindowLongPtr(popup.Handle, -20, new IntPtr(Native.GetWindowLongPtr(popup.Handle, -20).ToInt64() | 0x08000080));
                popup.RemoveHook(NoActivate); popup.AddHook(NoActivate);
            }
        };
        mouseProc = OnMouse;
        Loaded += (_, _) =>
        {
            Position();
            mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
            focusTimer.Start(); renderTimer.Start();
        };
        focusTimer.Tick += (_, _) =>
        {
            var foreground = Native.GetForegroundWindow(); Native.GetWindowThreadProcessId(foreground, out uint process);
            bool ownMenu = view.IsMenuOpen && process == Environment.ProcessId;
            if (Native.Down(0x1B) || (sourceWindow != IntPtr.Zero && foreground != sourceWindow && !ownMenu)) { DismissReason = Native.Down(0x1B) ? "escape" : "focus changed"; Close(); }
            // Fallback if hook installation is denied; no idle polling exists when the bubble is closed.
            else if (mouseHook == IntPtr.Zero && (Native.Down(1) || Native.Down(2) || Native.Down(4)))
            { var p = Native.Cursor(); if (!Contains(p)) Close(); }
        };
        renderTimer.Tick += (_, _) => { FlushRender(); if (resizePending && Environment.TickCount64 - lastResize >= 120) ResizeToText(); };
        Closed += (_, _) =>
        {
            closed = true;
            EndDrag();
            focusTimer.Stop(); renderTimer.Stop();
            Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 91);
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            pending = ""; view.Clear(); ToolTip = null; Content = null;
            var handler = Dismissed; Dismissed = null; handler?.Invoke();
        };
    }
    private static IntPtr NoActivate(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    { if (message == 0x21) { handled = true; return new IntPtr(3); } return IntPtr.Zero; }
    private bool Contains(Native.Point point)
    {
        Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
        return (point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom) || view.ContainsMenu(point);
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() == 0x20A && !view.IsMenuOpen && view.Viewport.ScrollableHeight > 0)
        {
            var point = Marshal.PtrToStructure<Native.Point>(data);
            var local = view.Viewport.PointFromScreen(new Point(point.X, point.Y));
            if (new Rect(view.Viewport.RenderSize).Contains(local) && SmoothScroll.Wheel(view.Viewport, (short)(Marshal.ReadInt32(data, 8) >> 16))) return new IntPtr(1);
        }
        if (code >= 0 && dragging)
        {
            if (message.ToInt32() == 0x200) MoveDrag(Marshal.PtrToStructure<Native.Point>(data));
            if (message.ToInt32() == 0x202) Dispatcher.BeginInvoke(new Action(EndDrag));
        }
        if (code >= 0 && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            var point = Marshal.PtrToStructure<Native.Point>(data);
            if (!Contains(point)) { DismissReason = "outside click"; Dispatcher.BeginInvoke(new Action(Close)); }
        }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }
    private void EndDrag() { dragging = false; if (view.DragHandle.IsMouseCaptured) view.DragHandle.ReleaseMouseCapture(); resizePending = true; }
    private void MoveDrag(Native.Point point)
    {
        int dx = point.X - dragStart.X, dy = point.Y - dragStart.Y;
        if (!positionedByUser && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
        var handle = new WindowInteropHelper(this).Handle; Native.GetWindowRect(handle, out var r); var work = Native.WorkArea(point);
        pinnedPosition = new(Math.Clamp(dragOrigin.X + dx, work.Left + 8, Math.Max(work.Left + 8, work.Right - (r.Right - r.Left) - 8)), Math.Clamp(dragOrigin.Y + dy, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - (r.Bottom - r.Top) - 8)));
        positionedByUser = true; if (pinnedPosition.X != r.Left || pinnedPosition.Y != r.Top) Native.SetWindowPos(handle, new IntPtr(-1), pinnedPosition.X, pinnedPosition.Y, 0, 0, 0x0011);
    }
    public void SetText(string value, string label, bool completed = false)
    {
        if (closed) return;
        pending = value; dirty = true; finalSize = completed; ToolTip = label;
    }
    private void FlushRender()
    {
        if (!dirty) return;
        dirty = false; view.Text = pending; resizePending = true;
    }
    private void ResizeToText(bool force = false)
    {
        if (closed || dragging || (!force && view.IsMenuOpen)) return;
        var work = Native.WorkArea(positionedByUser ? pinnedPosition : pointer); var dpi = VisualTreeHelper.GetDpi(this);
        double maxWidth = Math.Min(380, Math.Max(60, (work.Right - work.Left - 16) / dpi.DpiScaleX));
        MaxHeight = Math.Min(400, Math.Max(65, (work.Bottom - work.Top - 16) / dpi.DpiScaleY)); view.SetHeightLimit(MaxHeight);
        double desired = view.FittingWidth(maxWidth);
        if (!force && view.IsMouseOver) desired = Math.Max(Width, desired);
        if (!force && !finalSize) desired = Math.Min(maxWidth, Math.Max(Width, Math.Ceiling(desired / 16) * 16));
        if (Math.Abs(Width - desired) > .5) Width = desired;
        resizePending = false; lastResize = Environment.TickCount64;
    }
    private void Position()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var work = Native.WorkArea(positionedByUser ? pinnedPosition : pointer);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int maxWidth = Math.Max(60, work.Right - work.Left - 16);
        if (Width * scale > maxWidth) Width = maxWidth / scale;
        MaxHeight = Math.Min(400, Math.Max(65, (work.Bottom - work.Top - 16) / scale));
        Native.GetWindowRect(handle, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var p = positionedByUser ? new Native.Point(Math.Clamp(pinnedPosition.X, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8)), Math.Clamp(pinnedPosition.Y, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8))) : PopupPlacement.Near(pointer, anchor, width, height, sourceWindow);
        if (positionedByUser) pinnedPosition = p;
        Native.SetWindowPos(handle, new IntPtr(-1), p.X, p.Y, 0, 0, 0x0011); // NOSIZE | NOACTIVATE
    }
    internal static Native.Point Place(Native.Rect a, Native.Rect work, int width, int height)
    {
        int x = a.Right + 12;
        if (x + width > work.Right - 8) x = a.Left - width - 12;
        x = Math.Clamp(x, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        int y = Math.Clamp(a.Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
        return new Native.Point(x, y);
    }
}
