using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace QuietTranslate;

// Only test-window coordinates, process metadata and input counts; never captures clipboard text or window titles.
internal static class DesktopProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr window);
    internal static IntPtr ClipboardWindow() => CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    internal static void ShowFixture(Window window) { var handle = new WindowInteropHelper(window).Handle; ShowWindow(handle, 5); Focus(handle); }
    internal static void Focus(IntPtr window)
    {
        uint current = GetCurrentThreadId(), foreground = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { Native.SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    internal static async Task UntilAsync(Func<bool> condition, string failure)
    {
        for (int wait = 0; wait < 60 && !condition(); wait++) await Task.Delay(25);
        if (!condition()) throw new Exception(failure);
    }
    internal static bool Hits(Window window) => Native.WindowFromPoint(Native.Cursor()) == new WindowInteropHelper(window).Handle;
    internal static Task<(uint sequence, int attempts)> ReadExpectedAsync(string expected, Action? blocked = null) => Task.Run(() =>
    {
        // Retry acquisition only. A wrong value, missing format or failed copy must still fail immediately.
        for (int attempt = 1; attempt <= 40; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { blocked?.Invoke(); Thread.Sleep(20); continue; }
            try
            {
                var memory = GetClipboardData(13); var size = GlobalSize(memory).ToUInt64();
                if (memory == IntPtr.Zero || size < 2 || size > 2 * 1024 * 1024) throw new Exception("Readback Unicode data missing or oversized");
                var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) throw new Exception("Readback could not lock Unicode data");
                string actual;
                try { actual = Marshal.PtrToStringUni(pointer, (int)size / 2) ?? ""; } finally { GlobalUnlock(memory); }
                int end = actual.IndexOf('\0'); if (end >= 0) actual = actual[..end];
                if (actual != expected) throw new Exception("copied text mismatch");
                return (Native.GetClipboardSequenceNumber(), attempt);
            }
            finally { CloseClipboard(); }
        }
        throw new Exception("Readback clipboard stayed busy: " + JsonHolder());
    });
    private static string JsonHolder() => System.Text.Json.JsonSerializer.Serialize(ClipboardHolder());
    internal static object ClipboardHolder()
    {
        var window = GetOpenClipboardWindow(); Native.GetWindowThreadProcessId(window, out uint pid);
        string process = "unknown";
        try { if (pid > 0) process = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { }
        return new { openWindowPresent = window != IntPtr.Zero, processId = pid, process, writerBusy = ClipboardWriter.IsBusy };
    }
    internal static object Pointer(Window window, bool over)
    {
        var cursor = Native.Cursor(); var handle = new WindowInteropHelper(window).Handle;
        var target = Native.WindowFromPoint(cursor); Native.GetWindowThreadProcessId(target, out uint pid);
        return new { x = cursor.X, y = cursor.Y, nativeHitTarget = target == handle, hitOwnProcess = pid == Environment.ProcessId, wpfMouseOver = over };
    }
    internal sealed class Wheel : IDisposable
    {
        private readonly Native.HookProc proc;
        private IntPtr hook;
        private readonly IntPtr source;
        private int count;
        private bool ownTarget, sourceTarget, leftHeld;
        internal Wheel(IntPtr source)
        {
            this.source = source; proc = OnMouse;
            hook = Native.SetWindowsHookEx(14, proc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new Exception("Cannot install test wheel observer");
        }
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && message.ToInt32() is 0x20A or 0x20E)
            {
                count++; var target = Native.WindowFromPoint(Marshal.PtrToStructure<Native.Point>(data));
                Native.GetWindowThreadProcessId(target, out uint pid); ownTarget = pid == Environment.ProcessId; sourceTarget = target == source; leftHeld = Native.Down(1);
            }
            return Native.CallNextHookEx(hook, code, message, data);
        }
        internal object Snapshot() => new { count, ownTarget, sourceTarget, leftHeld };
        internal int Count => count;
        internal bool LastWasOwnReleasedWheel => ownTarget && !leftHeld;
        public void Dispose() { if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    }
}
