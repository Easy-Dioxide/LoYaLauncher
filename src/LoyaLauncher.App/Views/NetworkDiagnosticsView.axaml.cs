using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using LoyaLauncher.App.Services;
using LoyaLauncher.App.ViewModels;

namespace LoyaLauncher.App.Views;

public partial class NetworkDiagnosticsView : UserControl
{
    public NetworkDiagnosticsView() => InitializeComponent();

    private NetworkDiagnosticsViewModel? Vm => DataContext as NetworkDiagnosticsViewModel;

    /// <summary>复制脱敏诊断报告到剪贴板</summary>
    private async void OnCopyReport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null) return;
            await top.Clipboard.SetTextAsync(Vm.GenerateReport());
            Vm.StatusText = LangService.T("Diagnostics.Copied", "诊断报告已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Vm.StatusText = ex.Message;
        }
    }

    /// <summary>打开日志目录</summary>
    private void OnOpenLogDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null) return;
        try
        {
            LoyaLauncher.Core.LoyaPaths.EnsureDirectories();
            var dir = Vm.LogDirectory;
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", dir);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", dir);
            else
                System.Diagnostics.Process.Start("xdg-open", dir);
        }
        catch (Exception ex)
        {
            Vm.StatusText = ex.Message;
        }
    }
}
