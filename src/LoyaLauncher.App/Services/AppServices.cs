using LoyaLauncher.Auth;
using LoyaLauncher.Bedrock;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Java;
using LoyaLauncher.Mods;
using LoyaLauncher.Pack;
using LoyaLauncher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LoyaLauncher.App.Services;

/// <summary>
/// DI 容器组装。集中创建所有服务单例，ViewModel 通过构造函数注入获取。
/// </summary>
public static class AppServices
{
    public static IServiceProvider Provider { get; } = Configure();

    private static IServiceProvider Configure()
    {
        LoyaPaths.EnsureDirectories();

        var config = ConfigStore.Instance.Config;
        var services = new ServiceCollection();

        // ---------- 基础设施 ----------
        // ConfigStore 是进程级单例（读 .loya/config）：注册实例本身，
        // 各 ViewModel 通过构造注入获取同一份配置
        services.AddSingleton(ConfigStore.Instance);
        services.AddSingleton(EventBus.Instance);
        services.AddSingleton(new DownloadManager(EventBus.Instance, config.MaxConcurrentDownloads));

        // JavaInstaller / JavaEditionService 依赖镜像配置，传 ConfigStore 引用：
        // 安装时实时读取，设置里切换镜像源即时生效，无需重启
        services.AddSingleton(sp => new JavaInstaller(
            sp.GetRequiredService<DownloadManager>(),
            sp.GetRequiredService<Core.EventBus>(),
            sp.GetRequiredService<ConfigStore>()));
        services.AddSingleton(sp => new JavaEditionService(
            sp.GetRequiredService<JavaInstaller>(),
            sp.GetRequiredService<JreManager>(),
            sp.GetRequiredService<DownloadManager>(),
            sp.GetRequiredService<Core.EventBus>()));
        services.AddSingleton<JreManager>();
        services.AddSingleton(sp => new LaunchPreflight(
            sp.GetRequiredService<JavaInstaller>(),
            sp.GetRequiredService<JreManager>()));
        services.AddSingleton<AccountManager>();
        services.AddSingleton<MicrosoftAuthService>();
        services.AddSingleton<ModrinthClient>();
        services.AddSingleton(sp => new LoaderInstaller(
            sp.GetRequiredService<DownloadManager>(),
            sp.GetRequiredService<ConfigStore>()));
        services.AddSingleton<BedrockService>();

        // ---------- 整合包与自动更新 ----------
        services.AddSingleton<ModpackService>();
        services.AddSingleton<Pack.UpdateService>();

        // ---------- ViewModels ----------
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<VersionsViewModel>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<ModpacksViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp => new DownloadCenterViewModel(
            sp.GetRequiredService<JavaEditionService>(),
            sp.GetRequiredService<ModrinthClient>(),
            sp.GetRequiredService<Core.EventBus>(),
            sp.GetRequiredService<ConfigStore>()));
        services.AddSingleton<JavaManagerViewModel>();

        return services.BuildServiceProvider();
    }
}
