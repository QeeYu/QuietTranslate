using System;
using System.Collections.Generic;
using System.IO;

namespace QuietTranslate;

public sealed class Settings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public Provider Provider { get; set; }
    public Dictionary<Provider, ServiceSettings> Services { get; set; } = Defaults();
    public OcrProvider OcrProvider { get; set; } = OcrProvider.OCRSpace;
    public string OcrLanguage { get; set; } = "auto";
    public Dictionary<OcrProvider, ServiceSettings> OcrServices { get; set; } = OcrDefaults();
    public Dictionary<string, ServiceProfile> SavedServices { get; set; } = new();
    public string PauseHotkey { get; set; } = "Pause";
    public string ManualHotkey { get; set; } = "Alt+T";
    public bool PauseManual { get; set; }
    public string ManualSource { get; set; } = "auto";
    public string ManualTarget { get; set; } = "zh-Hans";
    public Provider? ManualProvider { get; set; }
    public int AutoTranslateDelay { get; set; } = 800;
    public bool ManualTopmost { get; set; }
    public List<AppRule> AppRules { get; set; } = new();
    public string BubbleBackgroundColor { get; set; } = "";
    public bool BubbleSmartColors { get; set; } = true;
    public string BubbleBorderColor { get; set; } = "";
    public double BubbleBorderOpacity { get; set; } = -1;
    public bool BubbleBorderVisible { get; set; } = true;
    public string ButtonBackground { get; set; } = "#247A85";
    public string ButtonForeground { get; set; } = "#FFFFFF";
    public string ButtonBorderColor { get; set; } = "#247A85";
    public double ButtonOpacity { get; set; } = .94;
    public double ButtonSize { get; set; } = 36;
    public double ButtonRadius { get; set; } = 10;
    public bool ButtonBorderVisible { get; set; }
    public bool ButtonShadow { get; set; }
    public string ButtonLabel { get; set; } = "译";
    public int ButtonPosition { get; set; } = 2;
    public int ButtonOffsetX { get; set; } = 12;
    public int ButtonOffsetY { get; set; }
    public int CompatibilityTimeout { get; set; } = 12;
    public string SelectionHotkey { get; set; } = "Ctrl+Alt+Q";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+W";
    public int CopyWaitMs { get; set; } = 900;
    public double BubbleOpacity { get; set; } = 0.92;
    public double BubbleFontSize { get; set; } = 16;
    public double WheelScrollSpeed { get; set; } = .65;
    public string BubbleTextColor { get; set; } = "";
    public bool SelectionButtonEnabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public ServiceSettings Current => Services[Provider];
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietTranslate");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static Settings Load() => SettingsStore.Load();
    public void Save() => SettingsStore.Save(this);

    internal static string NormalizeTextColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string hex = value.Trim();
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out _))
            throw new ArgumentException("文字颜色请输入 #RRGGBB，例如 #E8F5F7；留空则跟随主题。");
        return hex.ToUpperInvariant();
    }

    public static Uri CompletionEndpoint(Provider provider, string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("API 地址须为 HTTPS（本机 localhost 可用 HTTP），不能包含密钥、查询参数或 #。填写基础地址即可。");
        string path = uri.AbsoluteUri.TrimEnd('/');
        string suffix = IsAI(provider) ? "/chat/completions" : provider switch { Provider.Microsoft => "/translate", Provider.DeepL => "/v2/translate", _ => "" };
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path += suffix;
        if (provider == Provider.Microsoft) path += "?api-version=3.0&to=zh-Hans";
        return new Uri(path);
    }
    public static bool IsAI(Provider p) => p is Provider.OpenAI or Provider.Qwen or Provider.Doubao or Provider.GLM or Provider.Kimi or Provider.SiliconFlow or Provider.CustomAI;
    public static string Name(Provider p) => p switch { Provider.OpenAI => "DeepSeek", Provider.Microsoft => "Microsoft Translator", Provider.DeepL => "DeepL", Provider.Google => "Google Cloud 翻译", Provider.Baidu => "百度翻译", Provider.Tencent => "腾讯云翻译", Provider.Youdao => "有道翻译", Provider.LibreTranslate => "LibreTranslate", Provider.Qwen => "通义千问 / 百炼", Provider.Doubao => "豆包 / 火山方舟", Provider.GLM => "智谱 GLM", Provider.Kimi => "Kimi", Provider.SiliconFlow => "硅基流动", Provider.CustomAI => "自定义 AI（兼容接口）", _ => "自定义普通翻译" };
    public static string OcrName(OcrProvider p) => p switch { OcrProvider.Windows => "Windows 本地 OCR", OcrProvider.OCRSpace => "OCR.space（有免费套餐）", OcrProvider.Tencent => "腾讯云 OCR", OcrProvider.Baidu => "百度 OCR", OcrProvider.GoogleVision => "Google Cloud Vision", OcrProvider.VisionAI => "自定义视觉 AI OCR", OcrProvider.Local => "本机 OCR 服务（独立接口）", _ => "自定义 OCR 接口" };
    public static Dictionary<Provider, ServiceSettings> Defaults()
    {
        var result = new Dictionary<Provider, ServiceSettings>();
        foreach (Provider p in Enum.GetValues<Provider>()) result[p] = new ServiceSettings { DisableThinking = p == Provider.OpenAI, Endpoint = p switch {
            Provider.OpenAI => "https://api.deepseek.com", Provider.Microsoft => "https://api.cognitive.microsofttranslator.com", Provider.DeepL => "https://api-free.deepl.com",
            Provider.Google => "https://translation.googleapis.com/language/translate/v2", Provider.Baidu => "https://fanyi-api.baidu.com/api/trans/vip/translate",
            Provider.Tencent => "https://tmt.tencentcloudapi.com", Provider.Youdao => "https://openapi.youdao.com/api", Provider.LibreTranslate => "http://localhost:5000/translate",
            Provider.Qwen => "https://dashscope.aliyuncs.com/compatible-mode/v1", Provider.Doubao => "https://ark.cn-beijing.volces.com/api/v3", Provider.GLM => "https://open.bigmodel.cn/api/paas/v4",
            Provider.Kimi => "https://api.moonshot.cn/v1", Provider.SiliconFlow => "https://api.siliconflow.cn/v1", _ => "http://localhost:8000/v1" }, Model = p == Provider.OpenAI ? "deepseek-flash" : "" };
        result[Provider.Tencent].Region = "ap-guangzhou";
        return result;
    }
    public static Dictionary<OcrProvider, ServiceSettings> OcrDefaults()
    {
        var result = new Dictionary<OcrProvider, ServiceSettings>();
        foreach (OcrProvider p in Enum.GetValues<OcrProvider>()) result[p] = new ServiceSettings { DisableThinking = false, Model = "", Endpoint = p switch {
            OcrProvider.OCRSpace => "https://api.ocr.space/parse/image", OcrProvider.Tencent => "https://ocr.tencentcloudapi.com",
            OcrProvider.Baidu => "https://aip.baidubce.com/rest/2.0/ocr/v1/accurate_basic", OcrProvider.GoogleVision => "https://vision.googleapis.com/v1/images:annotate", OcrProvider.Local => "http://127.0.0.1:17321/ocr", _ => "http://localhost:8000/v1" },
            AllowAnonymous = p == OcrProvider.Local, Region = p == OcrProvider.Tencent ? "ap-guangzhou" : "", Prompt = ServiceSettings.DefaultOcrPrompt, BodyTemplate = "{\"image\":\"{image_base64}\",\"language\":\"auto\"}", ResponsePath = "text" };
        return result;
    }
}

// Windows Credential Manager encrypts secrets for the current Windows user.
