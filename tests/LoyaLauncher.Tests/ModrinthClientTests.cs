using System.Net;
using System.Text;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Mods;

namespace LoyaLauncher.Tests;

public sealed class ModrinthClientTests
{
    [Fact]
    public async Task SearchUsesStringFacetGroups()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"hits\":[],\"total\":0,\"offset\":0,\"limit\":20}")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.modrinth.com/v2/") };
        using var client = new ModrinthClient(http);

        await client.SearchAsync("sodium", "1.21.1", "fabric");

        Assert.NotNull(handler.LastUri);
        Assert.Equal("/v2/search", handler.LastUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(handler.LastUri.Query);
        Assert.Contains("[\"project_type:mod\"]", query);
        Assert.Contains("[\"categories:fabric\"]", query);
        Assert.Contains("[\"versions:1.21.1\"]", query);
        Assert.DoesNotContain("\"project_type\":\"mod\"", query);
    }

    [Fact]
    public async Task VersionLookupUsesGameVersionsAndLoadersParameters()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.modrinth.com/v2/") };
        using var client = new ModrinthClient(http);

        await client.GetVersionsAsync("sodium", "1.21.1", "fabric");

        Assert.NotNull(handler.LastUri);
        Assert.Equal("/v2/project/sodium/version", handler.LastUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(handler.LastUri.Query);
        Assert.Contains("game_versions=[\"1.21.1\"]", query);
        Assert.Contains("loaders=[\"fabric\"]", query);
        Assert.DoesNotContain("facets=", query);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }
}

/// <summary>
/// Modrinth 生产路径测试（清单 §5/§11）：API 请求走 DownloadManager
/// （重试 / 停滞看门狗 / 网络日志），文件下载保持版本数据原始 URL。
/// </summary>
public sealed class ModrinthClientProductionTests : IDisposable
{
    private readonly MockHttpServer _server = new();
    private readonly string _tempDir;
    private readonly EventBus _bus = new();

    public ModrinthClientProductionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "loya-modrinth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private DownloadManager CreateManager(int maxAttempts = 3)
        => new(_bus, maxConcurrency: 2, maxAttempts: maxAttempts,
            headersTimeoutSeconds: 15, stallTimeoutSeconds: 15);

    private ModrinthClient CreateClient(int maxAttempts = 3)
        => new(CreateManager(maxAttempts), apiBaseUrl: _server.Prefix);

    [Fact]
    public async Task 搜索_走统一下载器_带UserAgent且路径正确()
    {
        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes("{\"hits\":[],\"total\":0,\"offset\":0,\"limit\":20}"));

        await CreateClient().SearchAsync("sodium", "1.21.1", "fabric");

