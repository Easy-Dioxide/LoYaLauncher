using System.Runtime.InteropServices;
using System.Text;
using LoyaLauncher.Core;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App.Services;

/// <summary>
/// 启动阶段日志（P0 可观测性）。
/// <para>
/// 设计约束：它在 Avalonia、DI、配置体系就绪<em>之前</em>就要能写——
/// 因此只依赖文件 IO，不引用任何框架服务。写入
/// <c>%APPDATA%\.loya\logs\startup-{yyyyMMdd-HHmmss}.log</c>。
/// </para>
/// <para>
/// 所有写操作静默兜底：日志系统本身永远不许让进程崩溃；
/// 文件写失败时退化为内存副本（限长），至少保证异常信息不丢。
/// </para>
/// </summary>
internal static class StartupLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _failed;
    private static readonly StringBuilder Fallback = new();

    /// <summary>本次启动日志文件路径（写失败时为 null）</summary>
    public static string? LogFilePath => _path;

    /// <summary>内存副本内容（仅当文件写入失败时非空），供上层兜底转储</summary>
    public static string FallbackContent
    {
        get { lock (Gate) return Fallback.ToString(); }
    }

    /// <summary>进程入口最早调用：建目录、写环境信息头</summary>
    public static void Begin()
    {
        try
        {
            LoyaPaths.EnsureDirectories();
            _path = Path.Combine(LoyaPaths.Logs, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var header = new StringBuilder()
                .AppendLine($"=== LoyaLauncher startup {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===")
                .AppendLine($"version : {UpdateService.CurrentVersion}")
                .AppendLine($"os      : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
                .AppendLine($"runtime : {RuntimeInformation.FrameworkDescription}")
                .AppendLine($"process : {Environment.ProcessPath}")
                .AppendLine($"args    : {string.Join(' ', Environment.GetCommandLineArgs())}")
                .AppendLine($"64-bit  : {Environment.Is64BitProcess}")
                .AppendLine();
            File.WriteAllText(_path, header.ToString());
        }
        catch (Exception ex)
        {
            _failed = true;
            lock (Gate) Fallback.AppendLine("[StartupLog] Begin failed: " + ex);
        }
    }

    /// <summary>标记一个启动阶段（entry → pending-update → avalonia → … → exited-clean）</summary>
    public static void Stage(string stage) => Write("STAGE", stage);

    /// <summary>补充说明信息</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>记录异常（完整 ToString 含堆栈）</summary>
    public static void Error(string context, Exception? ex)
        => Write("ERROR", ex is null ? context : $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}";
        lock (Gate)
        {
            if (!_failed && _path is not null)
            {
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                    return;
                }
                catch
                {
                    _failed = true; // 磁盘满/权限问题时停止重试，避免拖慢启动
                }
            }
            if (Fallback.Length < 100_000)
                Fallback.AppendLine(line);
        }
    }
}
