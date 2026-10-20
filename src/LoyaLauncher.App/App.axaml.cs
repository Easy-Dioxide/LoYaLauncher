using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LoyaLauncher.App.Services;
using LoyaLauncher.App.ViewModels;
using LoyaLauncher.App.Views;
using LoyaLauncher.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LoyaLauncher.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 初始化数据目录与语言资源
        Core.LoyaPaths.EnsureDirectories();
        LangService.ApplyLanguage(ConfigStore.Instance.Config.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = AppServices.Provider.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
