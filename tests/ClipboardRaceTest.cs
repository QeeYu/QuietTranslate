using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace QuietTranslate;

internal static class ClipboardRaceTest
{
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);

    // A different clipboard writer completes while QuietTranslate is waiting for the lock.
    // The change happens before releasing the lock, so the race is deterministic.
    private static async Task<(Action release, Task finished)> HoldAsync(string? newer)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        new Thread(() =>
        {
            IntPtr owner = DesktopProbe.ClipboardWindow(), memory = IntPtr.Zero; bool open = false;
            try
            {
                if (newer != null)
                {
                    byte[] bytes = Encoding.Unicode.GetBytes(newer + '\0');
                    memory = GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
                    IntPtr pointer = GlobalLock(memory);
                    if (pointer == IntPtr.Zero) throw new Exception("Synthetic allocation failed");
                    try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(memory); }
                }
                for (int i = 0; i < 40 && !(open = OpenClipboard(owner)); i++) Thread.Sleep(20);
                if (!open) throw new Exception("Could not hold synthetic clipboard");
                ready.TrySetResult();
                if (!release.Wait(3000)) throw new Exception("Synthetic holder timed out");
                if (newer != null)
                {
                    if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) throw new Exception("Synthetic external copy failed");
                    memory = IntPtr.Zero;
                }
            }
            catch (Exception ex) { ready.TrySetException(ex); finished.TrySetException(ex); }
            finally
            {
                if (open) CloseClipboard();
                if (memory != IntPtr.Zero) GlobalFree(memory);
                if (owner != IntPtr.Zero) DesktopProbe.DestroyWindow(owner);
                release.Dispose(); finished.TrySetResult();
            }
        }) { IsBackground = true }.Start();
        await ready.Task;
        return (() => release.Set(), finished.Task);
    }

    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0; uint owned = 0;
        using var native = ClipboardSnapshot.Capture();
        if (SelectionReader.OleGetClipboard(out var original) != 0) throw new Exception("Cannot preserve clipboard");
        async Task Test(string name, Func<Task> run)
        {
            try { await run(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.Message }); }
        }
        try
        {
            await Test("Temporary selection copy restores rich data and releases after an empty selection", async () =>
            {
                IntPtr foreground = Native.GetForegroundWindow();
                var field = new TextBox { Text = "Synthetic selected text 日本語 中文", Margin = new Thickness(20) };
                var window = new Window { Title = "Synthetic clipboard selection", Content = field, Width = 500, Height = 180, Topmost = true };
                try
                {
                    window.Show(); window.Activate(); field.Focus(); field.SelectAll(); await Task.Delay(150);
                    IntPtr handle = new WindowInteropHelper(window).Handle;
                    if (Native.GetForegroundWindow() != handle) throw new Exception("Synthetic window did not receive focus");
                    var rich = new DataObject(); rich.SetText("Synthetic rich original"); rich.SetData(DataFormats.Html, "<b>Synthetic rich original</b>");
                    Clipboard.SetDataObject(rich, false); owned = Native.GetClipboardSequenceNumber();
                    if (await SelectionReader.ReadAsync(handle, 600, CancellationToken.None) != field.Text) throw new Exception("Selected text changed");
                    owned = Native.GetClipboardSequenceNumber();
                    await DesktopProbe.ReadExpectedAsync("Synthetic rich original");
                    if ((Clipboard.GetData(DataFormats.Html) as string)?.Contains("Synthetic rich original") != true) throw new Exception("Rich data was lost");
                    field.Select(0, 0); bool rejected = false;
                    try { await SelectionReader.ReadAsync(handle, 180, CancellationToken.None); }
                    catch (InvalidOperationException) { rejected = true; }
                    if (!rejected || ClipboardWriter.IsBusy) throw new Exception("Failed read retained reservation");
                    if (!await ClipboardWriter.CopyAsync("Synthetic write after failed read")) throw new Exception("Copy failed after selection timeout");
                    owned = Native.GetClipboardSequenceNumber();
                    await DesktopProbe.ReadExpectedAsync("Synthetic write after failed read");
                }
                finally { window.Close(); Native.SetForegroundWindow(foreground); }
            });
            foreach (bool newer in new[] { true, false })
            await Test(newer ? "A newer external copy supersedes a waiting copy" : "A busy clipboard still supports a bounded retry", async () =>
            {
                if (!await ClipboardWriter.CopyAsync("Synthetic original")) throw new Exception("Seed failed");
                owned = Native.GetClipboardSequenceNumber();
                var holder = await HoldAsync(newer ? "Synthetic newer external copy" : null);
                Task<bool> pending;
                try { pending = ClipboardWriter.CopyAsync("Synthetic waiting copy"); await Task.Delay(60); }
                finally { holder.release(); await holder.finished; owned = Native.GetClipboardSequenceNumber(); }
                bool copied = await pending; owned = Native.GetClipboardSequenceNumber();
                if (copied == newer) throw new Exception(newer ? "Waiting copy overwrote a newer external copy" : "Unchanged clipboard did not retry");
                await DesktopProbe.ReadExpectedAsync(newer ? "Synthetic newer external copy" : "Synthetic waiting copy");
            });
            await Test("Selection restoration excludes own copy operations and always releases its reservation", async () =>
            {
                if (!ClipboardWriter.TryAcquire()) throw new Exception("Reservation failed");
                uint before = Native.GetClipboardSequenceNumber();
                try
                {
                    if (await ClipboardWriter.CopyAsync("Must never replace clipboard")) throw new Exception("Copy bypassed active selection reservation");
                    bool rejected = false;
                    try { await SelectionReader.ReadAsync(Native.GetForegroundWindow(), 100, CancellationToken.None); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("另一个")) { rejected = true; }
                    if (!rejected || Native.GetClipboardSequenceNumber() != before) throw new Exception("Concurrent read was not excluded");
                }
                finally { ClipboardWriter.Release(); }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await SelectionReader.ReadAsync(Native.GetForegroundWindow(), 100, cancelled.Token); }
                catch (OperationCanceledException) { }
                if (ClipboardWriter.IsBusy || !await ClipboardWriter.CopyAsync("Synthetic reservation released")) throw new Exception("Reservation leaked after cancellation");
                owned = Native.GetClipboardSequenceNumber();
                await DesktopProbe.ReadExpectedAsync("Synthetic reservation released");
            });
        }
        finally
        {
            if (owned != 0 && Native.GetClipboardSequenceNumber() == owned)
            {
                if (native != null) native.Restore(owned);
                else if (SelectionReader.OleSetClipboard(original) == 0 && original != null) SelectionReader.OleFlushClipboard();
            }
            if (original != null && Marshal.IsComObject(original)) Marshal.ReleaseComObject(original);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "clipboard-races.json"), JsonSerializer.Serialize(new { failed, results, syntheticTextOnly = true, realCloudCalls = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