        var req = Assert.Single(_server.Requests);
        Assert.Equal("/search", req.Path);
        Assert.Contains("LoyaLauncher", req.UserAgent);
    }

    [Fact]
    public async Task 搜索_429重试耗尽_抛出限流异常()
    {
        _server.Handler = _ => new MockResponse(429, "application/json",
            Encoding.UTF8.GetBytes("{\"error\":\"rate limited\"}"));

        var ex = await Assert.ThrowsAsync<ModrinthApiException>(
            () => CreateClient(maxAttempts: 2).SearchAsync("sodium", "1.21.1", "fabric"));

        Assert.True(ex.IsRateLimited);
        Assert.Contains("限流", ex.Message);
        Assert.Equal(2, _server.RequestCount); // 证明了重试而非单次失败
    }

    [Fact]
    public async Task 搜索_500一次后恢复_重试成功()
    {
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            return attempt < 2
                ? new MockResponse(500, "text/plain", "boom"u8.ToArray())
                : new MockResponse(200, "application/json",
                    Encoding.UTF8.GetBytes("{\"hits\":[],\"total\":1,\"offset\":0,\"limit\":20}"));
        };

        var result = await CreateClient(maxAttempts: 3).SearchAsync("sodium", "1.21.1", "fabric");

        Assert.Equal(2, attempt);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task 搜索_无结果_返回空集合而非异常()
    {
        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes("{\"hits\":[],\"total\":0,\"offset\":0,\"limit\":20}"));

        var result = await CreateClient().SearchAsync("不存在的模组xyz", "1.21.1", "fabric");

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task 版本查询_400_抛出参数错误异常()
    {
        _server.Handler = _ => new MockResponse(400, "application/json",
            Encoding.UTF8.GetBytes("{\"error\":\"bad request\"}"));

        var ex = await Assert.ThrowsAsync<ModrinthApiException>(
            () => CreateClient(maxAttempts: 1).GetVersionsAsync("sodium", "1.21.1", "fabric"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.False(ex.IsRateLimited);
    }

    [Fact]
    public async Task 版本查询_返回版本与文件列表()
    {
        _server.Handler = _ => new MockResponse(200, "application/json", Encoding.UTF8.GetBytes("""
        [
          {
            "id": "v1", "project_id": "sodium", "version_number": "0.6.0",
            "name": "0.6.0", "game_versions": ["1.21.1"], "loaders": ["fabric"],
            "files": [
              {"url": "https://cdn.modrinth.com/data/sodium/versions/0.6.0/sodium-0.6.0.jar",
               "filename": "sodium-0.6.0.jar", "primary": true, "size": 100,
               "hashes": {"sha1": "abc", "sha512": "def"}}
            ],
            "date_published": "2025-01-01T00:00:00Z"
          }
        ]
        """));

        var versions = await CreateClient().GetVersionsAsync("sodium", "1.21.1", "fabric");

        var version = Assert.Single(versions);
        Assert.Equal("sodium", version.ProjectId);
        var file = Assert.Single(version.Files);
        Assert.Equal("sodium-0.6.0.jar", file.Filename);
        Assert.Equal("abc", file.Hashes["sha1"]);
    }

    [Fact]
    public async Task 下载Mod_文件名穿越_拒绝且不发起任何请求()
    {
        var file = new ModrinthFile
        {
            Url = _server.Url("data/evil.jar"),
            Filename = "../evil.jar",
            Hashes = new Dictionary<string, string>(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient().DownloadModAsync(file, _tempDir, CreateManager()));

        Assert.Contains("不安全", ex.Message);
        Assert.Empty(_server.Requests);
        Assert.False(File.Exists(Path.Combine(_tempDir, "..", "evil.jar")));
    }

    [Fact]
    public async Task 下载Mod_保留设备名_拒绝()
    {
        var file = new ModrinthFile
        {
            Url = _server.Url("data/CON.jar"),
            Filename = "CON.jar",
            Hashes = new Dictionary<string, string>(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient().DownloadModAsync(file, _tempDir, CreateManager()));
    }

    [Fact]
    public async Task 下载Mod_按版本数据原始URL下载_不替换域名()
    {
        var jarBytes = "PK-fake-jar-content"u8.ToArray();
        _server.Handler = req => req.Path.Contains("sodium")
            ? new MockResponse(200, "application/java-archive", jarBytes)
            : null;

        // 模拟 CDN 域名（modrinth 真实为 cdn.modrinth.com）：客户端不得改写
        var cdnUrl = _server.Url("data/sodium/versions/sodium-0.6.0.jar");
        var file = new ModrinthFile
        {
            Url = cdnUrl,
            Filename = "sodium-0.6.0.jar",
            Size = jarBytes.Length,
            Hashes = new Dictionary<string, string>(),
        };

        var dest = await CreateClient().DownloadModAsync(file, _tempDir, CreateManager());

        Assert.True(File.Exists(dest));
        Assert.Equal(jarBytes, await File.ReadAllBytesAsync(dest));
        // 请求原样打到给定 URL（host 未被换成 api.modrinth.com）
        Assert.Contains(_server.Requests, r => r.Path.Contains("sodium"));
    }

    [Fact]
    public async Task 下载Mod_sha1不匹配_失败且不留半成品()
    {
        _server.Handler = _ => new MockResponse(200, "application/java-archive", "PK-fake"u8.ToArray());

        var file = new ModrinthFile
        {
            Url = _server.Url("data/x.jar"),
            Filename = "x.jar",
            Hashes = new Dictionary<string, string> { ["sha1"] = "0".PadLeft(40, '0') },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient().DownloadModAsync(file, _tempDir, CreateManager(maxAttempts: 1)));

        Assert.False(File.Exists(Path.Combine(_tempDir, "x.jar")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "x.jar.part")));
    }
}
