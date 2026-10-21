using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QuietTranslate;

public sealed partial class App
{
    private LiveSession? speech;
    private SpeechWindow? speechWindow;
    private readonly SemaphoreSlim speechToggle = new(1, 1);
    private Task speechStartupCleanup = Task.CompletedTask;
    private bool speechClosing, speechStarting;

    private void InitializeSpeech(string testScope)
    {
        string root = testScope.Length == 0 ? SessionHistory.DefaultRoot : System.IO.Path.Combine(Environment.CurrentDirectory, "work", "speech-fixture-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(testScope))));
        speech = new LiveSession(root);
        speechStartupCleanup = Task.Run(() => SessionHistory.Cleanup(root));
        _ = ObserveSpeechCleanupAsync();
    }
    private async Task ObserveSpeechCleanupAsync()
    {
        try { await speechStartupCleanup; }
        catch { if (!exiting) MessageBox.Show("上次语音记录未能清理。请关闭占用记录文件的程序；再次开启语音时会重试清理。", "轻译"); }
    }
    private async Task ToggleSpeechAsync()
    {
        if (exiting || speech == null) return;
        if (speech.Enabled || speechStarting) { await StopSpeechAsync(); return; }
        if (recording) return;
        RefreshState(); if (blocked || speechClosing) return;
        if (!await speechToggle.WaitAsync(0)) return;
        speechStarting = true;
        try
        {
            try { await speechStartupCleanup; } catch { /* EnableAsync retries cleanup and reports failures. */ }
            if (exiting || blocked || speechClosing) return;
            var options = settings.Speech.Copy(); options.Validate();
            string key = Credentials.Get(options.SecretTarget);
            await speech.EnableAsync(options, key);
            if (exiting || blocked || !speech.Enabled) { await speech.DisableAsync(); return; }
            var view = new SpeechWindow(speech, () => _ = StopSpeechAsync(), OpenSpeechSettings, ChangeSpeechTargetAsync, SaveSpeechAppearance);
            view.Closed += (_, _) => { if (ReferenceEquals(speechWindow, view)) speechWindow = null; };
            speechWindow = view; view.Show(); RefreshSpeechTray();
        }
        catch (Exception ex)
        {
            if (speech.Enabled) try { await speech.DisableAsync(); } catch { }
            if (!exiting) { OpenSpeechSettings(); MessageBox.Show(ex is ArgumentException or InvalidOperationException ? ex.Message : "无法开启实时语音字幕，请检查密钥、音源和记录目录。", "轻译"); }
        }
        finally { speechStarting = false; speechToggle.Release(); }
    }
    private async Task StopSpeechAsync()
    {
        if (speech == null || speechClosing || exiting) return;
        speechClosing = true; speech.RequestDisable();
        var view = speechWindow; speechWindow = null; view?.Close(); RefreshSpeechTray();
        await speechToggle.WaitAsync();
        try { await speech.DisableAsync(); }
        catch { if (!exiting) MessageBox.Show("语音翻译已停止，但本次记录未能全部删除。下次启动会重试清理，请检查记录文件是否被其他程序占用。", "轻译"); }
        finally { speechClosing = false; speechToggle.Release(); if (!exiting) RefreshState(true); }
    }
    private async Task ChangeSpeechTargetAsync(string language)
    {
        if (speech is not { Enabled: true } || exiting || speechClosing) return;
        var updated = settings.Speech.Copy(); updated.Target = language; updated.Validate();
        await speech.ReconfigureAsync(updated, Credentials.Get(updated.SecretTarget));
        if (!speech.Enabled || exiting) return;
        settings.Speech.Target = language; settings.Save(); speechWindow?.ApplyOptions();
    }
    private void OpenSpeechSettings() { OpenSettings(); settingsWindow?.ShowPage(6); }
    private void SaveSpeechAppearance(SpeechOptions options)
    {
        var updated = settings.Speech.Copy();
        updated.CopyAppearanceFrom(options);
        updated.Validate(); var previous = settings.Speech; settings.Speech = updated;
        try { settings.Save(); }
        catch { settings.Speech = previous; throw; }
        speech?.ApplyAppearance(updated); settingsWindow?.SyncSpeechAppearance(updated);
    }
    private void RefreshSpeechTray()
    {
        trayMenu?.RefreshSpeech(speech?.Enabled == true, settings.Speech);
        if (tray != null && hotkeys?.SpeechUnavailable == true && speech?.Enabled != true && !blocked) { tray.Text = "轻译 · 语音快捷键冲突，可从托盘开关"; return; }
        if (tray != null && (!blocked || speech?.Enabled == true)) tray.Text = speech?.Enabled == true ? (blocked ? "轻译 · 当前应用划词暂停 · 语音仍开启" : "轻译 · 实时语音已开启 · 关闭即清空记录") : "轻译 · 已启用 · 语音关闭";
    }
    private async Task ApplySpeechOptionsAsync(SpeechOptions before, bool keyChanged = false)
    {
        if (speech is not { Enabled: true } || speechClosing || (!keyChanged && JsonSerializer.Serialize(before) == JsonSerializer.Serialize(settings.Speech))) return;
        if (!keyChanged && before.ConnectionIdentity == settings.Speech.ConnectionIdentity) { speech.ApplyAppearance(settings.Speech); speechWindow?.ApplyOptions(); return; }
        try
        {
            string key = Credentials.Get(settings.Speech.SecretTarget);
            if (keyChanged && string.IsNullOrWhiteSpace(key)) { await StopSpeechAsync(); return; }
            await speech.ReconfigureAsync(settings.Speech.Copy(), key); speechWindow?.ApplyOptions();
        }
        catch { if (!exiting) MessageBox.Show("语音设置未能应用，当前会话保留。请检查新设置或关闭语音功能。", "轻译"); }
    }
}
