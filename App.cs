using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuietTranslate;

public sealed partial class App : Application
{
    private Settings settings = new();
    private readonly TranslationService translator = new();
    private readonly OcrService ocr = new();
    private readonly DispatcherTimer policyTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Forms.NotifyIcon? tray;
    private TrayMenu? trayMenu;
    private Hotkeys? hotkeys;
    private SelectionTrigger? selectionTrigger;
    private BubbleWindow? bubble;
    private SettingsWindow? settingsWindow;
    private ManualWindow? manual;
    private CancellationTokenSource? operation;
    private readonly SemaphoreSlim preparation = new(1, 1);
    private bool exiting, paused, recording, blocked;
    private Mutex? instance;
    private EventWaitHandle? showWindow;
    private RegisteredWaitHandle? showWindowRegistration;
    private IntPtr observedWindow;
    private AppMode mode;
    private string sourceLanguage = "auto";
    internal Action<Exception>? TestException;

    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; if (app.TestException != null) app.TestException(e.Exception); else MessageBox.Show("工具遇到错误：" + e.Exception.Message, "轻译", MessageBoxButton.OK, MessageBoxImage.Warning); };
        app.Startup += async (_, _) =>
        {
            if (args.Length > 0 && args[0] == "--self-test") { int code = await SelfTest.RunAsync(args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "self-test-results.json")); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--ui-smoke") { int code = await SelfTest.UiSmokeAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--v13-test") { int code = await V13Test.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--bubble-test") { int code = await BubbleTest.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--growth-repro") { int code = await GrowthRepro.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--upgrade-test") { int code = await UpgradeTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--refinement-test") { int code = await RefinementTest.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--hover-test") { int code = await HoverTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--lifecycle-test") { int code = await app.RunLifecycleAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--selection-fixture") { SelfTest.OpenSelectionFixture(app); return; }
            if (args.Length > 0 && args[0] == "--scroll-selection-fixture") { InteractionTest.OpenFixture(app); return; }
            if (args.Length > 0 && args[0] == "--interaction-test") { int code = await InteractionTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--motion-test") { int code = await MotionTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--docs-capture") { int code = await DocsCapture.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--polish-test") { int code = await PolishTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            app.Start();
        };
        app.Exit += (_, _) => app.Cleanup(); return app.Run();
    }
    private void Start(Settings? initial = null, string testScope = "")
    {
        instance = new Mutex(true, "Local\\QuietTranslate.CurrentUser." + Environment.UserName + testScope, out bool created);
        string eventName = "Local\\QuietTranslate.ShowWindow." + Environment.UserName + testScope;
        if (!created)
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var running)) { using (running) running.Set(); }
            else MessageBox.Show("轻译正在启动，请稍候从托盘打开。", "轻译");
            Shutdown(); return;
        }
        showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(showWindow, (_, _) => { if (!exiting) Dispatcher.BeginInvoke(new Action(OpenSettings)); }, null, Timeout.Infinite, false);
        try { settings = initial ?? Settings.Load(); } catch (Exception ex) { MessageBox.Show(ex.Message, "轻译"); Shutdown(); return; }
        Ui.Theme = settings.Theme; SmoothScroll.Speed = settings.WheelScrollSpeed;
        hotkeys = new Hotkeys(); hotkeys.Pressed += id => { if (recording) return; if (id == 3) TogglePause(); else if (id == 4) ToggleManual(); else _ = ExecuteAsync(id == 2); };
        selectionTrigger = new SelectionTrigger((source, anchor, text) => _ = ExecuteAsync(false, source, anchor, text), () => settings, () => mode == AppMode.Compatibility);
        trayMenu = new TrayMenu(() => OpenManual(), () => _ = ExecuteAsync(true), OpenImage, TogglePause, OpenSettings, () => Shutdown());
        tray = new Forms.NotifyIcon { Icon = AppIcons.Active, Text = "轻译 · 已启用", Visible = true }; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(OpenSettings));
        tray.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) Dispatcher.BeginInvoke(new Action(() => trayMenu.Show())); };
        policyTimer.Tick += (_, _) => RefreshState(); policyTimer.Start();
        try { RefreshState(true); } catch (Exception ex) { MessageBox.Show(ex.Message, "轻译"); OpenSettings(); }
        OpenSettings();
    }
    internal static AppRule? FindRule(Settings s, string process) => s.AppRules.LastOrDefault(r => r.ProcessName.Equals(Path.GetFileNameWithoutExtension(process), StringComparison.OrdinalIgnoreCase));
    private void RefreshState(bool force = false)
    {
        if (exiting || recording || hotkeys == null) return;
        var foreground = Native.GetForegroundWindow(); if (!force && foreground == observedWindow) return;
        observedWindow = foreground; Native.GetWindowThreadProcessId(foreground, out uint id);
        AppRule? rule = null;
        try { using var process = Process.GetProcessById((int)id); rule = FindRule(settings, process.ProcessName); } catch { }
        mode = rule?.Mode ?? AppMode.Normal; sourceLanguage = rule?.SourceLanguage ?? "auto";
        bool nextBlocked = paused || mode == AppMode.Paused;
        try { hotkeys.Apply(settings, nextBlocked, nextBlocked && (settings.PauseManual || mode == AppMode.Paused)); }
        catch (Exception ex) { if (force) throw; if (tray != null) tray.Text = "轻译 · 快捷键冲突，请打开设置"; System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        if (selectionTrigger != null) try { selectionTrigger.Enabled = !nextBlocked && settings.SelectionButtonEnabled && mode != AppMode.HotkeysOnly; } catch { if (tray != null) tray.Text = "轻译 · 选词监听不可用"; }
        if (nextBlocked && !blocked) { operation?.Cancel(); bubble?.Close(); selectionTrigger?.Hide(); if (settings.PauseManual || mode == AppMode.Paused) manual?.CancelTranslation(); }
        blocked = nextBlocked;
        if (settingsWindow != null) settingsWindow.Icon = blocked ? AppIcons.PausedImage : AppIcons.Image;
        if (manual != null) manual.Icon = blocked ? AppIcons.PausedImage : AppIcons.Image;
        manual?.SetPaused(blocked && (settings.PauseManual || mode == AppMode.Paused));
        if (tray != null) { tray.Text = paused ? "轻译 · 已手动暂停" : mode == AppMode.Paused ? "轻译 · 当前应用自动暂停" : "轻译 · 已启用"; tray.Icon = blocked ? AppIcons.Paused : AppIcons.Active; }
        trayMenu?.Refresh(settings, paused, blocked);
    }
    private void TogglePause() { paused = !paused; RefreshState(true); }
    private void OpenImage()
    {
        RefreshState(); if (blocked) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", Title = "选择要识别的图片" };
        if (picker.ShowDialog() == true) _ = ExecuteAsync(true, imagePath: picker.FileName);
    }
    private void OpenSettings()
    {
        if (exiting) return;
        bubble?.Close(); selectionTrigger?.Hide();
        if (settingsWindow != null) { settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, value =>
        {
            var old = settings;
            try { hotkeys!.Apply(value, false); value.Save(); settings = value; Ui.Theme = settings.Theme; SmoothScroll.Speed = settings.WheelScrollSpeed; RefreshState(true); bubble?.ApplyAppearance(value); manual?.RefreshOptions(); }
            catch { settings = old; Ui.Theme = old.Theme; SmoothScroll.Speed = old.WheelScrollSpeed; RefreshState(true); throw; }
        });
        settingsWindow.RecordingChanged += value => { recording = value; if (value) { hotkeys?.Suspend(); if (selectionTrigger != null) selectionTrigger.Enabled = false; } else if (!exiting) RefreshState(true); };
        settingsWindow.Closed += (_, _) => { settingsWindow = null; recording = false; if (!exiting) RefreshState(true); }; settingsWindow.Show();
    }
    private void OpenManual(string? text = null, string source = "auto")
    {
        RefreshState(); if (exiting || (blocked && (settings.PauseManual || mode == AppMode.Paused))) return;
        bubble?.Close(); selectionTrigger?.Hide();
        var window = manual;
        if (window == null)
        {
            window = new ManualWindow(() => settings, (provider, from, to) => { settings.ManualProvider = provider; settings.ManualSource = from; settings.ManualTarget = to; settings.Save(); });
            manual = window;
            var opened = window; window.Closed += (_, _) => { if (ReferenceEquals(manual, opened)) manual = null; }; window.Show(); manualShownForTest?.Invoke(window);
        }
        // Show/Activate may dispatch window events. A close can clear the field before they return.
        if (!ReferenceEquals(manual, window) || !window.IsVisible || exiting) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!ReferenceEquals(manual, window) || !window.IsVisible) return;
        window.Activate(); if (text != null && ReferenceEquals(manual, window) && window.IsVisible) window.SetInput(text, source);
    }
    private void ToggleManual()
    {
        if (manual is { IsVisible: true } window && window.WindowState != WindowState.Minimized) window.Close();
        else OpenManual();
    }
    private BubbleWindow ShowBubble(Native.Rect anchor, IntPtr foreground, CancellationTokenSource owner)
    {
        var view = new BubbleWindow(anchor, foreground, settings.BubbleOpacity, settings.BubbleFontSize, textColor: settings.BubbleTextColor); view.ApplyAppearance(settings);
        view.Dismissed += () => { owner.Cancel(); if (ReferenceEquals(bubble, view)) bubble = null; }; bubble = view; view.Show(); return view;
    }
    private async Task ExecuteAsync(bool screenshot, IntPtr expectedWindow = default, Native.Rect? selectionAnchor = null, string? snapshot = null, string? imagePath = null)
    {
        RefreshState(); if (exiting || blocked || recording) return;
        var foreground = Native.GetForegroundWindow(); Native.GetWindowThreadProcessId(foreground, out uint process); if (process == Environment.ProcessId && !screenshot) return;
        selectionTrigger?.Hide(); operation?.Cancel(); bubble?.Close();
        var owner = new CancellationTokenSource(); operation = owner; var ct = owner.Token; BubbleWindow? view = null; bool acquired = false;
        var cursor = Native.Cursor();
        if (expectedWindow != IntPtr.Zero && foreground != expectedWindow) { owner.Dispose(); operation = null; return; }
        var anchor = selectionAnchor ?? new Native.Rect { Left = cursor.X, Right = cursor.X, Top = cursor.Y, Bottom = cursor.Y };
        var watch = Stopwatch.StartNew(); string source = "", secret = ""; string from = sourceLanguage;
        try
        {
            await preparation.WaitAsync(ct); acquired = true; ct.ThrowIfCancellationRequested();
            if (screenshot)
            {
                await SelectionReader.WaitForReleaseAsync(ct);
                using var capture = imagePath == null ? await CaptureWindow.SelectAsync(ct) : LoadImage(imagePath, anchor); if (capture == null) return;
                anchor = capture.Bounds; foreground = Native.GetForegroundWindow(); view = ShowBubble(anchor, foreground, owner);
                var config = settings.OcrServices[settings.OcrProvider]; view.SetText("正在识别文字…", Settings.OcrName(settings.OcrProvider));
                secret = settings.OcrProvider == OcrProvider.Windows ? "" : Credentials.Get(OcrService.SecretTarget(settings.OcrProvider, config));
                source = await ocr.RecognizeAsync(settings.OcrProvider, config, secret, capture.Bitmap, ct, settings.OcrLanguage);
            }
            else
            {
                if (snapshot != null) source = snapshot;
                else { var selected = await SelectionReader.GetAsync(foreground, settings.CopyWaitMs, ct); if (selected.Bounds.HasValue) anchor = selected.Bounds.Value; source = selected.Text; }
                ct.ThrowIfCancellationRequested(); view = ShowBubble(anchor, foreground, owner);
            }
            ct.ThrowIfCancellationRequested(); preparation.Release(); acquired = false;
            string editable = source; view.SetContext(screenshot ? "校正识别文字…" : "在翻译栏编辑原文…", () => OpenManual(editable, from));
            view.SetText("正在翻译…", ApiSupport.LanguageName(from) + " → 简体中文");
            var provider = settings.Provider; var service = settings.Current; secret = Credentials.Get(Credentials.Target(provider, service)); long? first = null;
            using var updates = new StreamedText(Dispatcher, part => { if (ct.IsCancellationRequested) return; first ??= watch.ElapsedMilliseconds; view.SetText(part, $"简体中文 · 首字 {first} ms"); });
            string result = await translator.TranslateAsync(provider, service, secret, source, updates.Report, ct, "zh-Hans", from);
            updates.Dispose();
            if (!ct.IsCancellationRequested) view.SetText(result, $"简体中文 · {watch.ElapsedMilliseconds} ms" + (TranslationService.IsWord(source) ? " · 短词有歧义时可编辑原文" : " · Esc 关闭"), completed: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested && !exiting) { view ??= ShowBubble(anchor, foreground, owner); view.SetText(ex.Message, "未完成 · 打开设置检查服务", completed: true); } }
        finally
        {
            source = secret = ""; if (acquired) preparation.Release();
            if (view == null || !view.IsVisible) { if (ReferenceEquals(operation, owner)) operation = null; owner.Dispose(); }
            else view.Closed += (_, _) => { if (ReferenceEquals(operation, owner)) operation = null; owner.Dispose(); };
        }
    }
    private static Capture LoadImage(string path, Native.Rect anchor)
    {
        if (new FileInfo(path).Length > 12 * 1024 * 1024) throw new InvalidOperationException("图片文件超过 12 MB，请裁剪后重试。");
        using var image = System.Drawing.Image.FromFile(path);
        if ((long)image.Width * image.Height > 16_000_000) throw new InvalidOperationException("图片分辨率过大，请裁剪到文字区域。");
        return new Capture(new System.Drawing.Bitmap(image), anchor);
    }
    private void Cleanup()
    {
        showWindowRegistration?.Unregister(null); showWindow?.Dispose();
        exiting = true; policyTimer.Stop(); selectionTrigger?.Dispose(); operation?.Cancel(); bubble?.Close(); manual?.Close(); settingsWindow?.Close(); hotkeys?.Dispose(); translator.Dispose(); ocr.Dispose();
        trayMenu?.Dispose(); if (tray != null) { tray.Visible = false; tray.Dispose(); } instance?.Dispose();
    }
}
