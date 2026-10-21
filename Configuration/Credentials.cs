using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace QuietTranslate;

internal static class Credentials
{
    internal static string Prefix { get; set; } = "QuietTranslate/";
    // Snapshot every affected credential before writing; settings and credentials succeed together.
    internal static void SaveChanges(IReadOnlyDictionary<string, string> changes, Action commit,
        Func<string, string>? read = null, Action<string, string>? write = null)
    {
        read ??= Get; write ??= Set;
        var previous = new Dictionary<string, string>(); var attempted = new List<string>();
        foreach (var pair in changes) { Validate(pair.Value); previous.Add(pair.Key, read(pair.Key)); }
        try
        {
            foreach (var pair in changes) { attempted.Add(pair.Key); write(pair.Key, pair.Value); }
            commit();
        }
        catch
        {
            bool failed = false;
            for (int i = attempted.Count - 1; i >= 0; i--)
                try { write(attempted[i], previous[attempted[i]]); } catch { failed = true; }
            if (failed) throw new InvalidOperationException("保存失败，部分密钥未能恢复。请重新填写并保存当前服务的密钥。");
            throw;
        }
        finally { previous.Clear(); }
    }
    private static void Validate(string key)
    {
        if (key.Length > 1200 || key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("API Key 格式或长度无效。");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Write(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Delete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
    // Bind each credential to the configured authority/path; changing a gateway never reuses another host's key.
    internal static string Target(Provider provider, string endpoint) => Prefix + provider + "/" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.TrimEnd('/'))));
    internal static string Target(Provider provider, ServiceSettings settings) => Target(provider, settings.Endpoint) + (settings.CredentialId.Length == 0 ? "" : "/" + settings.CredentialId);
    internal static string TargetSecret(Provider provider, ServiceSettings settings) => Get(Target(provider, settings));
    // Presence checks never copy the secret into a managed string or a visible field.
    internal static bool Exists(string target)
    {
        if (!Read(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认密钥保存状态。");
        }
        try { return Marshal.PtrToStructure<Credential>(pointer).CredentialBlobSize > 0; }
        finally { CredFree(pointer); }
    }
    internal static string Get(string target)
    {
        if (!Read(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return "";
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 Windows 凭据失败。");
        }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2) ?? "";
        }
        finally { CredFree(pointer); }
    }
    internal static void Set(string target, string key)
    {
        if (Prefix != "QuietTranslate/" && !target.StartsWith(Prefix, StringComparison.Ordinal) && !target.StartsWith("QuietTranslate.SelfTest/", StringComparison.Ordinal))
            throw new InvalidOperationException("测试进程不能修改正式凭据。");
        Validate(key);
        if (key.Length == 0)
        {
            if (!Delete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var pointer = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var c = new Credential { Type = 1, TargetName = target, Comment = "QuietTranslate API Key", CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(key), CredentialBlob = pointer, Persist = 2, UserName = Environment.UserName };
            if (!Write(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "保存 Windows 凭据失败。");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }
}
