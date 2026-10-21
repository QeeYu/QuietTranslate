using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QuietTranslate;

// One explicitly enabled use owns capture, connection and encrypted history.
// Cancellation invalidates callbacks before stopping workers or deleting files.
internal sealed class LiveSession
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly string historyRoot;
    private readonly Func<SpeechOptions, ChannelWriter<byte[]>, Action<double>, CancellationToken, Task> capture;
    private LiveCaptions? captions;
    private readonly Dictionary<long, (int index, double time, bool stable)> recordSlots = new();
    private CancellationTokenSource? active;
    private Task running = Task.CompletedTask;
    private SessionHistory? history;
    private long generation, disableVersion, sentBytes, usage = -1;
    private long captionTurn;
    private long historyRevision;
    private readonly object state = new();
    internal bool Enabled { get; private set; }
    internal SpeechOptions? Options { get; private set; }
    internal string Status { get; private set; } = "功能已关闭";
    internal string Original { get; private set; } = "";
    internal string Translation { get; private set; } = "";
    internal double Level { get; private set; }
    internal long Usage => Interlocked.Read(ref usage);
    internal double SentSeconds => Interlocked.Read(ref sentBytes) / 32000.0;
    internal int Records => history?.CountOf(false) ?? 0;
    internal int OriginalRecords => history?.CountOf(true) ?? 0;
    internal long HistoryBytes => history?.Bytes ?? 0;
    internal long HistoryRevision => Interlocked.Read(ref historyRevision);
    internal LiveSession(string? historyRoot = null, Func<SpeechOptions, ChannelWriter<byte[]>, Action<double>, CancellationToken, Task>? capture = null)
    {
        this.historyRoot = historyRoot ?? SessionHistory.DefaultRoot;
        this.capture = capture ?? ((options, frames, level, ct) => AudioCapture.RunAsync(options, frame =>
        {
            if (!frames.TryWrite(frame)) { Array.Clear(frame); throw new SpeechConnectionException("网络发送跟不上声音输入，已停止本次连接并丢弃积压音频。", true); }
        }, level, ct));
    }
    internal async Task EnableAsync(SpeechOptions options, string secret)
    {
        long requestedVersion = Interlocked.Read(ref disableVersion);
        options.Validate(); if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("请先在实时语音设置中保存 API Key。");
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Enabled) return;
            await Task.Run(() => SessionHistory.Cleanup(historyRoot)).ConfigureAwait(false);
            history = await Task.Run(() => new SessionHistory(historyRoot)).ConfigureAwait(false);
            bool abandoned;
            lock (state)
            {
                abandoned = requestedVersion != disableVersion;
                if (!abandoned) { Enabled = true; Options = options.Copy(); Interlocked.Exchange(ref sentBytes, 0); Interlocked.Exchange(ref usage, -1); Start(options, secret); }
            }
            if (abandoned) { var unused = history; history = null; await Task.Run(() => unused.Dispose()).ConfigureAwait(false); }
        }
        finally { lifecycle.Release(); }
    }
    internal async Task ReconfigureAsync(SpeechOptions options, string secret)
    {
        options.Validate(); if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("新语音服务未保存密钥。");
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Enabled) return;
            Invalidate(); await running.ConfigureAwait(false); active?.Dispose();
            lock (state) { if (Enabled) { Options = options.Copy(); Start(options, secret); } }
        }
        finally { lifecycle.Release(); }
    }
    private void Invalidate()
    {
        lock (state) { generation++; active?.Cancel(); Original = Translation = ""; Level = 0; ResetCaptions(); }
    }
    internal void RequestDisable()
    {
        lock (state) { Interlocked.Increment(ref disableVersion); Enabled = false; Invalidate(); Status = "正在关闭并删除本次记录…"; }
    }
    internal async Task DisableAsync()
    {
        RequestDisable(); await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // Also invalidate a startup which was still creating its history file.
            Enabled = false; Invalidate(); await running.ConfigureAwait(false); running = Task.CompletedTask; active?.Dispose(); active = null;
            var old = history; history = null;
            try { await Task.Run(() => { old?.Dispose(); SessionHistory.Cleanup(historyRoot); }).ConfigureAwait(false); lock (state) Status = "功能已关闭 · 本次记录已清空"; }
            catch { lock (state) Status = "功能已关闭，但临时记录未能删除；下次启动会重试清理。"; throw new InvalidOperationException(Status); }
            finally { Options = null; Interlocked.Exchange(ref usage, -1); Interlocked.Exchange(ref sentBytes, 0); }
        }
        finally { lifecycle.Release(); }
    }
    internal Task ClearHistoryAsync() => Task.Run(() => { lock (state) { history?.Clear(); recordSlots.Clear(); captions?.ClearHistory(); Interlocked.Increment(ref historyRevision); } });
    internal SpeechCue[] CueSnapshot() { lock (state) return captions?.Snapshot() ?? Array.Empty<SpeechCue>(); }
    internal string LatestSource { get { lock (state) return captions?.LatestSource ?? ""; } }
    internal void ApplyAppearance(SpeechOptions options) { lock (state) { if (Enabled) Options = options.Copy(); } }
    internal Task<System.Collections.Generic.IReadOnlyList<SpeechRecord>> ReadPageAsync(int page, bool original = false, int pageSize = 40) => Task.Run(() => history?.ReadPage(page, pageSize, original, newestFirst: true) ?? Array.Empty<SpeechRecord>());
    internal (string source, string target, string status, double level) Snapshot() { lock (state) return (Original, Translation, Status, Level); }
    private static string SourceName(SpeechOptions options) => options.CaptureProcess ? options.ProcessName : "播放设备";
    private void ResetCaptions()
    {
        recordSlots.Clear();
        captions = Enabled ? new LiveCaptions(() => ++captionTurn, SavePart) : null;
        if (Options != null) captions?.SetBudget((Options.WindowWidth - 36) / Options.FontSize * 2);
    }
    private void SavePart(ReadingPart part, bool original)
    {
        if (Options == null || history == null) return;
        double now = LiveCaptions.Now; bool exists = recordSlots.TryGetValue(part.Id, out var slot);
        if (exists && !part.Closed && slot.stable == part.Stable && now - slot.time < .6) return;
        var record = new SpeechRecord(DateTimeOffset.Now, SourceName(Options), original ? "auto" : Options.Target,
            original ? part.Text : "", original ? "" : part.Text, !part.Closed || part.Interrupted, original ? "original" : "translation", part.Id);
        try
        {
            int saved = history.Upsert(record, exists ? slot.index : null);
            if (saved >= 0) { recordSlots[part.Id] = (saved, now, part.Stable); Interlocked.Increment(ref historyRevision); }
            else Status = "字幕运行中 · 记录已达上限，停止追加；可查看或清空记录";
        }
        catch { Status = "字幕运行中 · 记录写入失败，请检查磁盘；未保存新的记录"; }
        if (part.Closed) recordSlots.Remove(part.Id);
    }
    private void Start(SpeechOptions options, string secret)
    {
        active = new CancellationTokenSource(); var token = active.Token; long owner;
        lock (state) { owner = ++generation; Original = Translation = ""; ResetCaptions(); Status = "正在连接语音翻译…"; }
        running = Task.Run(async () =>
        {
            int retries = 0;
            while (!token.IsCancellationRequested)
            {
                var connectedFor = Stopwatch.StartNew();
                try
                {
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var connectionToken = connection.Token;
                    var client = new GeminiLive(options, secret, (frames, ct) => capture(options, frames, value =>
                    { lock (state) { if (owner == generation && Enabled) { Level = value; captions?.Activity(value, LiveCaptions.Now); } } }, ct));
                    var receiving = client.RunAsync((source, target, complete) =>
                    {
                        lock (state)
                        {
                            if (owner != generation || !Enabled || token.IsCancellationRequested) return;
                            Original = source; Translation = target; captions?.Target(target, complete, LiveCaptions.Now);
                        }
                    }, value => { lock (state) { if (owner == generation && Enabled) Status = value; } },
                    bytes => { if (owner == Interlocked.Read(ref generation) && Enabled) Interlocked.Add(ref sentBytes, bytes); },
                    count => { if (owner == Interlocked.Read(ref generation) && Enabled) Interlocked.Exchange(ref usage, count); }, connectionToken,
                    (value, authoritative) =>
                    {
                        lock (state)
                        {
                            if (owner != generation || !Enabled || token.IsCancellationRequested) return;
                            if (authoritative) Original = value;
                            captions?.Source(value, authoritative, LiveCaptions.Now);
                        }
                    });
                    var processing = Task.Run(async () =>
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                        while (await timer.WaitForNextTickAsync(connectionToken).ConfigureAwait(false))
                            lock (state) if (owner == generation && Enabled && Options != null)
                            {
                                captions?.SetBudget((Options.WindowWidth - 36) / Options.FontSize * 2);
                                captions?.Tick(LiveCaptions.Now);
                            }
                    }, connectionToken);
                    try { await (await Task.WhenAny(receiving, processing).ConfigureAwait(false)).ConfigureAwait(false); }
                    finally { connection.Cancel(); try { await Task.WhenAll(receiving, processing).ConfigureAwait(false); } catch { } }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    lock (state) if (owner == generation && Enabled) captions?.Flush(LiveCaptions.Now);
                    if (connectedFor.Elapsed >= TimeSpan.FromSeconds(30)) retries = 0;
                    bool retry = ex is WebSocketException or OperationCanceledException or AudioCapture.AudioDeviceChangedException || ex is SpeechConnectionException { Retry: true };
                    string message = ex is SpeechConnectionException ? ex.Message : ex is AudioCapture.AudioDeviceChangedException ? "默认播放设备已切换。" : ex is WebSocketException or OperationCanceledException ? "语音网络连接失败或超时，请检查网络、接口和模型权限。" : "音频采集或语音数据处理失败，请检查音源和设备。";
                    if (retry && retries < 3)
                    {
                        retries++; lock (state) { if (owner != generation || !Enabled) break; Status = message + $" 重连 {retries}/3；断线期间内容不补传。"; Original = Translation = ""; Level = 0; ResetCaptions(); }
                        try { await Task.Delay(TimeSpan.FromSeconds(1 << (retries - 1)), token); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    lock (state) { if (owner == generation && Enabled) { Status = message + " 已停止采集；可查看记录，关闭功能后清空。"; Level = 0; } }
                    break;
                }
            }
        });
    }
}
