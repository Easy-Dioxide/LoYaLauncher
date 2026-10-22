using System.Diagnostics;
using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>游戏日志行事件</summary>
public sealed record GameLogEvent(string VersionId, string Line, bool IsError);

/// <summary>游戏进程退出事件（终态保证：正常退出、取消、异常一定会发布）</summary>
public sealed record GameExitedEvent(string VersionId, int ExitCode);

/// <summary>
/// 游戏进程运行器：启动 java 进程，异步捕获 stdout/stderr，
/// 经 EventBus 广播日志与退出事件。
/// <para>
/// 生命周期约定（P1-5 ~ P1-8）：
/// - <see cref="GameExitedEvent"/> 在任何终态下<em>必发且仅发一次</em>，
///   UI 状态机依赖它从 Running 回到 Idle；
/// - 取消（CancellationToken）视为预期路径：终止进程、发布退出事件、
///   抛出 OperationCanceledException，不记为游戏崩溃；
/// - 退出码与运行时长如实记录，供崩溃诊断区分"闪退"与"正常游玩后退出"。
/// </para>
/// </summary>
public sealed class GameProcessRunner : IDisposable
{
    private readonly Core.EventBus _bus;
    private Process? _process;
    private bool _exitPublished;

    public GameProcessRunner(Core.EventBus bus)
    {
        _bus = bus;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// 启动游戏进程并等待其退出，返回真实退出码。
    /// 取消时抛出 <see cref="OperationCanceledException"/>（调用方按预期路径处理）。
    /// </summary>
    public async Task<int> StartAsync(List<string> command, string gameDirectory, string versionId, CancellationToken ct = default)
    {
        var javaPath = command[0];
        var args = command.Skip(1).ToList();

        var psi = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var startedAt = DateTime.UtcNow;
        try
        {
            if (!_process.Start())
                throw new InvalidOperationException("进程启动失败");

            _bus.Publish(new GameLogEvent(versionId, $"[LoyaLauncher] 进程已启动 (PID {_process.Id})", false));

            // 异步读取输出流
            var stdoutTask = ReadStreamAsync(_process.StandardOutput, versionId, false, ct);
            var stderrTask = ReadStreamAsync(_process.StandardError, versionId, true, ct);

            try
            {
                await _process.WaitForExitAsync(ct);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                // 取消是预期路径（P1-7）：终止进程树、如实上报，不当成游戏崩溃
                Stop();
                var cancelled = DateTime.UtcNow - startedAt;
                _bus.Publish(new GameLogEvent(versionId,
                    $"[LoyaLauncher] 启动流程被取消（进程已终止，曾运行 {cancelled.TotalSeconds:F1}s）", false));
                throw;
            }

            var exitCode = _process.ExitCode;
            var duration = DateTime.UtcNow - startedAt;
            _bus.Publish(new GameLogEvent(versionId,
                $"[LoyaLauncher] 进程退出，代码 {exitCode}，运行时长 {FormatDuration(duration)}", exitCode != 0));
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _bus.Publish(new GameLogEvent(versionId, $"[LoyaLauncher] 启动失败: {ex.Message}", true));
            throw;
        }
        finally
        {
            // 终态保证：无论正常退出、取消还是异常，GameExitedEvent 必发一次——
            // UI 的 IsLaunching / State 依赖它复位（P1-8）
            if (!_exitPublished)
            {
                _exitPublished = true;
                var code = _process is { HasExited: true } ? _process.ExitCode : -1;
                _bus.Publish(new GameExitedEvent(versionId, code));
            }
        }
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F1} 分钟" : $"{span.TotalSeconds:F1} 秒";

    private async Task ReadStreamAsync(StreamReader reader, string versionId, bool isError, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // ReadLineAsync 到达流末尾返回 null，无需 EndOfStream（同步属性在异步路径中有阻塞风险）
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                _bus.Publish(new GameLogEvent(versionId, line, isError));
            }
        }
        catch (OperationCanceledException) { /* 取消时静默 */ }
        catch { /* 流关闭 */ }
    }

    /// <summary>请求结束游戏进程（整棵树，防止 java 派生的子进程残留）</summary>
    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* 进程可能已退出 */ }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        GC.SuppressFinalize(this);
    }
}
