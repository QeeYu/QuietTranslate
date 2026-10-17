using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuietTranslate;

internal sealed class SmoothScroll
{
    private static readonly DependencyProperty Behavior = DependencyProperty.RegisterAttached("Behavior", typeof(SmoothScroll), typeof(SmoothScroll));
    private readonly ScrollViewer view;
    private readonly Stopwatch watch = new();
    private double target, previousFrame, lastInput;
    private bool running;
    private SmoothScroll(ScrollViewer view)
    {
        this.view = view;
        view.PreviewMouseWheel += (_, e) => { if (Move(e.Delta)) e.Handled = true; };
        view.PreviewMouseDown += (_, e) => { if (e.OriginalSource is DependencyObject node && IsThumb(node)) Stop(); };
        view.PreviewKeyDown += (_, _) => Stop();
        view.Unloaded += (_, _) => Stop();
    }
    private static bool IsThumb(DependencyObject node)
    {
        for (var p = node; p != null; p = p is Visual ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p)) if (p is ScrollBar or Thumb) return true;
        return false;
    }
    private void Stop() { if (running) CompositionTarget.Rendering -= Render; running = false; watch.Reset(); }
    private void Render(object? sender, EventArgs e)
    {
        double now = watch.Elapsed.TotalMilliseconds, dt = Math.Clamp(now - previousFrame, 0, 50); previousFrame = now;
        target = Math.Clamp(target, 0, view.ScrollableHeight);
        double distance = target - view.VerticalOffset;
        bool done = Math.Abs(distance) < .35 || now - lastInput >= 160;
        view.ScrollToVerticalOffset(done ? target : view.VerticalOffset + distance * (1 - Math.Exp(-dt / 22)));
        if (done) Stop();
    }
    internal static void Attach(ScrollViewer view)
    {
        if (view.GetValue(Behavior) == null) view.SetValue(Behavior, new SmoothScroll(view));
    }
    internal static void Attach(TextBox field)
    {
        field.Loaded += (_, _) => { field.ApplyTemplate(); if (field.Template.FindName("PART_ContentHost", field) is ScrollViewer scroll) Attach(scroll); };
    }
    internal static bool Wheel(ScrollViewer view, int delta) { Attach(view); return ((SmoothScroll)view.GetValue(Behavior)).Move(delta); }
    private bool Move(int delta)
    {
        if (delta == 0 || view.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0) return false;
        double amount = SystemParameters.WheelScrollLines < 0 ? view.ViewportHeight : SystemParameters.WheelScrollLines * 18;
        double position = view.VerticalOffset;
        // Reverse immediately rather than first consuming a queued tail in the old direction.
        if (!running || (delta > 0 && target > position) || (delta < 0 && target < position)) target = position;
        target = Math.Clamp(target - delta / 120.0 * amount, 0, view.ScrollableHeight);
        if (!running) { running = true; previousFrame = 0; watch.Restart(); CompositionTarget.Rendering += Render; }
        lastInput = watch.Elapsed.TotalMilliseconds; return true;
    }
}
