using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace QuietTranslate;

public sealed class SpeechOptions
{
    public string Endpoint { get; set; } = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    public string Model { get; set; } = "gemini-3.5-live-translate-preview";
    public int Authentication { get; set; } // 0: key query, 1: x-goog-api-key, 2: Bearer
    public string Target { get; set; } = "zh-Hans";
    public string Hotkey { get; set; } = "Ctrl+Alt+S";
    public bool CaptureProcess { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStarted { get; set; }
    public string ProcessName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public bool Bilingual { get; set; }
    public bool PreviewOriginal { get; set; } = true;
    public bool EchoTarget { get; set; }
    public double FontSize { get; set; } = 24;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public int FontWeight { get; set; } = 400;
    public double Opacity { get; set; }
    public string Background { get; set; } = "#17212B";
    public string Foreground { get; set; } = "#F5F7FA";
    public bool ControlsHidden { get; set; }
    public int SubtitleLayoutVersion { get; set; } = 4;
    public double LineLifetime { get; set; } = 5;
    public double WindowWidth { get; set; } = 760;
    public double WindowHeight { get; set; } = 144;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    internal void CopyAppearanceFrom(SpeechOptions value)
    {
        Opacity = value.Opacity; Background = value.Background; Foreground = value.Foreground; ControlsHidden = value.ControlsHidden;
        WindowWidth = value.WindowWidth; WindowHeight = value.WindowHeight; WindowLeft = value.WindowLeft; WindowTop = value.WindowTop;
        FontFamily = value.FontFamily; FontWeight = value.FontWeight; FontSize = value.FontSize;
        SubtitleLayoutVersion = 4;
    }
    public static readonly string[] Languages = { "zh-Hans", "zh-Hant", "en", "ja", "ko", "fr", "de", "es", "pt-BR", "ru", "it", "ar", "hi", "th", "vi", "id", "tr", "pl", "uk", "nl" };
    internal SpeechOptions Copy() => (SpeechOptions)MemberwiseClone();
    internal string ConnectionIdentity => System.Text.Json.JsonSerializer.Serialize(new { Endpoint, Model, Authentication, Target, EchoTarget, CaptureProcess, ProcessId, ProcessStarted, ProcessName, DeviceId });
    internal Uri Address()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("语音接口须为完整 WSS 地址（本机可用 WS），不能包含密钥、查询参数或 #。");
        return uri;
    }
    internal string SecretTarget => Credentials.Prefix + "Speech/Gemini/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Address().AbsoluteUri.TrimEnd('/'))));
    internal void Validate()
    {
        Address(); _ = QuietTranslate.Hotkey.Parse(Hotkey);
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 160 || Model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) throw new ArgumentException("请填写有效的 Gemini Live Translate 模型名。");
        if (Authentication is < 0 or > 2 || !Languages.Contains(Target)) throw new ArgumentException("语音服务的鉴权或目标语言无效。");
        if (!double.IsFinite(FontSize) || FontSize is < 16 or > 40 || !double.IsFinite(Opacity) || Opacity is < 0 or > 1) throw new ArgumentException("字幕字号须为 16–40，背景不透明度须为 0%–100%。");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 160 || FontFamily.Any(c => char.IsControl(c) || "\\/:#@".Contains(c)) || FontWeight is not (300 or 400 or 500 or 600 or 700)) throw new ArgumentException("请填写本机字体名称，字重请选择纤细、常规、中等、半粗或粗体。");
        if (!double.IsFinite(LineLifetime) || LineLifetime is < 2 or > 15) throw new ArgumentException("每行保留时间须为 2–15 秒。");
        if (!double.IsFinite(WindowWidth) || WindowWidth is < 420 or > 10000 || !double.IsFinite(WindowHeight) || WindowHeight is < 100 or > 5000 || (WindowLeft.HasValue && !double.IsFinite(WindowLeft.Value)) || (WindowTop.HasValue && !double.IsFinite(WindowTop.Value))) throw new ArgumentException("字幕窗口位置或大小无效。");
        if (Settings.NormalizeTextColor(Background).Length == 0 || Settings.NormalizeTextColor(Foreground).Length == 0) throw new ArgumentException("字幕颜色不能为空。");
    }
    internal static string LanguageName(string code) => code switch { "zh-Hans" => "简体中文", "zh-Hant" => "繁體中文", "en" => "英语", "ja" => "日语", "ko" => "韩语", "pt-BR" => "葡萄牙语（巴西）", "ar" => "阿拉伯语", "hi" => "印地语", "th" => "泰语", "vi" => "越南语", "id" => "印度尼西亚语", "tr" => "土耳其语", "pl" => "波兰语", "uk" => "乌克兰语", "nl" => "荷兰语", _ => ApiSupport.LanguageName(code) };
}
