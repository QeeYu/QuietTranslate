using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietTranslate;

internal sealed class SmoothScroll
{
    private static readonly DependencyProperty Behavior = DependencyProperty.RegisterAttached("Behavior", typeof(SmoothScroll), typeof(SmoothScroll));
    private readonly ScrollViewer view;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch watch = new();
    private double origin, target;
    private SmoothScroll(ScrollViewer view)
    {
        this.view = view;
        timer = new DispatcherTimer(DispatcherPriority.Render, view.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        view.PreviewMouseWheel += (_, e) => { if (Move(e.Delta)) e.Handled = true; };
        view.PreviewMouseDown += (_, e) => { if (e.OriginalSource is DependencyObject node && IsThumb(node)) Stop(); };
        view.Unloaded += (_, _) => Stop();
        timer.Tick += (_, _) =>
        {
            target = Math.Clamp(target, 0, view.ScrollableHeight);
            double t = Math.Min(1, watch.Elapsed.TotalMilliseconds / 120);
            view.ScrollToVerticalOffset(origin + (target - origin) * (1 - Math.Pow(1 - t, 3)));
            if (t >= 1) Stop();
        };
    }
    private static bool IsThumb(DependencyObject node)
    {
        for (var p = node; p != null; p = p is Visual ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p)) if (p is ScrollBar or Thumb) return true;
        return false;
    }
    private void Stop() { timer.Stop(); watch.Reset(); }
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
        origin = view.VerticalOffset;
        target = Math.Clamp((timer.IsEnabled ? target : origin) - delta / 120.0 * amount, 0, view.ScrollableHeight);
        watch.Restart(); timer.Start(); return true;
    }
}
