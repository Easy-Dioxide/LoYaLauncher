using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using LoyaLauncher.Core;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App;

class Program
{
    // 初始化代码：AppMain 之前不要调用任何依赖 Avalonia 的 API
    [STAThread]
    public static void Main(string[] args)
    {
        try
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
        catch (Exception ex)
        {
            CrashReport.Save(ex);
            ShowCrashDialog(ex);
        }
    }

    /// <summary>
    /// 启动崩溃兜底：写日志文件 + 弹原生错误框。
    /// 没有这层保护时，启动早期异常会导致进程静默退出（双击"没反应"），
    /// 用户无从得知原因。错误框使用 Win32 API，不依赖 Avalonia——
    /// Avalonia 本身初始化失败时这是唯一还能显示的通道。
    /// </summary>
    private static void ShowCrashDialog(Exception ex)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var message =
                $"LoyaLauncher 启动失败：\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"详细信息已写入：\n{Path.Combine(LoyaPaths.Logs, "crash-*.log")}\n\n" +
                "如果刚下载，请确认：\n" +
                "1. 已完整下载（single.exe 约 48MB）\n" +
                "2. 未被安全软件隔离\n" +
                "3. 系统为 Windows 10 或更高版本";
            MessageBoxW(IntPtr.Zero, message, "LoyaLauncher 启动失败", 0x10 /* MB_ICONERROR */);
        }
        catch { /* 弹框失败也不能二次崩溃 */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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

/// <summary>启动崩溃报告：写入 %APPDATA%\.loya\logs\crash-时间.log</summary>
internal static class CrashReport
{
    public static void Save(Exception ex)
    {
        try
        {
            LoyaPaths.EnsureDirectories();
            var path = Path.Combine(LoyaPaths.Logs, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"LoyaLauncher 启动崩溃 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"64 位进程: {Environment.Is64BitProcess}");
            sb.AppendLine($"可执行文件: {Environment.ProcessPath}");
            sb.AppendLine($"命令行: {string.Join(' ', Environment.GetCommandLineArgs())}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* 写日志失败时至少不阻塞弹框 */ }
    }
}
