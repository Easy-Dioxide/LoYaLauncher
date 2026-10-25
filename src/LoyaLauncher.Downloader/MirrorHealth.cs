using System.Collections.Concurrent;

namespace LoyaLauncher.Downloader;

/// <summary>单个镜像主机的近期健康状态</summary>
public sealed record HostHealth(
    string Host,
    int SuccessCount,
    int FailureCount,
    int ConsecutiveFailures,
    double AvgLatencyMs,
    string? LastError,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc)
{
    /// <summary>近期成功率（无样本时为 null）</summary>
    public double? SuccessRate =>
        SuccessCount + FailureCount == 0 ? null : (double)SuccessCount / (SuccessCount + FailureCount);
}

/// <summary>
/// 镜像健康跟踪（清单 §4）：记录每个源的近期成功率与耗时，
/// 供自动切换决策与网络诊断页展示。进程内存态，不落盘。
/// 线程安全；日志/统计失败绝不影响下载。
/// </summary>
public static class MirrorHealth
{
    private static readonly ConcurrentDictionary<string, Stats> Data = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Stats
    {
        public int Success;
        public int Failure;
        public int ConsecutiveFailures;
        public double AvgLatencyMs;
        public string? LastError;
        public DateTime? LastSuccessUtc;
        public DateTime? LastFailureUtc;
    }

    /// <summary>记录一次成功（latencyMs 为请求耗时）</summary>
    public static void RecordSuccess(string host, double latencyMs)
    {
        if (string.IsNullOrEmpty(host)) return;
        var s = Data.GetOrAdd(host, _ => new Stats());
        lock (s)
        {
            s.Success++;
            s.ConsecutiveFailures = 0;
            s.AvgLatencyMs = s.AvgLatencyMs == 0 ? latencyMs : s.AvgLatencyMs * 0.7 + latencyMs * 0.3;
            s.LastSuccessUtc = DateTime.UtcNow;
        }
    }

    /// <summary>记录一次失败</summary>
    public static void RecordFailure(string host, string? error)
    {
        if (string.IsNullOrEmpty(host)) return;
        var s = Data.GetOrAdd(host, _ => new Stats());
        lock (s)
        {
            s.Failure++;
            s.ConsecutiveFailures++;
            s.LastError = error;
            s.LastFailureUtc = DateTime.UtcNow;
        }
    }

    /// <summary>查询某主机健康状态（无样本返回 null）</summary>
    public static HostHealth? Get(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        return Data.TryGetValue(host, out var s) ? Snapshot(host, s) : null;
    }

    /// <summary>全部主机快照（按失败次数降序，供诊断页）</summary>
    public static List<HostHealth> Snapshot() =>
        Data.Select(kv => Snapshot(kv.Key, kv.Value))
            .OrderByDescending(h => h.FailureCount)
            .ThenBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>清空统计（测试用）</summary>
    public static void Reset() => Data.Clear();

    private static HostHealth Snapshot(string host, Stats s)
    {
        lock (s)
        {
            return new HostHealth(
                host, s.Success, s.Failure, s.ConsecutiveFailures,
                Math.Round(s.AvgLatencyMs, 1), s.LastError, s.LastSuccessUtc, s.LastFailureUtc);
        }
    }
}
