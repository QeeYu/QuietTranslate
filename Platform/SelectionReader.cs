using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace QuietTranslate;

internal static class SelectionReader
{
    [DllImport("ole32.dll")] internal static extern int OleGetClipboard(out System.Runtime.InteropServices.ComTypes.IDataObject? data);
    [DllImport("ole32.dll")] internal static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? data);
    [DllImport("ole32.dll")] internal static extern int OleFlushClipboard();
    private static int probing;
    internal static IntPtr ProtectedWindow { get; private set; }
    internal static string ProbeState { get; private set; } = "idle";
    internal sealed record Selection(string Text, Native.Rect? Bounds, bool UsedClipboard);

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
    // Only one outstanding probe is allowed, so a stuck third-party provider cannot accumulate threads.
    public static async Task<Selection?> TryDirectAsync(IntPtr foreground, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref probing, 1, 0) != 0) return null;
        ProtectedWindow = IntPtr.Zero;
        var task = Task.Run(() =>
        {
            try
            {
                ProbeState = "reading focused element";
                var element = AutomationElement.FocusedElement;
                Native.GetWindowThreadProcessId(foreground, out uint process);
                if (element != null && element.Current.ProcessId == process && element.Current.IsPassword) { ProtectedWindow = foreground; return (Selection?)null; }
                Selection? ReadSelection(AutomationElement? candidate)
                {
                ProbeState = "checking text pattern";
                if (candidate == null || candidate.Current.ProcessId != process || candidate.Current.IsPassword ||
                    !candidate.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
                var ranges = ((TextPattern)pattern).GetSelection();
                ProbeState = "reading selected ranges";
                if (ranges.Length == 0) return null;
                var selected = new System.Text.StringBuilder();
                foreach (var range in ranges)
                {
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
                var focused = ReadSelection(element); if (focused != null) { ProbeState = "selected text read"; return focused; }
                // Electron message text may expose a range near the pointer while the input box owns focus.
                var cursor = Native.Cursor(); var near = AutomationElement.FromPoint(new System.Windows.Point(cursor.X, cursor.Y));
                for (int depth = 0; near != null && depth < 4; depth++)
                {
                    if (near.Current.ProcessId != process) break;
                    var selection = ReadSelection(near); if (selection != null) return selection;
                    near = TreeWalker.ControlViewWalker.GetParent(near);
                }
                ProbeState = "no readable text selection"; return (Selection?)null;
            }
            catch (Exception ex) { ProbeState = ex.GetType().Name; return (Selection?)null; }
            finally { Interlocked.Exchange(ref probing, 0); }
        });
        var completed = await Task.WhenAny(task, Task.Delay(160, ct));
        ct.ThrowIfCancellationRequested();
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新选中文字。");
        return completed == task ? await task : null;
    }

    public static async Task<Selection> GetAsync(IntPtr foreground, int waitMs, CancellationToken ct)
    {
        await WaitForReleaseAsync(ct);
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新选中文字。");
        var selection = await TryDirectAsync(foreground, ct);
        if (ProtectedWindow == foreground) throw new InvalidOperationException("密码输入区域不执行取词。");
        return selection ?? new Selection(await ReadAsync(foreground, waitMs, ct), null, true);
    }

    public static async Task<string> ReadAsync(IntPtr foreground, int waitMs, CancellationToken ct)
    {
        await WaitForReleaseAsync(ct);
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("目标窗口已切换，请重新选中文字后翻译。");
        System.Runtime.InteropServices.ComTypes.IDataObject? saved = null;
        bool captured = false;
        for (int retry = 0; retry < 6; retry++)
        {
            if (OleGetClipboard(out saved) == 0) { captured = true; break; }
            await Task.Delay(20, ct);
        }
        if (!captured) throw new InvalidOperationException("剪贴板正在被占用，未执行复制；请稍后重试。");
        uint before = Native.GetClipboardSequenceNumber();
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
                    if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    {
                        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
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
                for (int retry = 0; retry < 8; retry++)
                {
                    if (Native.GetClipboardSequenceNumber() != copied) { restored = true; break; }
                    if (OleSetClipboard(saved) == 0)
                    {
                        if (saved == null) restored = true;
                        else
                        {
                            uint restoring = Native.GetClipboardSequenceNumber();
                            for (int flush = 0; flush < 6; flush++)
                            {
                                if (Native.GetClipboardSequenceNumber() != restoring) { restored = true; break; }
                                if (OleFlushClipboard() == 0) { restored = true; break; }
                                await Task.Delay(15);
                            }
                        }
                        break;
                    }
                    // Keep this short and non-cancellable so a canceled request still restores the clipboard.
                    await Task.Delay(15);
                }
            }
            if (saved != null && Marshal.IsComObject(saved)) Marshal.ReleaseComObject(saved);
            if (!restored && !foreignCopy) throw new InvalidOperationException("原剪贴板未能完整恢复（被占用或存在延迟格式），请检查剪贴板后重试。");
        }
    }
}
