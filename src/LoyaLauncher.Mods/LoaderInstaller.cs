using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LoyaLauncher.Core;
using LoyaLauncher.Java;

namespace LoyaLauncher.Mods;

/// <summary>Mod 加载器类型</summary>
public enum LoaderKind
{
    Fabric,
    Forge,
    Quilt,
    NeoForge,
}

/// <summary>加载器安装进度</summary>
public sealed record LoaderInstallProgress(string Stage, string Detail, double Percent);

/// <summary>
/// 四加载器安装器。
/// - Fabric：直接生成 profile 版本 JSON（无需运行 installer，最快最干净）
/// - Forge / NeoForge / Quilt：运行官方 installer jar（--installClient）
/// </summary>
public sealed class LoaderInstaller
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Downloader.DownloadManager _downloader;
    private readonly HttpClient _http;
    private readonly Core.ConfigStore _config;

    public LoaderInstaller(Downloader.DownloadManager downloader, Core.ConfigStore config)
    {
        _downloader = downloader;
        _config = config;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "LoyaLauncher/0.1.0");
    }

    /// <summary>安装加载器，返回实例应切换到的版本 ID</summary>
    public async Task<string> InstallAsync(
        LoaderKind kind,
        string mcVersion,
        string? specificLoaderVersion,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        return kind switch
        {
            LoaderKind.Fabric => await InstallFabricAsync(mcVersion, specificLoaderVersion, progress, ct),
            LoaderKind.Quilt => await InstallQuiltAsync(mcVersion, specificLoaderVersion, progress, ct),
            LoaderKind.Forge => await InstallForgeAsync(mcVersion, specificLoaderVersion, progress, ct),
            LoaderKind.NeoForge => await InstallNeoForgeAsync(mcVersion, specificLoaderVersion, progress, ct),
            _ => throw new NotSupportedException(),
        };
    }

    // ======================= Fabric =======================

    private async Task<string> InstallFabricAsync(
        string mcVersion, string? version, IProgress<LoaderInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("获取 Fabric 版本", mcVersion, 0.1));
        var versions = await GetJsonAsync<List<FabricLoaderVersion>>(
            $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}", ct)
            ?? throw new InvalidOperationException("Fabric 不支持该 Minecraft 版本");

        var selected = version is null
            ? versions.FirstOrDefault(v => v.Stable) ?? versions.FirstOrDefault()
            : versions.FirstOrDefault(v => v.Version == version);
        if (selected is null)
            throw new InvalidOperationException($"未找到 Fabric 版本 {version}");

        // 获取完整 profile JSON（该 JSON 即标准版本 JSON，含 loader 全部依赖）
        progress?.Report(new("获取版本清单", selected.Version, 0.3));
        var profileJson = await GetStringAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{selected.Version}", ct);
        var profile = JsonSerializer.Deserialize<VersionJson>(profileJson, JsonOpts)
            ?? throw new InvalidOperationException("Fabric profile 解析失败");

        // 写入为新版本（版本 ID 不与官方冲突）
        var versionId = $"fabric-loader-{selected.Version}-{mcVersion}";
        profile.Id = versionId;
        profile.InheritsFrom = mcVersion;

        var versionDir = Path.Combine(LoyaPaths.Versions, versionId);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, $"{versionId}.json"), profileJson, ct);

        // 安装底层原版（fabric 依赖它）
        var installer = new JavaInstaller(_downloader, Core.EventBus.Instance, _config);
        await installer.InstallAsync(mcVersion, ct: ct);

        // 安装 profile 自身声明的 libraries（loader + intermediary + mixin 等）
        progress?.Report(new("下载 Fabric 库", selected.Version, 0.7));
        var items = profile.Libraries
            .Where(l => JavaInstaller.RulesAllow(l.Rules) && l.Downloads?.Artifact is not null)
            .Select(l =>
            {
                var mavenPath = JavaInstaller.LibraryToMavenPath(l.Name);
                return (
                    Url: l.Downloads!.Artifact!.Url,
                    Path: Path.Combine(LoyaPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1: (string?)l.Downloads!.Artifact!.Sha1,
                    Group: "libraries");
            });
        var result = await _downloader.DownloadManyAsync(items, ct);
        if (!result.Success) throw new InvalidOperationException($"Fabric 库下载失败: {result.Error}");

        progress?.Report(new("完成", versionId, 1.0));
        return versionId;
    }

    // ======================= Forge =======================

    private async Task<string> InstallForgeAsync(
        string mcVersion, string? version, IProgress<LoaderInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("获取 Forge 版本", mcVersion, 0.1));
        // promotions_slim.json 提供每个 MC 版本的 latest/recommended
        version ??= await GetLatestForgeVersionAsync(mcVersion, ct)
            ?? throw new InvalidOperationException($"Forge 不支持 Minecraft {mcVersion}");

        var forgeVersion = $"{mcVersion}-{version}";
        progress?.Report(new("下载安装器", forgeVersion, 0.3));
        var installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{forgeVersion}/forge-{forgeVersion}-installer.jar";
        var installerPath = Path.Combine(LoyaPaths.Cache, $"forge-{forgeVersion}-installer.jar");
        var r = await _downloader.DownloadFileAsync(installerUrl, installerPath, group: "loader", ct: ct);
        if (!r.Success) throw new InvalidOperationException($"Forge 安装器下载失败: {r.Error}");

        progress?.Report(new("执行安装", forgeVersion, 0.6));
        await RunInstallerAsync(installerPath, LoyaPaths.Instances, mcVersion, ct);
        progress?.Report(new("完成", forgeVersion, 1.0));
        return forgeVersion;
    }

    private static async Task<string?> GetLatestForgeVersionAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            var json = await http.GetStringAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("promos", out var promos) &&
                promos.TryGetProperty($"{mcVersion}-latest", out var latest))
                return latest.GetString();
        }
        catch { /* 网络失败时返回 null */ }
        return null;
    }

    // ======================= NeoForge =======================

    private async Task<string> InstallNeoForgeAsync(
        string mcVersion, string? version, IProgress<LoaderInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("获取 NeoForge 版本", mcVersion, 0.1));
        version ??= await GetLatestNeoForgeVersionAsync(mcVersion, ct)
            ?? throw new InvalidOperationException($"NeoForge 不支持 Minecraft {mcVersion}");

        var neoforgeVersion = $"{mcVersion}-{version}";
        progress?.Report(new("下载安装器", neoforgeVersion, 0.3));
        var installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoforgeVersion}/neoforge-{neoforgeVersion}-installer.jar";
        var installerPath = Path.Combine(LoyaPaths.Cache, $"neoforge-{neoforgeVersion}-installer.jar");
        var r = await _downloader.DownloadFileAsync(installerUrl, installerPath, group: "loader", ct: ct);
        if (!r.Success) throw new InvalidOperationException($"NeoForge 安装器下载失败: {r.Error}");

        progress?.Report(new("执行安装", neoforgeVersion, 0.6));
        await RunInstallerAsync(installerPath, LoyaPaths.Instances, mcVersion, ct);
        progress?.Report(new("完成", neoforgeVersion, 1.0));
        return neoforgeVersion;
    }

    private static async Task<string?> GetLatestNeoForgeVersionAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "LoyaLauncher/0.1.0");
            var xml = await http.GetStringAsync("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct);
            var doc = XDocument.Parse(xml);
            var versions = doc.Descendants("version")
                .Select(v => v.Value)
                .Where(v => v.StartsWith(mcVersion + "-"))
                .ToList();
            return versions.Count > 0 ? versions[^1] : null;
        }
        catch { return null; }
    }

    // ======================= Quilt =======================

    private async Task<string> InstallQuiltAsync(
        string mcVersion, string? version, IProgress<LoaderInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("获取 Quilt 版本", mcVersion, 0.1));
        var versions = await GetJsonAsync<List<QuiltLoaderVersion>>(
            $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}", ct)
            ?? throw new InvalidOperationException("Quilt 不支持该 Minecraft 版本");

        var selected = version is null
            ? versions.FirstOrDefault()
            : versions.FirstOrDefault(v => v.Loader?.Version == version);
        if (selected?.Loader is null)
            throw new InvalidOperationException($"未找到 Quilt 版本 {version}");

        // Quilt 官方 profile 位于 maven，按 Fabric 相同机制写入版本目录
        progress?.Report(new("获取版本清单", selected.Loader.Version, 0.3));
        var versionId = $"quilt-loader-{selected.Loader.Version}-{mcVersion}";
        var versionDir = Path.Combine(LoyaPaths.Versions, versionId);
        Directory.CreateDirectory(versionDir);

        // Quilt loader maven 坐标：org.quiltmc:quilt-loader:{version}
        var mavenPath = JavaInstaller.LibraryToMavenPath($"org.quiltmc:quilt-loader:{selected.Loader.Version}");
        var loaderJar = Path.Combine(LoyaPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
        var r = await _downloader.DownloadFileAsync(
            $"https://maven.quiltmc.org/repository/release/{mavenPath}",
            loaderJar, group: "loader", ct: ct);
        if (!r.Success) throw new InvalidOperationException($"Quilt loader 下载失败: {r.Error}");

        // Quilt profile：由 loader jar 内 META-INF 提供，直接继承原版 + loader jar
        var profile = new VersionJson
        {
            Id = versionId,
            MainClass = "org.quiltmc.loader.impl.launch.knot.KnotClient",
            InheritsFrom = mcVersion,
            Libraries =
            [
                new Library
                {
                    Name = $"org.quiltmc:quilt-loader:{selected.Loader.Version}",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadInfo { Url = $"https://maven.quiltmc.org/repository/release/{mavenPath}" },
                    },
                },
            ],
        };
        await File.WriteAllTextAsync(
            Path.Combine(versionDir, $"{versionId}.json"),
            JsonSerializer.Serialize(profile, JsonOpts), ct);

        var installer = new JavaInstaller(_downloader, Core.EventBus.Instance, _config);
        await installer.InstallAsync(mcVersion, ct: ct);

        progress?.Report(new("完成", versionId, 1.0));
        return versionId;
    }

    // ======================= Installer 执行 =======================

    /// <summary>
    /// 运行 Forge/NeoForge/Quilt 官方 installer jar。
    /// 安装器需要在包含 libraries/versions 的 MC 根目录下执行，
    /// 并需要 Java 运行时（由调用方保证）。
    /// </summary>
    private static async Task RunInstallerAsync(string installerJar, string mcRootDir, string mcVersion, CancellationToken ct)
    {
        // Forge/NeoForge installer 需要在一个"根"目录运行；我们指向共享的 .loya 数据根
        var workDir = LoyaPaths.Root;
        Directory.CreateDirectory(Path.Combine(workDir, "versions"));

        var psi = new System.Diagnostics.ProcessStartInfo(GetJavaForInstaller(), $"-jar \"{installerJar}\" --installClient \"{workDir}\"")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动安装器进程");
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        var error = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"安装器执行失败 (exit {proc.ExitCode}): {error[..Math.Min(500, error.Length)]}");
    }

    /// <summary>获取运行安装器所需的 Java（复用已托管 JRE 或系统 Java）</summary>
    private static string GetJavaForInstaller()
    {
        foreach (var major in new[] { 21, 17, 16, 8 })
        {
            var path = JreManager.GetManagedJavaPath(major);
            if (path is not null) return path;
        }
        return "java"; // 回退系统 Java
    }

    // ======================= 模型 =======================

    private sealed class FabricLoaderVersion
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("stable")]
        public bool Stable { get; set; }
    }

    private sealed class QuiltLoaderVersion
    {
        [JsonPropertyName("loader")]
        public QuiltLoader? Loader { get; set; }
    }

    private sealed class QuiltLoader
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
    }

    // ======================= HTTP 工具 =======================

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await GetStringAsync(url, ct), JsonOpts);

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
