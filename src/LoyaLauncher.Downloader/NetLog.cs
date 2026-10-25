using System.Collections.Concurrent;
using LoyaLauncher.Core;

namespace LoyaLauncher.Downloader;

/// <summary>网络请求阶段（用于诊断日志归类）</summary>
public enum NetStage
{
    /// <summary>准备发起请求（DNS 解析由系统完成，此处记录目标）</summary>
    Request,
    /// <summary>收到响应头</summary>
    Response,
    /// <summary>读取响应体</summary>
    Body,
    /// <summary>SHA1/完整性校验</summary>
    Verify,
    /// <summary>重试等待</summary>
    Retry,
    /// <summary>成功完成</summary>
    Complete,
    /// <summary>失败（含错误分类）</summary>
    Fail,
}

/// <summary>
/// 一条网络诊断记录。URL 在记录前已脱敏（仅保留 协议://主机[:端口]/路径，
/// 丢弃查询串与片段），任何令牌/设备码/授权头都不得进入本结构。
/// </summary>
public sealed record NetEntry(
    DateTime TimestampUtc,
    string Module,
    string Host,
    NetStage Stage,
    string Result,
    double DurationMs,
    int StatusCode,
    string? ErrorType,
    int RetryCount,
    string? Mirror,
    string? Detail);

/// <summary>
/// 统一网络诊断日志（清单 §2.2）：
/// 每个请求记录功能模块、脱敏主机名、阶段、耗时、HTTP 状态码、异常类型、
/// 重试次数、所用镜像与最终结果。内存环形缓冲供 UI 诊断页读取，
/// 异步落盘到 logs/network-YYYYMMDD.log。
/// 日志写入永不抛出异常，绝不影响下载主流程。
/// </summary>
public static class NetLog
{
    private const int Capacity = 1000;
    private const int FileFlushIntervalMs = 2000;
    private const int FileFlushBatch = 200;

    private static readonly Lock Gate = new();
    private static readonly List<NetEntry> Ring = new(Capacity);
    private static readonly ConcurrentQueue<string> FileQueue = new();

    private static StreamWriter? _writer;
    private static string? _writerDate;
    private static Task? _flusher;
    private static volatile bool _fileSinkEnabled = true;

    /// <summary>是否写日志文件（测试可关闭）</summary>
    public static bool FileSinkEnabled
    {
        get => _fileSinkEnabled;
        set
        {
            _fileSinkEnabled = value;
            if (!value) CloseWriter();
        }
    }

    /// <summary>记录一条网络事件（线程安全，永不抛出）</summary>
    public static void Log(
        string module,
        string url,
        NetStage stage,
        string result,
        double durationMs = 0,
        int statusCode = 0,
        string? errorType = null,
        int retryCount = 0,
        string? mirror = null,
        string? detail = null)
    {
        try
        {
            var host = SafeHost(url);
            var entry = new NetEntry(
                DateTime.UtcNow, module, host, stage, result,
                durationMs, statusCode, errorType, retryCount, mirror, detail);

            lock (Gate)
            {
                Ring.Add(entry);
                if (Ring.Count > Capacity)
                    Ring.RemoveRange(0, Ring.Count - Capacity);
            }

            if (_fileSinkEnabled)
            {
                FileQueue.Enqueue(FormatLine(entry));
                EnsureFlusher();
            }
        }
        catch
        {
            // 诊断日志失败绝不能影响主流程
        }
    }

    /// <summary>便捷方法：请求开始</summary>
    public static void Request(string module, string url, string? mirror = null) =>
        Log(module, url, NetStage.Request, "start", mirror: mirror);

    /// <summary>便捷方法：收到响应</summary>
    public static void Response(string module, string url, int statusCode, double durationMs, long? bytes = null, string? mirror = null) =>
        Log(module, url, NetStage.Response, $"http_{statusCode}", durationMs, statusCode,
            detail: bytes is > 0 ? $"{bytes}B" : null, mirror: mirror);

    /// <summary>便捷方法：成功完成</summary>
    public static void Complete(string module, string url, double durationMs, long bytes, string? mirror = null) =>
        Log(module, url, NetStage.Complete, "ok", durationMs, detail: $"{bytes}B", mirror: mirror);

    /// <summary>便捷方法：失败</summary>
    public static void Fail(string module, string url, string errorType, string? message, double durationMs = 0, int retryCount = 0, string? mirror = null) =>
        Log(module, url, NetStage.Fail, errorType, durationMs, errorType: errorType, retryCount: retryCount,
            mirror: mirror, detail: Truncate(message));

    /// <summary>便捷方法：安排重试</summary>
    public static void Retry(string module, string url, int attempt, double delayMs, string reason) =>
        Log(module, url, NetStage.Retry, "retry", detail: $"attempt={attempt} delay={delayMs:F0}ms reason={Truncate(reason)}");

