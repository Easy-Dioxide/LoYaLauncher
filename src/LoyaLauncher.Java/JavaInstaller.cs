using System.Text.Json;
using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>
/// Java 版安装引擎。流程：版本清单 → 版本 JSON（含继承链合并）→ 客户端 jar →
/// libraries（Rules 过滤 + natives 解压）→ assets 索引与对象。
/// </summary>
public sealed class JavaInstaller
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Downloader.DownloadManager _downloader;
    private readonly Core.EventBus _bus;
    private readonly Core.MirrorMode _mirror;

    /// <summary>安装阶段事件，供 UI 显示当前步骤</summary>
    public sealed record InstallProgressEvent(string VersionId, string Stage, string Detail, double Percent);

    public JavaInstaller(Downloader.DownloadManager downloader, Core.EventBus bus, Core.MirrorMode mirror)
    {
        _downloader = downloader;
        _bus = bus;
        _mirror = mirror;
    }

    // ======================= 版本清单 =======================

    /// <summary>获取版本清单（缓存 6 小时）</summary>
    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        var cachePath = Path.Combine(LoyaPaths.Cache, "version_manifest_v2.json");
        if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromHours(6))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cachePath, ct), JsonOpts);
                if (cached is not null) return cached;
            }
            catch { /* 缓存损坏则重新下载 */ }
        }

        Exception? lastError = null;
        foreach (var mirror in Downloader.MirrorSource.ResolveChain(_mirror))
        {
            try
            {
                var result = await _downloader.DownloadFileAsync(mirror.VersionManifest, cachePath, group: "manifest", ct: ct);
                if (result.Success)
                    return JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cachePath, ct), JsonOpts)
                        ?? throw new InvalidOperationException("版本清单解析为空");
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw new InvalidOperationException($"无法获取版本清单: {lastError?.Message}");
    }

    /// <summary>下载并解析版本 JSON（含继承链合并）</summary>
    public async Task<VersionJson> GetVersionJsonAsync(string versionId, CancellationToken ct = default)
    {
        var jsonPath = Path.Combine(LoyaPaths.Versions, versionId, $"{versionId}.json");
        if (!File.Exists(jsonPath))
        {
            var downloaded = false;
            foreach (var mirror in Downloader.MirrorSource.ResolveChain(_mirror))
            {
                var result = await _downloader.DownloadFileAsync(
                    mirror.VersionJson(versionId), jsonPath, group: "version", ct: ct);
                if (result.Success) { downloaded = true; break; }
            }
            if (!downloaded)
                throw new InvalidOperationException($"无法下载版本 {versionId} 的 JSON");
        }
        var root = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(jsonPath, ct), JsonOpts)
            ?? throw new InvalidOperationException($"版本 {versionId} JSON 解析失败");

        // 合并继承链（如快照版继承正式版）
        if (!string.IsNullOrEmpty(root.InheritsFrom))
        {
            var parentPath = Path.Combine(LoyaPaths.Versions, root.InheritsFrom, $"{root.InheritsFrom}.json");
            if (!File.Exists(parentPath))
            {
                foreach (var mirror in Downloader.MirrorSource.ResolveChain(_mirror))
                {
                    var result = await _downloader.DownloadFileAsync(
                        mirror.VersionJson(root.InheritsFrom), parentPath, group: "version", ct: ct);
                    if (result.Success) break;
                }
            }
            if (File.Exists(parentPath))
            {
                var parent = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(parentPath, ct), JsonOpts);
                if (parent is not null)
                    root = MergeVersion(root, parent);
            }
        }
        return root;
    }

    /// <summary>合并子版本与父版本：子覆盖父，libraries 合并去重</summary>
    private static VersionJson MergeVersion(VersionJson child, VersionJson parent)
    {
        var mergedLibs = new List<Library>();
        var seen = new HashSet<string>();
        foreach (var lib in child.Libraries.Concat(parent.Libraries))
        {
            var key = lib.Name.Split(':')[..2].Aggregate((a, b) => a + ":" + b);
            if (seen.Add(key))
                mergedLibs.Add(lib);
        }
        child.Libraries = mergedLibs;
        child.AssetIndex ??= parent.AssetIndex;
        child.Downloads ??= parent.Downloads;
        child.JavaVersion ??= parent.JavaVersion;
        child.Assets = string.IsNullOrEmpty(child.Assets) ? parent.Assets : child.Assets;
        return child;
    }

    // ======================= 安装 =======================

    /// <summary>安装指定版本（幂等：已安装则跳过）</summary>
    public async Task<bool> InstallAsync(string versionId, IProgress<InstallProgressEvent>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new(versionId, "读取版本信息", versionId, 0));
        var json = await GetVersionJsonAsync(versionId, ct);

        var versionDir = Path.Combine(LoyaPaths.Versions, versionId);
        Directory.CreateDirectory(versionDir);

        // 1. 客户端 jar
        progress?.Report(new(versionId, "下载客户端", $"{versionId}.jar", 0.05));
        var clientJar = Path.Combine(versionDir, $"{versionId}.jar");
        if (json.Downloads?.Client is { } client)
        {
            var r = await _downloader.DownloadFileAsync(
                PickUrl(client, mirror => mirror.ClientJar(client.Sha1)),
                clientJar, client.Sha1, group: "client", ct: ct);
            if (!r.Success) throw new InvalidOperationException($"客户端 jar 下载失败: {r.Error}");
        }

        // 2. libraries（过滤 Rules，区分普通库与 natives）
        progress?.Report(new(versionId, "下载依赖库", "libraries", 0.2));
        var nativeDir = Path.Combine(versionDir, "natives");
        var artifacts = new List<(string Url, string Path, string? Sha1)>();
        var nativeJars = new List<string>();

        foreach (var lib in json.Libraries)
        {
            if (!RulesAllow(lib.Rules)) continue;

            if (lib.Downloads?.Artifact is { } artifact)
            {
                var mavenPath = LibraryToMavenPath(lib.Name);
                var dest = Path.Combine(LoyaPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
                artifacts.Add((PickUrl(artifact, m => m.Library(mavenPath)), dest, artifact.Sha1));
            }

            // natives 分类器
            if (lib.Natives is { Count: > 0 } natives && lib.Downloads?.Classifiers is { } classifiers)
            {
                var osKey = CurrentOsName; // windows / osx / linux
                var template = natives.TryGetValue(osKey, out var t) ? t : natives.Values.FirstOrDefault();
                if (template is not null && template.Length > 0)
                {
                    var classifier = template.Replace("${arch}", CurrentArch switch
                    {
                        "x64" => "64",
                        _ => "32",
                    });
                    foreach (var (name, info) in classifiers)
                    {
                        if (name.Contains("natives", StringComparison.OrdinalIgnoreCase) ||
                            lib.Name.EndsWith(":" + classifier, StringComparison.Ordinal))
                        {
                            var mavenPath = LibraryToMavenPath(lib.Name, name);
                            var dest = Path.Combine(LoyaPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
                            artifacts.Add((PickUrl(info, m => m.Library(mavenPath)), dest, info.Sha1));
                            nativeJars.Add(dest);
                        }
                    }
                }
            }
        }

        var libResult = await _downloader.DownloadManyAsync(
            artifacts.Select(a => (a.Url, a.Path, (string?)a.Sha1, "libraries")), ct);
        if (!libResult.Success) throw new InvalidOperationException($"依赖库下载失败: {libResult.Error}");

        // 3. 解压 natives
        progress?.Report(new(versionId, "解压本地库", "natives", 0.6));
        Directory.CreateDirectory(nativeDir);
        foreach (var jar in nativeJars.Distinct())
        {
            if (File.Exists(jar))
                ExtractNatives(jar, nativeDir);
        }

        // 4. assets
        progress?.Report(new(versionId, "下载资源文件", "assets", 0.65));
        var assetIndexId = json.AssetIndex?.Id ?? "legacy";
        var indexPath = Path.Combine(LoyaPaths.Assets, "indexes", $"{assetIndexId}.json");
        if (json.AssetIndex is { } ai)
        {
            var r = await _downloader.DownloadFileAsync(
                PickUrl(ai, m => m.AssetsIndex(assetIndexId)), indexPath, ai.Sha1, group: "assets-index", ct: ct);
            if (!r.Success) throw new InvalidOperationException($"资源索引下载失败: {r.Error}");
        }

        var assetIndex = JsonSerializer.Deserialize<AssetIndex>(await File.ReadAllTextAsync(indexPath, ct), JsonOpts);
        if (assetIndex is { Objects.Count: > 0 })
        {
            var objects = assetIndex.Objects
                .Where(o => !File.Exists(Path.Combine(LoyaPaths.Assets, "objects", o.Value.Hash[..2], o.Value.Hash)))
                .Select(o => (
                    Url: PickObjectUrl(o.Value.Hash),
                    Path: Path.Combine(LoyaPaths.Assets, "objects", o.Value.Hash[..2], o.Value.Hash),
                    Sha1: (string?)o.Value.Hash,
                    Group: "assets"))
                .ToList();
            await _downloader.DownloadManyAsync(objects, ct);
            // 资源对象允许部分失败（少数非关键文件），不阻断安装
        }

        // 5. 写入安装元数据
        progress?.Report(new(versionId, "完成", versionId, 1.0));
        var meta = new InstalledVersionMeta
        {
            Id = versionId,
            Type = json.Type,
            InheritsFrom = json.InheritsFrom,
            MainClass = json.MainClass,
            JavaMajor = GetRequiredJavaMajor(json),
            Libraries = json.Libraries.Where(l => RulesAllow(l.Rules)).Select(l => l.Name).ToList(),
            AssetIndexId = assetIndexId,
        };
        await File.WriteAllTextAsync(
            Path.Combine(versionDir, "loya.json"),
            JsonSerializer.Serialize(meta, JsonOpts), ct);
        return true;
    }

    private string PickObjectUrl(string hash)
    {
        var mirror = Downloader.MirrorSource.Create(
            _mirror == Core.MirrorMode.OfficialOnly ? Downloader.MirrorKind.Official : Downloader.MirrorKind.Bmclapi);
        return mirror.AssetObject(hash);
    }

    /// <summary>根据镜像模式选择 URL：优先使用 manifest/JSON 自带的官方 URL，镜像模式下降级为镜像 URL</summary>
    private string PickUrl(DownloadInfo info, Func<Downloader.MirrorUrlSet, string> mirrorSelector)
    {
        if (_mirror == Core.MirrorMode.OfficialOnly)
            return info.Url;
        var mirror = Downloader.MirrorSource.Create(Downloader.MirrorKind.Bmclapi);
        return mirrorSelector(mirror);
    }

    private static string PickUrl(AssetIndexRef info, Func<Downloader.MirrorUrlSet, string> mirrorSelector)
    {
        var mirror = Downloader.MirrorSource.Create(Downloader.MirrorKind.Bmclapi);
        return mirrorSelector(mirror);
    }

    // ======================= 工具方法 =======================

    /// <summary>当前 OS 名称（MC rules 约定值）</summary>
    public static string CurrentOsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    /// <summary>当前架构（MC rules 约定值）</summary>
    public static string CurrentArch =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64";

    /// <summary>评估 Rules 列表是否允许当前平台</summary>
    public static bool RulesAllow(List<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;
        // MC 规范：存在 rules 时默认不允许，仅当命中 allow 规则才放行
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Os is { } os && !OsMatches(os)) continue;
            allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool OsMatches(OsRule os)
    {
        if (os.Name is { Length: > 0 } name && name != CurrentOsName) return false;
        if (os.Arch is { Length: > 0 } arch && arch != CurrentArch) return false;
        if (os.Version is { Length: > 0 } ver && !OperatingSystem.IsLinux())
            return false; // version 规则主要面向 Linux 内核版本，非 Linux 平台直接视为不匹配
        return true;
    }

    /// <summary>Maven 坐标 → Maven 仓库相对路径。classifier 可从参数传入，也可内嵌于坐标第四段。</summary>
    public static string LibraryToMavenPath(string name, string? classifier = null)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) throw new FormatException($"非法的 Maven 坐标: {name}");
        if (classifier is null && parts.Length > 3) classifier = parts[3];
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var suffix = classifier is null ? "" : $"-{classifier}";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{suffix}.jar";
    }

    /// <summary>推断所需 Java 主版本</summary>
    public static int GetRequiredJavaMajor(VersionJson json)
    {
        if (json.JavaVersion?.MajorVersion is > 0) return json.JavaVersion.MajorVersion;
        // 无声明时按版本号经验推断
        var id = json.Id;
        var m = System.Text.RegularExpressions.Regex.Match(id, @"1\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return 8; // rd-/a/b/c 等远古版本，无 javaVersion 声明时按 Java 8 处理
        var minor = int.Parse(m.Groups[1].Value);
        var patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (minor < 17) return 8;
        if (minor == 17) return 16;
        if (minor is 18 or 19) return 17;
        if (minor == 20 && patch < 5) return 17;
        return 21; // 1.20.5+ / 1.21+
    }

    /// <summary>解压 natives jar（排除 META-INF 等非库文件）</summary>
    private static void ExtractNatives(string jarPath, string targetDir)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(jarPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.FullName.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) continue;

            var dest = Path.Combine(targetDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var entryStream = entry.Open();
            using var fileStream = File.Create(dest);
            entryStream.CopyTo(fileStream);
        }
    }

    /// <summary>是否已安装</summary>
    public bool IsInstalled(string versionId) =>
        File.Exists(Path.Combine(LoyaPaths.Versions, versionId, "loya.json"));

    /// <summary>列出已安装版本</summary>
    public List<InstalledVersionMeta> ListInstalled()
    {
        var result = new List<InstalledVersionMeta>();
        if (!Directory.Exists(LoyaPaths.Versions)) return result;
        foreach (var dir in Directory.EnumerateDirectories(LoyaPaths.Versions))
        {
            var metaPath = Path.Combine(dir, "loya.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<InstalledVersionMeta>(File.ReadAllText(metaPath), JsonOpts);
                    if (meta is not null) result.Add(meta);
                }
                catch { /* 跳过损坏记录 */ }
            }
        }
        return result;
    }
}
