using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Pack;

namespace LoyaLauncher.Tests;

/// <summary>
/// UpdateService 测试（清单 §8 / §11）：
/// - CheckAsync 六状态拆分：无更新 / 有更新 / 资产缺失 / 检查失败 / 限流；
/// - SHA-256：资产、SHA256SUMS、Release Notes 三种来源，匹配与不匹配路径；
/// - PrepareUpdateAsync：下载重试、校验失败删包、恶意 zip 拒绝；
/// - ApplyPendingUpdate / RollbackIfUnconfirmed / ConfirmStartup：
///   备份、回滚、启动成功确认与首次启动宽限。
/// </summary>
public sealed class UpdateServiceTests : IAsyncLifetime
{
    private readonly MockHttpServer _server = new();
    private readonly string _tempDir;
    private readonly string _originalRoot;
    private readonly string _root;
    private readonly EventBus _bus = new();

    public UpdateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "loya-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalRoot = LoyaPaths.Root;
        _root = Path.Combine(_tempDir, "root");
        LoyaPaths.Root = _root;
        LoyaPaths.EnsureDirectories();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync()
    {
        LoyaPaths.Root = _originalRoot;
        return Task.CompletedTask;
    }

    private DownloadManager CreateManager(int maxAttempts = 3)
        => new(_bus, maxConcurrency: 2, maxAttempts: maxAttempts,
            headersTimeoutSeconds: 15, stallTimeoutSeconds: 15);

    private UpdateService CreateService(int maxAttempts = 3)
        => new(CreateManager(maxAttempts), apiBaseUrl: _server.Prefix.TrimEnd('/'));

