using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace QuietTranslate;

internal sealed record AudioChoice(string Id, string Label, int ProcessId = 0, long Started = 0, string ProcessName = "");

public static class AudioCapture
{
    internal static bool SupportsProcess => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    internal static IReadOnlyList<AudioChoice> Choices(bool processes)
    {
        var choices = new List<AudioChoice>();
        using var enumerator = new MMDeviceEnumerator();
        if (!processes) choices.Add(new("", "默认播放设备（跟随系统）"));
        var ids = new HashSet<int>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device)
        {
            if (!processes) { choices.Add(new(device.ID, device.FriendlyName)); continue; }
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                using (var session = sessions[i])
                {
                    int pid = (int)session.GetProcessID; if (pid <= 0 || pid == Environment.ProcessId || !ids.Add(pid)) continue;
                    try { using var process = Process.GetProcessById(pid); choices.Add(new(pid.ToString(), process.ProcessName + " · " + pid, pid, process.StartTime.ToUniversalTime().Ticks, process.ProcessName)); } catch { }
                }
            }
            catch { }
        }
        return choices;
    }
    // All capture COM objects live on this worker. No audio file or unbounded queue is created.
    internal static Task RunAsync(SpeechOptions options, Action<byte[]> frame, Action<double> level, CancellationToken ct) => Task.Run(() =>
    {
        using var enumerator = new MMDeviceEnumerator(); MMDevice? device = null; AudioClient? client = null; Process? process = null;
        try
        {
            if (options.CaptureProcess)
            {
                if (!SupportsProcess) throw new InvalidOperationException("当前 Windows 不支持按应用采集，请改选播放设备。");
                process = Process.GetProcessById(options.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != options.ProcessStarted || process.ProcessName != options.ProcessName) throw new InvalidOperationException("应用已重新启动，请重新选择音源。");
                client = ActivateProcess(options.ProcessId, ct);
            }
            else { device = options.DeviceId.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(options.DeviceId); client = device.AudioClient; }
            var format = new WaveFormat(16000, 16, 1);
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, 1000000, 0, format, Guid.Empty);
            var capture = client.AudioCaptureClient; byte[] chunk = new byte[3200]; int filled = 0; long lastCheck = 0, lastFrame = Environment.TickCount64;
            client.Start();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (Environment.TickCount64 - lastCheck > 500)
                    {
                        lastCheck = Environment.TickCount64;
                        if (process != null && process.HasExited) throw new InvalidOperationException("所选应用已退出，请关闭功能并重新选择音源。");
                        if (device != null && device.State != DeviceState.Active) throw new InvalidOperationException("所选播放设备已断开。");
                        if (device != null && options.DeviceId.Length == 0)
                        {
                            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                            if (current.ID != device.ID) throw new AudioDeviceChangedException();
                        }
                    }
                    int packet;
                    while ((packet = capture.GetNextPacketSize()) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                        try
                        {
                            int bytes = checked(frames * 2); if (bytes > 1024 * 1024) throw new InvalidOperationException("音频缓冲异常。");
                            byte[] part = new byte[bytes];
                            if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(data, part, 0, bytes);
                            for (int offset = 0; offset < bytes;)
                            {
                                int take = Math.Min(bytes - offset, chunk.Length - filled); Buffer.BlockCopy(part, offset, chunk, filled, take); offset += take; filled += take;
                                if (filled == chunk.Length)
                                {
                                    int peak = 0; for (int p = 0; p < chunk.Length; p += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(chunk, p)));
                                    level(peak / 32768.0); frame(chunk); chunk = new byte[3200]; filled = 0; lastFrame = Environment.TickCount64;
                                }
                            }
                        }
                        finally { capture.ReleaseBuffer(frames); }
                    }
                    // Loopback may stop producing packets when playback stops. Supply the
                    // trailing silence needed to finish the last spoken sentence.
                    if (Environment.TickCount64 - lastFrame >= 100)
                    {
                        level(0); frame(chunk); chunk = new byte[3200]; filled = 0; lastFrame = Environment.TickCount64;
                    }
                    if (ct.WaitHandle.WaitOne(10)) break;
                }
            }
            finally { client.Stop(); Array.Clear(chunk); }
        }
        finally { client?.Dispose(); device?.Dispose(); process?.Dispose(); }
    }, ct);

    internal sealed class AudioDeviceChangedException : Exception { }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessParameters { public int Type; public uint Pid; public int Mode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public uint Size; [FieldOffset(16)] public IntPtr Data; }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOperation { [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated); }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICompletion { [PreserveSig] int ActivateCompleted(IOperation operation); }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface IAgile { }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : ICompletion, IAgile
    {
        internal readonly TaskCompletionSource<AudioClient> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private bool abandoned;
        internal void Abandon() { lock (gate) { abandoned = true; if (Done.Task.IsCompletedSuccessfully) Done.Task.Result.Dispose(); } }
        public int ActivateCompleted(IOperation operation)
        {
            object? activated = null;
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int result, out activated)); Marshal.ThrowExceptionForHR(result);
                lock (gate)
                {
                    if (abandoned) { if (Marshal.IsComObject(activated)) Marshal.ReleaseComObject(activated); }
                    else Done.TrySetResult(new AudioClient((IAudioClient)activated));
                }
            }
            catch { Done.TrySetException(new InvalidOperationException("无法启动按应用音频采集，请尝试播放设备采集。")); }
            return 0;
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid, ref Variant parameters, ICompletion completed, out IOperation operation);
    private static AudioClient ActivateProcess(int pid, CancellationToken ct)
    {
        var completion = new Completion(); var parameters = new ProcessParameters { Type = 1, Pid = (uint)pid, Mode = 0 };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<ProcessParameters>()); IOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(parameters, memory, false);
            var variant = new Variant { Type = 65, Size = (uint)Marshal.SizeOf<ProcessParameters>(), Data = memory };
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, completion, out operation));
            try { return completion.Done.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).GetAwaiter().GetResult(); }
            catch
            {
                completion.Abandon();
                throw;
            }
        }
        finally { Marshal.FreeHGlobal(memory); if (operation != null && Marshal.IsComObject(operation)) Marshal.ReleaseComObject(operation); GC.KeepAlive(completion); }
    }
}
