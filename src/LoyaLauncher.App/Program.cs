using System.Diagnostics;
using Avalonia;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App;

class Program
{
    // 初始化代码：AppMain 之前不要调用任何依赖 Avalonia 的 API
    [STAThread]
    public static void Main(string[] args)
    {
        // 启动早期应用上次下载的更新（此时旧进程已退出，文件无占用）。
        // 若发生了替换则直接重启，加载新版本。
        if (UpdateService.ApplyPendingUpdate())
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                return;
            }
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia 配置：设计器同样使用此方法，请勿删除
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
