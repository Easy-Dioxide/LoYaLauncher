using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Media;
using LoyaLauncher.Auth;
using LoyaLauncher.App.ViewModels;

namespace LoyaLauncher.App.Views;

public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // DataContext 可能在订阅前已被设置（设计期/热重载），补一次挂接
        HookClipboard(DataContext as AccountsViewModel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
        => HookClipboard(DataContext as AccountsViewModel);

    /// <summary>把剪贴板写入能力挂给 VM（VM 不直接依赖 UI 层）</summary>
    private void HookClipboard(AccountsViewModel? vm)
    {
        if (vm is null) return;
        vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        };
    }
}

/// <summary>账户类型 → 徽标底色：微软正版用成功色淡染，离线/外置用中性白</summary>
public sealed class AccountKindToBadgeBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AccountKind.Microsoft
            ? new SolidColorBrush(Color.Parse("#1F4ADE80"))
            : new SolidColorBrush(Color.Parse("#14FFFFFF"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串非空 → true（错误提示可见性）</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
