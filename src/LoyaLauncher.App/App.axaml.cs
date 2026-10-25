using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LoyaLauncher.App.Services;
using LoyaLauncher.App.ViewModels;
using LoyaLauncher.App.Views;
using LoyaLauncher.Core;
using Microsoft.Extensions.DependencyInjection;
// 命名空间 LoyaLauncher.App.Motion 与类名 Motion 同名，直接用会解析为命名空间，须起别名
using LoyaMotion = LoyaLauncher.App.Motion.Motion;

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
        // 减少动效开关（清单 §9 P1）：全局生效，设置页切换即时更新
        LoyaMotion.Enabled = !ConfigStore.Instance.Config.ReduceMotion;
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
                // ===== 启动成功确认（清单 §8）=====
                // 主窗口已显示：若上次启动应用了自动更新，说明新版本运行正常，
                // 清理 applied 标记与备份；若从未走到这里，下次启动会触发回滚。
                Pack.UpdateService.ConfirmStartup();
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
