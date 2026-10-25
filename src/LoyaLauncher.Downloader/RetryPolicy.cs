using System.Net;

namespace LoyaLauncher.Downloader;

/// <summary>
/// 有限重试策略（清单 §2.4）：
/// 网络异常、408、429（遵循 Retry-After）、部分 5xx 可重试；
/// 404、认证失败（401/403）、请求格式错误（400）不重试；
/// 指数退避 + 随机抖动；用户取消不重试。
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>默认最大尝试次数（首次 + 2 次重试）</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>退避基数</summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>退避上限</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);

    /// <summary>Retry-After 可接受上限，超出则放弃而非无限等待</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly int _maxAttempts;

    public RetryPolicy(int maxAttempts = DefaultMaxAttempts)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>最大尝试次数</summary>
    public int MaxAttempts => _maxAttempts;

    /// <summary>是否还有下一次尝试</summary>
    public bool HasNext(int attempt) => attempt < _maxAttempts;

    /// <summary>
    /// 判断 HTTP 状态码是否可重试。
    /// 404（资源不存在）、400/401/403（请求或认证问题）重试无意义。
    /// </summary>
    public static bool IsRetryableStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,      // 408
        HttpStatusCode.TooManyRequests => true,     // 429
        HttpStatusCode.InternalServerError => true, // 500
        HttpStatusCode.BadGateway => true,          // 502
        HttpStatusCode.ServiceUnavailable => true,  // 503
        HttpStatusCode.GatewayTimeout => true,      // 504
        _ => false,
    };

    /// <summary>
    /// 判断异常是否可重试（传输层故障）。
    /// 注意：调用方需先区分"用户取消"与"超时取消"，用户取消不重试。
    /// </summary>
    public static bool IsRetryableException(Exception ex) => ex switch
    {
        HttpRequestException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        System.Security.Authentication.AuthenticationException => true, // TLS 握手瞬时失败
        TimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次失败后的等待时间（attempt 从 1 开始）。
    /// 429/503 且带 Retry-After 时优先遵循（封顶 <see cref="MaxRetryAfter"/>），
    /// 否则指数退避加 ±30% 抖动。
    /// </summary>
    public static TimeSpan DelayFor(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            var capped = ra > MaxRetryAfter ? MaxRetryAfter : ra;
            return WithJitter(capped);
        }
        var exponential = BaseDelay * Math.Pow(2, Math.Max(0, attempt - 1));
        var clamped = exponential > MaxDelay ? MaxDelay : exponential;
        return WithJitter(clamped);
    }

    /// <summary>从响应头解析 Retry-After（秒或 HTTP 日期），失败返回 null</summary>
    public static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value.Trim(), out var date))
        {
            var delta = date - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }

    private static readonly ThreadLocal<Random> JitterRandom = new(() => new Random());

    private static TimeSpan WithJitter(TimeSpan baseDelay)
    {
        var factor = 0.7 + 0.6 * JitterRandom.Value!.NextDouble(); // 0.7 ~ 1.3
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }
}
