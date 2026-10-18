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
    internal static double Speed { get; set; } = .65;
    private readonly ScrollViewer view;
    private readonly Stopwatch watch = new();
    private double target, position, previousFrame, lastInput;
    private int direction;
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
        double scale = VisualTreeHelper.GetDpi(view).DpiScaleY;
        double distance = target - position;
        bool done = Math.Abs(distance) * scale < .4 || now - lastInput >= 300;
        // Keep a continuous position between frames, but draw on physical pixels.
        // A longer response joins consecutive wheel detents instead of finishing each as a short jump.
        position = done ? target : position + distance * (1 - Math.Exp(-dt / 70));
        view.ScrollToVerticalOffset(Math.Clamp(Math.Round(position * scale) / scale, 0, view.ScrollableHeight));
        if (done) Stop();
    }
    internal static void Attach(ScrollViewer view)
    {
        if (view.GetValue(Behavior) == null) view.SetValue(Behavior, new SmoothScroll(view));
    }
    internal static void Attach(TextBox field)
    {
        TextOptions.SetTextFormattingMode(field, TextFormattingMode.Display); TextOptions.SetTextHintingMode(field, TextHintingMode.Fixed);
        field.Loaded += (_, _) => { field.ApplyTemplate(); if (field.Template.FindName("PART_ContentHost", field) is ScrollViewer scroll) Attach(scroll); };
    }
    internal static bool Wheel(ScrollViewer view, int delta) { Attach(view); return ((SmoothScroll)view.GetValue(Behavior)).Move(delta); }
    private bool Move(int delta)
    {
        if (delta == 0 || view.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0) return false;
        double amount = (SystemParameters.WheelScrollLines < 0 ? view.ViewportHeight : SystemParameters.WheelScrollLines * 18) * Math.Clamp(Speed, .25, 1.5);
        if (!running)
        {
            position = view.VerticalOffset;
            double scale = VisualTreeHelper.GetDpi(view).DpiScaleY;
            if (Math.Abs(position - Math.Round(target * scale) / scale) > .1) target = position;
        }
        // Reverse immediately rather than first consuming a queued tail in the old direction.
        if (direction != 0 && Math.Sign(delta) != direction) target = position = view.VerticalOffset;
        direction = Math.Sign(delta);
        target = Math.Clamp(target - delta / 120.0 * amount, 0, view.ScrollableHeight);
        if (!running) { running = true; previousFrame = 0; watch.Restart(); CompositionTarget.Rendering += Render; }
        lastInput = watch.Elapsed.TotalMilliseconds; return true;
    }
}