    private static string PlatformZipName => $"LoyaLauncher-{LoyaPaths.PlatformId switch
    {
        "windows" => "win",
        "mac" => "osx",
        _ => "linux",
    }}-{(LoyaPaths.ArchId == "aarch64" ? "arm64" : "x64")}.zip";

    /// <summary>构造 GitHub Release JSON；资产下载地址指向 mock 服务器</summary>
    private static string ReleaseJson(string urlPrefix, string tag, string? assetName = null,
        string? body = null, bool draft = false, string extraAssets = "")
    {
        var assetsText = "";
        if (assetName is not null)
            assetsText += $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"{urlPrefix}/{assetName}\",\"size\":1234}}";
        if (assetsText.Length > 0 && extraAssets.Length > 0)
            assetsText += ",";
        assetsText += extraAssets;
        var bodyText = body is null ? "null" : JsonSerializer.Serialize(body);
        return $"{{\"tag_name\":\"{tag}\",\"name\":\"{tag}\",\"draft\":{(draft ? "true" : "false")}," +
               $"\"prerelease\":false,\"body\":{bodyText},\"assets\":[{assetsText}]}}";
    }

    // ======================= CheckAsync：状态拆分 =======================

    [Fact]
    public async Task 检查更新_有新版本和资产_返回UpdateAvailable()
    {
        var current = UpdateService.CurrentVersion;
        var higher = "999.0.0";
        Assert.True(UpdateService.CompareVersions(higher, current) > 0, "测试版本号必须高于当前版本");

        _server.Handler = _ => new MockResponse(200, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "v999.0.0", PlatformZipName, body: "release notes")));

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, info.Status);
        Assert.True(info.HasUpdate);
        Assert.Equal("999.0.0", info.LatestVersion);
        Assert.NotNull(info.DownloadUrl);
        Assert.Null(info.Error);
        Assert.Null(info.ExpectedSha256); // 发布方未提供 → 走警告路径
    }

    [Fact]
    public async Task 检查更新_无新版本_返回UpToDate()
    {
        var current = UpdateService.CurrentVersion;
        var lower = "0.0.1";
        Assert.True(UpdateService.CompareVersions(lower, current) <= 0, "测试版本号必须不高于当前版本");

        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "v0.0.1")));

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.UpToDate, info.Status);
        Assert.False(info.HasUpdate);
        Assert.Null(info.Error);
    }

    [Fact]
    public async Task 检查更新_草稿Release_返回UpToDate()
    {
        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "v999.0.0", PlatformZipName, draft: true)));

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.UpToDate, info.Status);
    }

    [Fact]
    public async Task 检查更新_有更新但无安装资产_返回AssetMissing()
    {
        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "v999.0.0", "LoyaLauncher-src-0.7.1.zip")));

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.AssetMissing, info.Status);
        Assert.True(info.HasUpdate);
        Assert.Contains("999.0.0", info.Error);
    }

    [Fact]
    public async Task 检查更新_API限流403_返回RateLimited()
    {
        _server.Handler = _ => new MockResponse(403, "application/json",
            Encoding.UTF8.GetBytes("{\"message\":\"API rate limit exceeded\"}"));

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.RateLimited, info.Status);
        Assert.False(info.HasUpdate);
        Assert.Contains("403", info.Error);
    }

    [Fact]
    public async Task 检查更新_API限流429_返回RateLimited()
    {
        _server.Handler = _ => new MockResponse(429, "text/plain", "too many requests"u8.ToArray());

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.RateLimited, info.Status);
    }

    [Fact]
    public async Task 检查更新_500后恢复_经重试成功()
    {
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            return attempt < 2
                ? new MockResponse(500, "text/plain", "boom"u8.ToArray())
                : new MockResponse(200, "application/json",
                    Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "v999.0.0", PlatformZipName)));
        };

        var info = await CreateService(maxAttempts: 3).CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, info.Status);
        Assert.Equal(2, attempt); // 证明发生了重试而不是一次失败就返回
    }

    [Fact]
    public async Task 检查更新_非JSON响应_返回CheckFailed()
    {
        _server.Handler = _ => new MockResponse(200, "text/html", "<html>login</html>"u8.ToArray());

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.CheckFailed, info.Status);
        Assert.Contains("JSON", info.Error);
    }

    [Fact]
    public async Task 检查更新_tag为空_返回CheckFailed()
    {
        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "")));

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.CheckFailed, info.Status);
    }

    // ======================= CheckAsync：SHA-256 来源 =======================

    [Fact]
    public async Task 检查更新_sha256资产_提取期望哈希()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData("fake-zip"u8.ToArray()));
        var shaAssetName = PlatformZipName + ".sha256";

        _server.Handler = req =>
        {
            if (req.Path.EndsWith(shaAssetName, StringComparison.Ordinal))
                return new MockResponse(200, "text/plain",
                    Encoding.UTF8.GetBytes($"{expected}  {PlatformZipName}\n"));
            if (req.Path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return new MockResponse(200, "application/json", Encoding.UTF8.GetBytes(ReleaseJson(
                    _server.Prefix.TrimEnd('/'), "v999.0.0", PlatformZipName,
                    extraAssets: $"{{\"name\":\"{shaAssetName}\",\"browser_download_url\":\"{_server.Url(shaAssetName)}\",\"size\":80}}")));
            return null;
        };

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, info.Status);
        Assert.Equal(expected, info.ExpectedSha256);
        Assert.Null(info.Error); // 有期望值时不带警告
    }

    [Fact]
    public async Task 检查更新_SHA256SUMS多文件_只取匹配行()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData("fake-zip"u8.ToArray()));
        var other = Convert.ToHexStringLower(SHA256.HashData("other"u8.ToArray()));

        _server.Handler = req =>
        {
            if (req.Path.EndsWith("/SHA256SUMS", StringComparison.Ordinal))
                return new MockResponse(200, "text/plain", Encoding.UTF8.GetBytes(
                    $"{other}  LoyaLauncher-other.zip\n{expected}  {PlatformZipName}\n"));
            if (req.Path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return new MockResponse(200, "application/json", Encoding.UTF8.GetBytes(ReleaseJson(
                    _server.Prefix.TrimEnd('/'), "v999.0.0", PlatformZipName,
                    extraAssets: $"{{\"name\":\"SHA256SUMS\",\"browser_download_url\":\"{_server.Url("SHA256SUMS")}\",\"size\":120}}")));
            return null;
        };

        var info = await CreateService().CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, info.Status);
        Assert.Equal(expected, info.ExpectedSha256);
    }

    [Fact]
    public async Task 检查更新_ReleaseNotes含唯一哈希_提取()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData("notes"u8.ToArray()));

        _server.Handler = _ => new MockResponse(200, "application/json",
            Encoding.UTF8.GetBytes(ReleaseJson(_server.Prefix.TrimEnd('/'), "v999.0.0", PlatformZipName,
                body: $"## 更新\n\nSHA256: {expected}\n")));

        var info = await CreateService().CheckAsync();

        Assert.Equal(expected, info.ExpectedSha256);
    }

    // ======================= PrepareUpdateAsync =======================

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task 准备更新_下载校验通过_写pending标记并清理安装包()
    {
        var zipBytes = MakeZip(("LoyaLauncher.dll", "new-dll"), ("sub/file.txt", "new-txt"));
        var expected = Convert.ToHexStringLower(SHA256.HashData(zipBytes));

        _server.Handler = req =>
        {
            if (req.Path.Contains(PlatformZipName, StringComparison.Ordinal))
                return new MockResponse(200, "application/zip", zipBytes);
            return null;
        };

        var info = new UpdateInfo(true, "0.7.0", "0.8.0",
            _server.Url(PlatformZipName), zipBytes.Length,
            expectedSha256: expected);

        var result = await CreateService().PrepareUpdateAsync(info);

        Assert.Equal(UpdateStatus.Prepared, result.Status);
        Assert.NotNull(result.StagingDir);
        Assert.True(Directory.Exists(result.StagingDir));
        Assert.Equal("new-dll", await File.ReadAllTextAsync(Path.Combine(result.StagingDir, "LoyaLauncher.dll")));
        Assert.Equal("new-txt", await File.ReadAllTextAsync(Path.Combine(result.StagingDir, "sub", "file.txt")));

        // pending 标记写入且含校验信息
        var markerPath = Path.Combine(LoyaPaths.Root, "updates", "pending.json");
        Assert.True(File.Exists(markerPath));
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
        Assert.Equal("0.8.0", marker.RootElement.GetProperty("Version").GetString());
        Assert.True(marker.RootElement.GetProperty("Sha256Verified").GetBoolean());
        Assert.Equal(expected, marker.RootElement.GetProperty("Sha256").GetString());

        // 安装包被清理，不留在 cache
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Cache, "LoyaLauncher-0.8.0.zip")));
    }

    [Fact]
    public async Task 准备更新_sha256不匹配_返回VerifyFailed并删除安装包()
    {
        var zipBytes = MakeZip(("LoyaLauncher.dll", "tampered"));
        var wrong = Convert.ToHexStringLower(SHA256.HashData("expected"u8.ToArray()));

        _server.Handler = _ => new MockResponse(200, "application/zip", zipBytes);

        var info = new UpdateInfo(true, "0.7.0", "0.8.0",
            _server.Url(PlatformZipName), zipBytes.Length, expectedSha256: wrong);

        var result = await CreateService().PrepareUpdateAsync(info);

        Assert.Equal(UpdateStatus.VerifyFailed, result.Status);
        Assert.Contains("校验失败", result.Error);
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Cache, "LoyaLauncher-0.8.0.zip")));
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Root, "updates", "pending.json")));
    }

    [Fact]
    public async Task 准备更新_发布方未提供sha_带警告但就绪()
    {
        var zipBytes = MakeZip(("LoyaLauncher.dll", "new"));

        _server.Handler = _ => new MockResponse(200, "application/zip", zipBytes);

        var info = new UpdateInfo(true, "0.7.0", "0.8.0", _server.Url(PlatformZipName), zipBytes.Length);

        var result = await CreateService().PrepareUpdateAsync(info);

        Assert.Equal(UpdateStatus.Prepared, result.Status);
        Assert.Contains("SHA-256", result.Warning);
        // pending 标记记录"未校验"
        var markerPath = Path.Combine(LoyaPaths.Root, "updates", "pending.json");
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
        Assert.False(marker.RootElement.GetProperty("Sha256Verified").GetBoolean());
    }

    [Fact]
    public async Task 准备更新_下载404_返回DownloadFailed()
    {
        _server.Handler = _ => new MockResponse(404, "text/plain", "not found"u8.ToArray());

        var info = new UpdateInfo(true, "0.7.0", "0.8.0", _server.Url(PlatformZipName), 100);

        var result = await CreateService(maxAttempts: 1).PrepareUpdateAsync(info);

        Assert.Equal(UpdateStatus.DownloadFailed, result.Status);
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Root, "updates", "pending.json")));
    }

    [Fact]
    public async Task 准备更新_恶意zip目录穿越_返回PrepareFailed且不落盘()
    {
        byte[] evilZip;
        var evilTarget = Path.Combine(_tempDir, "escaped-update.txt");
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("../escaped-update.txt");
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write("pwned");
            }
            evilZip = ms.ToArray();
        }

        _server.Handler = _ => new MockResponse(200, "application/zip", evilZip);

        var info = new UpdateInfo(true, "0.7.0", "0.8.0", _server.Url(PlatformZipName), evilZip.Length);

        var result = await CreateService().PrepareUpdateAsync(info);

        Assert.Equal(UpdateStatus.PrepareFailed, result.Status);
        Assert.False(File.Exists(evilTarget), "恶意条目不得越出暂存目录");
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Root, "updates", "pending.json")));
    }

    [Fact]
    public async Task 准备更新_含符号链接的zip_链接条目被跳过不影响就绪()
    {
        byte[] zipWithLink;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var good = zip.CreateEntry("LoyaLauncher.dll");
                using (var w = new StreamWriter(good.Open(), Encoding.UTF8)) w.Write("ok");

                var link = zip.CreateEntry("evil-link");
                // Unix mode S_IFLNK (0xA000) | 0777，位于 external_attributes 高 16 位
                link.ExternalAttributes = unchecked((int)(0xA1FF << 16));
                using (var w = new StreamWriter(link.Open(), Encoding.UTF8)) w.Write("/etc/passwd");
            }
            zipWithLink = ms.ToArray();
        }

        _server.Handler = _ => new MockResponse(200, "application/zip", zipWithLink);

        var info = new UpdateInfo(true, "0.7.0", "0.8.0", _server.Url(PlatformZipName), zipWithLink.Length);

        var result = await CreateService().PrepareUpdateAsync(info);

        Assert.Equal(UpdateStatus.Prepared, result.Status);
        Assert.True(File.Exists(Path.Combine(result.StagingDir!, "LoyaLauncher.dll")));
        Assert.False(File.Exists(Path.Combine(result.StagingDir!, "evil-link")), "符号链接条目必须被跳过");
    }

    // ======================= Apply / Rollback / Confirm =======================

    private static void WriteMarker(string dir, object marker)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pending.json"),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void 应用更新_替换文件_写备份与applied标记并清除pending()
    {
        var staging = Path.Combine(LoyaPaths.Root, "updates", "0.8.0");
        Directory.CreateDirectory(Path.Combine(staging, "sub"));
        File.WriteAllText(Path.Combine(staging, "LoyaLauncher.dll"), "new-dll");
        File.WriteAllText(Path.Combine(staging, "sub", "x.txt"), "new-x");

        var appDir = Path.Combine(_tempDir, "app");
        Directory.CreateDirectory(Path.Combine(appDir, "sub"));
        File.WriteAllText(Path.Combine(appDir, "LoyaLauncher.dll"), "old-dll");
        File.WriteAllText(Path.Combine(appDir, "sub", "x.txt"), "old-x");
        File.WriteAllText(Path.Combine(appDir, "keep.txt"), "keep");

        WriteMarker(Path.Combine(LoyaPaths.Root, "updates"),
            new { Version = "0.8.0", SourceDir = staging, PreparedAt = DateTime.UtcNow });

        var applied = UpdateService.ApplyPendingUpdate(appDir);

        Assert.True(applied);
        Assert.Equal("new-dll", File.ReadAllText(Path.Combine(appDir, "LoyaLauncher.dll")));
        Assert.Equal("new-x", File.ReadAllText(Path.Combine(appDir, "sub", "x.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(appDir, "keep.txt")));

        // pending 已清除，applied 标记已写入
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Root, "updates", "pending.json")));
        var appliedPath = Path.Combine(LoyaPaths.Root, "updates", "applied.json");
        Assert.True(File.Exists(appliedPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(appliedPath));
        Assert.True(doc.RootElement.GetProperty("AwaitingFirstStart").GetBoolean());

        // 备份目录保存了旧版本文件
        var backupDir = doc.RootElement.GetProperty("BackupDir").GetString()!;
        Assert.True(Directory.Exists(backupDir));
        Assert.Equal("old-dll", File.ReadAllText(Path.Combine(backupDir, "LoyaLauncher.dll")));
        Assert.Equal("old-x", File.ReadAllText(Path.Combine(backupDir, "sub", "x.txt")));
    }

    [Fact]
    public void 应用更新_pending标记SourceDir越界_拒绝执行()
    {
        var evilSource = Path.Combine(_tempDir, "evil-source");
        Directory.CreateDirectory(evilSource);
        File.WriteAllText(Path.Combine(evilSource, "evil.txt"), "pwned");

        var appDir = Path.Combine(_tempDir, "app-guarded");
        Directory.CreateDirectory(appDir);

        WriteMarker(Path.Combine(LoyaPaths.Root, "updates"),
            new { Version = "9.9.9", SourceDir = evilSource, PreparedAt = DateTime.UtcNow });

        var applied = UpdateService.ApplyPendingUpdate(appDir);

        Assert.False(applied);
        Assert.False(File.Exists(Path.Combine(appDir, "evil.txt")), "越界 SourceDir 不得被复制进应用目录");
    }

    [Fact]
    public void 应用更新_无pending标记_返回false()
    {
        Assert.False(UpdateService.ApplyPendingUpdate(_tempDir));
    }

    [Fact]
    public void 回滚检查_首次启动放行_再次启动则回滚旧版本()
    {
        var staging = Path.Combine(LoyaPaths.Root, "updates", "0.8.0");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "LoyaLauncher.dll"), "new-dll");

        var appDir = Path.Combine(_tempDir, "app-rollback");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "LoyaLauncher.dll"), "old-dll");

        WriteMarker(Path.Combine(LoyaPaths.Root, "updates"),
            new { Version = "0.8.0", SourceDir = staging, PreparedAt = DateTime.UtcNow });

        Assert.True(UpdateService.ApplyPendingUpdate(appDir));
        Assert.Equal("new-dll", File.ReadAllText(Path.Combine(appDir, "LoyaLauncher.dll")));

        // 第一次启动（应用更新后的重启）：放行，不滚
        Assert.False(UpdateService.RollbackIfUnconfirmed(appDir));
        Assert.Equal("new-dll", File.ReadAllText(Path.Combine(appDir, "LoyaLauncher.dll")));

        // 第二次启动仍未确认：回滚到旧版本
        Assert.True(UpdateService.RollbackIfUnconfirmed(appDir));
        Assert.Equal("old-dll", File.ReadAllText(Path.Combine(appDir, "LoyaLauncher.dll")));

        // 回滚后标记与备份均已清理，不会重复回滚
        Assert.False(UpdateService.RollbackIfUnconfirmed(appDir));
        Assert.False(File.Exists(Path.Combine(LoyaPaths.Root, "updates", "applied.json")));
    }

    [Fact]
    public void 启动确认_清理applied标记与备份目录()
    {
        var staging = Path.Combine(LoyaPaths.Root, "updates", "0.8.0");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "LoyaLauncher.dll"), "new-dll");

        var appDir = Path.Combine(_tempDir, "app-confirm");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "LoyaLauncher.dll"), "old-dll");

        WriteMarker(Path.Combine(LoyaPaths.Root, "updates"),
            new { Version = "0.8.0", SourceDir = staging, PreparedAt = DateTime.UtcNow });

        Assert.True(UpdateService.ApplyPendingUpdate(appDir));
        var appliedPath = Path.Combine(LoyaPaths.Root, "updates", "applied.json");
        string backupDir;
        using (var doc = JsonDocument.Parse(File.ReadAllText(appliedPath)))
            backupDir = doc.RootElement.GetProperty("BackupDir").GetString()!;
        Assert.True(Directory.Exists(backupDir));

        UpdateService.ConfirmStartup();

        Assert.False(File.Exists(appliedPath));
        Assert.False(Directory.Exists(backupDir), "确认后备份必须清理");
        // 应用后的新文件保留
        Assert.Equal("new-dll", File.ReadAllText(Path.Combine(appDir, "LoyaLauncher.dll")));
    }

    // ======================= 纯函数 =======================

    [Theory]
    [InlineData("0.7.1", "0.7.0", 1)]
    [InlineData("0.7.0", "0.7.1", -1)]
    [InlineData("0.7.0", "0.7.0", 0)]
    [InlineData("v0.8.0", "0.7.10", 1)]
    [InlineData("0.7.10", "0.7.9", 1)]
    [InlineData("1.0", "0.9.9", 1)]
    [InlineData("0.8.0-rc.1", "0.7.0", 1)]
    [InlineData("0.7", "0.7.0", 0)]
    [InlineData("0.7.x", "0.7.0", 0)]
    public void 版本比较_语义化(string a, string b, int expectedSign)
    {
        var cmp = UpdateService.CompareVersions(a, b);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public void 资产选择_平台rid优先_排除源码包_回退任意zip()
    {
        var assets = new List<LoyaLauncher.Pack.GitHubAsset>
        {
            new() { Name = "LoyaLauncher-src-0.7.1.zip", BrowserDownloadUrl = "https://x/src.zip", Size = 1 },
            new() { Name = "LoyaLauncher-win-x64.zip", BrowserDownloadUrl = "https://x/win.zip", Size = 2 },
            new() { Name = "LoyaLauncher-linux-x64.zip", BrowserDownloadUrl = "https://x/linux.zip", Size = 3 },
        };
        // 补充当前平台资产：linux/win 已在列表中；mac 需要显式加入才能走到精确匹配分支
        // （原先断言写死 "linux-x64"，在 macOS 上精确匹配不存在、回退到 win 包即失败）
        if (!assets.Any(a => string.Equals(a.Name, PlatformZipName, StringComparison.OrdinalIgnoreCase)))
            assets.Add(new() { Name = PlatformZipName, BrowserDownloadUrl = "https://x/current.zip", Size = 4 });

        var picked = UpdateService.PickAssetForPlatform(assets);

        // 当前平台 rid 精确匹配，而不是回退到任意 zip
        Assert.NotNull(picked);
        Assert.Equal(PlatformZipName, picked.Name);

        // 无精确匹配时回退到任意可安装 zip，且排除源码包。
        // 构造不含当前平台资产的列表：若本机就是 mac arm64，
        // 用 linux 包充当"其他平台"（否则 osx-arm64 会精确匹配，走不到回退分支）。
        var otherPlatformZip = PlatformZipName == "LoyaLauncher-osx-arm64.zip"
            ? "LoyaLauncher-linux-x64.zip"
            : "LoyaLauncher-osx-arm64.zip";
        var fallback = UpdateService.PickAssetForPlatform(new List<LoyaLauncher.Pack.GitHubAsset>
        {
            new() { Name = "LoyaLauncher-src-0.7.1.zip", BrowserDownloadUrl = "https://x/src.zip", Size = 1 },
            new() { Name = otherPlatformZip, BrowserDownloadUrl = "https://x/mac.zip", Size = 2 },
        });
        Assert.NotNull(fallback);
        Assert.DoesNotContain("-src-", fallback.Name);
        Assert.Equal(otherPlatformZip, fallback.Name);

        Assert.Null(UpdateService.PickAssetForPlatform(new List<LoyaLauncher.Pack.GitHubAsset>()));
    }

    [Fact]
    public void 解析校验文本_单文件与多文件格式()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData("x"u8.ToArray()));

        // 纯 hash
        Assert.Equal(hash, UpdateService.ParseChecksumText(hash + "\n", "a.zip"));
        // "<hash>  <name>"
        Assert.Equal(hash, UpdateService.ParseChecksumText($"{hash}  a.zip\n", "a.zip"));
        // SHA256SUMS：其他文件的行必须被忽略
        var other = Convert.ToHexStringLower(SHA256.HashData("y"u8.ToArray()));
        Assert.Equal(hash, UpdateService.ParseChecksumText($"{other}  b.zip\n{hash}  a.zip\n", "a.zip"));
        Assert.Null(UpdateService.ParseChecksumText($"{other}  b.zip\n", "a.zip"));
        // 非十六进制内容
        Assert.Null(UpdateService.ParseChecksumText("garbage\n", "a.zip"));
    }

    [Fact]
    public void 提取ReleaseNotes哈希_唯一时采用_多个且无名则不采用()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData("x"u8.ToArray()));
        var other = Convert.ToHexStringLower(SHA256.HashData("y"u8.ToArray()));

        Assert.Equal(hash, UpdateService.ExtractShaFromNotes($"sha256: {hash}", "a.zip"));
        Assert.Equal(hash, UpdateService.ExtractShaFromNotes(
            $"first {other}\nthen a.zip = {hash}\n", "a.zip"));
        Assert.Null(UpdateService.ExtractShaFromNotes($"{other} and {hash}", "a.zip"));
        Assert.Null(UpdateService.ExtractShaFromNotes(null, "a.zip"));
        Assert.Null(UpdateService.ExtractShaFromNotes("", "a.zip"));
    }

    [Fact]
    public async Task 计算SHA256_已知向量()
    {
        var path = Path.Combine(_tempDir, "empty.bin");
        await File.WriteAllTextAsync(path, "");

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            await UpdateService.ComputeSha256Async(path));
    }

    [Fact]
    public void CurrentVersion_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(UpdateService.CurrentVersion));
    }
}
