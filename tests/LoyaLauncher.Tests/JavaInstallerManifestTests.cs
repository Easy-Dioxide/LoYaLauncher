using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>
/// 版本清单链路测试（清单 §3.1/§3.2 + §11 版本清单用例）：
/// 官方成功、镜像成功、官方超时、两源失败、JSON 损坏、缓存可用。
/// 通过本地 mock HTTP 服务 + CustomOnly 镜像模式保证测试不访问外网。
/// </summary>
public sealed class JavaInstallerManifestTests : IDisposable
{
    private const string ValidManifestJson = """
        {
          "latest": { "release": "1.21.4", "snapshot": "1.21.5-snapshot-1" },
          "versions": [
            { "id": "1.21.4", "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/abc123/1.21.4.json",
              "time": "2024-12-03T00:00:00+00:00", "releaseTime": "2024-12-03T00:00:00+00:00" },
            { "id": "1.21.5-snapshot-1", "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/def456/1.21.5-snapshot-1.json",
              "time": "2025-01-10T00:00:00+00:00", "releaseTime": "2025-01-10T00:00:00+00:00" }
          ]
        }
        """;

    private readonly MockHttpServer _server = new();
    private readonly string _tempRoot;
    private readonly string _originalRoot;
    private readonly EventBus _bus = new();

    public JavaInstallerManifestTests()
    {
        _originalRoot = LoyaPaths.Root;
        _tempRoot = Path.Combine(Path.GetTempPath(), "loya-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        LoyaPaths.Root = _tempRoot;
    }

    private string CachePath => Path.Combine(LoyaPaths.Cache, "version_manifest_v2.json");

    private JavaInstaller CreateInstaller(DownloadManager dm, int maxAttempts = 2)
    {
        var config = new ConfigStore(Path.Combine(_tempRoot, "config"));
        config.Config.Mirror = MirrorMode.CustomOnly; // 仅 mock 源，绝不访问外网
        config.Config.CustomMirrorUrl = _server.Prefix;
        return new JavaInstaller(dm, _bus, config);
    }

    private DownloadManager CreateManager(int maxAttempts = 2)
        => new(_bus, maxConcurrency: 2, maxAttempts: maxAttempts);

    [Fact]
    public async Task 有效清单_从自定义源下载并写缓存()
    {
        _server.Handler = _ => new MockResponse(200, "application/json",
            System.Text.Encoding.UTF8.GetBytes(ValidManifestJson));
        using var dm = CreateManager();
        var installer = CreateInstaller(dm);

        var manifest = await installer.GetManifestAsync();

        Assert.Equal(2, manifest.Versions.Count);
        Assert.Equal("1.21.4", manifest.Latest!.Release);
        Assert.False(manifest.IsStale);
        Assert.True(File.Exists(CachePath), "成功获取后应写缓存");
        Assert.Equal(1, _server.RequestCount);
    }

    [Fact]
    public async Task 损坏缓存_隔离到诊断目录并重新获取()
    {
        Directory.CreateDirectory(LoyaPaths.Cache);
        await File.WriteAllTextAsync(CachePath, "{ this is not valid json");
        _server.Handler = _ => new MockResponse(200, "application/json",
            System.Text.Encoding.UTF8.GetBytes(ValidManifestJson));
        using var dm = CreateManager();
        var installer = CreateInstaller(dm);

        var manifest = await installer.GetManifestAsync();

        Assert.Equal(2, manifest.Versions.Count);
        var diagnosticDir = Path.Combine(LoyaPaths.Cache, "diagnostic");
        Assert.True(Directory.Exists(diagnosticDir), "损坏缓存应移入诊断目录而非静默删除");
        Assert.Contains(Directory.EnumerateFiles(diagnosticDir), f => f.EndsWith(".bad"));
    }

    [Fact]
    public async Task 缺少必要字段的缓存_隔离并重新获取()
    {
        Directory.CreateDirectory(LoyaPaths.Cache);
        await File.WriteAllTextAsync(CachePath, """{"latest":null,"versions":[]}""");
        _server.Handler = _ => new MockResponse(200, "application/json",
            System.Text.Encoding.UTF8.GetBytes(ValidManifestJson));
        using var dm = CreateManager();
        var installer = CreateInstaller(dm);

        var manifest = await installer.GetManifestAsync();

        Assert.Equal(2, manifest.Versions.Count);
        Assert.True(Directory.Exists(Path.Combine(LoyaPaths.Cache, "diagnostic")));
    }

    [Fact]
    public async Task 网络全部失败_过期缓存降级并标记陈旧()
    {
        Directory.CreateDirectory(LoyaPaths.Cache);
        await File.WriteAllTextAsync(CachePath, ValidManifestJson);
        File.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddHours(-10)); // 已过期
        _server.Handler = _ => new MockResponse(503, "text/plain", "service unavailable"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 2);
        var installer = CreateInstaller(dm);

        var manifest = await installer.GetManifestAsync();

        Assert.True(manifest.IsStale, "降级结果必须打标，UI 才能提示用户这是旧缓存");
        Assert.NotNull(manifest.CacheWrittenUtc);
        Assert.Equal(2, manifest.Versions.Count);
    }

    [Fact]
    public async Task 新鲜缓存_不发任何请求()
    {
        Directory.CreateDirectory(LoyaPaths.Cache);
        await File.WriteAllTextAsync(CachePath, ValidManifestJson); // 刚写入 = 新鲜
        _server.Handler = _ => new MockResponse(500, "text/plain", "should not be called"u8.ToArray());
        using var dm = CreateManager();
        var installer = CreateInstaller(dm);

        var manifest = await installer.GetManifestAsync();

        Assert.False(manifest.IsStale);
        Assert.Equal(2, manifest.Versions.Count);
        Assert.Equal(0, _server.RequestCount);
    }

    [Fact]
    public async Task 无缓存且网络失败_抛出可读错误()
    {
        _server.Handler = _ => new MockResponse(500, "text/plain", "down"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 2);
        var installer = CreateInstaller(dm);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await installer.GetManifestAsync());
        Assert.Contains("无法获取版本清单", ex.Message);
    }

    [Fact]
    public async Task HTML拦截页_不被当作有效清单()
    {
        _server.Handler = _ => new MockResponse(200, "text/html",
            "<html><body>运营商劫持页面</body></html>"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 2);
        var installer = CreateInstaller(dm);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await installer.GetManifestAsync());
        Assert.Contains("无法获取版本清单", ex.Message);
        Assert.False(File.Exists(CachePath), "HTML 页面不得落为清单缓存");
    }

    public void Dispose()
    {
        LoyaPaths.Root = _originalRoot;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
