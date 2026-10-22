using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using LoyaLauncher.App.Services;
using LoyaLauncher.Core;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App;

class Program
{
    // 初始化代码：AppMain 之前不要调用任何依赖 Avalonia 的 API
    [STAThread]
    public static void Main(string[] args)
    {
        // ===== 阶段日志：整条启动路径的第一行 =====
        StartupLog.Begin();
        StartupLog.Stage("entry");

        // ===== P0-2：全局未处理异常兜底 =====
        // 无论在哪个线程、哪个阶段炸掉，都先留下完整堆栈再决定怎么死。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            StartupLog.Error("unhandled-exception", e.ExceptionObject as Exception);
            CrashReport.Save(e.ExceptionObject as Exception ?? new Exception("unknown unhandled object"));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 后台任务异常没人 await 时默认静默吞掉，这里显式记录并标记已观察，
            // 否则 .NET 可能直接终结进程
            StartupLog.Error("unobserved-task-exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            // 启动早期应用上次下载的更新（此时旧进程已退出，文件无占用）。
            // 若发生了替换则直接重启，加载新版本。
            StartupLog.Stage("pending-update:checking");
            if (UpdateService.ApplyPendingUpdate())
            {
                StartupLog.Stage("pending-update:applied, restarting");
                var exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                    StartupLog.Stage("pending-update:restarted");
                    return;
                }
                StartupLog.Error("pending-update: ProcessPath is null, continuing in-place", null);
            }
            else
            {
                StartupLog.Stage("pending-update:none");
            }

            StartupLog.Stage("avalonia:starting");
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            StartupLog.Stage("avalonia:exited-clean");
        }
        catch (Exception ex)
        {
            // ===== P0-3：标记致命阶段 + P0-7：可见错误反馈 =====
            StartupLog.Stage("fatal");
            StartupLog.Error("main", ex);
            CrashReport.Save(ex);
            ShowCrashDialog(ex);
        }
    }

    /// <summary>
    /// 启动崩溃兜底：写日志文件 + 弹原生错误框。
    /// 没有这层保护时，启动早期异常会导致进程静默退出（双击"没反应"），
    /// 用户无从得知原因。错误框使用平台原生通道，不依赖 Avalonia——
    /// Avalonia 本身初始化失败时这是唯一还能显示的路径。
    /// Windows 用 MessageBoxW，macOS 用 osascript，Linux 依次尝试 zenity / xmessage。
    /// </summary>
    private static void ShowCrashDialog(Exception ex)
    {
        try
        {
            var logPath = StartupLog.LogFilePath
                ?? Path.Combine(LoyaPaths.Logs, "startup-*.log");
            var detail =
                $"LoyaLauncher 启动失败：\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"详细信息已写入：\n{logPath}\n\n" +
                "如果刚下载，请确认：\n" +
                "1. 已完整下载（single.exe 约 48MB）\n" +
                "2. 未被安全软件隔离\n" +
                "3. 系统版本满足要求（Windows 10+ / macOS 12+ / 常见 Linux 桌面）";

            if (OperatingSystem.IsWindows())
            {
                MessageBoxW(IntPtr.Zero, detail, "LoyaLauncher 启动失败", 0x10 /* MB_ICONERROR */);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                // AppleScript 弹原生对话框；双引号需转义
                var escaped = detail.Replace("\"", "\\\"");
                var script = $"display dialog \"{escaped}\" with title \"LoyaLauncher 启动失败\" buttons {"OK"} default button {"OK"} with icon stop";
                TryRun("osascript", "-e", script);
                return;
            }

            // Linux：优先 zenity，退化 xmessage；都没有时至少 stderr 有输出
            Console.Error.WriteLine(detail);
            TryRun("zenity", "--error", "--title=LoyaLauncher 启动失败", $"--text={detail}");
        }
        catch { /* 弹框失败也不能二次崩溃 */ }
    }

    /// <summary>尽力执行一个外部命令；不存在或失败时静默跳过</summary>
    private static void TryRun(string file, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(30_000);
        }
        catch { /* 命令不存在或执行失败时忽略 */ }
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
            // 附上启动阶段日志副本：两条记录并排放，定位阶段一目了然
            var startup = StartupLog.FallbackContent;
            if (!string.IsNullOrEmpty(startup))
                sb.AppendLine("---- 启动阶段日志（内存副本，文件写入失败时）----").AppendLine(startup);
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* 写日志失败时至少不阻塞弹框 */ }
    }
}
