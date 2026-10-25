using Avalonia.Controls;
using Avalonia.Interactivity;
using LoyaLauncher.App.ViewModels;

namespace LoyaLauncher.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>跳转到网络诊断页（经主窗口 VM 导航，保持页面过渡动画）</summary>
    private void OnOpenDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window &&
            window.DataContext is MainWindowViewModel mainVm)
        {
            mainVm.Navigate(LauncherPage.Diagnostics);
        }
    }
}
