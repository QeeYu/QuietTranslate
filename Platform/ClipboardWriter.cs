using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuietTranslate;

internal static class ClipboardWriter
{
    private static int busy;
    internal static bool IsBusy => Volatile.Read(ref busy) != 0;
    internal static bool TryAcquire() => Interlocked.CompareExchange(ref busy, 1, 0) == 0;
    internal static void Release() => Interlocked.Exchange(ref busy, 0);
    // Eager Unicode text avoids OLE's flush/marshalling path. Clipboard ownership
    // and bounded retries stay off the UI thread; no stale copy requests are queued.
    internal static Task<bool> CopyAsync(string text)
    {
        if (text.Length == 0 || !TryAcquire()) return Task.FromResult(false);
        uint requestedSequence = Native.GetClipboardSequenceNumber();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            bool success = false;
            try { success = WriteText(text, requestedSequence); }
            catch { /* Clipboard contents and exception details are never logged. */ }
            finally { Release(); completion.TrySetResult(success); }
        }) { IsBackground = true, Name = "QuietTranslate.Copy" };
        try { worker.SetApartmentState(ApartmentState.STA); worker.Start(); }
        catch { Release(); completion.TrySetResult(false); }
        return completion.Task;
    }
    private static bool WriteText(string text, uint requestedSequence)
    {
        IntPtr owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) return false;
        IntPtr memory = IntPtr.Zero; bool open = false;
        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(0x0042, (UIntPtr)bytes.Length); // movable, zero initialized
            if (memory == IntPtr.Zero) return false;
            IntPtr buffer = GlobalLock(memory); if (buffer == IntPtr.Zero) return false;
            try { Marshal.Copy(bytes, 0, buffer, bytes.Length); } finally { GlobalUnlock(memory); }
            for (int i = 0; i < 30 && !(open = OpenClipboard(owner)); i++) Thread.Sleep(20);
            // Check under the acquired lock: a copy made while we waited always wins.
            if (!open || Native.GetClipboardSequenceNumber() != requestedSequence ||
                !EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) return false; // CF_UNICODETEXT
            memory = IntPtr.Zero; // ownership transferred to Windows, survives the worker/window
            return true;
        }
        finally { if (open) CloseClipboard(); if (memory != IntPtr.Zero) GlobalFree(memory); DestroyWindow(owner); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
