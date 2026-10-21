using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Collections.Generic;

namespace QuietTranslate;

internal static class SelectionReader
{
    [DllImport("ole32.dll")] internal static extern int OleGetClipboard(out System.Runtime.InteropServices.ComTypes.IDataObject? data);
    [DllImport("ole32.dll")] internal static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? data);
    [DllImport("ole32.dll")] internal static extern int OleFlushClipboard();
    private static readonly HashSet<uint> probing = new();
    internal static IntPtr ProtectedWindow { get; private set; }
    internal static string ProbeState { get; private set; } = "idle";
    internal sealed record Selection(string Text, Native.Rect? Bounds, bool UsedClipboard);

    // Discord's accessibility ranges can include unselected timestamps and reaction/action labels.
    // Only an explicit translation action may copy; never scrub words from a user's selection.
    internal static bool PreferClipboard(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant() is "discord" or "discordptb" or "discordcanary" or "discorddevelopment";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static async Task WaitForReleaseAsync(CancellationToken ct)
    {
        for (int i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Native.Down(0x11) && !Native.Down(0x12) && !Native.Down(0x10) && !Native.Down(0x5B) && !Native.Down(0x5C)) return;
            await Task.Delay(10, ct);
        }
        throw new InvalidOperationException("请松开快捷键后重试。");
    }

    // UI Automation is optional: some Electron/Discord builds do not expose text ranges.
    // A stuck provider blocks only its own process. Bound outstanding probes to avoid accumulating threads.
    public static async Task<Selection?> TryDirectAsync(IntPtr foreground, CancellationToken ct, Native.Point? selectionPoint = null, bool onlyCheckPassword = false)
    {
        ct.ThrowIfCancellationRequested();
        Native.GetWindowThreadProcessId(foreground, out uint probeProcess);
        lock (probing) { if (probing.Contains(probeProcess) || probing.Count >= 4) return null; probing.Add(probeProcess); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(160);
        var probeToken = deadline.Token;
        ProtectedWindow = IntPtr.Zero;
        bool password = false;
        var task = Task.Run(() =>
        {
            try
            {
                ProbeState = "reading focused element";
                var element = AutomationElement.FocusedElement;
                Native.GetWindowThreadProcessId(foreground, out uint process);
                if (element != null && element.Current.ProcessId == process && element.Current.IsPassword) { password = true; return (Selection?)null; }
                if (onlyCheckPassword) { ProbeState = "clipboard selection policy"; return (Selection?)null; }
                Selection? ReadSelection(AutomationElement? candidate)
                {
                probeToken.ThrowIfCancellationRequested();
                ProbeState = "checking text pattern";
                if (candidate == null || candidate.Current.ProcessId != process || candidate.Current.IsPassword ||
                    !candidate.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
                var ranges = ((TextPattern)pattern).GetSelection();
                probeToken.ThrowIfCancellationRequested();
                ProbeState = "reading selected ranges";
                if (ranges.Length == 0) return null;
                var selected = new System.Text.StringBuilder();
                foreach (var range in ranges)
                {
                    probeToken.ThrowIfCancellationRequested();
                    selected.Append(range.GetText(Math.Max(1, 8001 - selected.Length)));
                    if (selected.Length >= 8001) break;
                }
                if (string.IsNullOrWhiteSpace(selected.ToString())) return null;
                var rectangles = ranges[^1].GetBoundingRectangles();
                Native.Rect? bounds = null;
                if (rectangles.Length > 0 && !rectangles[^1].IsEmpty)
                { var r = rectangles[^1]; bounds = new Native.Rect { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom }; }
                return new Selection(selected.ToString(), bounds, false);
                }
                // Mouse gestures use their release point, even if the pointer has since moved away.
                // Table cells can be many raw-tree levels below the document that owns the selection.
                if (selectionPoint == null)
                { var focused = ReadSelection(element); if (focused != null) { ProbeState = "selected text read"; return focused; } }
                var cursor = selectionPoint ?? Native.Cursor(); var near = AutomationElement.FromPoint(new System.Windows.Point(cursor.X, cursor.Y));
                for (int depth = 0; near != null && depth < 16; depth++)
                {
                    probeToken.ThrowIfCancellationRequested();
                    if (near.Current.ProcessId != process) break;
                    var selection = ReadSelection(near); if (selection != null) return selection;
                    near = TreeWalker.RawViewWalker.GetParent(near);
                }
                if (selectionPoint != null)
                { var focused = ReadSelection(element); if (focused != null) { ProbeState = "selected text read"; return focused; } }
                ProbeState = "no readable text selection"; return (Selection?)null;
            }
            catch (Exception ex) { ProbeState = ex.GetType().Name; return (Selection?)null; }
            finally { lock (probing) probing.Remove(probeProcess); }
        });
        var completed = await Task.WhenAny(task, Task.Delay(160, ct));
        ct.ThrowIfCancellationRequested();
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新选中文字。");
        if (completed != task) { deadline.Cancel(); return null; }
        var result = await task; if (password) ProtectedWindow = foreground; return result;
    }

    public static async Task<Selection> GetAsync(IntPtr foreground, int waitMs, CancellationToken ct)
    {
        await WaitForReleaseAsync(ct);
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新选中文字。");
        var selection = await TryDirectAsync(foreground, ct, onlyCheckPassword: PreferClipboard(foreground));
        if (ProtectedWindow == foreground) throw new InvalidOperationException("密码输入区域不执行取词。");
        return selection != null && !PreferClipboard(foreground) ? selection : new Selection(await ReadAsync(foreground, waitMs, ct), null, true);
    }

    public static async Task<string> ReadAsync(IntPtr foreground, int waitMs, CancellationToken ct)
    {
        await WaitForReleaseAsync(ct);
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新选中文字后翻译。");
        // Reserve the whole temporary copy/restore transaction, not only individual writes.
        // Do not queue stale requests or hold Windows' clipboard lock while awaiting input.
        if (!ClipboardWriter.TryAcquire()) throw new InvalidOperationException("轻译的另一个复制或取词操作尚未完成，请稍后重试。");
        try { return await ReadClipboardAsync(foreground, waitMs, ct); }
        finally { ClipboardWriter.Release(); }
    }

    private static async Task<string> ReadClipboardAsync(IntPtr foreground, int waitMs, CancellationToken ct)
    {
        await EnsureResponsiveAsync(Native.GetClipboardOwner(), ct);
        System.Runtime.InteropServices.ComTypes.IDataObject? saved = null;
        uint before = Native.GetClipboardSequenceNumber();
        // Snapshot eager native data before OLE creates a clipboard proxy or synthetic formats.
        using var nativeSnapshot = ClipboardSnapshot.Capture();
        bool captured = nativeSnapshot != null;
        for (int retry = 0; !captured && retry < 6; retry++)
        {
            before = Native.GetClipboardSequenceNumber();
            if (OleGetClipboard(out saved) == 0) { captured = true; break; }
            await Task.Delay(20, ct);
        }
        if (!captured) throw new InvalidOperationException("剪贴板正在被占用，未执行复制；请稍后重试。");
        if (Native.GetClipboardSequenceNumber() != before || Native.GetForegroundWindow() != foreground || ct.IsCancellationRequested)
        {
            if (saved != null && Marshal.IsComObject(saved)) Marshal.ReleaseComObject(saved);
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("窗口或剪贴板已变化，未执行复制；请重新翻译。");
        }
        uint copied = before;
        Native.GetWindowThreadProcessId(foreground, out uint sourceProcess);
        bool foreignCopy = false;
        try
        {
            Native.CopyKeys();
            int elapsed = 0;
            while (elapsed < waitMs)
            {
                await Task.Delay(15, ct); elapsed += 15;
                if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重试。");
                copied = Native.GetClipboardSequenceNumber();
                if (copied == before) continue;
                Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out uint clipboardProcess);
                if (clipboardProcess != 0 && clipboardProcess != sourceProcess)
                { foreignCopy = true; throw new InvalidOperationException("其他应用修改了剪贴板，已保留最新内容；请重新翻译。"); }
                try
                {
                    await EnsureResponsiveAsync(Native.GetClipboardOwner(), ct);
                    if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新翻译。");
                    if (ClipboardSnapshot.ReadUnicodeText() is string text)
                    {
                        // Sequence-number ownership prevents overwriting a later user/app copy.
                        if (Native.GetClipboardSequenceNumber() != copied) throw new InvalidOperationException("剪贴板已再次变化，请重新翻译。");
                        return text;
                    }
                }
                catch (COMException) { /* retry a busy clipboard */ }
            }
            throw new InvalidOperationException("没有复制到选中文字。Discord 请先拖选消息正文；也可用截图翻译。");
        }
        finally
        {
            try
            {
                // Include changes made before cancellation, including a copy delivered while polling.
                if (copied == before)
                {
                    uint latest = Native.GetClipboardSequenceNumber();
                    Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out uint ownerProcess);
                    if (ownerProcess == sourceProcess) copied = latest;
                }
                bool changed = copied != before;
                bool restored = !changed;
                if (changed && !foreignCopy)
                {
                    // Clipboard sync tools can hold the lock for over a second. Retry asynchronously,
                    // checking ownership each time so a newer user copy always wins.
                    for (int retry = 0; retry < 80; retry++)
                    {
                        if (Native.GetClipboardSequenceNumber() != copied) { restored = true; break; }
                        if (nativeSnapshot != null)
                        {
                            if (nativeSnapshot.Restore(copied)) { restored = true; break; }
                            await Task.Delay(25); continue;
                        }
                        if (OleSetClipboard(saved) == 0)
                        {
                            if (saved == null) restored = true;
                            else
                            {
                                uint restoring = Native.GetClipboardSequenceNumber();
                                for (int flush = 0; flush < 80; flush++)
                                {
                                    if (Native.GetClipboardSequenceNumber() != restoring) { restored = true; break; }
                                    if (OleFlushClipboard() == 0) { restored = true; break; }
                                    await Task.Delay(25);
                                }
                            }
                            break;
                        }
                        // Keep this short and non-cancellable so a canceled request still restores the clipboard.
                        await Task.Delay(25);
                    }
                }
                if (!restored && !foreignCopy) throw new InvalidOperationException("原剪贴板未能完整恢复（其他程序持续占用或存在延迟格式），请检查剪贴板同步软件后重试。");
            }
            finally { if (saved != null && Marshal.IsComObject(saved)) Marshal.ReleaseComObject(saved); }
        }
    }
    private static async Task EnsureResponsiveAsync(IntPtr owner, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (owner == IntPtr.Zero || !IsWindow(owner) || SendMessageTimeout(owner, 0, IntPtr.Zero, IntPtr.Zero, 0x0002 | 0x0020, 40, out _) != IntPtr.Zero) return;
            await Task.Delay(20, ct);
        }
        throw new InvalidOperationException("剪贴板所属应用暂未响应，未读取延迟内容；请稍后重试或使用截图翻译。");
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
