using System.Diagnostics;
using System.Net;
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
public sealed record DownloadResult(bool Success, string? Error, bool Skipped = false)
{
    public static DownloadResult Ok(bool skipped = false) => new(true, null, skipped);
    public static DownloadResult Fail(string error) => new(false, error);
}

/// <summary>
/// 并发下载引擎。
/// 特性：共享 HttpClient 连接池、.part 断点续传、SHA1 校验、原子替换、
/// 进度节流广播（经 EventBus）、取消支持。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    /// <summary>进度广播最小间隔，避免 UI 线程过载</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _concurrency;
    private readonly Core.EventBus _bus;
    private bool _disposed;

    public DownloadManager(Core.EventBus bus, int maxConcurrency = 8)
    {
        _bus = bus;
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // 单次请求不设总超时，由调用方 CancellationToken 控制
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 LoyaLauncher/0.1.0");
    }

    /// <summary>
    /// 下载单个文件。目标已存在且 SHA1 匹配时直接跳过。
    /// </summary>
    public async Task<DownloadResult> DownloadFileAsync(
        string url,
        string destinationPath,
        string? expectedSha1 = null,
        string group = "misc",
        string taskId = "",
        CancellationToken ct = default)
    {
        var id = string.IsNullOrEmpty(taskId) ? Guid.NewGuid().ToString("N") : taskId;
        var dir = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(dir);

        // 已存在且校验通过 → 跳过
        if (File.Exists(destinationPath))
        {
            if (expectedSha1 is null)
                return DownloadResult.Ok(skipped: true);
            try
            {
                if (await Sha1Util.ComputeFileHashAsync(destinationPath, ct) == expectedSha1)
                    return DownloadResult.Ok(skipped: true);
            }
            catch
            {
                // 校验失败则重新下载
            }
        }

        var partPath = destinationPath + ".part";
        await _concurrency.WaitAsync(ct);
        try
        {
            await DownloadToFileAsync(url, partPath, destinationPath, id, group, ct);
            if (expectedSha1 is not null)
            {
                _bus.Publish(new DownloadProgressEvent(id, 1, 1, 0, "verify"));
                var actual = await Sha1Util.ComputeFileHashAsync(destinationPath, ct);
                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(destinationPath);
                    return DownloadResult.Fail($"SHA1 校验失败: 期望 {expectedSha1}, 实际 {actual}");
                }
            }
            _bus.Publish(new DownloadCompletedEvent(id, true, null, group));
            return DownloadResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return DownloadResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            _bus.Publish(new DownloadCompletedEvent(id, false, ex.Message, group));
            return DownloadResult.Fail(ex.Message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task DownloadToFileAsync(
        string url, string partPath, string destinationPath,
        string taskId, string group, CancellationToken ct)
    {
        long existingLength = 0;
        if (File.Exists(partPath))
        {
            try { existingLength = new FileInfo(partPath).Length; } catch { existingLength = 0; }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        var append = response.StatusCode == HttpStatusCode.PartialContent && existingLength > 0;
        var startBytes = append ? existingLength : 0;
        if (!append && existingLength > 0)
            TryDelete(partPath);

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long received = startBytes;
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            if (sw.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = sw.Elapsed;
                var speed = received / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                _bus.Publish(new DownloadProgressEvent(taskId, received, total, speed, group));
            }
        }

        await fileStream.FlushAsync(ct);
        // 原子替换到目标路径
        File.Move(partPath, destinationPath, overwrite: true);
    }

    /// <summary>并发批量下载。任一任务失败即整体返回失败（保留已完成文件，便于重试续传）。</summary>
    public async Task<DownloadResult> DownloadManyAsync(
        IEnumerable<(string Url, string Path, string? Sha1, string Group)> items,
        CancellationToken ct = default)
    {
        var failures = new List<string>();
        var tasks = items.Select(item => Task.Run(async () =>
        {
            var result = await DownloadFileAsync(item.Url, item.Path, item.Sha1, item.Group, ct: ct);
            if (!result.Success)
                lock (failures) failures.Add($"{Path.GetFileName(item.Path)}: {result.Error}");
        }, ct));

        await Task.WhenAll(tasks);
        return failures.Count == 0
            ? DownloadResult.Ok()
            : DownloadResult.Fail($"{failures.Count} 个文件下载失败: {string.Join("; ", failures.Take(5))}");
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
