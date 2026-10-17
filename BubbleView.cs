using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuietTranslate;

// Settings and live bubbles share layout, palette and glyph measurements.
internal sealed class BubbleView : Border
{
    private readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer scroll;
    private readonly Button copyButton = ActionButton("\uE8C8", "复制译文"), moreButton = ActionButton("\uE712", "更多操作"), closeButton = ActionButton("\uE8BB", "关闭（Esc）");
    private readonly ContextMenu menu = new() { FontSize = 13, Placement = PlacementMode.Top, StaysOpen = false };
    private readonly MenuItem contextItem = new() { Header = "在翻译栏编辑原文…", Visibility = Visibility.Collapsed };
    private bool dark;
    internal ScrollViewer Viewport => scroll;
    internal void SetTheme(bool value) { dark = value; Ui.Apply(this, dark); }
    private readonly Border toolbar = new() { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(-5, 0, -6, 5), Padding = new Thickness(0, 0, 0, 2) };
    internal Border DragHandle { get; } = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "拖动气泡 · 本次保持位置" };
    private Action? contextAction;
    internal bool IsMenuOpen => menu.IsOpen;
    internal Button MoreButton => moreButton;
    internal ContextMenu ActionsMenu => menu;
    internal bool ColorAdjusted { get; private set; }
    internal Color SurfaceColor => ((SolidColorBrush)Background).Color;
    internal BubbleView(double opacity, double fontSize, string textColor, bool? darkTheme, Action close, Action copy)
    {
        dark = darkTheme ?? Ui.IsDark; Ui.Apply(this, dark);
        menu.Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
        CornerRadius = new CornerRadius(11); Padding = new Thickness(12, 5, 12, 11); BorderThickness = new Thickness(1);
        System.Windows.Documents.TextElement.SetFontFamily(this, new FontFamily("Segoe UI, Microsoft YaHei UI"));
        var stack = new StackPanel();
        scroll = new ScrollViewer { Content = text, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.Attach(scroll);
        var header = new DockPanel { Height = 22, LastChildFill = true };
        var footer = new StackPanel { Orientation = Orientation.Horizontal };
        copyButton.Width = moreButton.Width = closeButton.Width = 22;
        copyButton.Click += (_, _) => copy();
        moreButton.Click += (_, _) => { menu.PlacementTarget = moreButton; menu.IsOpen = true; };
        closeButton.Click += (_, _) => close();
        footer.Children.Add(copyButton); footer.Children.Add(moreButton); footer.Children.Add(closeButton); DockPanel.SetDock(footer, Dock.Right); header.Children.Add(footer);
        DragHandle.Child = new TextBlock { Text = "⠿", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Opacity = .48 };
        header.Children.Add(DragHandle); toolbar.Child = header; stack.Children.Add(toolbar); stack.Children.Add(scroll);
        var copyItem = new MenuItem { Header = "复制译文" }; copyItem.Click += (_, _) => { menu.IsOpen = false; copy(); };
        contextItem.Click += (_, _) => { var action = contextAction; menu.IsOpen = false; action?.Invoke(); };
        var closeItem = new MenuItem { Header = "关闭气泡（Esc）" }; closeItem.Click += (_, _) => { menu.IsOpen = false; close(); };
        menu.Items.Add(copyItem); menu.Items.Add(contextItem); menu.Items.Add(new Separator()); menu.Items.Add(closeItem);
        ContextMenu = menu; Child = stack; ApplyAppearance(opacity, fontSize, textColor);
    }
    internal void ApplyAppearance(double opacity, double fontSize, string textColor)
    {
        var surface = ((SolidColorBrush)Resources["UiSurface"]).Color;
        SetPalette(surface, textColor.Length == 0 ? ((SolidColorBrush)Resources["UiText"]).Color : Ui.Color(Settings.NormalizeTextColor(textColor)), ((SolidColorBrush)Resources["UiLine"]).Color, opacity, opacity);
        text.FontSize = Math.Clamp(fontSize, 12, 22); text.LineHeight = text.FontSize * 1.45;
        ColorAdjusted = false;
    }
    internal static SolidColorBrush Alpha(Color color, double opacity) => new(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B));
    internal static Color Blend(Color a, Color b, double amount) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));
    internal static Color ReadableText(Color foreground, Color surface)
    {
        if (Ui.Contrast(foreground, surface) >= 4.5) return foreground;
        var ink = Ui.Color("#19232D"); var paper = Ui.Color("#F5F8FB");
        Color target = Ui.Contrast(ink, surface) >= Ui.Contrast(paper, surface) ? ink : paper;
        if (Ui.Contrast(target, surface) < 4.5) target = Ui.Contrast(Colors.Black, surface) >= Ui.Contrast(Colors.White, surface) ? Colors.Black : Colors.White;
        double low = 0, high = 1;
        // Keep the chosen hue as far as possible, instead of replacing every accent with black/white.
        for (int i = 0; i < 12; i++) { double mid = (low + high) / 2; if (Ui.Contrast(Blend(foreground, target, mid), surface) >= 4.5) high = mid; else low = mid; }
        return Blend(foreground, target, high);
    }
    internal void ApplyAppearance(Settings s)
    {
        ApplyAppearance(s.BubbleOpacity, s.BubbleFontSize, s.BubbleTextColor);
        Color surface = s.BubbleBackgroundColor.Length > 0 ? Ui.Color(Settings.NormalizeTextColor(s.BubbleBackgroundColor)) : Ui.Color(dark ? "#20222F" : "#FAFBFD");
        Color requested = s.BubbleTextColor.Length > 0 ? Ui.Color(Settings.NormalizeTextColor(s.BubbleTextColor)) : Ui.Color(dark ? "#ECF2F7" : "#25313D");
        Color foreground = s.BubbleSmartColors ? ReadableText(requested, surface) : requested;
        // Automatic text follows the actual chosen background even if the Windows theme is different.
        if (s.BubbleTextColor.Length == 0)
        {
            var ink = Ui.Color("#25313D"); var paper = Ui.Color("#F1F5F9");
            foreground = ReadableText(Ui.Contrast(ink, surface) >= Ui.Contrast(paper, surface) ? ink : paper, surface);
        }
        ColorAdjusted = foreground != requested;
        Color line = s.BubbleBorderColor.Length > 0 ? Ui.Color(Settings.NormalizeTextColor(s.BubbleBorderColor)) : Blend(surface, ReadableText(foreground, surface), .17);
        SetPalette(surface, foreground, line, s.BubbleOpacity, s.BubbleBorderOpacity < 0 ? s.BubbleOpacity : s.BubbleBorderOpacity);
        BorderThickness = new Thickness(s.BubbleBorderVisible ? 1 : 0);
        toolbar.BorderThickness = new Thickness(0, 0, 0, s.BubbleBorderVisible ? 1 : 0);
    }
    private void SetPalette(Color surface, Color foreground, Color line, double opacity, double borderOpacity)
    {
        Background = Alpha(surface, opacity); BorderBrush = Alpha(line, borderOpacity); text.Foreground = new SolidColorBrush(foreground);
        toolbar.BorderBrush = Alpha(line, borderOpacity * .5);
        copyButton.Foreground = moreButton.Foreground = closeButton.Foreground = text.Foreground;
        ((TextBlock)DragHandle.Child).Foreground = text.Foreground;
        Resources["UiHover"] = new SolidColorBrush(Blend(surface, foreground, .13));
        Resources["UiSurface"] = new SolidColorBrush(surface); Resources["UiText"] = new SolidColorBrush(ReadableText(foreground, surface)); Resources["UiLine"] = new SolidColorBrush(line);
        menu.Background = new SolidColorBrush(surface); menu.Foreground = new SolidColorBrush(ReadableText(foreground, surface)); menu.BorderBrush = new SolidColorBrush(line); menu.BorderThickness = new Thickness(1);
        menu.Resources["UiText"] = menu.Foreground;
        menu.Resources["UiHover"] = new SolidColorBrush(Blend(surface, foreground, .13));
        menu.Resources["UiLine"] = new SolidColorBrush(line);
    }
    // Measure actual glyph advances (including emoji/font fallback), then balance wrapping of longer paragraphs.
    internal double FittingWidth(double maximumWidth)
    {
        maximumWidth = Math.Max(60, maximumWidth);
        double chrome = Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;
        double minimum = Math.Min(118, maximumWidth), limit = Math.Max(1, maximumWidth - chrome);
        string value = string.IsNullOrWhiteSpace(Text) ? "正在翻译…" : Text.Trim();
        if (value.Length > 320) return maximumWidth;
        var formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { LineHeight = text.LineHeight };
        double natural = formatted.WidthIncludingTrailingWhitespace;
        double content = natural <= limit ? natural : Math.Clamp(Math.Sqrt(Math.Max(1, natural * formatted.Height) * 2.8), Math.Min(150, limit), limit);
        formatted.MaxTextWidth = Math.Max(1, content);
        // One rounding guard prevents a final glyph wrapping because of fractional monitor scaling.
        return Math.Clamp(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + chrome + 2), minimum, maximumWidth);
    }
    internal void SetHeightLimit(double height) => scroll.MaxHeight = Math.Max(20, height - Padding.Top - Padding.Bottom - BorderThickness.Top - BorderThickness.Bottom - 29 - toolbar.BorderThickness.Bottom);
    internal string Text { get => text.Text; set { if (text.Text != value) { text.Text = value; copyButton.Content = "\uE8C8"; } } }
    internal void Notify(string value) { copyButton.Content = value == "已复制" ? "\uE73E" : "\uE7BA"; copyButton.ToolTip = value; }
    internal void SetContext(string label, Action action) { contextItem.Header = label; contextAction = action; contextItem.Visibility = Visibility.Visible; }
    internal void Clear() { menu.IsOpen = false; text.Text = ""; contextAction = null; contextItem.Visibility = Visibility.Collapsed; }
    internal byte BackgroundAlpha => ((SolidColorBrush)Background).Color.A;
    internal Color TextColor => ((SolidColorBrush)text.Foreground).Color;
    internal bool ContainsMenu(Native.Point point)
    {
        if (!menu.IsOpen || PresentationSource.FromVisual(menu) is not System.Windows.Interop.HwndSource source) return false;
        Native.GetWindowRect(source.Handle, out var r); return point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom;
    }
    private static Button ActionButton(string label, string tooltip) { var button = Ui.IconButton(label, tooltip); button.Focusable = false; button.Padding = new Thickness(4); button.FontSize = 10; button.Height = 22; button.MinHeight = 0; return button; }
}
