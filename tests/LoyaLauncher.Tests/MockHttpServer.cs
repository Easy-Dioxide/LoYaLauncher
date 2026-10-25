using System.Net;
using System.Text;

namespace LoyaLauncher.Tests;

/// <summary>mock HTTP 服务记录到的一次请求</summary>
public sealed record RecordedRequest(string Method, string Path, string Query, string? RangeHeader, string? UserAgent);

/// <summary>mock HTTP 服务的响应定义</summary>
public sealed record MockResponse(
    int Status,
    string? ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null,
    int DelayBeforeBodyMs = 0);

/// <summary>
/// 可控的本地 mock HTTP 服务（127.0.0.1 回环，清单 §11 "使用可控的本地 mock HTTP 服务模拟失败场景"）。
/// 支持：任意状态码、Content-Type、自定义头（429+Retry-After、Location 重定向、Content-Range）、
/// Range 续传、响应头后延迟（停滞测试）、HEAD 方法。所有请求被记录供断言。
/// </summary>
public sealed class MockHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    /// <summary>请求处理委托；返回 null 表示 404</summary>
    public Func<RecordedRequest, MockResponse?> Handler { get; set; } = _ => null;

    /// <summary>已记录的请求（按到达顺序）</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    /// <summary>收到的请求总数</summary>
    public int RequestCount
    {
        get { lock (_requests) return _requests.Count; }
    }

    public MockHttpServer()
    {
        var port = GetFreePort();
        Prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoop);
    }

    /// <summary>服务基地址（含尾斜杠）</summary>
    public string Prefix { get; }

    /// <summary>拼接完整 URL</summary>
    public string Url(string relativePath) => Prefix + relativePath.TrimStart('/');

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // listener 已关闭
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var recorded = new RecordedRequest(
            req.HttpMethod,
            req.Url?.AbsolutePath ?? "/",
            req.Url?.Query ?? "",
            req.Headers["Range"],
            req.Headers["User-Agent"]);
        lock (_requests) _requests.Add(recorded);

        var isHead = req.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        try
        {
            var response = Handler(recorded) ?? new MockResponse(404, "text/plain", "not found"u8.ToArray());
            ctx.Response.StatusCode = response.Status;
            if (response.ContentType is not null)
                ctx.Response.ContentType = response.ContentType;
            if (response.Headers is not null)
                foreach (var (k, v) in response.Headers)
                    ctx.Response.AddHeader(k, v);

            if (isHead)
            {
                ctx.Response.ContentLength64 = 0;
            }
            else
            {
                ctx.Response.ContentLength64 = response.Body.Length;
                if (response.DelayBeforeBodyMs > 0 && response.Body.Length > 0)
                {
                    // 先写首字节并冲刷，确保客户端收到响应头后再停滞
                    // （仅设置 ContentLength64+Flush 不足以让 HttpListener 发出响应头）
                    await ctx.Response.OutputStream.WriteAsync(response.Body.AsMemory(0, 1));
                    await ctx.Response.OutputStream.FlushAsync();
                    await Task.Delay(response.DelayBeforeBodyMs);
                    await ctx.Response.OutputStream.WriteAsync(response.Body.AsMemory(1));
                }
                else if (response.Body.Length > 0)
                {
                    await ctx.Response.OutputStream.WriteAsync(response.Body);
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                var bytes = Encoding.UTF8.GetBytes(ex.Message);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch { /* 响应已开始，忽略 */ }
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
