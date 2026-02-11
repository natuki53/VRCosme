using System.Globalization;
using System.Threading;
using System.Windows;

namespace VRCosme.Services;

public static class LocalizationService
{
    private const string DictionaryPrefix = "pack://application:,,,/VRCosme;component/Localization/Strings.";
    private const string DictionarySuffix = ".xaml";

    public static string CurrentLanguageCode { get; private set; } = ThemeService.DefaultLanguageCode;

    public static void Initialize()
    {
        SetLanguage(ThemeService.GetLanguage(), persist: false);
    }

    public static void SetLanguage(string languageCode, bool persist = true)
    {
        var normalizedLanguageCode = NormalizeLanguageCode(languageCode);

        if (persist)
            ThemeService.SaveLanguage(normalizedLanguageCode);

        CurrentLanguageCode = normalizedLanguageCode;

        var culture = new CultureInfo(normalizedLanguageCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        ApplyLanguageDictionary(normalizedLanguageCode);
    }

    public static string GetString(string key, string fallback = "")
    {
        if (Application.Current?.TryFindResource(key) is string value && !string.IsNullOrWhiteSpace(value))
            return value;

        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    public static string Format(string key, string fallbackFormat, params object[] args)
    {
        var format = GetString(key, fallbackFormat);
        return string.Format(CultureInfo.CurrentCulture, format, args);
    }

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return ThemeService.DefaultLanguageCode;

        var normalized = languageCode.Trim();
        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return ThemeService.EnglishLanguageCode;
        if (normalized.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return ThemeService.KoreanLanguageCode;
        if (normalized.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MY", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
            return ThemeService.ChineseSimplifiedLanguageCode;
        if (normalized.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase))
            return ThemeService.ChineseTraditionalLanguageCode;
        if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return ThemeService.JapaneseLanguageCode;
        return ThemeService.DefaultLanguageCode;
    }

    private static void ApplyLanguageDictionary(string languageCode)
    {
        if (Application.Current == null)
            return;

        var dictionariesToRemove = new List<ResourceDictionary>();
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            var source = dict.Source?.ToString();
            if (source != null && source.Contains("/Localization/Strings.", StringComparison.Ordinal))
                dictionariesToRemove.Add(dict);
        }

        foreach (var dict in dictionariesToRemove)
            Application.Current.Resources.MergedDictionaries.Remove(dict);

        var uri = new Uri($"{DictionaryPrefix}{languageCode}{DictionarySuffix}", UriKind.Absolute);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }
}
