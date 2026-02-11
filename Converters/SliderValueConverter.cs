using System.Globalization;
using System.Windows.Data;

namespace VRCosme.Converters;

/// <summary>
/// スライダー値の double ⇔ 文字列変換。ConverterParameter は "最小値,最大値,書式"（例: "-100,100,F0"）。
/// インスタンスごとに最後の有効値を保持し、不正入力時は変更前に戻す。
/// </summary>
public class SliderValueConverter : IValueConverter
{
    private double _lastValidValue;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double d) return "0";
        _lastValidValue = d;
        var (_, _, format) = ParseParameter(parameter);
        return d.ToString(format, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return _lastValidValue;
        var (min, max, _) = ParseParameter(parameter);
        // 全角数字を半角に変換
        s = ConvertFullWidthToHalfWidth(s.Trim());
        if (!double.TryParse(s, NumberStyles.Any, culture, out var v)) return _lastValidValue;
        var clamped = Math.Clamp(v, min, max);
        _lastValidValue = clamped;
        return clamped;
    }

    /// <summary>全角数字・記号を半角に変換</summary>
    private static string ConvertFullWidthToHalfWidth(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return input
            .Replace('０', '0').Replace('１', '1').Replace('２', '2').Replace('３', '3').Replace('４', '4')
            .Replace('５', '5').Replace('６', '6').Replace('７', '7').Replace('８', '8').Replace('９', '9')
            .Replace('－', '-').Replace('＋', '+').Replace('．', '.'); // マイナス、プラス、小数点も変換
    }

    private static (double min, double max, string format) ParseParameter(object? parameter)
    {
        const double defaultMin = 0, defaultMax = 100;
        const string defaultFormat = "F0";
        var s = parameter?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return (defaultMin, defaultMax, defaultFormat);
        var parts = s.Split(',');
        if (parts.Length < 3) return (defaultMin, defaultMax, defaultFormat);
        double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var min);
        double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var max);
        var format = parts[2].Trim();
        if (string.IsNullOrEmpty(format)) format = defaultFormat;
        return (min, max, format);
    }
}
