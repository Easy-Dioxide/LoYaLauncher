using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LoyaLauncher.App.ViewModels;

namespace LoyaLauncher.App.Converters;

/// <summary>
/// 探测状态 → 画刷：可达=Success / 不可达=Error / 测试中=Accent / 待测试=Muted。
/// 资源缺失时返回 null（由 XAML 侧默认值兜底），绝不抛异常。
/// </summary>
public sealed class DiagStateBrushConverter : IValueConverter
{
    public static readonly DiagStateBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DiagState.Ok => "SuccessBrush",
            DiagState.Failed => "ErrorBrush",
            DiagState.Testing => "AccentBrush",
            _ => "TextMutedBrush",
        };
        if (Application.Current?.TryFindResource(key, out var res) == true && res is IBrush brush)
            return brush;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
