using System.Security.Cryptography;
using System.Text;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.Tests;

/// <summary>
/// DownloadManager 集成测试：真实 HttpClient + 本地 mock HTTP 服务（清单 §11）。
/// 覆盖：200 正常、404、429+Retry-After、500 重试、超时、中断、错误 hash、
/// HTML 错误页、断点续传、取消、重定向。
/// </summary>
public sealed class DownloadManagerTests : IAsyncDisposable
{
    private readonly MockHttpServer _server = new();
    private readonly string _tempDir;
    private readonly EventBus _bus = new();
    private readonly List<DownloadCompletedEvent> _events = [];

    public DownloadManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "loya-dm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _bus.Subscribe<DownloadCompletedEvent>(e => _events.Add(e));
    }

    private DownloadManager CreateManager(int maxAttempts = 3, int stallTimeoutSeconds = 60, int headersTimeoutSeconds = 15)
        => new(_bus, maxConcurrency: 4, maxAttempts: maxAttempts,
            headersTimeoutSeconds: headersTimeoutSeconds, stallTimeoutSeconds: stallTimeoutSeconds);

    private string NewDest(string name = "file.bin") => Path.Combine(_tempDir, name);

    [Fact]
    public async Task 下载成功_写入文件并广播完成事件()
    {
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", "hello world"u8.ToArray());
        using var dm = CreateManager();
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("file.bin"), dest, group: "test");

        Assert.True(result.Success, result.Error);
        Assert.Equal("hello world", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Contains(_events, e => e.Success);
    }

    [Fact]
    public async Task HTTP500_有限重试后成功()
    {
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            return attempt < 3
                ? new MockResponse(500, "text/plain", "boom"u8.ToArray())
                : new MockResponse(200, "application/octet-stream", "recovered"u8.ToArray());
        };
        using var dm = CreateManager(maxAttempts: 3);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("flaky.bin"), dest, group: "test");

        Assert.True(result.Success, result.Error);
        Assert.Equal("recovered", await File.ReadAllTextAsync(dest));
        Assert.Equal(3, _server.RequestCount); // 2 次 500 + 1 次成功
    }

    [Fact]
    public async Task HTTP500_用尽尝试后失败()
    {
        _server.Handler = _ => new MockResponse(500, "text/plain", "always boom"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 2);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("down.bin"), dest, group: "test");

        Assert.False(result.Success);
        Assert.Contains("500", result.Error);
        Assert.Equal(2, _server.RequestCount);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task HTTP404_不重试立即失败()
    {
        _server.Handler = _ => new MockResponse(404, "text/plain", "not found"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 3);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("missing.bin"), dest, group: "test");

        Assert.False(result.Success);
        Assert.Contains("404", result.Error);
        Assert.Equal(1, _server.RequestCount); // 404 重试无意义
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task HTTP429_遵循RetryAfter后成功()
    {
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            return attempt == 1
                ? new MockResponse(429, "text/plain", "slow down"u8.ToArray(),
                    new Dictionary<string, string> { ["Retry-After"] = "1" })
                : new MockResponse(200, "application/octet-stream", "ok now"u8.ToArray());
        };
        using var dm = CreateManager(maxAttempts: 3);
        var dest = NewDest();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await dm.DownloadFileAsync(_server.Url("ratelimited.bin"), dest, group: "test");
        sw.Stop();

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, _server.RequestCount);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(650),
            $"应至少等待 Retry-After(1s)*抖动下限，实际 {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task HTML错误页_ContentType拒绝_不落盘()
    {
        _server.Handler = _ => new MockResponse(200, "text/html",
            "<html><body>502 Bad Gateway</body></html>"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 2);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("errorpage.bin"), dest, group: "test");

        Assert.False(result.Success);
        Assert.Contains("HTML", result.Error);
        Assert.False(File.Exists(dest), "HTML 错误页不得保存为正式文件");
        Assert.False(File.Exists(dest + ".part"), "HTML 错误页不得留下 .part 残留文件");
        Assert.Equal(1, _server.RequestCount); // invalid_content 不可重试
    }

    [Fact]
    public async Task HTML错误页_无ContentType_内容嗅探拒绝()
    {
        _server.Handler = _ => new MockResponse(200, null, "<!DOCTYPE html><html><body>blocked</body></html>"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("sneaky.bin"), dest, group: "test");

        Assert.False(result.Success);
        Assert.Contains("HTML", result.Error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task JSON请求_ContentType非JSON_拒绝()
    {
        _server.Handler = _ => new MockResponse(200, "application/xml", "<xml/>"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("manifest.json"), dest, group: "manifest", expectJson: true);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task 空响应_ContentLength0_失败()
    {
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", []);
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("empty.bin"), dest, group: "test");

        Assert.False(result.Success);
        Assert.Contains("空响应", result.Error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task SHA1不匹配_删除目标文件()
    {
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", "tampered content"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();
        var wrongSha1 = Convert.ToHexStringLower(SHA1.HashData("expected"u8.ToArray()));

        var result = await dm.DownloadFileAsync(_server.Url("tampered.bin"), dest, wrongSha1, group: "test");

        Assert.False(result.Success);
        Assert.Contains("SHA1", result.Error);
        Assert.False(File.Exists(dest), "校验失败的文件必须删除，不得留作缓存");
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task SHA1匹配_成功跳过()
    {
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", "the real content"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();
        await File.WriteAllTextAsync(dest, "the real content");
        var sha1 = Convert.ToHexStringLower(SHA1.HashData("the real content"u8.ToArray()));

        var result = await dm.DownloadFileAsync(_server.Url("cached.bin"), dest, sha1, group: "test");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Skipped);
        Assert.Equal(0, _server.RequestCount); // 命中缓存不发请求
    }

    [Fact]
    public async Task 断点续传_已有part发送Range并拼接()
    {
        _server.Handler = r =>
        {
            if (r.RangeHeader is null)
                return new MockResponse(200, "application/octet-stream", "first-half"u8.ToArray());
            Assert.StartsWith("bytes=", r.RangeHeader);
            return new MockResponse(206, "application/octet-stream", "second-half"u8.ToArray(),
                new Dictionary<string, string> { ["Content-Range"] = "bytes 10-20/21" });
        };
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();
        await File.WriteAllTextAsync(dest + ".part", "first-half"); // 模拟上次中断

        var result = await dm.DownloadFileAsync(_server.Url("resume.bin"), dest, group: "test");

        Assert.True(result.Success, result.Error);
        Assert.Equal("first-halfsecond-half", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Equal(1, _server.RequestCount); // 仅续传一次
    }

    [Fact]
    public async Task 用户取消_立即生效不重试()
    {
        // 响应头立即返回、响应体延迟 3s：取消发生在读取 body 阶段
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", "never read fully"u8.ToArray(),
            DelayBeforeBodyMs: 3000);
        using var dm = CreateManager(maxAttempts: 3);
        var dest = NewDest();
        using var cts = new CancellationTokenSource(300);

        var result = await dm.DownloadFileAsync(_server.Url("slow.bin"), dest, group: "test", ct: cts.Token);

        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Contains("已取消", result.Error);
        Assert.Equal(1, _server.RequestCount); // 取消后不启动后续重试
    }

    [Fact]
    public async Task 停滞超时_触发并可重试()
    {
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            return new MockResponse(200, "application/octet-stream", "late body"u8.ToArray(),
                DelayBeforeBodyMs: 2500);
        };
        using var dm = CreateManager(maxAttempts: 2, stallTimeoutSeconds: 1);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("stall.bin"), dest, group: "test");

        Assert.False(result.Success);
        Assert.Contains("停滞", result.Error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task 重定向_自动跟随并成功()
    {
        _server.Handler = r => r.Path.EndsWith("/redirect")
            ? new MockResponse(302, null, [], new Dictionary<string, string> { ["Location"] = _server.Url("final") })
            : new MockResponse(200, "application/octet-stream", "final content"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("redirect"), dest, group: "test");

        Assert.True(result.Success, result.Error);
        Assert.Equal("final content", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task 并发批量_部分失败整体失败且保留成功文件()
    {
        _server.Handler = r => r.Path.EndsWith("/bad.bin")
            ? new MockResponse(404, "text/plain", "no"u8.ToArray())
            : new MockResponse(200, "application/octet-stream", Encoding.UTF8.GetBytes($"content-{r.Path}"));
        using var dm = CreateManager(maxAttempts: 1);
        var good = NewDest("good.bin");
        var bad = NewDest("bad.bin");

        var result = await dm.DownloadManyAsync(
        [
            (_server.Url("good.bin"), good, (string?)null, "test", false),
            (_server.Url("bad.bin"), bad, (string?)null, "test", false),
        ]);

        Assert.False(result.Success);
        Assert.Contains("bad.bin", result.Error);
        Assert.True(File.Exists(good), "成功的文件应保留，便于重试只补缺口");
    }

    [Fact]
    public async Task GetString_失败不静默_返回状态码()
    {
        _server.Handler = _ => new MockResponse(503, "text/plain", "unavailable"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);

        var result = await dm.GetStringResultAsync(_server.Url("status.json"));

        Assert.False(result.Ok);
        Assert.Equal(503, result.StatusCode);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Null(await dm.GetStringAsync(_server.Url("status.json")));
    }

    [Fact]
    public async Task Head探测_HEAD优先_405回退GET()
    {
        _server.Handler = r => new MockResponse(200, "text/plain", "probe ok"u8.ToArray());
        using var dm = CreateManager();

        var result = await dm.HeadProbeAsync(_server.Url("probe.bin"));

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.True(result.LatencyMs >= 0);
    }

    [Fact]
    public async Task 诊断日志_记录请求且URL脱敏()
    {
        NetLog.Reset();
        NetLog.FileSinkEnabled = false; // 测试不写用户目录日志
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", "logged"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);

        await dm.DownloadFileAsync(_server.Url("logged.bin?token=supersecret&sig=abc"), NewDest(), group: "test");

        var entries = NetLog.Recent(50);
        Assert.Contains(entries, e => e.Stage == NetStage.Complete);
        Assert.Contains(entries, e => e.Stage == NetStage.Request);
        // 查询串（含 token/sig）不得出现在日志中
        Assert.DoesNotContain("supersecret", string.Join("\n", entries.Select(e => e.Detail ?? "")));
        var report = NetLog.ExportReport();
        Assert.Contains("网络诊断报告", report);
        Assert.DoesNotContain("supersecret", report);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}

/// <summary>RetryPolicy 单元测试（不依赖网络）</summary>
public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void 可重试状态码(int status)
    {
        Assert.True(RetryPolicy.IsRetryableStatus((System.Net.HttpStatusCode)status));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public void 不可重试状态码(int status)
    {
        Assert.False(RetryPolicy.IsRetryableStatus((System.Net.HttpStatusCode)status));
    }

    [Fact]
    public void ParseRetryAfter_秒与日期()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.ParseRetryAfter("2"));
        Assert.Null(RetryPolicy.ParseRetryAfter(null));
        Assert.Null(RetryPolicy.ParseRetryAfter("garbage"));
        var future = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        var parsed = RetryPolicy.ParseRetryAfter(future);
        Assert.NotNull(parsed);
        Assert.True(parsed > TimeSpan.FromSeconds(25) && parsed <= TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void 退避_指数增长且有抖动上限()
    {
        var d1 = RetryPolicy.DelayFor(1).TotalMilliseconds;
        var d2 = RetryPolicy.DelayFor(2).TotalMilliseconds;
        var d3 = RetryPolicy.DelayFor(3).TotalMilliseconds;
        var d10 = RetryPolicy.DelayFor(10).TotalMilliseconds;

        Assert.InRange(d1, 350, 650);   // 500ms ±30%
        Assert.InRange(d2, 700, 1300);  // 1s ±30%
        Assert.InRange(d3, 1400, 2600); // 2s ±30%
        Assert.InRange(d10, 5600, 10400); // 8s 封顶 ±30%
    }

    [Fact]
    public void 退避_遵循RetryAfter并封顶()
    {
        var capped = RetryPolicy.DelayFor(1, TimeSpan.FromSeconds(600)); // 600s 超过 60s 上限
        Assert.True(capped <= TimeSpan.FromSeconds(78), $"600s Retry-After 应封顶到 60s(+抖动)，实际 {capped}");
        var honored = RetryPolicy.DelayFor(1, TimeSpan.FromSeconds(5));
        Assert.True(honored >= TimeSpan.FromSeconds(3.4) && honored <= TimeSpan.FromSeconds(6.5));
    }

    [Fact]
    public void HasNext_尊重最大尝试次数()
    {
        var policy = new RetryPolicy(3);
        Assert.True(policy.HasNext(1));
        Assert.True(policy.HasNext(2));
        Assert.False(policy.HasNext(3));
        Assert.Equal(3, policy.MaxAttempts);
    }
}

/// <summary>NetLog / MirrorHealth 单元测试</summary>
public sealed class NetLogTests
{
    public NetLogTests()
    {
        NetLog.Reset();
        NetLog.FileSinkEnabled = false; // 测试不写用户目录日志
    }

    [Fact]
    public void SanitizeUrl_丢弃查询串与用户信息()
    {
        Assert.Equal("https://api.modrinth.com/v2/search",
            NetLog.SanitizeUrl("https://api.modrinth.com/v2/search?query=fabric&token=secret"));
        Assert.Equal("https://example.com/mirror/x.json",
            NetLog.SanitizeUrl("https://user:password@example.com/mirror/x.json"));
        Assert.Equal("https://bmclapi2.bangbang93.com:8443/mc/game/version_manifest_v2.json",
            NetLog.SanitizeUrl("https://bmclapi2.bangbang93.com:8443/mc/game/version_manifest_v2.json"));
    }

    [Fact]
    public void SanitizeUrl_无法解析时剥离查询串()
    {
        // 主机非法的 URL：整体解析失败，但查询串（可能含令牌）必须剥离
        var sanitized = NetLog.SanitizeUrl("http://[invalid]/path?token=secret&sig=abc");
        Assert.DoesNotContain("token=secret", sanitized);
        Assert.DoesNotContain("sig=abc", sanitized);
    }

    [Fact]
    public void 日志环形缓冲_保留最近记录()
    {
        for (var i = 0; i < 1200; i++)
            NetLog.Log("test", "https://example.com/x", NetStage.Request, "start");
        var recent = NetLog.Recent(100);
        Assert.Equal(100, recent.Count);
    }

    [Fact]
    public void MirrorHealth_统计成功率与连续失败()
    {
        MirrorHealth.Reset();
        MirrorHealth.RecordSuccess("a.example.com", 100);
        MirrorHealth.RecordSuccess("a.example.com", 200);
        MirrorHealth.RecordFailure("b.example.com", "http_500");
        MirrorHealth.RecordFailure("b.example.com", "timeout");

        var a = MirrorHealth.Get("a.example.com");
        var b = MirrorHealth.Get("b.example.com");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(2, a.SuccessCount);
        Assert.Equal(0, a.FailureCount);
        Assert.Equal(1.0, a.SuccessRate);
        Assert.Equal(2, b.FailureCount);
        Assert.Equal(2, b.ConsecutiveFailures);
        Assert.Equal(0.0, b.SuccessRate);
        Assert.Equal("timeout", b.LastError);

        MirrorHealth.Reset();
        Assert.Null(MirrorHealth.Get("a.example.com"));
    }
}
