using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietTranslate;

internal sealed class ManualWindow : Window
{
    private readonly Func<Settings> options;
    private readonly Action<Provider, string, string> preferences;
    private readonly TranslationService translator;
    private readonly bool ownsTranslator;
    private readonly Func<Provider, ServiceSettings, string> secretReader;
    private readonly ComboBox providers = new(), sources = new(), targets = new();
    internal readonly TextBox Input = new() { Name = "ManualInput", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxLength = 8000 };
    internal readonly TextBox Output = new() { Name = "ManualOutput", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, AcceptsReturn = true };
    private readonly TextBlock status = new() { Name = "ManualStatus", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock count = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock inputHint = new() { Text = "输入文字，停顿后自动翻译", IsHitTestVisible = false, FontSize = 14, Margin = new Thickness(12, 30, 36, 10), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock outputHint = new() { Text = "译文会显示在这里", IsHitTestVisible = false, FontSize = 14, Margin = new Thickness(12, 30, 36, 10), TextWrapping = TextWrapping.Wrap };
    private readonly Button copy = Ui.IconButton("\uE8C8", "复制译文"), retry = new() { Content = "翻译", Name = "TranslateNow", FontSize = 12 }, swap = Ui.IconButton("\uE8AB", "交换语言"), copyInput = Ui.IconButton("\uE8C8", "复制原文");
    private readonly Button close = Ui.IconButton("\uE8BB", "关闭输入栏");
    private readonly DispatcherTimer debounce = new(), layout = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private CancellationTokenSource? active;
    private bool composing, closed, initializing, paused, copying;
    private int revision;
    private string lastSignature = "";
    internal ManualWindow(Func<Settings> options, Action<Provider, string, string> preferences, TranslationService? translator = null, Func<Provider, ServiceSettings, string>? secretReader = null, bool? dark = null)
    {
        this.options = options; this.preferences = preferences; this.translator = translator ?? new(); ownsTranslator = translator == null; this.secretReader = secretReader ?? Credentials.TargetSecret; initializing = true;
        Title = "轻译 · 输入翻译"; Width = 436; MinWidth = 360; MinHeight = 238;
        MaxHeight = Math.Max(210, Math.Min(620, SystemParameters.WorkArea.Height - 32));
        MinHeight = Math.Min(MinHeight, MaxHeight);
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen; Icon = AppIcons.Image;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14; UseLayoutRounding = true; SnapsToDevicePixels = true; Ui.Apply(this, dark); Topmost = options().ManualTopmost;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        var surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9, 12, 10) };
        Ui.Bind(surface, Border.BackgroundProperty, "Surface"); Ui.Bind(surface, Border.BorderBrushProperty, "Line"); Content = surface;
        var root = new StackPanel(); surface.Child = root;
        var header = new DockPanel { LastChildFill = true, Height = 30, Margin = new Thickness(-3, -2, -3, 9), Background = Brushes.Transparent };
        root.Children.Add(header);
        close.Name = "ManualClose"; close.Width = 26; close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); UpdateCloseHint();
        providers.ItemsSource = Enum.GetValues<Provider>().Select(Settings.Name).ToArray();
        sources.ItemsSource = ApiSupport.Languages.Select(ApiSupport.LanguageName).ToArray(); targets.ItemsSource = ApiSupport.Languages.Skip(1).Select(ApiSupport.LanguageName).ToArray(); LoadChoices();
        var service = Picker(providers, "翻译服务", "ManualService", true); DockPanel.SetDock(service, Dock.Right); header.Children.Add(service);
        retry.Padding = new Thickness(10, 5, 10, 5); retry.Margin = new Thickness(4, 1, 5, 1); retry.BorderThickness = new Thickness(0);
        retry.ToolTip = "Ctrl+Enter 立即翻译 · 再点可取消"; Ui.Bind(retry, Control.BackgroundProperty, "Accent"); Ui.Bind(retry, Control.ForegroundProperty, "OnAccent");
        retry.Click += async (_, _) => { if (active != null) CancelTranslation(); else { lastSignature = ""; await TranslateNowAsync(); } }; DockPanel.SetDock(retry, Dock.Right); header.Children.Add(retry);
        var languages = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(languages, Dock.Left); header.Children.Add(languages);
        languages.Children.Add(Picker(sources, "源语言", "SourceLanguage"));
        swap.Name = "SwapLanguages"; swap.Width = 24; swap.Click += (_, _) => SwapLanguages(); languages.Children.Add(swap);
        languages.Children.Add(Picker(targets, "目标语言", "TargetLanguage"));
        var grip = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "拖动输入翻译栏", MinWidth = 12 };
        grip.Child = new TextBlock { Text = "⠿", FontSize = 12, Opacity = .4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; Ui.Bind(grip.Child, TextBlock.ForegroundProperty, "Muted");
        grip.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } e.Handled = true; } }; header.Children.Add(grip);
        Field(root, Input, copyInput, inputHint, "原文", false); Field(root, Output, copy, outputHint, "译文", true);
        var feedback = new DockPanel { Margin = new Thickness(3, 1, 3, 0), LastChildFill = true, Height = 17 };
        Ui.Bind(count, TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(count, Dock.Right); feedback.Children.Add(count); status.Margin = new Thickness(0, 0, 10, 0); feedback.Children.Add(status); root.Children.Add(feedback);
        copy.Name = "CopyOutput"; copyInput.Name = "CopyInput"; copy.IsEnabled = false; copyInput.IsEnabled = false; SetStatus("停顿后自动翻译 · Esc 关闭");
        copyInput.Click += async (_, _) => await CopyAsync(Input.Text, "原文"); copy.Click += async (_, _) => { if (copy.IsEnabled) await CopyAsync(Output.Text, "译文"); };
        layout.Tick += (_, _) => { layout.Stop(); Fit(); };
        Input.TextChanged += (_, _) => { copyInput.IsEnabled = Input.Text.Length > 0; count.Text = Input.Text.Length == 0 ? "" : Input.Text.Length.ToString("N0") + " / 8,000"; inputHint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; QueueFit(); Schedule(); };
        Output.TextChanged += (_, _) => { outputHint.Visibility = Output.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; QueueFit(); };
        SizeChanged += (_, _) => QueueFit();
        debounce.Tick += async (_, _) => { debounce.Stop(); await TranslateNowAsync(); };
        TextCompositionManager.AddPreviewTextInputStartHandler(Input, (_, _) => { composing = true; Invalidate(); SetStatus("输入法组词中…"); });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Input, (_, _) => { composing = true; Invalidate(); });
        TextCompositionManager.AddPreviewTextInputHandler(Input, (_, _) => { composing = false; Schedule(); });
        providers.SelectionChanged += (_, _) => ChangedPreference(); sources.SelectionChanged += (_, _) => ChangedPreference(); targets.SelectionChanged += (_, _) => ChangedPreference();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { if (composing) return; Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && !composing) { e.Handled = true; await TranslateNowAsync(); }
        };
        Closed += (_, _) => { closed = true; Invalidate(); layout.Stop(); CloseMenus(); Input.Clear(); Output.Clear(); lastSignature = ""; Content = null; if (ownsTranslator) this.translator.Dispose(); };
        initializing = false; UpdateSwap(); Fit(); Loaded += (_, _) => { Input.Focus(); QueueFit(); };
    }
    private Button Picker(ComboBox choices, string title, string name, bool compact = false)
    {
        var button = new Button { Name = name, FontSize = 12, Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(0), Background = Brushes.Transparent, MaxWidth = compact ? 27 : 102 };
        AutomationProperties.SetName(button, title);
        var menu = new ContextMenu { FontSize = 12 }; Ui.Apply(menu, ((SolidColorBrush)Resources["UiSurface"]).Color.R < 100); button.ContextMenu = menu;
        for (int i = 0; i < choices.Items.Count; i++)
        {
            int index = i; var item = new MenuItem { Header = choices.Items[i], IsCheckable = true, Padding = new Thickness(8, 6, 8, 6) }; item.Click += (_, _) => { choices.SelectedIndex = index; Input.Focus(); }; menu.Items.Add(item);
        }
        void Display() { button.Content = compact ? "⋯" : choices.SelectedItem; button.ToolTip = title + " · " + choices.SelectedItem; }
        choices.SelectionChanged += (_, _) => Display(); Display();
        button.Click += (_, _) => { foreach (MenuItem item in menu.Items) item.IsChecked = menu.Items.IndexOf(item) == choices.SelectedIndex; menu.PlacementTarget = button; menu.IsOpen = true; };
        return button;
    }
    private void Field(StackPanel root, TextBox field, Button action, TextBlock hint, string title, bool result)
    {
        field.MinHeight = 76; field.Padding = new Thickness(11, 29, 11, 10); field.FontSize = 15; field.BorderThickness = new Thickness(0); field.Background = Brushes.Transparent;
        var box = new Grid { Margin = new Thickness(0, 0, 0, 8) }; box.Children.Add(field);
        var label = new TextBlock { Text = title, FontSize = 10, IsHitTestVisible = false, Margin = new Thickness(12, 9, 0, 0), VerticalAlignment = VerticalAlignment.Top }; Ui.Bind(label, TextBlock.ForegroundProperty, result ? "Accent" : "Muted"); box.Children.Add(label);
        action.Width = action.Height = 25; action.HorizontalAlignment = HorizontalAlignment.Right; action.VerticalAlignment = VerticalAlignment.Top; action.Margin = new Thickness(0, 4, 5, 0); box.Children.Add(action); Ui.Bind(hint, TextBlock.ForegroundProperty, "Muted"); box.Children.Add(hint);
        var frame = new Border { CornerRadius = new CornerRadius(10), Child = box, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 7) };
        Ui.Bind(frame, Border.BackgroundProperty, result ? "AccentSoft" : "Input"); Ui.Bind(frame, Border.BorderBrushProperty, "Line"); box.Margin = new Thickness(0);
        field.GotKeyboardFocus += (_, _) => Ui.Bind(frame, Border.BorderBrushProperty, "Accent"); field.LostKeyboardFocus += (_, _) => Ui.Bind(frame, Border.BorderBrushProperty, "Line"); root.Children.Add(frame);
        var edit = new ContextMenu(); Ui.Apply(edit, Resources["UiThemeDark"] is true);
        foreach (var entry in new[] { ("剪切", ApplicationCommands.Cut), ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
            if (!field.IsReadOnly || entry.Item2 == ApplicationCommands.Copy || entry.Item2 == ApplicationCommands.SelectAll) edit.Items.Add(new MenuItem { Header = entry.Item1, Command = entry.Item2, CommandTarget = field });
        field.ContextMenu = edit;
        field.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, async (_, e) => { e.Handled = true; await CopyAsync(field.SelectedText, result ? "译文" : "原文"); }, (_, e) => { e.CanExecute = field.SelectionLength > 0; e.Handled = true; }));
        SmoothScroll.Attach(field);
    }
    private void LoadChoices()
    {
        var value = options(); var provider = value.ManualProvider ?? value.Provider;
        providers.SelectedIndex = Enum.IsDefined(provider) ? (int)provider : 0;
        sources.SelectedIndex = Math.Max(0, Array.IndexOf(ApiSupport.Languages, value.ManualSource));
        targets.SelectedIndex = Math.Max(0, Array.IndexOf(ApiSupport.Languages.Skip(1).ToArray(), value.ManualTarget));
    }
    private void UpdateSwap() { swap.IsEnabled = sources.SelectedIndex > 0; swap.ToolTip = swap.IsEnabled ? "交换源语言与目标语言" : "自动识别暂不能交换，请先指定源语言"; }
    private void SwapLanguages()
    {
        if (sources.SelectedIndex <= 0) return;
        int from = sources.SelectedIndex, to = targets.SelectedIndex + 1; initializing = true; sources.SelectedIndex = to; targets.SelectedIndex = from - 1; initializing = false; ChangedPreference();
    }
    private void ChangedPreference()
    {
        UpdateSwap(); if (initializing || closed || providers.SelectedIndex < 0 || sources.SelectedIndex < 0 || targets.SelectedIndex < 0) return;
        try { preferences((Provider)providers.SelectedIndex, ApiSupport.Languages[sources.SelectedIndex], ApiSupport.Languages[targets.SelectedIndex + 1]); }
        catch { SetStatus("偏好暂未保存，请检查配置文件是否可写", "Warning"); }
        lastSignature = ""; Schedule();
    }
    private void Invalidate() { revision++; debounce.Stop(); var previous = active; active = null; previous?.Cancel(); copy.IsEnabled = false; retry.Content = "翻译"; Output.Opacity = Output.Text.Length > 0 ? .45 : 1; }
    private void Schedule()
    {
        if (closed || initializing) return;
        Invalidate(); outputHint.Text = "译文会显示在这里"; Ui.Bind(outputHint, TextBlock.ForegroundProperty, "Muted");
        if (paused) { SetStatus("已暂停 · 恢复后继续", "Warning"); return; }
        if (string.IsNullOrWhiteSpace(Input.Text)) { Output.Clear(); lastSignature = ""; SetStatus("停顿后自动翻译 · Esc 关闭"); return; }
        SetStatus(composing ? "输入法组词中…" : Input.Text.Length >= 8000 ? "已达 8,000 字上限" : "等待输入停顿…");
        if (!composing) { debounce.Interval = TimeSpan.FromMilliseconds(Math.Clamp(options().AutoTranslateDelay, 300, 2000)); debounce.Start(); }
    }
    private void SetStatus(string value, string color = "Muted") { status.Text = value; status.ToolTip = value; Ui.Bind(status, TextBlock.ForegroundProperty, color); }
    private async Task CopyAsync(string value, string label)
    {
        if (copying || value.Length == 0 || closed) return;
        copying = true; int version = revision; SetStatus("正在复制" + label + "…");
        bool copied = await ClipboardWriter.CopyAsync(value); copying = false;
        if (!closed && version == revision) SetStatus(copied ? "已复制" + label : "剪贴板被占用，请重试", copied ? "Accent" : "Warning");
    }
    internal async Task TranslateNowAsync()
    {
        debounce.Stop(); if (closed || paused || composing || string.IsNullOrWhiteSpace(Input.Text)) return;
        string text = Input.Text.Trim(); var provider = (Provider)providers.SelectedIndex;
        string source = ApiSupport.Languages[Math.Max(0, sources.SelectedIndex)], target = ApiSupport.Languages[Math.Max(0, targets.SelectedIndex) + 1];
        // A matching signature is useful only while its completed result is still current.
        // Editing away and back, cancellation or a partial response must remain retryable.
        string signature;
        ServiceSettings config;
        try { config = JsonSerializer.Deserialize<ServiceSettings>(JsonSerializer.Serialize(options().Services[provider]))!; signature = ApiSupport.Hash(text + "\0" + provider + "\0" + source + "\0" + target + "\0" + JsonSerializer.Serialize(config)); }
        catch { ShowError("翻译服务配置无效，请打开设置重新保存。"); return; }
        if (signature == lastSignature && copy.IsEnabled) return;
        Invalidate(); int version = revision; var owner = new CancellationTokenSource(); active = owner; var ct = owner.Token;
        Output.Clear(); Output.Opacity = 1; outputHint.Text = "正在翻译…"; Ui.Bind(outputHint, TextBlock.ForegroundProperty, "Muted"); SetStatus("正在翻译…", "Accent"); retry.Content = "取消";
        try
        {
            string result;
            if (source == target) result = text;
            else
            {
                using var updates = new StreamedText(Dispatcher, part => { if (!closed && !ct.IsCancellationRequested && version == revision) Output.Text = part; });
                result = await translator.TranslateAsync(provider, config, secretReader(provider, config), text, updates.Report, ct, target, source);
            }
            if (!closed && !ct.IsCancellationRequested && version == revision)
            {
                Output.Text = result; Output.Opacity = 1; copy.IsEnabled = true; lastSignature = signature; SetStatus(source == target ? "语言相同 · 已保留原文" : "已完成 · " + Settings.Name(provider), "Accent");
                if (TranslationService.IsWord(text)) status.ToolTip = "短词可能有歧义，可指定源语言或补充上下文。";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && version == revision) ShowError(ex.Message); }
        finally { if (ReferenceEquals(active, owner)) active = null; if (!closed && version == revision) retry.Content = "翻译"; owner.Dispose(); }
    }
    private void ShowError(string message) { Output.Clear(); outputHint.Text = message; Ui.Bind(outputHint, TextBlock.ForegroundProperty, "Warning"); SetStatus("未完成 · 点击翻译重试", "Warning"); status.ToolTip = message; lastSignature = ""; copy.IsEnabled = false; QueueFit(); }
    private void QueueFit() { if (!closed && !layout.IsEnabled) layout.Start(); }
    private void Fit()
    {
        if (closed) return;
        double width = Math.Max(220, ActualWidth > 0 ? ActualWidth - 52 : Width - 52);
        double total = Math.Max(152, MaxHeight - 80);
        void Measure(TextBox field, TextBlock hint, double cap)
        {
            string value = field.Text.Length > 0 ? field.Text : hint.Text;
            var f = new FormattedText(value.Length == 0 ? " " : value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(field.FontFamily, field.FontStyle, field.FontWeight, field.FontStretch), field.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width, LineHeight = 22 };
            field.Height = Math.Clamp(Math.Ceiling(f.Height + 43), 76, Math.Max(76, cap));
        }
        Measure(Input, inputHint, Math.Min(190, total * .44)); Measure(Output, outputHint, Math.Min(245, total * .56));
    }
    private void CloseMenus()
    {
        void Close(DependencyObject node) { if (node is FrameworkElement element && element.ContextMenu != null) element.ContextMenu.IsOpen = false; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Close(VisualTreeHelper.GetChild(node, i)); }
        if (Content is DependencyObject root) Close(root);
    }
    internal void SetInput(string text, string source = "auto") { initializing = true; sources.SelectedIndex = Math.Max(0, Array.IndexOf(ApiSupport.Languages, source)); initializing = false; UpdateSwap(); Input.Text = text; Input.CaretIndex = Input.Text.Length; Input.Focus(); }
    private void UpdateCloseHint() => close.ToolTip = "关闭输入栏 · Esc / " + options().ManualHotkey + " · 清空原文与译文";
    internal void RefreshOptions() { Ui.RefreshTheme(this, Ui.IsDark); UpdateCloseHint(); initializing = true; LoadChoices(); initializing = false; UpdateSwap(); Topmost = options().ManualTopmost; lastSignature = ""; Schedule(); }
    internal void CancelTranslation() { Invalidate(); lastSignature = ""; outputHint.Text = "已取消，可继续输入或点击翻译"; SetStatus("已取消 · 窗口保留"); QueueFit(); }
    internal void SetPaused(bool value)
    {
        if (paused == value || closed) return;
        paused = value; Invalidate(); retry.IsEnabled = !paused;
        if (paused) { outputHint.Text = "翻译已暂停"; SetStatus("已暂停 · 恢复后继续", "Warning"); }
        else { lastSignature = ""; Schedule(); }
    }
}
