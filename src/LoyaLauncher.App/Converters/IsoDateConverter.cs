using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace LoyaLauncher.App.Converters;

/// <summary>
/// ISO 8601 时间字符串 → "yyyy-MM-dd"。解析失败时原样返回（不丢信息）。
/// </summary>
public sealed class IsoDateConverter : IValueConverter
{
    public static readonly IsoDateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s) &&
            DateTime.TryParse(s, culture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd", culture);
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
