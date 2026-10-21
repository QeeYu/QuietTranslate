using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuietTranslate;

internal static partial class Ui
{
    internal static ThemeMode Theme { get; set; }
    internal static bool ResolveTheme(ThemeMode mode) => mode == ThemeMode.Dark || (mode == ThemeMode.System && IsSystemDark);
    internal static bool IsDark => ResolveTheme(Theme);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    internal static int ApplyCaption(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.WindowStyle == WindowStyle.None) return 0;
        int dark = window.Resources["UiThemeDark"] is true ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, 4);
        int Ref(string key) { var c = ((SolidColorBrush)window.Resources["Ui" + key]).Color; return c.R | c.G << 8 | c.B << 16; }
        int surface = Ref("Surface"), text = Ref("Text"), line = Ref("Line");
        // Windows 11 supports exact caption colors; earlier Windows keeps its native fallback.
        int result = DwmSetWindowAttribute(hwnd, 35, ref surface, 4);
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);
        DwmSetWindowAttribute(hwnd, 34, ref line, 4);
        return result;
    }
    internal static void RefreshTheme(FrameworkElement root, bool dark)
    {
        void Visit(DependencyObject node)
        {
            if (node is FrameworkElement element)
            {
                if (element.Resources.Contains("UiThemeDark")) Apply(element, dark);
                if (element.ContextMenu is ContextMenu menu) Apply(menu, dark);
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Apply(root, dark); Visit(root);
    }
}
