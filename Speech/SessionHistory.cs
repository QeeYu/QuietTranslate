using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuietTranslate;

internal sealed record SpeechRecord(DateTimeOffset Time, string Source, string Target, string Original, string Translation, bool Pending = false, string Kind = "translation", long Context = 0, bool Failed = false);

// Only file offsets are retained in RAM. Each immutable record is protected for the Windows user.
internal sealed class SessionHistory : IDisposable
{
    internal static string DefaultRoot => Path.Combine(Settings.DirectoryPath, "LiveHistory");
    private readonly object gate = new();
    private readonly string path;
    private FileStream? file;
    private readonly List<long> offsets = new();
    private readonly List<bool> originals = new();
    private readonly long limit;
    internal bool Full { get; private set; }
    internal int Count { get { lock (gate) return offsets.Count; } }
    internal int CountOf(bool original) { lock (gate) return originals.Count(value => value == original); }
    internal long Bytes { get { lock (gate) return file?.Length ?? 0; } }
    internal SessionHistory(string root, long limit = 20 * 1024 * 1024)
    {
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("记录目录不能是链接。");
        this.limit = limit; path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".qth");
        file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.RandomAccess);
    }
    internal bool Append(SpeechRecord record) => Upsert(record) >= 0;
    internal int Upsert(SpeechRecord record, int? replace = null)
    {
        lock (gate) { if (file == null || Full) return -1; }
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record), protectedBytes;
        try { if (plain.Length > 128 * 1024) throw new IOException("单条字幕过长。"); protectedBytes = Protect(plain, false); }
        finally { Array.Clear(plain); }
        try
        {
            lock (gate)
            {
                if (file == null || Full) return -1;
                if (file.Length + 4 + protectedBytes.Length > limit) { Full = true; return -1; }
                long position = file.Length; file.Position = position;
                try
                {
                    file.Write(BitConverter.GetBytes(protectedBytes.Length)); file.Write(protectedBytes); file.Flush();
                }
                catch { file.SetLength(position); throw; }
                if (replace is int index && index >= 0 && index < offsets.Count) { offsets[index] = position; originals[index] = record.Kind == "original"; return index; }
                offsets.Add(position); originals.Add(record.Kind == "original"); return offsets.Count - 1;
            }
        }
        finally { Array.Clear(protectedBytes); }
    }
    internal IReadOnlyList<SpeechRecord> ReadPage(int page, int pageSize = 40, bool original = false, bool newestFirst = false)
    {
        lock (gate)
        {
            var result = new List<SpeechRecord>(); if (file == null) return result;
            int start = Math.Clamp(pageSize, 1, 50) * Math.Max(0, page);
            var indices = newestFirst ? Enumerable.Range(0, offsets.Count).Select(i => offsets.Count - 1 - i) : Enumerable.Range(0, offsets.Count);
            var selected = indices.Where(i => originals[i] == original).Skip(start).Take(Math.Clamp(pageSize, 1, 50));
            Span<byte> header = stackalloc byte[4];
            foreach (int i in selected)
            {
                file.Position = offsets[i]; file.ReadExactly(header);
                int size = BitConverter.ToInt32(header); if (size is < 1 or > 160 * 1024) throw new IOException("临时记录格式无效。");
                byte[] encrypted = new byte[size]; file.ReadExactly(encrypted); byte[] plain = Protect(encrypted, true);
                try { result.Add(JsonSerializer.Deserialize<SpeechRecord>(plain) ?? throw new IOException("临时记录格式无效。")); }
                finally { Array.Clear(encrypted); Array.Clear(plain); }
            }
            return result;
        }
    }
    internal void Clear()
    {
        lock (gate) { if (file == null) return; file.SetLength(0); file.Flush(); offsets.Clear(); originals.Clear(); Full = false; }
    }
    public void Dispose()
    {
        lock (gate) { file?.Dispose(); file = null; offsets.Clear(); originals.Clear(); }
        try { File.Delete(path); }
        catch { MarkPending(Path.GetDirectoryName(path)!); throw new IOException("临时记录未能删除，下次启动会重试清理。"); }
    }
    internal static void Cleanup(string root)
    {
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("记录目录不能是链接。");
        bool failed = false;
        foreach (var item in Directory.EnumerateFiles(root, "*.qth"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(item), "N", out _)) continue;
            try { File.Delete(item); } catch { failed = true; }
        }
        if (failed) { MarkPending(root); throw new IOException("上次使用的语音记录未能清理，请关闭占用记录的程序后重试。"); }
        File.Delete(Path.Combine(root, "cleanup.pending"));
    }
    private static void MarkPending(string root) { try { File.WriteAllText(Path.Combine(root, "cleanup.pending"), "pending"); } catch { } }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Protect(byte[] data, bool decrypt)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned); Blob output = default;
        try
        {
            var input = new Blob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok || output.Size is < 1 or > 160 * 1024) throw new IOException("无法加密或读取当前用户的临时字幕记录。");
            var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) { if (decrypt && output.Size is > 0 and <= 160 * 1024) for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
            pinned.Free();
        }
    }
}
