using System.IO;
using System.Text.Json;
using System.Windows;
using VRCosme.Models;

namespace VRCosme.Services;

/// <summary>テーマ設定の管理</summary>
public static class ThemeService
{
    public const string JapaneseLanguageCode = "ja-JP";
    public const string EnglishLanguageCode = "en-US";
    public const string KoreanLanguageCode = "ko-KR";
    public const string ChineseSimplifiedLanguageCode = "zh-CN";
    public const string ChineseTraditionalLanguageCode = "zh-TW";
    public const string DefaultLanguageCode = JapaneseLanguageCode;

    private const string SettingsFileName = "settings.json";
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCosme");
    private static readonly string SettingsPath = Path.Combine(DataDir, SettingsFileName);

    /// <summary>現在のテーマモードを取得</summary>
    public static AppThemeMode GetThemeMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return AppThemeMode.System;
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(json);
            return settings?.ThemeMode ?? AppThemeMode.System;
        }
        catch
        {
            return AppThemeMode.System;
        }
    }

    /// <summary>テーマモードを保存</summary>
    public static void SaveThemeMode(AppThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.ThemeMode = mode;
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    /// <summary>右パネル幅を取得（未設定時は 350）</summary>
    public static double GetRightPanelWidth()
    {
        try
        {
            var settings = LoadSettings();
            var w = settings.RightPanelWidth;
            return w is > 0 ? w : 350;
        }
        catch
        {
            return 350;
        }
    }

    /// <summary>右パネル幅を保存</summary>
    public static void SaveRightPanelWidth(double width)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.RightPanelWidth = width;
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    /// <summary>三分割法グリッド線の表示を取得</summary>
    public static bool GetShowRuleOfThirdsGrid()
    {
        try
        {
            var settings = LoadSettings();
            return settings.ShowRuleOfThirdsGrid;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>三分割法グリッド線の表示を保存</summary>
    public static void SaveShowRuleOfThirdsGrid(bool value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.ShowRuleOfThirdsGrid = value;
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    /// <summary>ルーラーの表示を取得</summary>
    public static bool GetShowRuler()
    {
        try
        {
            var settings = LoadSettings();
            return settings.ShowRuler;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ルーラーの表示を保存</summary>
    public static void SaveShowRuler(bool value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.ShowRuler = value;
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    /// <summary>初期書き出しディレクトリを取得</summary>
    public static string GetDefaultExportDirectory()
    {
        try { return LoadSettings().DefaultExportDirectory; }
        catch { return ""; }
    }

    /// <summary>初期書き出しディレクトリを保存</summary>
    public static void SaveDefaultExportDirectory(string value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.DefaultExportDirectory = value;
            SaveSettings(settings);
        }
        catch { }
    }

    /// <summary>初期書き出し形式を取得</summary>
    public static string GetDefaultExportFormat()
    {
        try
        {
            var fmt = LoadSettings().DefaultExportFormat;
            return fmt is "PNG" or "JPEG" ? fmt : "PNG";
        }
        catch { return "PNG"; }
    }

    /// <summary>初期書き出し形式を保存</summary>
    public static void SaveDefaultExportFormat(string value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.DefaultExportFormat = value;
            SaveSettings(settings);
        }
        catch { }
    }

    /// <summary>初期JPEG品質を取得</summary>
    public static int GetDefaultJpegQuality()
    {
        try
        {
            var q = LoadSettings().DefaultJpegQuality;
            return q is >= 1 and <= 100 ? q : 90;
        }
        catch { return 90; }
    }

    /// <summary>初期JPEG品質を保存</summary>
    public static void SaveDefaultJpegQuality(int value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.DefaultJpegQuality = Math.Clamp(value, 1, 100);
            SaveSettings(settings);
        }
        catch { }
    }

    /// <summary>AI自動選択用ONNXモデルパスを取得</summary>
    public static string GetAutoMaskModelPath()
    {
        try { return LoadSettings().AutoMaskModelPath; }
        catch { return ""; }
    }

    /// <summary>AI自動選択用ONNXモデルパスを保存</summary>
    public static void SaveAutoMaskModelPath(string value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.AutoMaskModelPath = value;
            SaveSettings(settings);
        }
        catch { }
    }

    public static AutoMaskTargetKind GetAutoMaskTargetKind()
    {
        try
        {
            var value = LoadSettings().AutoMaskTargetKind;
            return Enum.TryParse<AutoMaskTargetKind>(value, ignoreCase: true, out var parsed)
                ? parsed
                : AutoMaskTargetKind.Human;
        }
        catch
        {
            return AutoMaskTargetKind.Human;
        }
    }

    public static void SaveAutoMaskTargetKind(AutoMaskTargetKind value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.AutoMaskTargetKind = value.ToString();
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    public static bool GetAutoMaskMultiPassEnabled()
    {
        try { return LoadSettings().AutoMaskMultiPassEnabled; }
        catch { return true; }
    }

    public static void SaveAutoMaskMultiPassEnabled(bool value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.AutoMaskMultiPassEnabled = value;
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    /// <summary>言語設定を取得</summary>
    public static string GetLanguage()
    {
        try
        {
            var language = LoadSettings().Language;
            return NormalizeLanguageCode(language);
        }
        catch
        {
            return DefaultLanguageCode;
        }
    }

    /// <summary>言語設定を保存</summary>
    public static void SaveLanguage(string languageCode)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var settings = LoadSettings();
            settings.Language = NormalizeLanguageCode(languageCode);
            SaveSettings(settings);
        }
        catch
        {
            // 保存失敗は無視
        }
    }

    private static Settings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new Settings();
        var json = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
    }

    private static void SaveSettings(Settings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(SettingsPath, json);
    }

    private static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return DefaultLanguageCode;

        var normalized = languageCode.Trim();
        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return EnglishLanguageCode;
        if (normalized.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return KoreanLanguageCode;
        if (normalized.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MY", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
            return ChineseSimplifiedLanguageCode;
        if (normalized.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase))
            return ChineseTraditionalLanguageCode;

        if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return JapaneseLanguageCode;

        return DefaultLanguageCode;
    }

    /// <summary>システムのテーマがダークかどうかを判定</summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            // Windows 10/11のレジストリからテーマ設定を取得
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            // 取得失敗時はダークテーマとみなす
            return true;
        }
    }

    /// <summary>実際に適用すべきテーマ（Systemの場合はシステム設定を参照）</summary>
    public static AppThemeMode GetEffectiveTheme()
    {
        var mode = GetThemeMode();
        if (mode == AppThemeMode.System)
            return IsSystemDarkTheme() ? AppThemeMode.Dark : AppThemeMode.Light;
        return mode;
    }

    private class Settings
    {
        public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
        public string Language { get; set; } = DefaultLanguageCode;
        public double RightPanelWidth { get; set; }
        public bool ShowRuleOfThirdsGrid { get; set; }
        public bool ShowRuler { get; set; }
        public string DefaultExportDirectory { get; set; } = "";
        public string DefaultExportFormat { get; set; } = "PNG";
        public int DefaultJpegQuality { get; set; } = 90;
        public string AutoMaskModelPath { get; set; } = "";
        public string AutoMaskTargetKind { get; set; } = nameof(VRCosme.Models.AutoMaskTargetKind.Human);
        public bool AutoMaskMultiPassEnabled { get; set; } = true;
    }
}
