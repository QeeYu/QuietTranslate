using System;
using System.IO;
using System.Text.Json;

namespace QuietTranslate;

internal static class SettingsStore
{
    private static string DirectoryPath => Settings.DirectoryPath;
    private static string FilePath => Settings.FilePath;

    internal static Settings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            string json = File.ReadAllText(FilePath);
            var value = JsonSerializer.Deserialize<Settings>(json) ?? new();
            if (!Enum.IsDefined(value.Provider) || value.Services == null) throw new FormatException();
            foreach (var pair in new Settings().Services) value.Services.TryAdd(pair.Key, pair.Value);
            value.OcrServices ??= Settings.OcrDefaults();
            foreach (var pair in Settings.OcrDefaults()) value.OcrServices.TryAdd(pair.Key, pair.Value);
            if (!Enum.IsDefined(value.OcrProvider)) value.OcrProvider = OcrProvider.OCRSpace;
            if (!Enum.IsDefined(value.Theme)) value.Theme = ThemeMode.System;
            value.AppRules ??= new();
            value.Speech ??= new();
            using (var saved = JsonDocument.Parse(json))
                if (saved.RootElement.TryGetProperty("Speech", out var oldSpeech) && oldSpeech.ValueKind == JsonValueKind.Object && !oldSpeech.TryGetProperty("SubtitleLayoutVersion", out _))
                { if (!oldSpeech.TryGetProperty("ControlsHidden", out _)) value.Speech.ControlsHidden = false; value.Speech.Bilingual = false; }
            if (value.Speech.SubtitleLayoutVersion < 4)
            {
                if (value.Speech.WindowHeight == 170) value.Speech.WindowHeight = value.Speech.ControlsHidden ? 120 : 144;
                value.Speech.SubtitleLayoutVersion = 4;
            }
            value.SavedServices ??= new();
            value.CopyWaitMs = Math.Clamp(value.CopyWaitMs, 300, 2500);
            value.BubbleOpacity = double.IsFinite(value.BubbleOpacity) ? Math.Clamp(value.BubbleOpacity, 0, 1) : .92;
            value.BubbleFontSize = double.IsFinite(value.BubbleFontSize) ? Math.Clamp(value.BubbleFontSize, 12, 22) : 16;
            value.WheelScrollSpeed = double.IsFinite(value.WheelScrollSpeed) ? Math.Clamp(value.WheelScrollSpeed, .25, 1.5) : .65;
            value.BubbleTextColor = Settings.NormalizeTextColor(value.BubbleTextColor);
            value.AutoTranslateDelay = Math.Clamp(value.AutoTranslateDelay, 300, 2000);
            value.ButtonSize = double.IsFinite(value.ButtonSize) ? Math.Clamp(value.ButtonSize, 28, 56) : 36;
            return value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        { throw new InvalidOperationException("配置文件损坏，请重命名 %LOCALAPPDATA%\\QuietTranslate\\settings.json 后重试。", ex); }
    }

    internal static void Save(Settings value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
