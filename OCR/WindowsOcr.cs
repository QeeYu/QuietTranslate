using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace QuietTranslate;

internal static class ScreenshotOcr
{
    public static string Languages => string.Join("、", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
    public static string MissingLanguages
    {
        get
        {
            var tags = OcrEngine.AvailableRecognizerLanguages;
            return string.Join("、", new[] { tags.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ? "" : "英语", tags.Any(l => l.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) ? "" : "日语" }.Where(t => t.Length > 0));
        }
    }
    public static bool IsReady { get { try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; } catch { return false; } } }
    public static string ReadinessMessage
    {
        get { try { return IsReady ? "本机可用语言：" + Languages + "。缺少的日英组件：" + (MissingLanguages.Length == 0 ? "无" : MissingLanguages) + "；云端 OCR 不依赖这些组件。" : "没有本机 OCR 语言组件，可直接选择云端 OCR。"; } catch { return "Windows OCR 暂不可用，可选择云端 OCR。"; } }
    }
    public static void RequireLanguages()
    { if (!IsReady) throw new InvalidOperationException(ReadinessMessage + "\n托盘 → 设置 → 打开语言设置，安装对应的光学字符识别组件后重启轻译。"); }
    public static async Task<string> RecognizeAsync(Bitmap source, CancellationToken ct, string language = "auto")
    {
        RequireLanguages();
        var tags = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        var japanese = tags.FirstOrDefault(t => t.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
        var english = tags.FirstOrDefault(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        double scale = Math.Min(2, (double)OcrEngine.MaxImageDimension / Math.Max(source.Width, source.Height));
        using var resized = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using (var graphics = Graphics.FromImage(resized))
        { graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; graphics.DrawImage(source, 0, 0, resized.Width, resized.Height); }
        using var memory = new MemoryStream(); resized.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(memory.ToArray()); await writer.StoreAsync(); }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        ct.ThrowIfCancellationRequested();
        async Task<string> Read(string tag)
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (engine == null) return "";
            var result = await engine.RecognizeAsync(software);
            ct.ThrowIfCancellationRequested();
            return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
        }
        if (language != "auto")
        {
            var preferred = tags.FirstOrDefault(t => t.StartsWith(language, StringComparison.OrdinalIgnoreCase));
            if (preferred == null) throw new InvalidOperationException("未安装指定语种的本机 OCR 组件，可改用自动识别或云端 OCR。");
            return await Read(preferred);
        }
        // Kana is strong evidence; otherwise use the English pass for Latin text.
        // Windows OCR has no confidence score and kanji-only Japanese cannot be perfectly auto classified.
        string ja = japanese == null ? "" : await Read(japanese);
        if (ja.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u4e00' and <= '\u9fff')) return ja;
        string en = english == null ? "" : await Read(english);
        if (japanese == null && english == null && tags.Count > 0) return await Read(tags[0]);
        return string.IsNullOrWhiteSpace(en) ? ja : en;
    }
}
