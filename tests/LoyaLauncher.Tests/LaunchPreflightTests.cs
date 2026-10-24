using LoyaLauncher.Core;
using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>
/// 启动前自检测试。磁盘/内存/系统 Java 探测全部注入假实现，保证确定性。
/// 通过重定向 LoyaPaths.Root 到临时目录隔离数据。
/// </summary>
public class LaunchPreflightTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly string _tempRoot;
    private readonly string _originalRoot;

    public LaunchPreflightTests()
    {
        _originalRoot = LoyaPaths.Root;
        _tempRoot = Path.Combine(Path.GetTempPath(), "loya-preflight-" + Guid.NewGuid().ToString("N")[..8]);
        LoyaPaths.Root = _tempRoot;
        LoyaPaths.EnsureDirectories();
    }

    public void Dispose()
    {
        LoyaPaths.Root = _originalRoot;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    /// <summary>创建"已安装"的假版本（loya.json + {id}.json）</summary>
    private static void CreateInstalledVersion(string versionId = "1.21.4", int requiredJava = 21)
    {
        var dir = Path.Combine(LoyaPaths.Versions, versionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "loya.json"), "{}");
        File.WriteAllText(
            Path.Combine(dir, versionId + ".json"),
            "{\"id\":\"" + versionId + "\",\"mainClass\":\"net.minecraft.client.main.Main\"," +
            "\"javaVersion\":{\"component\":\"jre-legacy\",\"majorVersion\":" + requiredJava + "}}");
    }

    /// <summary>创建假托管 JRE</summary>
    private static void CreateManagedJava(int major)
    {
        var bin = Path.Combine(LoyaPaths.JavaRuntimes, $"java{major}", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, LoyaPaths.JavaExecutableName), "fake-java");
    }

    private static LaunchPreflight CreatePreflight(
        long? diskBytes = 100 * Gb,
        long? memoryBytes = null,
        (string? Path, int Major)? systemJava = null)
    {
        var installer = new JavaInstaller(
            new Downloader.DownloadManager(new EventBus()), new EventBus(), ConfigStore.Instance);
        var jre = new JreManager(new Downloader.DownloadManager(new EventBus()));
        return new LaunchPreflight(
            installer, jre,
            diskProbe: _ => diskBytes,
            memoryProbe: () => memoryBytes,
            systemJavaProbe: _ => Task.FromResult(systemJava ?? ((string?)null, 0)));
    }

    private static GameInstance CreateInstance(string versionId = "1.21.4") => new()
    {
        Id = "test-instance",
        Name = "测试实例",
        Edition = GameEdition.Java,
        VersionId = versionId,
    };

    // ---------- 版本检查 ----------

    [Fact]
    public async Task NoVersion_BlocksWithError()
    {
        var preflight = CreatePreflight();
        var report = await preflight.CheckAsync(CreateInstance(versionId: ""), 4096);

        Assert.False(report.CanLaunch);
        Assert.Equal(PreflightSeverity.Error, report.Level);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.NoVersion");
    }

    [Fact]
    public async Task VersionNotInstalled_IsInfo_DoesNotBlock()
    {
        var preflight = CreatePreflight();
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.VersionNotInstalled");
        Assert.DoesNotContain(report.Issues, i => i.BlocksLaunch);
    }

    // ---------- Java 检查 ----------

    [Fact]
    public async Task ManagedJava_ReportsInfo()
    {
        CreateInstalledVersion(requiredJava: 21);
        CreateManagedJava(21);
        var preflight = CreatePreflight();

        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.JavaManaged" && (int)i.Args[0]! == 21);
        Assert.DoesNotContain(report.Issues, i => i.Code is "Preflight.JavaSystem" or "Preflight.JavaWillDownload");
    }

    [Fact]
    public async Task SystemJavaSatisfies_ReportsInfo()
    {
        CreateInstalledVersion(requiredJava: 21);
        var preflight = CreatePreflight(systemJava: ("/usr/bin/java", 21));

        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.Contains(report.Issues, i => i.Code == "Preflight.JavaSystem");
        Assert.DoesNotContain(report.Issues, i => i.Code == "Preflight.JavaWillDownload");
    }

    [Fact]
    public async Task NoJavaAtAll_ReportsWillDownload_DoesNotBlock()
    {
        CreateInstalledVersion(requiredJava: 21);
        var preflight = CreatePreflight(systemJava: (null, 0));

        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.JavaWillDownload");
    }

    [Fact]
    public async Task SystemJavaTooOld_ReportsWillDownload()
    {
        CreateInstalledVersion(requiredJava: 21);
        var preflight = CreatePreflight(systemJava: ("/usr/bin/java", 8));

        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.Contains(report.Issues, i => i.Code == "Preflight.JavaWillDownload");
    }

    // ---------- 磁盘检查 ----------

    [Fact]
    public async Task DiskBelowErrorThreshold_Blocks()
    {
        var preflight = CreatePreflight(diskBytes: 500 * 1024 * 1024); // 0.5GB
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.False(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.DiskCritical");
    }

    [Fact]
    public async Task DiskBelowWarnThreshold_WarnsOnly()
    {
        var preflight = CreatePreflight(diskBytes: 2 * Gb);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.DiskLow" && i.Severity == PreflightSeverity.Warning);
    }

    [Fact]
    public async Task DiskSufficient_NoDiskIssue()
    {
        var preflight = CreatePreflight(diskBytes: 100 * Gb);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("Preflight.Disk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiskProbeFails_SkipsCheckGracefully()
    {
        var preflight = CreatePreflight(diskBytes: null);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("Preflight.Disk", StringComparison.Ordinal));
    }

    // ---------- 内存检查 ----------

    [Fact]
    public async Task HeapEqualsPhysicalMemory_Blocks()
    {
        var preflight = CreatePreflight(memoryBytes: 4096L * 1024 * 1024);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.False(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.MemoryExceeds");
    }

    [Fact]
    public async Task HeapExceedsPhysicalMemory_Blocks()
    {
        var preflight = CreatePreflight(memoryBytes: 3000L * 1024 * 1024);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.False(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.MemoryExceeds");
    }

    [Fact]
    public async Task MemoryReserveBelow2Gb_WarnsOnly()
    {
        var preflight = CreatePreflight(memoryBytes: 5000L * 1024 * 1024); // 余 904MB
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.MemoryTight" && i.Severity == PreflightSeverity.Warning);
    }

    [Fact]
    public async Task MemorySufficient_NoMemoryIssue()
    {
        var preflight = CreatePreflight(memoryBytes: 16L * 1024 * 1024 * 1024);
        var report = await preflight.CheckAsync(CreateInstance(), 2048);

        Assert.True(report.CanLaunch);
        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("Preflight.Memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemoryProbeFails_SkipsCheckGracefully()
    {
        var preflight = CreatePreflight(memoryBytes: null);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.True(report.CanLaunch);
        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("Preflight.Memory", StringComparison.Ordinal));
    }

    // ---------- 报告聚合 ----------

    [Fact]
    public async Task AllHealthy_ReportLevelIsInfoWithJavaHint()
    {
        CreateInstalledVersion(requiredJava: 21);
        CreateManagedJava(21);
        var preflight = CreatePreflight(diskBytes: 100 * Gb, memoryBytes: 16L * 1024 * 1024 * 1024);

        var report = await preflight.CheckAsync(CreateInstance(), 2048);

        Assert.True(report.CanLaunch);
        // 仅剩 JavaManaged 提示，无阻断/警告
        Assert.Equal(PreflightSeverity.Info, report.Level);
        Assert.Single(report.Issues);
        Assert.DoesNotContain(report.Issues, i => i.Severity is PreflightSeverity.Warning or PreflightSeverity.Error);
    }

    [Fact]
    public async Task MultipleIssues_AggregatedWithHighestLevel()
    {
        var preflight = CreatePreflight(diskBytes: 500 * 1024 * 1024, memoryBytes: 3000L * 1024 * 1024);
        var report = await preflight.CheckAsync(CreateInstance(), 4096);

        Assert.False(report.CanLaunch);
        Assert.Equal(PreflightSeverity.Error, report.Level);
        Assert.Contains(report.Issues, i => i.Code == "Preflight.DiskCritical");
        Assert.Contains(report.Issues, i => i.Code == "Preflight.MemoryExceeds");
    }
}
