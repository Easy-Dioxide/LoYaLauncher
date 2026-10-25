using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LoyaLauncher.Downloader;

/// <summary>SHA1 工具</summary>
public static class Sha1Util
{
    /// <summary>计算文件 SHA1（小写十六进制）</summary>
    public static async Task<string> ComputeFileHashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>同步计算文件 SHA1</summary>
    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>下载结果</summary>
public sealed record DownloadResult(bool Success, string? Error, bool Skipped = false, bool Canceled = false)
{
    public static DownloadResult Ok(bool skipped = false) => new(true, null, skipped);
    public static DownloadResult Fail(string error) => new(false, error);
    public static DownloadResult Cancel() => new(false, "已取消", Canceled: true);
}

/// <summary>HTTP 非成功状态响应（携带状态码与 Retry-After，供重试策略判断）</summary>
public sealed class HttpStatusException(HttpStatusCode status, TimeSpan? retryAfter, string message)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>文本获取结果（不再静默吞掉错误，清单 §2.3）</summary>
public sealed record StringFetchResult(bool Ok, int StatusCode, string? Content, string? Error)
{
    public static StringFetchResult Succeed(int status, string content) => new(true, status, content, null);
    public static StringFetchResult Failed(int status, string error) => new(false, status, null, error);
}

/// <summary>连通性探测结果（网络诊断页用）</summary>
public sealed record ProbeResult(string Url, bool Ok, int StatusCode, double LatencyMs, string? Error);

/// <summary>
/// 并发下载引擎。
/// 特性：共享 HttpClient 连接池、有限重试（指数退避+抖动，区分错误类型）、
/// 请求头/停滞双超时、.part 断点续传、响应体验证（拒绝 HTML 错误页/空响应）、
/// SHA1 校验、原子替换、进度节流广播（经 EventBus）、统一诊断日志（NetLog）、
/// 镜像健康跟踪（MirrorHealth）、取消支持。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    /// <summary>进度广播最小间隔，避免 UI 线程过载</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>HttpClient 总超时兜底（个别挂死连接的最终防线）</summary>
    private static readonly TimeSpan HttpBackstopTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _concurrency;
    private readonly Core.EventBus _bus;
    private readonly RetryPolicy _retry;
    private readonly int _headersTimeoutSeconds;
    private readonly int _stallTimeoutSeconds;
    private bool _disposed;

    /// <summary>最大尝试次数（含首次）</summary>
    public int MaxAttempts => _retry.MaxAttempts;

    /// <summary>响应头超时（秒）</summary>
    public int HeadersTimeoutSeconds => _headersTimeoutSeconds;

    /// <summary>传输停滞超时（秒）</summary>
    public int StallTimeoutSeconds => _stallTimeoutSeconds;

