using System;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietTranslate;

internal sealed class SpeechWindow : Window
{
    private static readonly FontFamily textFont = new("Microsoft YaHei UI, Segoe UI");
    private FontFamily subtitleFont = textFont;
    private FontWeight subtitleWeight = FontWeights.Normal;
    private string subtitleFontKey = "";
    private readonly LiveSession session;
    private readonly Action stop, settings;
    private readonly Func<string, Task> target;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock status = new() { FontSize = 11 };
    private readonly CueRows cueLayout = new();
    private readonly TextBlock[] cueRows = new TextBlock[3];
    private string displayCopy = "";
    private readonly DispatcherTimer geometrySave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Grid captions = new() { ClipToBounds = true };
    private readonly DockPanel footer = new() { Margin = new Thickness(0, 7, 0, 0), Name = "SpeechStatus" };
    private readonly Border face;
    private readonly Border controlFace;
    private readonly DockPanel header = new() { LastChildFill = true, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 7), Name = "SpeechDrag" };
    private readonly Action<SpeechOptions>? saveAppearance;
    private readonly Popup appearance = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
    private readonly Slider opacity = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 190, Name = "SpeechOpacitySlider", Margin = new Thickness(0, 10, 0, 4) };
    private readonly TextBlock opacityLabel = new() { FontSize = 12 };
    private readonly Popup diagnostics = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
    private readonly Border idleHandle = new() { Width = 28, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 13), Name = "SpeechIdleHandle" };
    private readonly Border appearanceFace;
    private readonly Button themeButton;
    private bool appearanceDirty;
    private readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ProgressBar meter = new() { Minimum = 0, Maximum = 1, Width = 52, Height = 3, Margin = new Thickness(8, 0, 4, 0) };
    private readonly ComboBox languages = new() { Width = 125, FontSize = 11, Name = "LiveTarget" };
    private readonly Button lockButton;
    private SpeechHistoryWindow? history;
    private bool locked, applying, stopped, ready;
    private Point? dragStart;
    private readonly RowDefinition headerRow = new();
    private double headerHeight;
    internal SpeechWindow(LiveSession session, Action stop, Action settings, Func<string, Task> target, Action<SpeechOptions>? saveAppearance = null)
    {
        this.session = session; this.stop = stop; this.settings = settings; this.target = target; this.saveAppearance = saveAppearance;
        Title = "轻译 · 实时语音字幕"; Name = "SpeechOverlay"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        FontFamily = textFont; status.FontFamily = opacityLabel.FontFamily = textFont;
        var initial = session.Options ?? new SpeechOptions();
        headerHeight = initial.ControlsHidden ? 12 : 36;
        Width = initial.WindowWidth; Height = initial.WindowHeight; MinWidth = 420; MinHeight = 120; MaxWidth = 10000;
        Left = initial.WindowLeft ?? Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2); Top = initial.WindowTop ?? Math.Max(0, SystemParameters.WorkArea.Bottom - Height - 40); Ui.Apply(this, true);
        var content = new Grid(); content.RowDefinitions.Add(headerRow); content.RowDefinitions.Add(new RowDefinition());
        face = new Border { Child = content, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(1), Name = "SpeechCaptionFace" };
        var surface = new Grid { Background = Brushes.Transparent }; surface.Children.Add(face); Content = surface; AddResizeEdges(surface);
        controlFace = new Border { Name = "SpeechControlFace", Child = header, CornerRadius = new CornerRadius(7), Padding = new Thickness(4, 3, 4, 3), VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(controlFace); header.VerticalAlignment = VerticalAlignment.Top; header.Margin = new Thickness(0); Panel.SetZIndex(controlFace, 3);
        content.Children.Add(idleHandle); idleHandle.VerticalAlignment = VerticalAlignment.Top; idleHandle.Margin = new Thickness(0, 4, 0, 0); idleHandle.IsHitTestVisible = false;
        DockPanel.SetDock(toolbar, Dock.Right); header.Children.Insert(0, toolbar);
        BuildLanguageSelector();
        languages.ItemsSource = SpeechOptions.Languages.Select(SpeechOptions.LanguageName).ToArray(); toolbar.Children.Add(languages);
        Button Add(string glyph, string label, Action action) { var b = Ui.IconButton(glyph, label); b.Click += (_, _) => action(); toolbar.Children.Add(b); return b; }
        Add("\uE8C8", "复制当前译文", () => _ = CopyAsync());
        Add("\uE81C", "本次记录", ShowHistory);
        var opacityButton = Add("\uE790", "调整字幕背景透明度", () => appearance.IsOpen = !appearance.IsOpen); opacityButton.Name = "SpeechOpacityButton";
        appearance.PlacementTarget = opacityButton;
        var appearanceBody = new StackPanel(); appearanceBody.Children.Add(opacityLabel); appearanceBody.Children.Add(opacity);
        appearanceFace = new Border { Child = appearanceBody, CornerRadius = new CornerRadius(9), Padding = new Thickness(14), BorderThickness = new Thickness(1), Width = 226 };
        System.Windows.Documents.TextElement.SetFontFamily(appearanceFace, textFont);
        appearance.Child = appearanceFace;
        themeButton = Add("\uE706", "切换为明亮字幕", SwitchTheme); themeButton.Name = "SpeechThemeButton";
        Add("\uE70E", "收起顶部工具栏；双击字幕文字或空白处恢复", ToggleControls).Name = "SpeechHideControls";
        lockButton = Add("\uE785", "锁定字幕并穿透鼠标；从托盘解锁", () => SetLocked(true));
        Add("\uE713", "语音设置", settings);
        var infoButton = Add("\uE946", "查看采集、连接状态和用量", () => diagnostics.IsOpen = !diagnostics.IsOpen); infoButton.Name = "SpeechDiagnostics"; diagnostics.PlacementTarget = infoButton;
        Add("\uE8BB", "关闭功能并删除本次记录", stop);
        for (int i = 0; i < 3; i++) captions.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(captions, 1); content.Children.Add(captions);
        for (int i = 0; i < 3; i++)
        {
            var row = new TextBlock { Name = "SpeechCueRow" + i, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.NoWrap, Visibility = Visibility.Collapsed, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            cueRows[i] = row; Grid.SetRow(row, i); captions.Children.Add(row);
        }
        DockPanel.SetDock(meter, Dock.Right); footer.Children.Add(meter); footer.Children.Add(status);
        status.TextWrapping = TextWrapping.Wrap; status.TextTrimming = TextTrimming.None; status.MaxWidth = 340;
        var detailFace = new Border { Child = footer, CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Width = 360 }; Ui.Apply(detailFace); System.Windows.Documents.TextElement.SetFontFamily(detailFace, textFont); diagnostics.Child = detailFace;
        surface.MouseLeftButtonDown += (_, e) => { if (!locked && e.ClickCount == 1 && !IsControl(e.OriginalSource)) { dragStart = e.GetPosition(this); surface.CaptureMouse(); e.Handled = true; } };
        surface.MouseMove += (_, e) =>
        {
            if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || locked) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragStart = null; surface.ReleaseMouseCapture(); DragMove();
        };
        surface.MouseLeftButtonUp += (_, _) => { dragStart = null; surface.ReleaseMouseCapture(); };
        surface.PreviewMouseLeftButtonDown += (_, e) => { if (!locked && e.ClickCount == 2 && !IsControl(e.OriginalSource)) { dragStart = null; surface.ReleaseMouseCapture(); ToggleControls(); e.Handled = true; } };
        SizeChanged += (_, _) => QueueGeometrySave();
        LocationChanged += (_, _) => QueueGeometrySave();
        geometrySave.Tick += (_, _) => { geometrySave.Stop(); PersistAppearance(); };
        opacity.ValueChanged += (_, _) =>
        {
            opacityLabel.Text = $"背景不透明度 · {opacity.Value:0}%";
            if (applying || stopped || session.Options == null) return;
            var updated = session.Options.Copy(); updated.Opacity = opacity.Value / 100; session.ApplyAppearance(updated); appearanceDirty = true; ApplyOptions();
        };
        appearance.Closed += (_, _) => { if (appearanceDirty && !stopped) PersistAppearance(); };
        languages.SelectionChanged += async (_, _) =>
        {
            if (applying || languages.SelectedIndex < 0 || !session.Enabled) return;
            languages.IsEnabled = false;
            try { await target(SpeechOptions.Languages[languages.SelectedIndex]); }
            catch { status.Text = "语言切换失败，请检查设置。"; }
            finally { if (!stopped) languages.IsEnabled = true; }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { stop(); e.Handled = true; } };
        Loaded += (_, _) => { RestoreWindowBounds(); ApplyOptions(); UpdateLayout(); Tick(); ready = true; timer.Start(); };
        timer.Tick += (_, _) => Tick();
        Closed += (_, _) => { geometrySave.Stop(); PersistAppearance(); stopped = true; appearance.IsOpen = diagnostics.IsOpen = false; timer.Stop(); history?.Close(); cueLayout.Clear(); foreach (var row in cueRows) row.Text = ""; if (session.Enabled) Dispatcher.BeginInvoke(stop, DispatcherPriority.Send); };
    }
    private static bool IsControl(object source)
    {
        for (var item = source as DependencyObject; item != null; item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item is ButtonBase or Selector or Slider or Thumb) return true;
        return false;
    }
    private void BuildLanguageSelector()
    {
        languages.Height = 28; languages.FontFamily = textFont; languages.VerticalContentAlignment = VerticalAlignment.Center;
        languages.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent" CornerRadius="5"><TextBlock Text="⌄" FontFamily="Segoe UI" FontSize="12" Foreground="{DynamicResource UiAccent}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,5,0"/></Border></ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter x:Name="SpeechLanguageContent" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="5,0,23,0" VerticalAlignment="Center" IsHitTestVisible="False"/>
                <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False">
                  <Border Background="{DynamicResource UiSurface}" BorderBrush="{DynamicResource UiLine}" BorderThickness="1" CornerRadius="7" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="240"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border>
                </Popup>
              </Grid>
            </ControlTemplate>
            """);
    }
    private void AddResizeEdges(Grid surface)
    {
        // Layered WPF windows do not reliably retain native non-client resizing.
        // Invisible native WPF thumbs keep edges usable without a visible grip.
        // Alpha 1 keeps rounded, otherwise fully transparent corners hit-testable
        // in a layered HWND, without drawing a visible resize decoration.
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
        foreach (int x in new[] { -1, 0, 1 }) foreach (int y in new[] { -1, 0, 1 })
        {
            if (x == 0 && y == 0) continue;
            var edge = new Thumb { Template = template, Focusable = false, Name = $"SpeechResize{x + 1}{y + 1}",
                HorizontalAlignment = x < 0 ? HorizontalAlignment.Left : x > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                VerticalAlignment = y < 0 ? VerticalAlignment.Top : y > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                Width = x == 0 ? double.NaN : 6, Height = y == 0 ? double.NaN : 6,
                Cursor = x == 0 ? Cursors.SizeNS : y == 0 ? Cursors.SizeWE : x == y ? Cursors.SizeNWSE : Cursors.SizeNESW };
            edge.DragDelta += (_, e) =>
            {
                if (locked) return;
                if (x != 0) { double previous = ActualWidth, value = Math.Clamp(previous + x * e.HorizontalChange, MinWidth, MaxWidth); Width = value; if (x < 0) Left += previous - value; }
                if (y != 0) { double previous = ActualHeight, value = Math.Clamp(previous + y * e.VerticalChange, MinHeight, SystemParameters.WorkArea.Height); Height = value; if (y < 0) Top += previous - value; }
            };
            Panel.SetZIndex(edge, x != 0 && y != 0 ? 2 : 1); surface.Children.Add(edge);
        }
    }
    private bool IsDarkBackground => session.Options is { } options && Ui.Contrast(Ui.Color(options.Background), Colors.White) > Ui.Contrast(Ui.Color(options.Background), Colors.Black);
    private void SwitchTheme()
    {
        if (session.Options == null) return; bool dark = !IsDarkBackground; var updated = session.Options.Copy();
        updated.Background = dark ? "#17212B" : "#F6F8FB"; updated.Foreground = dark ? "#F5F7FA" : "#202D3A";
        session.ApplyAppearance(updated); ApplyOptions(); PersistAppearance();
    }
    private void ToggleControls()
    {
        if (session.Options == null) return; appearance.IsOpen = false;
        var updated = session.Options.Copy(); updated.ControlsHidden = !updated.ControlsHidden; session.ApplyAppearance(updated); ApplyOptions(); PersistAppearance();
    }
    private void PersistAppearance()
    {
        if (session.Options == null) return;
        var options = session.Options.Copy();
        if (ready)
        {
            double correction = (options.ControlsHidden ? 12 : 36) - headerHeight;
            options.WindowWidth = Math.Max(MinWidth, Width); options.WindowHeight = Math.Max(MinHeight, Height) + correction; options.WindowLeft = Left; options.WindowTop = Top - correction;
        }
        try { saveAppearance?.Invoke(options); session.ApplyAppearance(options); appearanceDirty = false; }
        catch { MessageBox.Show(this, "字幕外观已临时应用，但未能保存。请检查配置文件是否被占用。", "轻译"); }
    }
    private async Task CopyAsync()
    {
        string value = displayCopy; if (value.Length == 0) return;
        bool copied = await ClipboardWriter.CopyAsync(value); if (!stopped) status.Text = copied ? "已复制当前译文" : "未复制，剪贴板被占用或已更新。";
    }
    internal void ApplyOptions()
    {
        var options = session.Options; if (options == null) return;
        string fontKey = options.FontFamily + "|" + options.FontWeight;
        if (fontKey != subtitleFontKey)
        {
            subtitleFont = new FontFamily(options.FontFamily + ", Microsoft YaHei UI, Segoe UI");
            subtitleWeight = FontWeight.FromOpenTypeWeight(options.FontWeight);
            subtitleFontKey = fontKey; cueLayout.InvalidateMeasurements();
        }
        applying = true; languages.SelectedIndex = Array.IndexOf(SpeechOptions.Languages, options.Target); applying = false;
        var bg = Ui.Color(options.Background); var fg = BubbleView.ReadableText(Ui.Color(options.Foreground), bg);
        Ui.Apply(this, IsDarkBackground); Ui.Apply(appearanceFace, IsDarkBackground);
        var menuBackground = new SolidColorBrush(bg);
        var menuText = new SolidColorBrush(fg);
        var menuLine = new SolidColorBrush(Ui.Color(IsDarkBackground ? "#40535F" : "#D7E1E7"));
        if (diagnostics.Child is Border detail) { Ui.Apply(detail, IsDarkBackground); detail.Background = menuBackground; status.Foreground = menuText; status.Opacity = 1; }
        controlFace.Background = Brushes.Transparent; appearanceFace.Background = menuBackground; appearanceFace.BorderBrush = menuLine;
        opacityLabel.Foreground = menuText; opacityLabel.Opacity = 1;
        face.Background = BubbleView.Alpha(bg, options.Opacity); face.BorderBrush = BubbleView.Alpha(fg, .16 * options.Opacity);
        languages.Resources["UiInput"] = Brushes.Transparent; languages.Resources["UiLine"] = Brushes.Transparent;
        languages.Resources["UiText"] = menuText; languages.Resources["UiAccent"] = new SolidColorBrush(Ui.Color("#74B9B2"));
        Foreground = new SolidColorBrush(fg); 
        foreach (var button in toolbar.Children.OfType<Button>()) { button.Foreground = new SolidColorBrush(Ui.Color("#338691")); button.Background = Brushes.Transparent; button.Width = 24; button.Margin = new Thickness(.5, 0, .5, 0); }
        themeButton.ToolTip = IsDarkBackground ? "切换为明亮字幕" : "切换为深色字幕"; themeButton.Content = IsDarkBackground ? "\uE706" : "\uE708";
        header.Visibility = options.ControlsHidden || locked ? Visibility.Collapsed : Visibility.Visible;
        controlFace.Visibility = header.Visibility;
        double nextHeader = header.Visibility == Visibility.Visible ? 36 : 12;
        double minimum = Math.Max(100, options.FontSize * 3.75 + nextHeader + 14);
        if (ready && headerHeight != nextHeader)
        {
            double top = Top, height = ActualHeight, delta = nextHeader - headerHeight;
            MinHeight = minimum; Height = Math.Max(minimum, height + delta); Top = top - delta;
        }
        else { MinHeight = minimum; if (Height < minimum) Height = minimum; }
        headerHeight = nextHeader; headerRow.Height = new GridLength(nextHeader);
        if (header.Visibility != Visibility.Visible) diagnostics.IsOpen = false;
        // A minimal hit surface allows dragging unlocked transparent blank areas.
        // Locked cinema mode removes it entirely, including the resize edges.
        Background = locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        if (Content is Grid surface) foreach (var edge in surface.Children.OfType<Thumb>()) edge.Opacity = locked ? 0 : 1;
        applying = true; opacity.Value = options.Opacity * 100; opacityLabel.Text = $"背景不透明度 · {opacity.Value:0}%"; applying = false;
        foreach (var row in captions.RowDefinitions) row.Height = new GridLength(options.FontSize * 1.25);
        var shadow = options.Opacity < .8 && Ui.Contrast(fg, Colors.Black) > Ui.Contrast(fg, Colors.White)
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = .45, BlurRadius = 1, ShadowDepth = 1 } : null;
        foreach (var row in cueRows) { row.FontFamily = subtitleFont; row.FontWeight = subtitleWeight; row.FontSize = options.FontSize; row.LineHeight = options.FontSize * 1.25; row.Foreground = Foreground; row.Effect = shadow; }
        var label = new FormattedText(SpeechOptions.LanguageName(options.Target), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(textFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        languages.Width = Math.Clamp(Math.Ceiling(label.WidthIncludingTrailingWhitespace) + 30, 92, 148);
    }
    private void QueueGeometrySave() { if (!ready || stopped) return; geometrySave.Stop(); geometrySave.Start(); }
    private void RestoreWindowBounds()
    {
        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2)); var work = Native.WorkArea(new Native.Point((int)center.X, (int)center.Y));
        var a = PointFromScreen(new Point(work.Left, work.Top)); var b = PointFromScreen(new Point(work.Right, work.Bottom));
        double left = Left + a.X, top = Top + a.Y, width = b.X - a.X, height = b.Y - a.Y;
        MaxWidth = Math.Max(MinWidth, width); Width = Math.Clamp(Width, MinWidth, MaxWidth); Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, height));
        Left = Math.Clamp(Left, left, left + Math.Max(0, width - Width)); Top = Math.Clamp(Top, top, top + Math.Max(0, height - Height));
    }
    private void Tick()
    {
        var view = session.Snapshot(); var options = session.Options; if (options == null) return;
        var snapshot = session.CueSnapshot(); double font = options.FontSize;
        var selected = options.Bilingual ? snapshot.Where(cue => cue.Translation.Length > 0).ToArray() : snapshot;
        int capacity = options.Bilingual ? 2 : 3;
        var rows = cueLayout.Layout(selected, LiveCaptions.Now, options.LineLifetime, options.PreviewOriginal, (text, draft) =>
        {
            double width = Math.Max(60, ActualWidth - 36);
            // Keep identical wrapping when a draft becomes normal text.
            foreach (var style in new[] { FontStyles.Normal, FontStyles.Italic })
            {
                var measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(subtitleFont, style, subtitleWeight, FontStretches.Normal), font, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                if (measured.WidthIncludingTrailingWhitespace > width) return false;
            }
            return true;
        }, ActualWidth + font * 10000, capacity);
        // LatestSource is a history stream and may still belong to the preceding translation.
        // Only show source held by a visible cue, or a fresh unclaimed preview.
        var current = snapshot.LastOrDefault();
        string original = CueRows.InlineText(current?.Original ?? "");
        bool visibleSource = current != null && (current.Translation.Length == 0 || rows.Any(row => row.Id == current.Id));
        if (options.Bilingual && rows.Length > 0 && visibleSource && original.Length > 0)
            rows = rows.Append(new CueRows.Row(0, Tail(original, 100), true, true)).ToArray();
        for (int i = 0; i < cueRows.Length; i++)
        {
            var row = cueRows[i]; row.Visibility = i < rows.Length ? Visibility.Visible : Visibility.Collapsed;
            row.Text = i < rows.Length ? rows[i].Text : "";
            row.FontStyle = i < rows.Length && rows[i].Draft ? FontStyles.Italic : FontStyles.Normal;
            row.Opacity = i < rows.Length && rows[i].Draft ? rows[i].Source ? .55 : .7 : 1;
            row.TextTrimming = options.Bilingual && i < rows.Length && rows[i].Source ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        }
        var visible = rows.Select(row => row.Id).ToHashSet();
        displayCopy = string.Join("\n", snapshot.Where(cue => visible.Contains(cue.Id) && cue.Translation.Length > 0).Select(cue => cue.Translation));
        UpdateStatus(view, rows.Length == 0, options);
    }
    private void UpdateStatus((string source, string target, string status, double level) view, bool idle, SpeechOptions options)
    {
        idleHandle.Background = BubbleView.Alpha(Ui.Color(options.Foreground), .4);
        idleHandle.Visibility = idle && options.Opacity <= .05 ? Visibility.Visible : Visibility.Collapsed;
        meter.Value = view.level;
        status.Text = view.status + $" · 已发送 {TimeSpan.FromSeconds(session.SentSeconds):mm\\:ss}" + (session.Usage >= 0 ? $" · 最近{"Live"}返回 {session.Usage:N0} tokens" : " · Live用量未返回") + (locked ? " · 已锁定，从托盘解锁" : "");
    }
    private static string Tail(string text, int length) => text.Length > length ? "…" + text[^length..] : text;
    internal void SetLocked(bool value)
    {
        locked = value; IntPtr h = new WindowInteropHelper(this).Handle; long style = Native.GetWindowLongPtr(h, -20).ToInt64();
        Native.SetWindowLongPtr(h, -20, new IntPtr(value ? style | 0x20 | 0x08000000 : style & ~0x20 & ~0x08000000));
        if (value) appearance.IsOpen = false; ApplyOptions(); if (!value) { Show(); Activate(); }
    }
    private void ShowHistory()
    {
        if (history != null) { history.Activate(); return; }
        history = new SpeechHistoryWindow(session); history.Closed += (_, _) => history = null; history.Show();
    }
}

internal sealed class SpeechHistoryWindow : Window
{
    private readonly LiveSession session;
    private readonly StackPanel rows = new();
    private readonly TextBlock info = Ui.Hint("");
    private readonly Button previous = new() { Content = "上一页" }, next = new() { Content = "下一页" };
    private readonly ComboBox stream = new() { ItemsSource = new[] { "译文记录", "原文记录" }, SelectedIndex = 0, Width = 145, Name = "HistoryStream" };
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private int page, loadedCount = -1, loadRevision;
    private long loadedHistoryRevision = -1;
    private bool closed, loading;
    internal SpeechHistoryWindow(LiveSession session)
    {
        this.session = session; Title = "轻译 · 本次语音记录"; Width = 680; Height = 500; MinWidth = 420; MinHeight = 280;
        Icon = AppIcons.Image; Ui.Apply(this); Ui.Bind(this, BackgroundProperty, "Bg"); Ui.Bind(this, ForegroundProperty, "Text");
        CommandManager.AddPreviewExecutedHandler(this, async (_, e) =>
        {
            if (e.Command != ApplicationCommands.Copy || e.OriginalSource is not TextBox box || box.SelectionLength == 0) return;
            e.Handled = true; bool ok = await ClipboardWriter.CopyAsync(box.SelectedText); if (!closed) info.Text = ok ? "已复制" : "未复制，请重试。";
        });
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var refresh = new Button { Content = "刷新" }; var clear = new Button { Content = "清空本次记录" }; bar.Children.Add(stream); bar.Children.Add(previous); bar.Children.Add(next); bar.Children.Add(refresh); bar.Children.Add(clear);
        stream.SelectionChanged += async (_, _) => { page = 0; await LoadPage(); };
        previous.Click += async (_, _) => { page = Math.Max(0, page - 1); await LoadPage(); }; next.Click += async (_, _) => { page++; await LoadPage(); };
        refresh.Click += async (_, _) => { page = 0; await LoadPage(); scroll.ScrollToTop(); }; clear.Click += async (_, _) => { try { await session.ClearHistoryAsync(); page = 0; await LoadPage(); } catch { info.Text = "记录清空失败，请检查磁盘。"; } };
        scroll.Content = rows;
        DockPanel.SetDock(info, Dock.Bottom); root.Children.Add(info); root.Children.Add(scroll);
        refreshTimer.Tick += async (_, _) =>
        {
            if (closed || loading || page != 0 || scroll.VerticalOffset > 8) return;
            if (rows.Children.OfType<StackPanel>().SelectMany(body => body.Children.OfType<TextBox>()).Any(box => box.SelectionLength > 0)) return;
            int count = stream.SelectedIndex == 1 ? session.OriginalRecords : session.Records;
            if (count != loadedCount) { await LoadPage(); scroll.ScrollToTop(); }
            else if (loadedHistoryRevision != session.HistoryRevision && rows.Children.Count > 0)
            {
                loading = true; int version = loadRevision, mode = stream.SelectedIndex; long historyVersion = session.HistoryRevision;
                try
                {
                    var latest = (await session.ReadPageAsync(0, mode == 1, 1)).FirstOrDefault();
                    if (closed || version != loadRevision || mode != stream.SelectedIndex || latest == null) return;
                    var body = (StackPanel)rows.Children[0];
                    if (Equals(body.Tag, latest.Context))
                    {
                        ((TextBlock)body.Children[0]).Text = RecordHeading(latest);
                        var box = (TextBox)body.Children[1]; string value = mode == 1 ? latest.Original : latest.Translation;
                        if (box.Text != value) box.Text = value;
                        loadedHistoryRevision = historyVersion;
                    }
                    else await LoadPage();
                }
                catch { if (!closed) info.Text = "记录暂时无法刷新，可稍后重试。"; }
                finally { if (version == loadRevision) loading = false; }
            }
        };
        Loaded += async (_, _) => { await LoadPage(); refreshTimer.Start(); }; Closed += (_, _) => { closed = true; refreshTimer.Stop(); rows.Children.Clear(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    private async Task LoadPage()
    {
        int revision = ++loadRevision; loading = true;
        try
        {
            int mode = stream.SelectedIndex; bool originalStream = mode == 1;
            int count = originalStream ? session.OriginalRecords : session.Records; long historyVersion = session.HistoryRevision;
            page = Math.Clamp(page, 0, Math.Max(0, (count - 1) / 40));
            var records = await session.ReadPageAsync(page, originalStream); if (closed || revision != loadRevision || mode != stream.SelectedIndex) return; rows.Children.Clear(); loadedCount = count; loadedHistoryRevision = historyVersion;
            foreach (var record in records)
            {
                var body = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Tag = record.Context };
                body.Children.Add(Ui.Hint(RecordHeading(record)));
                var text = new TextBox { Name = originalStream ? "HistoryOriginalText" : "HistoryTranslationText", Text = originalStream ? record.Original : record.Translation, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 0), FontSize = 15 };
                Ui.Bind(text, Control.ForegroundProperty, "Text"); body.Children.Add(text); rows.Children.Add(body);
            }
            previous.IsEnabled = page > 0; next.IsEnabled = (page + 1) * 40 < count;
            info.Text = $"最新在上 · 第 {page + 1} 页 · {count} 条 · {session.HistoryBytes / 1024.0:N0} KB / 20 MB · 关闭功能后删除\n原文、译文独立分段；未建立可靠逐句关联。";
        }
        catch { if (!closed) info.Text = "无法读取本次记录，可能已随功能关闭清理。"; }
        finally { if (revision == loadRevision) loading = false; }
    }
    private static string RecordHeading(SpeechRecord record) => $"{record.Time:HH:mm:ss} · {record.Source}" + (record.Pending ? " · 未完成片段" : "");
}

