using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class AppIcons
{
    internal static readonly Icon Active = Load("QuietTranslate.ico");
    internal static readonly Icon Paused = Load("QuietTranslate-Paused.ico");
    internal static readonly ImageSource Image = LoadImage("QuietTranslate.ico");
    internal static readonly ImageSource PausedImage = LoadImage("QuietTranslate-Paused.ico");
    private static ImageSource LoadImage(string name)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/" + name), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First(); frame.Freeze(); return frame;
    }
    private static Icon Load(string name)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + name)).Stream;
        using var icon = new Icon(stream); return (Icon)icon.Clone();
    }
}
