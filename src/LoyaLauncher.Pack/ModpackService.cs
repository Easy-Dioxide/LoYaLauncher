using System.IO.Compression;
using System.Text.Json;
using LoyaLauncher.Core;
using LoyaLauncher.Java;

namespace LoyaLauncher.Pack;

/// <summary>
/// 整合包导入导出服务。zip 布局：
/// <code>
/// modpack.json    清单
/// instance.json   GameInstance 序列化（导入时以新 ID 重建）
/// instance/mods/**        mod 文件（解压到 gameDir）
/// instance/config/**      mod 配置（解压到 gameDir）
/// instance/options.txt    游戏设置（解压到 gameDir）
/// loader/**               加载器注入 jar（解压到实例根目录）
/// </code>
/// 不包含 versions/libraries/assets（这些由启动器按版本 ID 自动下载，保证包体小巧）。
/// </summary>
public sealed class ModpackService
{
    private const string ManifestEntry = "modpack.json";
    private const string InstanceEntry = "instance.json";
    private const string InstancePrefix = "instance/";
    private const string LoaderPrefix = "loader/";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>读取整合包清单（不导入，用于导入前预览）</summary>
    public static async Task<ModpackInfo> ReadManifestAsync(string zipPath, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("不是有效的 LoyaLauncher 整合包：缺少 modpack.json");
        using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<ModpackManifest>(stream, JsonOpts, ct)
            ?? throw new InvalidDataException("整合包清单解析失败");
        return new ModpackInfo(zipPath, manifest);
    }

    /// <summary>把实例导出为整合包 zip</summary>
    public async Task<string> ExportAsync(
        GameInstance instance,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var gameDir = instance.GameDirectory;

        // 收集要打包的文件（相对 zip 的条目名 → 磁盘路径）
        var files = new List<(string Entry, string Source)>();
        var modsDir = instance.ModsDirectory;
        if (Directory.Exists(modsDir))
            foreach (var f in Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories))
                files.Add(($"{InstancePrefix}mods/{Path.GetRelativePath(modsDir, f).Replace('\\', '/')}", f));

