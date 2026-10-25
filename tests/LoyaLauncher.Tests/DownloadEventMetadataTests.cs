using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.Tests;

/// <summary>
/// 下载事件元数据测试（清单 §9 P1）：进度/完成事件携带源名称与重试次数，
/// MirrorNames 对官方源/镜像/CDN 的友好名映射。
/// </summary>
public sealed class DownloadEventMetadataTests : IAsyncDisposable
{
    private readonly MockHttpServer _server = new();
    private readonly string _tempDir;
    private readonly EventBus _bus = new();
    private readonly List<DownloadProgressEvent> _progress = [];
    private readonly List<DownloadCompletedEvent> _completed = [];

    public DownloadEventMetadataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "loya-dm-meta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _bus.Subscribe<DownloadProgressEvent>(e => _progress.Add(e));
        _bus.Subscribe<DownloadCompletedEvent>(e => _completed.Add(e));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略 */ }
    }

    private DownloadManager CreateManager(int maxAttempts = 3)
        => new(_bus, maxConcurrency: 2, maxAttempts: maxAttempts,
            headersTimeoutSeconds: 15, stallTimeoutSeconds: 30);

    private string NewDest(string name = "file.bin") => Path.Combine(_tempDir, name);

    // ======================= MirrorNames =======================

    [Theory]
    [InlineData("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", "Mojang 官方")]
    [InlineData("https://piston-data.mojang.com/v1/objects/ab/client.jar", "Mojang 官方")]
    [InlineData("https://libraries.minecraft.net/org/x/y.jar", "Mojang 官方")]
    [InlineData("https://resources.download.minecraft.net/ab/hash", "Mojang 官方")]
    [InlineData("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", "BMCLAPI")]
    [InlineData("https://api.modrinth.com/v2/search", "Modrinth")]
    [InlineData("https://cdn.modrinth.com/data/x/files/y.jar", "Modrinth")]
    [InlineData("https://api.adoptium.net/v3/info/available_releases", "Adoptium")]
    [InlineData("https://api.github.com", "GitHub")]
    [InlineData("https://objects.githubusercontent.com/x", "GitHub")]
    [InlineData("https://mirror.example.com/maven/x.jar", "mirror.example.com")]
    [InlineData("launchermeta.mojang.com", "Mojang 官方")]
    [InlineData("", "未知源")]
    [InlineData(null, "未知源")]
    public void MirrorNames_FriendlyMapping(string? input, string expected)
    {
        Assert.Equal(expected, MirrorNames.Friendly(input));
    }

    // ======================= 事件元数据 =======================

    [Fact]
    public async Task 完成事件_携带源名称()
    {
        _server.Handler = _ => new MockResponse(200, "application/octet-stream", "hello"u8.ToArray());
        using var dm = CreateManager();
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("file.bin"), dest, group: "test");

        Assert.True(result.Success, result.Error);
        var evt = Assert.Single(_completed);
        Assert.True(evt.Success);
        Assert.Equal("127.0.0.1", evt.Mirror); // 本地 mock：未识别主机返回主机名
    }

    [Fact]
    public async Task 进度事件_携带源名称与重试次数()
    {
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            return attempt < 3
                ? new MockResponse(500, "text/plain", "boom"u8.ToArray())
                : new MockResponse(200, "application/octet-stream",
                    Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), DelayBeforeBodyMs: 250);
        };
        using var dm = CreateManager(maxAttempts: 3);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("file.bin"), dest, group: "test");

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(_progress);
        // 第三次尝试才成功：进度事件应带 2 次重试
        Assert.All(_progress, e => Assert.Equal(2, e.RetryCount));
        Assert.All(_progress, e => Assert.Equal("127.0.0.1", e.Mirror));
        Assert.Contains(_progress, e => e.TotalBytes == 64);
    }

    [Fact]
    public async Task 失败事件_携带源名称与错误()
    {
        _server.Handler = _ => new MockResponse(500, "text/plain", "boom"u8.ToArray());
        using var dm = CreateManager(maxAttempts: 1);
        var dest = NewDest();

        var result = await dm.DownloadFileAsync(_server.Url("file.bin"), dest, group: "test");

        Assert.False(result.Success);
        var evt = Assert.Single(_completed);
        Assert.False(evt.Success);
        Assert.Equal("127.0.0.1", evt.Mirror);
        Assert.Contains("500", evt.Error);
    }
}
