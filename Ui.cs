using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuietTranslate;

internal static partial class Ui
{
    private sealed class ToolTipContentSelector : DataTemplateSelector
    {
        private readonly DataTemplate wrapped = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding}" TextWrapping="Wrap"/></DataTemplate>
            """);
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => item is string ? wrapped : base.SelectTemplate(item, container);
    }
    private static readonly DataTemplateSelector toolTipContent = new ToolTipContentSelector();
    internal static bool IsSystemDark
    {
        get { try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; } catch { return false; } }
    }
    internal static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    internal static double Contrast(Color a, Color b)
    {
        double Channel(byte channel) { double value = channel / 255.0; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    internal static void Apply(FrameworkElement target, bool? dark = null)
    {
        bool night = dark ?? IsDark;
        bool themed = target.Resources.Contains("UiThemeDark");
        target.Resources["UiThemeDark"] = night;
        string[] keys = { "Bg", "Surface", "Text", "Muted", "Line", "Input", "Control", "Hover", "Accent", "AccentSoft", "Warning", "OnAccent" };
        string[] values = night
            ? new[] { "#14151D", "#1C1E29", "#EAECF5", "#A6ADC2", "#343747", "#171923", "#292C3C", "#34394C", "#B9C4FF", "#292E46", "#F3C38B", "#191D32" }
            : new[] { "#F1F5F7", "#FFFFFF", "#24343E", "#576D7A", "#DCE5EA", "#F5F8FA", "#EDF2F5", "#E5EDF1", "#247A85", "#EAF5F4", "#8D591D", "#FFFFFF" };
        for (int i = 0; i < keys.Length; i++) target.Resources["Ui" + keys[i]] = new SolidColorBrush(Color(values[i]));
        if (target is Window window)
        {
            if (!themed) window.SourceInitialized += (_, _) => ApplyCaption(window);
            ApplyCaption(window);
        }
        if (themed) return;
        target.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="ToolTip"><Setter Property="Background" Value="{DynamicResource UiSurface}"/><Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="BorderBrush" Value="{DynamicResource UiLine}"/><Setter Property="MaxWidth" Value="300"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8" Padding="10,7"><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>
              <Style TargetType="ContextMenu">
                <Setter Property="Background" Value="{DynamicResource UiSurface}"/><Setter Property="Foreground" Value="{DynamicResource UiText}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource UiLine}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="FontSize" Value="13"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" Padding="4"><ScrollViewer MaxHeight="360" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/></ScrollViewer></Border></ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="MenuItem">
                <Setter Property="Foreground" Value="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <Setter Property="Padding" Value="10,8"/><Setter Property="MinWidth" Value="116"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem"><Border x:Name="body" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}"><DockPanel><Grid Width="23"><ContentPresenter x:Name="icon" ContentSource="Icon" VerticalAlignment="Center"/><TextBlock x:Name="check" Text="✓" Visibility="Hidden" FontSize="11"/></Grid><TextBlock DockPanel.Dock="Right" Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource UiMuted}" FontSize="11" Margin="24,0,0,0" VerticalAlignment="Center"/><ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/></DockPanel></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="body" Property="Background" Value="{DynamicResource UiHover}"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="check" Property="Visibility" Value="Visible"/><Setter TargetName="icon" Property="Visibility" Value="Hidden"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="Separator"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Margin="8,4" Background="{DynamicResource UiLine}"/></ControlTemplate></Setter.Value></Setter></Style>
              <Style TargetType="TextBlock"><Setter Property="Foreground" Value="{DynamicResource UiText}"/></Style>
              <Style TargetType="Button">
                <Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="Background" Value="{DynamicResource UiControl}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource UiLine}"/><Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="13,9"/><Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                  <Grid>
                    <Border x:Name="body" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7"/>
                    <Border x:Name="hover" Background="{TemplateBinding Foreground}" CornerRadius="7" Opacity="0" IsHitTestVisible="False"/>
                    <Border x:Name="focus" BorderBrush="{DynamicResource UiAccent}" BorderThickness="1" CornerRadius="7" Opacity="0" IsHitTestVisible="False"/>
                    <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" IsHitTestVisible="False"/>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="hover" Property="Opacity" Value="0.07"/></Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter TargetName="hover" Property="Opacity" Value="0.13"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="Opacity" Value="1"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="CaretBrush" Value="{DynamicResource UiAccent}"/>
                <Setter Property="Background" Value="{DynamicResource UiInput}"/><Setter Property="BorderBrush" Value="{DynamicResource UiLine}"/>
                <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,9"/>
                <Setter Property="SelectionBrush" Value="{DynamicResource UiAccent}"/><Setter Property="SelectionOpacity" Value="0.25"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
                  <Border x:Name="body" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7">
                    <ScrollViewer x:Name="PART_ContentHost"/>
                  </Border>
                  <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="body" Property="BorderBrush" Value="{DynamicResource UiAccent}"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="PasswordBox">
                <Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="CaretBrush" Value="{DynamicResource UiAccent}"/>
                <Setter Property="Background" Value="{DynamicResource UiInput}"/><Setter Property="BorderBrush" Value="{DynamicResource UiLine}"/>
                <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,9"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="PasswordBox">
                  <Border x:Name="body" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7"><ScrollViewer x:Name="PART_ContentHost"/></Border>
                  <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="body" Property="BorderBrush" Value="{DynamicResource UiAccent}"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="Padding" Value="10,10"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="body" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="5"><ContentPresenter/></Border>
                  <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="body" Property="Background" Value="{DynamicResource UiHover}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="body" Property="Background" Value="{DynamicResource UiAccentSoft}"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ComboBox">
                <Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="Background" Value="{DynamicResource UiInput}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
                  <Grid>
                    <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="{DynamicResource UiInput}" BorderBrush="{DynamicResource UiLine}" BorderThickness="1" CornerRadius="7" Padding="11,10"><TextBlock Text="⌄" HorizontalAlignment="Right" Foreground="{DynamicResource UiMuted}"/></Border></ControlTemplate></ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="11,9,30,9" VerticalAlignment="Center" IsHitTestVisible="False"/>
                    <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False">
                      <Border Background="{DynamicResource UiSurface}" BorderBrush="{DynamicResource UiLine}" BorderThickness="1" CornerRadius="8" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="220"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border>
                    </Popup>
                  </Grid>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{DynamicResource UiText}"/><Setter Property="Margin" Value="0,5,0,10"/><Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <Border x:Name="box" Width="17" Height="17" CornerRadius="4" VerticalAlignment="Top" Background="{DynamicResource UiInput}" BorderBrush="{DynamicResource UiLine}" BorderThickness="1"><TextBlock x:Name="mark" Text="✓" Visibility="Hidden" FontSize="12" Foreground="{DynamicResource UiOnAccent}" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                  <ContentPresenter Grid.Column="1" Margin="8,0,0,0" RecognizesAccessKey="True"/>
                </Grid><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="mark" Property="Visibility" Value="Visible"/><Setter TargetName="box" Property="Background" Value="{DynamicResource UiAccent}"/><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource UiAccent}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource UiAccent}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource UiMuted}"/></Style>
              <Style TargetType="Slider">
                <Setter Property="Height" Value="24"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider">
                  <Grid Margin="0,2"><Track x:Name="PART_Track">
                    <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Height="4" CornerRadius="2" Background="{DynamicResource UiAccent}"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                    <Track.Thumb><Thumb Width="16" Height="16"><Thumb.Template><ControlTemplate><Ellipse Fill="{DynamicResource UiSurface}" Stroke="{DynamicResource UiAccent}" StrokeThickness="2"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                    <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Height="4" CornerRadius="2" Background="{DynamicResource UiLine}"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                  </Track></Grid>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ScrollBar"><Setter Property="Width" Value="7"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
                <Track x:Name="PART_Track" IsDirectionReversed="True"><Track.Thumb><Thumb><Thumb.Template><ControlTemplate><Border Background="{DynamicResource UiLine}" CornerRadius="3" Margin="1"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track>
              </ControlTemplate></Setter.Value></Setter></Style>
            </ResourceDictionary>
            """));
        var toolTip = new Style(typeof(ToolTip), (Style)target.Resources[typeof(ToolTip)]);
        toolTip.Setters.Add(new Setter(ContentControl.ContentTemplateSelectorProperty, toolTipContent)); target.Resources[typeof(ToolTip)] = toolTip;
        target.Resources[MenuItem.SeparatorStyleKey] = target.Resources[typeof(Separator)];
    }
    internal static void Bind(DependencyObject target, DependencyProperty property, string key)
    { if (target is FrameworkElement element) element.SetResourceReference(property, "Ui" + key); }
    internal static Button IconButton(string glyph, string tooltip)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Width = 26, Height = 26, Padding = new Thickness(5), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip); return button;
    }
    internal static TextBlock Hint(string text) { var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19 }; Bind(block, TextBlock.ForegroundProperty, "Muted"); return block; }
    internal static Border Card(UIElement child) { var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = child }; Bind(border, Border.BackgroundProperty, "Surface"); return border; }
}