        var configDir = instance.ConfigsDirectory;
        if (Directory.Exists(configDir))
            foreach (var f in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
                files.Add(($"{InstancePrefix}config/{Path.GetRelativePath(configDir, f).Replace('\\', '/')}", f));

        var optionsPath = instance.OptionsPath;
        if (File.Exists(optionsPath))
            files.Add(($"{InstancePrefix}options.txt", optionsPath));

        var loaderDir = Path.Combine(instance.RootDirectory, "loader");
        if (Directory.Exists(loaderDir))
            foreach (var f in Directory.EnumerateFiles(loaderDir, "*", SearchOption.AllDirectories))
                files.Add(($"{LoaderPrefix}{Path.GetRelativePath(loaderDir, f).Replace('\\', '/')}", f));

        // 清单：只记录元数据 + mod 文件名列表（不复制实际文件元数据）
        var manifest = new ModpackManifest
        {
            Name = instance.Name,
            MinecraftVersion = instance.VersionId,
            Loader = instance.Loader,
            LoaderVersion = instance.LoaderVersion,
            ModFiles = Directory.Exists(modsDir)
                ? Directory.EnumerateFiles(modsDir).Select(f => Path.GetFileName(f)!).ToList()
                : [],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var zipStream = File.Create(outputPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // 1. 清单
        progress?.Report(0.05);
        await using (var entryStream = archive.CreateEntry(ManifestEntry).Open())
            await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOpts, ct);

        // 2. 实例元数据
        progress?.Report(0.1);
        await using (var entryStream = archive.CreateEntry(InstanceEntry).Open())
            await JsonSerializer.SerializeAsync(entryStream, instance, JsonOpts, ct);

        // 3. 内容文件
        for (var i = 0; i < files.Count; i++)
        {
            var (entry, source) = files[i];
            await using var sourceStream = File.OpenRead(source);
            await using var entryStream = archive.CreateEntry(entry).Open();
            await sourceStream.CopyToAsync(entryStream, ct);
            progress?.Report(0.1 + 0.9 * (i + 1) / files.Count);
        }

        progress?.Report(1.0);
        return outputPath;
    }

    /// <summary>
    /// 导入整合包：解压内容 → 以新 ID 创建实例 → 返回实例。
    /// versions/libraries/assets 不导入，首次启动时按 manifest 声明的版本自动下载。
    /// </summary>
    public async Task<ModpackImportResult> ImportAsync(
        string zipPath,
        string? nameOverride = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // 先读清单做校验
        var info = await ReadManifestAsync(zipPath, ct);
        var manifest = info.Manifest;
        if (manifest.SchemaVersion > 1)
            throw new InvalidDataException($"整合包清单版本过新（schema {manifest.SchemaVersion}），请升级启动器");
        if (string.IsNullOrWhiteSpace(manifest.MinecraftVersion))
            throw new InvalidDataException("整合包清单缺少 MinecraftVersion，文件可能已损坏");

        string? warning = null;
        if (!string.IsNullOrEmpty(manifest.Loader))
            warning = $"导入的整合包包含 {manifest.Loader} 加载器，首次启动前请先在 Mod 页安装对应加载器";

        // 读取 instance.json（旧包可能没有，此时按 manifest 重建）
        Core.GameInstance? packed = null;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entry = archive.GetEntry(InstanceEntry);
            if (entry is not null)
            {
                using var stream = entry.Open();
                packed = await JsonSerializer.DeserializeAsync<Core.GameInstance>(stream, JsonOpts, ct);
            }
        }

        // 以新 ID 重建实例，避免与本地实例冲突
        var instance = new Core.GameInstance
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(nameOverride) ? manifest.Name : nameOverride,
            Edition = packed?.Edition ?? GameEdition.Java,
            VersionId = manifest.MinecraftVersion,
            Loader = manifest.Loader,
            LoaderVersion = manifest.LoaderVersion,
        };
        if (string.IsNullOrWhiteSpace(instance.Name))
            instance.Name = Path.GetFileNameWithoutExtension(zipPath);

        Directory.CreateDirectory(instance.RootDirectory);
        Directory.CreateDirectory(instance.GameDirectory);
        Directory.CreateDirectory(instance.ModsDirectory);
        Directory.CreateDirectory(instance.ConfigsDirectory);
        progress?.Report(0.2);

        // 解压内容文件：instance/ 前缀进 gameDir，loader/ 前缀进实例根目录
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var contentEntries = archive.Entries
                .Where(e => e.Length > 0 &&
                            (e.FullName.StartsWith(InstancePrefix, StringComparison.Ordinal) ||
                             e.FullName.StartsWith(LoaderPrefix, StringComparison.Ordinal)))
                .ToList();

            for (var i = 0; i < contentEntries.Count; i++)
            {
                var entry = contentEntries[i];
                // instance/ 前缀是虚拟根（解压到 gameDir）；loader/ 是实例根目录下的真实子目录，原样保留
                var isLoader = entry.FullName.StartsWith(LoaderPrefix, StringComparison.Ordinal);
                var relative = isLoader
                    ? entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                    : entry.FullName[InstancePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var baseDir = isLoader ? instance.RootDirectory : instance.GameDirectory;
                var dest = Path.Combine(baseDir, relative);

                // 防目录穿越：解压目标必须位于目标基目录内
                var fullDest = Path.GetFullPath(dest);
                if (!fullDest.StartsWith(Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException($"整合包含有非法路径: {entry.FullName}");

                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                await using var sourceStream = entry.Open();
                await using var destStream = File.Create(fullDest);
                await sourceStream.CopyToAsync(destStream, ct);
                progress?.Report(0.2 + 0.7 * (i + 1) / contentEntries.Count);
            }
        }

        await JavaEditionService.SaveInstanceAsync(instance, ct);
        progress?.Report(1.0);
        return new ModpackImportResult(instance, warning);
    }
}
