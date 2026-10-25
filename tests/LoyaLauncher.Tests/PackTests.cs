using System.IO.Compression;
using LoyaLauncher.Core;
using LoyaLauncher.Java;
using LoyaLauncher.Pack;

namespace LoyaLauncher.Tests;

/// <summary>
/// 整合包测试：导出 → 导入 round-trip。
/// 通过重定向 LoyaPaths.Root 到临时目录隔离数据，不碰真实 ~/.loya。
/// </summary>
public class ModpackServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _originalRoot;

    public ModpackServiceTests()
    {
        _originalRoot = LoyaPaths.Root;
        _tempRoot = Path.Combine(Path.GetTempPath(), "loya-test-" + Guid.NewGuid().ToString("N")[..8]);
        LoyaPaths.Root = _tempRoot;
        LoyaPaths.EnsureDirectories();
    }

    public void Dispose()
    {
        LoyaPaths.Root = _originalRoot;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private static GameInstance CreateTestInstance()
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "测试整合包",
            Edition = GameEdition.Java,
            VersionId = "1.21.4",
            Loader = "fabric",
            LoaderVersion = "0.16.9",
        };
        Directory.CreateDirectory(instance.RootDirectory);
        Directory.CreateDirectory(instance.GameDirectory);
        Directory.CreateDirectory(instance.ModsDirectory);
        Directory.CreateDirectory(instance.ConfigsDirectory);
        return instance;
    }

    [Fact]
    public async Task ExportThenImport_RoundTrips()
    {
        // 准备带内容的实例
        var instance = CreateTestInstance();
        await File.WriteAllTextAsync(Path.Combine(instance.ModsDirectory, "sodium-fabric.jar"), "fake-mod");
        await File.WriteAllTextAsync(Path.Combine(instance.ConfigsDirectory, "sodium.json"), "{}");
        await File.WriteAllTextAsync(instance.OptionsPath, "fov:90");
        Directory.CreateDirectory(Path.Combine(instance.RootDirectory, "loader"));
        await File.WriteAllTextAsync(Path.Combine(instance.RootDirectory, "loader", "fabric-loader.jar"), "fake-loader");

        var service = new ModpackService();
        var zipPath = Path.Combine(_tempRoot, "export", "test-pack.zip");

        // 导出
        var result = await service.ExportAsync(instance, zipPath);
        Assert.True(File.Exists(result));

        // 清单可独立读取
        var info = await ModpackService.ReadManifestAsync(zipPath);
        Assert.Equal("测试整合包", info.Manifest.Name);
        Assert.Equal("1.21.4", info.Manifest.MinecraftVersion);
        Assert.Equal("fabric", info.Manifest.Loader);
        Assert.Contains("sodium-fabric.jar", info.Manifest.ModFiles);

        // 导入（覆盖名称）
        var import = await service.ImportAsync(zipPath, "导入的实例");
        Assert.Equal("导入的实例", import.Instance.Name);
        Assert.Equal("1.21.4", import.Instance.VersionId);
        Assert.Equal("fabric", import.Instance.Loader);
        Assert.NotEqual(instance.Id, import.Instance.Id); // 新 ID，不冲突

        // 内容完整还原
        Assert.True(File.Exists(Path.Combine(import.Instance.ModsDirectory, "sodium-fabric.jar")));
        Assert.True(File.Exists(Path.Combine(import.Instance.ConfigsDirectory, "sodium.json")));
        Assert.True(File.Exists(import.Instance.OptionsPath));
        Assert.True(File.Exists(Path.Combine(import.Instance.RootDirectory, "loader", "fabric-loader.jar")));

        // 实例元数据已持久化，可被列表发现
        var listed = JavaEditionService.ListInstances();
        Assert.Contains(listed, i => i.Id == import.Instance.Id);
    }

    [Fact]
    public async Task Import_RejectsInvalidZip()
    {
        var bogus = Path.Combine(_tempRoot, "bogus.zip");
        using (var archive = ZipFile.Open(bogus, ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ModpackService().ImportAsync(bogus));
    }

    [Fact]
    public async Task Import_RejectsPathTraversal()
    {
        var evil = Path.Combine(_tempRoot, "evil.zip");
        await using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("modpack.json");
            await using (var w = new StreamWriter(manifestEntry.Open()))
                await w.WriteAsync("""{"SchemaVersion":1,"Name":"evil","MinecraftVersion":"1.21.4"}""");
            var evilEntry = archive.CreateEntry("instance/../../escape.txt");
            await using (var w = new StreamWriter(evilEntry.Open()))
                await w.WriteAsync("evil");
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ModpackService().ImportAsync(evil));
    }
}
