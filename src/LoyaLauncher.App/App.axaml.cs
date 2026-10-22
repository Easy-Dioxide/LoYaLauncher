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
        // ===== P0-3：启动阶段标记 =====
        StartupLog.Stage("app:framework-init");

        // 初始化数据目录与语言资源
        Core.LoyaPaths.EnsureDirectories();
        LangService.ApplyLanguage(ConfigStore.Instance.Config.Language);
        StartupLog.Stage("app:lang-applied");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupLog.Stage("app:building-main-window");
            var splash = new SplashWindow();
            StartupLog.Stage("app:splash-created");

            var main = new MainWindow
            {
                DataContext = AppServices.Provider.GetRequiredService<MainWindowViewModel>(),
            };
            StartupLog.Stage("app:main-window-created");

            main.Opened += (_, _) =>
            {
                StartupLog.Stage("app:main-window-opened");
                _ = splash.CloseWithFadeAsync();
            };
            desktop.MainWindow = main;

            // 启动画面在 lifetime Startup（窗口系统就绪、主窗口显示前）才 Show。
            // 在 OnFrameworkInitializationCompleted 里直接 Show 在部分平台时序不安全，
            // 是"双击后连窗口都不显示"回归的疑点之一。
            desktop.Startup += (_, _) =>
            {
                StartupLog.Stage("app:lifetime-startup");
                splash.Show();
                StartupLog.Stage("app:splash-shown");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