    public DownloadManager(
        Core.EventBus bus,
        int maxConcurrency = 8,
        int maxAttempts = RetryPolicy.DefaultMaxAttempts,
        int headersTimeoutSeconds = 60,
        int stallTimeoutSeconds = 60,
        HttpMessageHandler? handler = null)
    {
        _bus = bus;
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _retry = new RetryPolicy(maxAttempts);
        _headersTimeoutSeconds = headersTimeoutSeconds;
        _stallTimeoutSeconds = stallTimeoutSeconds;

        var inner = handler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true, // BMCLAPI 等镜像会 302 到带签名 CDN 地址，必须跟随
        };
        _http = new HttpClient(inner, disposeHandler: true)
        {
            Timeout = HttpBackstopTimeout,
        };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "LoyaLauncher/0.7.1 (https://github.com/Easy-Dioxide/LoyaLauncher)");
    }

    /// <summary>
    /// 下载单个文件。目标已存在且 SHA1 匹配时直接跳过。
    /// 传输层故障按 <see cref="RetryPolicy"/> 有限重试；用户取消立即生效且不重试。
    /// </summary>
    public async Task<DownloadResult> DownloadFileAsync(
        string url,
        string destinationPath,
        string? expectedSha1 = null,
        string group = "misc",
        string taskId = "",
        bool expectJson = false,
        CancellationToken ct = default)
    {
        var id = string.IsNullOrEmpty(taskId) ? Guid.NewGuid().ToString("N") : taskId;
        var dir = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(dir);

        // 只有存在可信的 SHA-1 且校验通过时才跳过已有文件。
        // 无校验值的清单/JRE 压缩包不能仅凭“文件存在”判断完整，否则上次中断留下的
        // 损坏文件会永久被当作成功缓存，导致后续每次安装都复用它。
        if (File.Exists(destinationPath) && !string.IsNullOrWhiteSpace(expectedSha1))
        {
            try
            {
                var actual = await Sha1Util.ComputeFileHashAsync(destinationPath, ct);
                if (string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    NetLog.Complete(group, url, 0, 0);
                    return DownloadResult.Ok(skipped: true);
                }
            }
            catch
            {
                // 校验失败则重新下载
            }
        }

        var partPath = destinationPath + ".part";
        try
        {
            await _concurrency.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return DownloadResult.Cancel();
        }
        try
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                NetLog.Request(group, url);

                try
                {
                    await DownloadToFileAsync(url, partPath, destinationPath, id, group, expectJson, ct, attempt);

                    if (expectedSha1 is not null)
                    {
                        _bus.Publish(new DownloadProgressEvent(id, 1, 1, 0, "verify", MirrorNames.Friendly(url)));
                        var actual = await Sha1Util.ComputeFileHashAsync(destinationPath, ct);
                        if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(destinationPath);
                            var msg = $"SHA1 校验失败: 期望 {expectedSha1}, 实际 {actual}";
                            NetLog.Fail(group, url, "hash_mismatch", msg, sw.Elapsed.TotalMilliseconds, attempt);
                            MirrorHealth.RecordFailure(SafeHost(url), "hash_mismatch");
                            return DownloadResult.Fail(msg);
                        }
                    }
                    sw.Stop();
                    var size = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
                    NetLog.Complete(group, url, sw.Elapsed.TotalMilliseconds, size);
                    MirrorHealth.RecordSuccess(SafeHost(url), sw.Elapsed.TotalMilliseconds);
                    _bus.Publish(new DownloadCompletedEvent(id, true, null, group, MirrorNames.Friendly(url)));
                    return DownloadResult.Ok();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 用户取消：不重试、不记失败统计（属主动行为）
                    return DownloadResult.Cancel();
                }
                catch (Exception ex) when (_retry.HasNext(attempt) && IsTransportRetryable(ex))
                {
                    var retryAfter = ex is HttpStatusException hse ? hse.RetryAfter : null;
                    var delay = RetryPolicy.DelayFor(attempt, retryAfter);
                    NetLog.Retry(group, url, attempt, delay.TotalMilliseconds, ex.Message);
                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // 退避等待期间被取消：同样视为用户取消，不进入下一轮尝试
                        return DownloadResult.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    // 不可重试或已用尽尝试
                    if (ex is InvalidDataException) TryDelete(partPath); // 错误页内容不留残留文件
                    NetLog.Fail(group, url, ClassifyError(ex), ex.Message, sw.Elapsed.TotalMilliseconds, attempt);
                    MirrorHealth.RecordFailure(SafeHost(url), ClassifyError(ex));
                    _bus.Publish(new DownloadCompletedEvent(id, false, ex.Message, group, MirrorNames.Friendly(url)));
                    return DownloadResult.Fail(ex.Message);
                }
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>判断是否为可重试的传输层故障（不含用户取消）</summary>
    private static bool IsTransportRetryable(Exception ex) => ex switch
    {
        HttpStatusException hse => RetryPolicy.IsRetryableStatus(hse.Status),
        TimeoutException => true,
        IOException => true,
        HttpRequestException => true,
        _ => false,
    };

    /// <summary>错误分类（诊断日志与 UI 展示共用）</summary>
    public static string ClassifyError(Exception ex) => ex switch
    {
        HttpStatusException hse => $"http_{(int)hse.Status}",
        TimeoutException => "timeout",
        OperationCanceledException => "canceled",
        InvalidDataException => "invalid_content",
        System.Text.Json.JsonException => "json_error",
        UnauthorizedAccessException => "io_denied",
        IOException => "io_error",
        HttpRequestException => "network_error",
        _ => ex.GetType().Name,
    };

    private async Task DownloadToFileAsync(
        string url, string partPath, string destinationPath,
        string taskId, string group, bool expectJson, CancellationToken ct, int attempt = 1)
    {
        long existingLength = 0;
        if (File.Exists(partPath))
        {
            try { existingLength = new FileInfo(partPath).Length; } catch { existingLength = 0; }
        }

        // 响应头超时：与用户取消令牌链接，超时抛出 TimeoutException（可重试）
        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headersCts.CancelAfter(TimeSpan.FromSeconds(_headersTimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingLength > 0)
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连接/响应头超时（>{_headersTimeoutSeconds}s）");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = RetryPolicy.ParseRetryAfter(
                    response.Headers.TryGetValues("Retry-After", out var ra) ? ra.FirstOrDefault() : null);
                throw new HttpStatusException(
                    response.StatusCode, retryAfter,
                    $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
            }

            // 响应体验证（清单 §2.6）：拒绝 HTML 错误页、空响应、非预期 JSON 类型
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"服务器返回 HTML 页面而非文件内容（Content-Type: {contentType}），疑似错误页/拦截页");
            if (response.Content.Headers.ContentLength == 0)
                throw new InvalidDataException("服务器返回空响应（Content-Length: 0）");
            if (expectJson && contentType.Length > 0 &&
                !contentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"期望 JSON 响应但 Content-Type 为 {contentType}");
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            if (!string.Equals(SafeHost(finalUrl), SafeHost(url), StringComparison.OrdinalIgnoreCase))
                NetLog.Response(group, finalUrl, (int)response.StatusCode, 0, mirror: "redirect");

            var total = response.Content.Headers.ContentLength ?? -1L;
            var append = response.StatusCode == HttpStatusCode.PartialContent && existingLength > 0;
            var startBytes = append ? existingLength : 0;
            if (!append && existingLength > 0)
                TryDelete(partPath); // 服务器不支持续传：从头下载

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = startBytes;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var firstRead = true;

            while (true)
            {
                int read;
                try
                {
                    // 停滞超时：每个分块都重新计时，慢速但持续的连接不会被误杀
                    var readTask = contentStream.ReadAsync(buffer, ct).AsTask();
                    var stall = Task.Delay(TimeSpan.FromSeconds(_stallTimeoutSeconds), ct);
                    var completed = await Task.WhenAny(readTask, stall).ConfigureAwait(false);
                    if (completed == stall)
                    {
                        // 用户取消也会让 stall 延时任务结束：先区分取消与真停滞
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        throw new TimeoutException($"下载停滞超过 {_stallTimeoutSeconds}s 无数据");
                    }
                    read = await readTask;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"下载停滞超过 {_stallTimeoutSeconds}s 无数据");
                }

                if (read == 0) break;

                if (firstRead)
                {
                    firstRead = false;
                    if (LooksLikeHtml(buffer.AsSpan(0, read)))
                        throw new InvalidDataException("响应体为 HTML 内容（疑似错误页/拦截页）");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                if (sw.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = sw.Elapsed;
                    var speed = received / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    _bus.Publish(new DownloadProgressEvent(
                        taskId, received, total, speed, group,
                        MirrorNames.Friendly(url), attempt - 1));
                }
            }

            await fileStream.FlushAsync(ct);
            // 原子替换到目标路径（校验在调用方进行）
            File.Move(partPath, destinationPath, overwrite: true);
        }
    }

    /// <summary>响应体开头是否为 HTML（BOM/空白后的 &lt;!doctype html 或 &lt;html，大小写不敏感）</summary>
    private static bool LooksLikeHtml(ReadOnlySpan<byte> span)
    {
        var i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t' ||
               span[i] == (byte)'\r' || span[i] == (byte)'\n' || span[i] == 0xEF || span[i] == 0xBB || span[i] == 0xBF))
            i++;
        ReadOnlySpan<byte> doctype = "<!doctype html"u8;
        ReadOnlySpan<byte> html = "<html"u8;
        var tail = span[i..];
        return StartsWithIgnoreCase(tail, doctype) || StartsWithIgnoreCase(tail, html);
    }

    /// <summary>ASCII 大小写不敏感前缀比较</summary>
    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> prefixLower)
    {
        if (span.Length < prefixLower.Length) return false;
        for (var i = 0; i < prefixLower.Length; i++)
        {
            var c = span[i];
            if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
            if (c != prefixLower[i]) return false;
        }
        return true;
    }

    /// <summary>并发批量下载（不期望 JSON 的便捷重载）</summary>
    public Task<DownloadResult> DownloadManyAsync(
        IEnumerable<(string Url, string Path, string? Sha1, string Group)> items,
        CancellationToken ct = default)
        => DownloadManyAsync(items.Select(i => (i.Url, i.Path, i.Sha1, i.Group, false)), ct);

    /// <summary>并发批量下载。任一任务失败即整体返回失败（保留已完成文件，便于重试续传）。</summary>
    public async Task<DownloadResult> DownloadManyAsync(
        IEnumerable<(string Url, string Path, string? Sha1, string Group, bool ExpectJson)> items,
        CancellationToken ct = default)
    {
        var failures = new List<string>();
        var tasks = items.Select(item => Task.Run(async () =>
        {
            var result = await DownloadFileAsync(item.Url, item.Path, item.Sha1, item.Group, expectJson: item.ExpectJson, ct: ct);
            if (!result.Success)
                lock (failures) failures.Add($"{Path.GetFileName(item.Path)}: {result.Error}");
        }, ct));

        await Task.WhenAll(tasks);
        return failures.Count == 0
            ? DownloadResult.Ok()
            : DownloadResult.Fail($"{failures.Count} 个文件下载失败: {string.Join("; ", failures.Take(5))}");
    }

    /// <summary>
    /// 获取文本内容（不落盘、不走断点续传缓存）。失败记录诊断日志并返回 null，
    /// 调用方应结合 <see cref="GetStringResultAsync"/> 获取错误详情。
    /// </summary>
    public async Task<string?> GetStringAsync(string url, CancellationToken ct = default)
    {
        var result = await GetStringResultAsync(url, ct: ct);
        return result.Ok ? result.Content : null;
    }

    /// <summary>
    /// 获取文本内容并返回完整状态（错误不再静默，清单 §2.3）。
    /// 可重试的传输层故障按 <see cref="RetryPolicy"/> 有限重试；
    /// 用户取消立即生效且不重试。
    /// </summary>
    public async Task<StringFetchResult> GetStringResultAsync(string url, string group = "fetch", CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var host = SafeHost(url);
        NetLog.Request(group, url);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var retryAfter = RetryPolicy.ParseRetryAfter(
                        response.Headers.TryGetValues("Retry-After", out var ra) ? ra.FirstOrDefault() : null);
                    if (attempt < _retry.MaxAttempts && RetryPolicy.IsRetryableStatus(response.StatusCode))
                    {
                        var delay = RetryPolicy.DelayFor(attempt, retryAfter);
                        NetLog.Retry(group, url, attempt, delay.TotalMilliseconds, $"HTTP {(int)response.StatusCode}");
                        await Task.Delay(delay, ct);
                        continue;
                    }
                    var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    NetLog.Fail(group, url, "http-status", error, sw.Elapsed.TotalMilliseconds);
                    MirrorHealth.RecordFailure(host, "http-status");
                    return StringFetchResult.Failed((int)response.StatusCode, error);
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                sw.Stop();
                NetLog.Complete(group, url, sw.Elapsed.TotalMilliseconds, content.Length);
                MirrorHealth.RecordSuccess(host, sw.Elapsed.TotalMilliseconds);
                return StringFetchResult.Succeed((int)response.StatusCode, content);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户取消：不重试、不记失败
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var status = ex is HttpStatusException hse ? (int)hse.Status
                    : ex is HttpRequestException { StatusCode: { } sc } ? (int)sc : 0;
                var errorType = ClassifyError(ex);
                NetLog.Fail(group, url, errorType, ex.Message, sw.Elapsed.TotalMilliseconds);
                MirrorHealth.RecordFailure(host, errorType);
                return StringFetchResult.Failed(status, ex.Message);
            }
        }
    }

    /// <summary>
    /// 连通性探测（网络诊断页用）：HEAD 优先，服务器不支持时回退 GET。
    /// 返回状态码与耗时；不用于下载文件。
    /// </summary>
    public async Task<ProbeResult> HeadProbeAsync(string url, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            var ok = response.IsSuccessStatusCode;
            if (!ok && (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                // 回退 GET（仅读响应头，不读 body）
                sw.Restart();
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                using var r2 = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                return new ProbeResult(url, r2.IsSuccessStatusCode, (int)r2.StatusCode, sw.Elapsed.TotalMilliseconds,
                    r2.IsSuccessStatusCode ? null : $"HTTP {(int)r2.StatusCode}");
            }
            return new ProbeResult(url, ok, (int)response.StatusCode, sw.Elapsed.TotalMilliseconds,
                ok ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(url, false, 0, sw.Elapsed.TotalMilliseconds, ClassifyError(ex) + ": " + ex.Message);
        }
    }

    private static string SafeHost(string? url)
    {
        try { return url is null ? "" : new Uri(url).Host; }
        catch { return ""; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _http.Dispose();
            _concurrency.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
