using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuietTranslate;

// Immediate native text can outlive its writer window and needs an owned snapshot.
// A live owner may render formats lazily: preserve those through OLE without synchronous native reads.
internal sealed class ClipboardSnapshot : IDisposable
{
    private readonly List<(uint format, byte[] bytes)> items = new();
    internal static ClipboardSnapshot? Capture()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        var snapshot = new ClipboardSnapshot(); bool complete = false;
        try
        {
            if (Native.GetClipboardOwner() != IntPtr.Zero) return null;
            long total = 0; uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (format is not (1 or 7 or 13 or 16))
                {
                    var name = new System.Text.StringBuilder(256);
                    if (format < 0xC000 || GetClipboardFormatName(format, name, name.Capacity) == 0 ||
                        name.ToString() is not ("HTML Format" or "Rich Text Format" or "Rich Text Format Without Objects" or "CSV")) return null;
                }
                var memory = GetClipboardData(format); ulong size = GlobalSize(memory).ToUInt64();
                if (memory == IntPtr.Zero || size == 0 || size > 16 * 1024 * 1024 || (total += (long)size) > 16 * 1024 * 1024) return null;
                var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) return null;
                var bytes = new byte[(int)size];
                try { Marshal.Copy(pointer, bytes, 0, bytes.Length); } finally { GlobalUnlock(memory); }
                snapshot.items.Add((format, bytes));
            }
            if (Marshal.GetLastWin32Error() != 0) return null;
            complete = true; return snapshot;
        }
        finally { CloseClipboard(); if (!complete) snapshot.Dispose(); }
    }
    internal bool Restore(uint expectedSequence)
    {
        if (Native.GetClipboardSequenceNumber() != expectedSequence) return true;
        var allocated = new List<(uint format, IntPtr memory)>();
        IntPtr owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        bool open = false;
        try
        {
            if (owner == IntPtr.Zero) return false;
            foreach (var item in items)
            {
                var memory = GlobalAlloc(0x0042, (UIntPtr)item.bytes.Length); if (memory == IntPtr.Zero) return false;
                allocated.Add((item.format, memory)); var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) return false;
                try { Marshal.Copy(item.bytes, 0, pointer, item.bytes.Length); } finally { GlobalUnlock(memory); }
            }
            if (!(open = OpenClipboard(owner))) return false;
            if (Native.GetClipboardSequenceNumber() != expectedSequence) return true;
            if (!EmptyClipboard()) return false;
            for (int i = 0; i < allocated.Count; i++)
            {
                var item = allocated[i];
                if (SetClipboardData(item.format, item.memory) == IntPtr.Zero)
                    throw new InvalidOperationException("原剪贴板未能完整恢复，请检查剪贴板后重试。");
                allocated[i] = (item.format, IntPtr.Zero); // Windows now owns the copied storage.
            }
            return true;
        }
        finally
        {
            if (open) CloseClipboard();
            foreach (var item in allocated) if (item.memory != IntPtr.Zero) GlobalFree(item.memory);
            if (owner != IntPtr.Zero) DestroyWindow(owner);
        }
    }
    public void Dispose() { foreach (var item in items) Array.Clear(item.bytes); items.Clear(); }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
