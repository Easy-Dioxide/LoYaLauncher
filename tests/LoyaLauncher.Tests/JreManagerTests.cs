using LoyaLauncher.Core;
using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>
/// JreManager 托管运行时列表/删除测试。
/// 通过重定向 LoyaPaths.Root 到临时目录隔离数据，不碰真实 ~/.loya。
/// </summary>
public class JreManagerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _originalRoot;

    public JreManagerTests()
    {
        _originalRoot = LoyaPaths.Root;
        _tempRoot = Path.Combine(Path.GetTempPath(), "loya-java-" + Guid.NewGuid().ToString("N")[..8]);
        LoyaPaths.Root = _tempRoot;
        LoyaPaths.EnsureDirectories();
    }

    public void Dispose()
    {
        LoyaPaths.Root = _originalRoot;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    /// <summary>创建假 JRE 目录：{root}/java/java{major}/{innerName}/bin/java[.exe] + release</summary>
    private static string CreateFakeJre(int major, string innerName = "jdk-17.0.2+8", string? releaseVersion = null)
    {
        var inner = Path.Combine(LoyaPaths.JavaRuntimes, $"java{major}", innerName);
        var bin = Path.Combine(inner, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, LoyaPaths.JavaExecutableName);
        File.WriteAllText(exe, "fake-java-binary");
        if (releaseVersion is not null)
            File.WriteAllText(Path.Combine(inner, "release"), $"JAVA_VERSION=\"{releaseVersion}\"\nOS_NAME=\"Linux\"\n");
        return inner;
    }

    private static JreManager CreateManager() =>
        new(new Downloader.DownloadManager(new Core.EventBus()));

    [Fact]
    public void ListManagedRuntimes_Empty_WhenNothingInstalled()
    {
        var runtimes = CreateManager().ListManagedRuntimes();
        Assert.Empty(runtimes);
    }

    [Fact]
    public void ListManagedRuntimes_ReadsVersionFromReleaseFile()
    {
        CreateFakeJre(17, releaseVersion: "17.0.2");
        var runtimes = CreateManager().ListManagedRuntimes();

        var runtime = Assert.Single(runtimes);
        Assert.Equal(17, runtime.Major);
        Assert.Equal("17.0.2", runtime.Version);
        Assert.True(runtime.SizeBytes > 0);
        Assert.EndsWith(LoyaPaths.JavaExecutableName, runtime.Path);
    }

    [Fact]
    public void ListManagedRuntimes_FallsBackToDirectoryName_WhenNoReleaseFile()
    {
        CreateFakeJre(21, innerName: "jdk-21.0.1+12");
        var runtimes = CreateManager().ListManagedRuntimes();

        var runtime = Assert.Single(runtimes);
        Assert.Equal(21, runtime.Major);
        Assert.Equal("21.0.1+12", runtime.Version);
    }

    [Fact]
    public void ListManagedRuntimes_SkipsBrokenDirectory_WithoutExecutable()
    {
        // 下载中断：目录存在但无可执行文件
        Directory.CreateDirectory(Path.Combine(LoyaPaths.JavaRuntimes, "java17", "bin"));
        var runtimes = CreateManager().ListManagedRuntimes();
        Assert.Empty(runtimes);
    }

    [Fact]
    public void ListManagedRuntimes_IgnoresNonJavaDirectories()
    {
        Directory.CreateDirectory(Path.Combine(LoyaPaths.JavaRuntimes, "temp"));
        File.WriteAllText(Path.Combine(LoyaPaths.JavaRuntimes, "loose-file.txt"), "x");
        var runtimes = CreateManager().ListManagedRuntimes();
        Assert.Empty(runtimes);
    }

    [Fact]
    public void ListManagedRuntimes_SortedByMajorAscending()
    {
        CreateFakeJre(21, innerName: "jdk-21");
        CreateFakeJre(8, innerName: "jre8");
        CreateFakeJre(17, innerName: "jdk-17");

        var runtimes = CreateManager().ListManagedRuntimes();
        Assert.Equal(new[] { 8, 17, 21 }, runtimes.Select(r => r.Major));
    }

    [Fact]
    public void RemoveManagedRuntime_RemovesExistingDirectory()
    {
        CreateFakeJre(17);
        var result = CreateManager().RemoveManagedRuntime(17);

        Assert.Equal(RemoveRuntimeResult.Removed, result);
        Assert.False(Directory.Exists(Path.Combine(LoyaPaths.JavaRuntimes, "java17")));
    }

    [Fact]
    public void RemoveManagedRuntime_ReturnsNotFound_WhenAbsent()
    {
        var result = CreateManager().RemoveManagedRuntime(17);
        Assert.Equal(RemoveRuntimeResult.NotFound, result);
    }

    [Fact]
    public void RemoveManagedRuntime_RejectsWhenInUse_AndKeepsDirectory()
    {
        CreateFakeJre(17);
        using var lease = JavaUsageRegistry.Acquire(17);

        var result = CreateManager().RemoveManagedRuntime(17);

        Assert.Equal(RemoveRuntimeResult.InUse, result);
        Assert.True(Directory.Exists(Path.Combine(LoyaPaths.JavaRuntimes, "java17")));
    }

    [Fact]
    public void RemoveManagedRuntime_SucceedsAfterLeaseDisposed()
    {
        CreateFakeJre(17);
        using (JavaUsageRegistry.Acquire(17)) { /* 占用 */ }

        var result = CreateManager().RemoveManagedRuntime(17);
        Assert.Equal(RemoveRuntimeResult.Removed, result);
    }
}

/// <summary>占用登记表单元测试</summary>
public class JavaUsageRegistryTests
{
    [Fact]
    public void IsInUse_TracksAcquireAndRelease()
    {
        Assert.False(JavaUsageRegistry.IsInUse(17));
        using (JavaUsageRegistry.Acquire(17))
        {
            Assert.True(JavaUsageRegistry.IsInUse(17));
        }
        Assert.False(JavaUsageRegistry.IsInUse(17));
    }

    [Fact]
    public void ConcurrentLeaves_ReleaseOneKeepsOtherAlive()
    {
        var first = JavaUsageRegistry.Acquire(17);
        var second = JavaUsageRegistry.Acquire(17);

        first.Dispose();
        Assert.True(JavaUsageRegistry.IsInUse(17)); // 第二个租约仍在

        second.Dispose();
        Assert.False(JavaUsageRegistry.IsInUse(17));
    }
}
