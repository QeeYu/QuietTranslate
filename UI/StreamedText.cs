using System;
using System.Windows.Threading;

namespace QuietTranslate;

// Keep only the newest partial result. Network callbacks never enqueue per-token UI work.
internal sealed class StreamedText : IDisposable
{
    private readonly DispatcherTimer timer;
    private readonly Action<string> apply;
    private readonly object gate = new();
    private string? latest;
    private bool stopped;
    internal StreamedText(Dispatcher dispatcher, Action<string> apply)
    {
        this.apply = apply;
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += Tick; timer.Start();
    }
    internal void Report(string value) { lock (gate) { if (!stopped) latest = value; } }
    private void Tick(object? sender, EventArgs e)
    {
        string? value;
        lock (gate) { value = latest; latest = null; }
        if (value != null) apply(value);
    }
    public void Dispose()
    {
        lock (gate) { stopped = true; latest = null; }
        timer.Stop(); timer.Tick -= Tick;
    }
}
