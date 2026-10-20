using System.Diagnostics;
using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>游戏日志行事件</summary>
public sealed record GameLogEvent(string VersionId, string Line, bool IsError);

/// <summary>游戏进程退出事件</summary>
public sealed record GameExitedEvent(string VersionId, int ExitCode);

/// <summary>
/// 游戏进程运行器：启动 java 进程，异步捕获 stdout/stderr，
/// 经 EventBus 广播日志与退出事件。
/// </summary>
public sealed class GameProcessRunner : IDisposable
{
    private readonly Core.EventBus _bus;
    private Process? _process;

    public GameProcessRunner(Core.EventBus bus)
    {
        _bus = bus;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>启动游戏进程</summary>
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

        try
        {
            if (!_process.Start())
                throw new InvalidOperationException("进程启动失败");
        }
        catch (Exception ex)
        {
            _bus.Publish(new GameLogEvent(versionId, $"[LoyaLauncher] 启动失败: {ex.Message}", true));
            _bus.Publish(new GameExitedEvent(versionId, -1));
            throw;
        }

        _bus.Publish(new GameLogEvent(versionId, $"[LoyaLauncher] 进程已启动 (PID {_process.Id})", false));

        // 异步读取输出流
        var stdoutTask = ReadStreamAsync(_process.StandardOutput, versionId, false, ct);
        var stderrTask = ReadStreamAsync(_process.StandardError, versionId, true, ct);

        await _process.WaitForExitAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);

        var exitCode = _process.ExitCode;
        _bus.Publish(new GameExitedEvent(versionId, exitCode));
        return exitCode;
    }

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

    /// <summary>请求结束游戏进程</summary>
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
