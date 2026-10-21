using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuietTranslate;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static Point Cursor() { GetCursorPos(out var point); return point; }
    internal static Rect WorkArea(Point point)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(point, 2), ref info)) throw new Win32Exception();
        return info.Work;
    }
    internal static void CopyKeys()
    {
        var inputs = new Input[4];
        ushort[] keys = { 0x11, 0x43, 0x43, 0x11 };
        for (int i = 0; i < 4; i++) inputs[i] = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = keys[i], Flags = i >= 2 ? 2u : 0 } } };
        if (SendInput(4, inputs, Marshal.SizeOf<Input>()) != 4)
        {
            // Release synthetic keys even when a target with higher integrity rejects input.
            for (int i = 0; i < 4; i++) inputs[i].Data.Keyboard.Flags = 2;
            SendInput(4, inputs, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("无法发送复制按键。请让本工具与目标应用使用相同权限，或改用截图翻译。");
        }
    }
    internal static void TapKey(ushort key)
    {
        var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = key } } }, new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = key, Flags = 2 } } } };
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("无法发送测试按键。");
    }
    internal static void TestChord(Hotkey hotkey)
    {
        ushort[] modifiers = { 0x11, 0x12, 0x10, 0x5B }; uint[] masks = { 2, 1, 4, 8 };
        var inputs = new System.Collections.Generic.List<Input>();
        Input Key(ushort vk, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Flags = up ? 2u : 0 } } };
        for (int i = 0; i < 4; i++) if ((hotkey.Modifiers & masks[i]) != 0) inputs.Add(Key(modifiers[i], false));
        inputs.Add(Key((ushort)hotkey.Key, false)); inputs.Add(Key((ushort)hotkey.Key, true));
        for (int i = 3; i >= 0; i--) if ((hotkey.Modifiers & masks[i]) != 0) inputs.Add(Key(modifiers[i], true));
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) != inputs.Count)
        { for (int i = 0; i < 4; i++) { var release = new[] { Key(modifiers[i], true) }; SendInput(1, release, Marshal.SizeOf<Input>()); } throw new InvalidOperationException("测试组合键发送失败。"); }
    }
}

internal readonly record struct Hotkey(uint Modifiers, uint Key)
{
    internal static Hotkey Parse(string value)
    {
        uint mods = 0, key = 0;
        foreach (string item in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (item.ToUpperInvariant())
            {
                case "CTRL": mods |= 2; break;
                case "ALT": mods |= 1; break;
                case "SHIFT": mods |= 4; break;
                case "WIN": mods |= 8; break;
                default:
                    if (key != 0) throw new ArgumentException("快捷键只允许一个字母、数字或 F1–F12。");
                    if (item.Length == 1 && char.IsAsciiLetterOrDigit(item[0])) key = char.ToUpperInvariant(item[0]);
                    else if (item.Equals("Pause", StringComparison.OrdinalIgnoreCase) || item.Equals("Break", StringComparison.OrdinalIgnoreCase)) key = 0x13;
                    else if (item.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(item[1..], out int f) && f >= 1 && f <= 11) key = (uint)(0x6F + f);
                    else throw new ArgumentException("快捷键示例：Ctrl+Alt+Q 或 Ctrl+Shift+F8。");
                    break;
            }
        }
        if (key == 0 || (mods == 0 && key != 0x13 && key is not (>= 0x70 and <= 0x7A))) throw new ArgumentException("字母、数字需组合键；可单独使用 Pause 或 F1–F11。F12 为系统调试器保留。");
        return new Hotkey(mods, key);
    }
    internal static string Format(uint mods, uint key) => ((mods & 2) != 0 ? "Ctrl+" : "") + ((mods & 1) != 0 ? "Alt+" : "") + ((mods & 4) != 0 ? "Shift+" : "") + ((mods & 8) != 0 ? "Win+" : "") + (key == 0x13 ? "Pause" : key is >= 0x70 and <= 0x7A ? "F" + (key - 0x6F) : ((char)key).ToString());
}

internal sealed class Hotkeys : IDisposable
{
    private readonly HwndSource source = new(new HwndSourceParameters("QuietTranslate.Hotkeys") { ParentWindow = new IntPtr(-3) });
    public event Action<int>? Pressed;
    private readonly System.Collections.Generic.Dictionary<int, Hotkey> registered = new();
    public Hotkeys()
    {
        source.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        { if (msg == 0x0312) { handled = true; Pressed?.Invoke(w.ToInt32()); } return IntPtr.Zero; });
    }
    public void Apply(string selection, string screenshot)
    {
        ApplyMap(new() { [1] = Hotkey.Parse(selection), [2] = Hotkey.Parse(screenshot) });
    }
    internal bool SpeechUnavailable { get; private set; }
    internal void Apply(Settings s, bool blocked, bool manualBlocked = false, bool speechEnabled = false)
    {
        ApplyMap(blocked
            ? (manualBlocked ? new() { [3] = Hotkey.Parse(s.PauseHotkey) } : new() { [3] = Hotkey.Parse(s.PauseHotkey), [4] = Hotkey.Parse(s.ManualHotkey) })
            : new() { [1] = Hotkey.Parse(s.SelectionHotkey), [2] = Hotkey.Parse(s.ScreenshotHotkey), [3] = Hotkey.Parse(s.PauseHotkey), [4] = Hotkey.Parse(s.ManualHotkey) });
        SpeechUnavailable = false;
        if (blocked && !speechEnabled) return;
        var key = Hotkey.Parse(s.Speech.Hotkey);
        SpeechUnavailable = !Native.RegisterHotKey(source.Handle, 5, key.Modifiers | 0x4000, key.Key);
        if (!SpeechUnavailable) registered[5] = key;
    }
    private void ApplyMap(System.Collections.Generic.Dictionary<int, Hotkey> next)
    {
        if (new System.Collections.Generic.HashSet<Hotkey>(next.Values).Count != next.Count) throw new ArgumentException("快捷键不能重复。");
        var previous = new System.Collections.Generic.Dictionary<int, Hotkey>(registered); Suspend();
        try { foreach (var pair in next) { if (!Native.RegisterHotKey(source.Handle, pair.Key, pair.Value.Modifiers | 0x4000, pair.Value.Key)) throw new InvalidOperationException(Hotkey.Format(pair.Value.Modifiers, pair.Value.Key) + " 被占用，请更换快捷键。"); registered[pair.Key] = pair.Value; } }
        catch { Suspend(); foreach (var pair in previous) if (Native.RegisterHotKey(source.Handle, pair.Key, pair.Value.Modifiers | 0x4000, pair.Value.Key)) registered[pair.Key] = pair.Value; throw; }
    }
    public void Suspend() { foreach (int id in registered.Keys) Native.UnregisterHotKey(source.Handle, id); registered.Clear(); }
    internal static bool Available(Hotkey key)
    {
        if (!Native.RegisterHotKey(IntPtr.Zero, 0x4A71, key.Modifiers | 0x4000, key.Key)) return false;
        Native.UnregisterHotKey(IntPtr.Zero, 0x4A71); return true;
    }
    public void Dispose() { Suspend(); source.Dispose(); }
}