    /// <summary>最近 N 条记录（新的在前），供诊断页展示</summary>
    public static List<NetEntry> Recent(int count = 200)
    {
        lock (Gate)
        {
            var skip = Math.Max(0, Ring.Count - count);
            return Ring.GetRange(skip, Math.Min(count, Ring.Count))
                       .OrderByDescending(e => e.TimestampUtc)
                       .ToList();
        }
    }

    /// <summary>清空内存记录（测试用）</summary>
    public static void Reset()
    {
        lock (Gate) Ring.Clear();
        while (FileQueue.TryDequeue(out _)) { }
        CloseWriter();
    }

    /// <summary>
    /// 导出脱敏诊断报告（"复制诊断信息"按钮内容）。
    /// 包含环境信息、代理状态、当前镜像配置（仅主机名）与近期请求摘要。
    /// </summary>
    public static string ExportReport(IEnumerable<string>? mirrorHosts = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== LoyaLauncher 网络诊断报告 ===");
        sb.AppendLine($"时间(UTC): {DateTime.UtcNow:O}");
        sb.AppendLine($"版本: {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"}");
        sb.AppendLine($"系统: {Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"运行时: .NET {Environment.Version}");
        sb.AppendLine($"代理: HTTP_PROXY={Mask(Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("http_proxy"))}, " +
                      $"HTTPS_PROXY={Mask(Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy"))}");
        if (mirrorHosts is not null)
            sb.AppendLine($"镜像链: {string.Join(" -> ", mirrorHosts)}");
        sb.AppendLine();

        List<NetEntry> recent;
        lock (Gate) recent = Ring.ToList();
        sb.AppendLine($"近期请求（共 {recent.Count} 条，最多保留 1000）:");
        foreach (var e in recent.TakeLast(60))
        {
            sb.AppendLine(FormatLine(e));
        }
        return sb.ToString();
    }

    // ---------- 内部实现 ----------

    private static string? Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(未设置)";
        // 代理地址可能含凭据：仅保留协议与主机
        try
        {
            var uri = new Uri(value);
            return uri.UserInfo.Length > 0 ? $"{uri.Scheme}://***@{uri.Host}:{uri.Port}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }
        catch { return "(已设置，格式无法解析)"; }
    }

    /// <summary>URL 脱敏：仅保留 协议://主机[:端口]/路径，丢弃 userinfo、查询串与片段</summary>
    public static string SanitizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(empty)";
        try
        {
            var uri = new Uri(url);
            var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
            var path = uri.AbsolutePath;
            if (path.Length > 120) path = path[..120] + "…";
            return $"{uri.Scheme}://{uri.Host}{port}{path}";
        }
        catch
        {
            // 无法解析的 URL：剥离查询串/片段后截断展示，避免泄露可能的令牌
            var cut = url;
            var q = cut.IndexOfAny(['?', '#']);
            if (q >= 0) cut = cut[..q];
            return cut.Length > 80 ? cut[..80] + "…" : cut;
        }
    }

    private static string SafeHost(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch { return "(invalid-url)"; }
    }

    private static string Truncate(string? s) => s switch
    {
        null or "" => "",
        { Length: <= 300 } => s,
        _ => s[..300] + "…",
    };

    private static string FormatLine(NetEntry e) =>
        $"{e.TimestampUtc:HH:mm:ss.fff} [{e.Module}] {e.Stage,-8} {e.Result,-18} " +
        $"host={e.Host} status={e.StatusCode} dur={e.DurationMs:F0}ms retry={e.RetryCount} " +
        (e.Mirror is null ? "" : $"mirror={e.Mirror} ") +
        (string.IsNullOrEmpty(e.Detail) ? "" : $"detail={e.Detail}");

    private static void EnsureFlusher()
    {
        if (_flusher is { IsCompleted: false }) return;
        lock (Gate)
        {
            if (_flusher is { IsCompleted: false }) return;
            _flusher = Task.Run(FlushLoop);
        }
    }

    private static async Task FlushLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(FileFlushIntervalMs);
                FlushBatch();
            }
            catch
            {
                return; // 退出 flush 循环，绝不影响主流程
            }
        }
    }

    private static void FlushBatch()
    {
        if (!_fileSinkEnabled || FileQueue.IsEmpty) return;
        try
        {
            var date = DateTime.UtcNow.ToString("yyyyMMdd");
            if (_writer is null || _writerDate != date)
            {
                CloseWriter();
                var dir = LoyaPaths.Logs;
                Directory.CreateDirectory(dir);
                _writer = new StreamWriter(Path.Combine(dir, $"network-{date}.log"), append: true)
                {
                    AutoFlush = false,
                };
                _writerDate = date;
            }

            var written = 0;
            while (written < FileFlushBatch && FileQueue.TryDequeue(out var line))
            {
                _writer.WriteLine(line);
                written++;
            }
            _writer.Flush();
        }
        catch
        {
            // 落盘失败（如目录不可写）仅丢弃本次批次
            CloseWriter();
        }
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _writerDate = null;
    }
}
