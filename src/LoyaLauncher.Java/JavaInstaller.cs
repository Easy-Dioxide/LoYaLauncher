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
    private readonly Core.ConfigStore _config;

    /// <summary>安装阶段事件，供 UI 显示当前步骤</summary>
    public sealed record InstallProgressEvent(string VersionId, string Stage, string Detail, double Percent);

    public JavaInstaller(Downloader.DownloadManager downloader, Core.EventBus bus, Core.ConfigStore config)
    {
        _downloader = downloader;
        _bus = bus;
        _config = config;
    }

    /// <summary>
    /// 解析下载源链：每次安装实时读取配置（切换镜像即时生效，无需重启）。
    /// 自定义源地址为空时，CustomFirst/CustomOnly 自动退化为 BMCLAPI 优先，
    /// 避免把整条链打断。
    /// </summary>
    private IReadOnlyList<Downloader.MirrorUrlSet> Chain()
    {
        var mode = _config.Config.Mirror;
        var custom = _config.Config.CustomMirrorUrl;
        if (mode is Core.MirrorMode.CustomFirst or Core.MirrorMode.CustomOnly &&
            string.IsNullOrWhiteSpace(custom))
            mode = Core.MirrorMode.BmclapiFirst;
        return Downloader.MirrorSource.ResolveChain(mode, custom);
    }

    // ======================= 版本清单 =======================

    /// <summary>清单缓存有效期</summary>
    private static readonly TimeSpan ManifestCacheTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// 获取版本清单（缓存 6 小时）。
    /// 缓存策略（清单 §3.2）：区分“缓存存在”与“缓存有效”——
    /// 过期但可解析的缓存保留为降级候选；损坏缓存移入诊断目录后重新请求；
    /// 全部源网络失败时返回陈旧缓存并打标 <see cref="VersionManifest.IsStale"/>。
    /// </summary>
    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        var cachePath = Path.Combine(LoyaPaths.Cache, "version_manifest_v2.json");

        VersionManifest? fresh = null;
        VersionManifest? staleFallback = null;
        if (File.Exists(cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            try
            {
                var cached = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cachePath, ct), JsonOpts);
                if (cached is { Versions.Count: > 0 })
                {
                    cached.CacheWrittenUtc = File.GetLastWriteTimeUtc(cachePath);
                    if (age < ManifestCacheTtl)
                        fresh = cached;
                    else
                        staleFallback = cached; // 过期但可用：网络失败时降级
                }
                else
                {
                    QuarantineCache(cachePath, "empty-versions");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
            {
                if (ex is not OperationCanceledException)
                    QuarantineCache(cachePath, ex is JsonException ? "corrupt-json" : "io-error");
                else throw;
            }
        }

        if (fresh is not null) return fresh;

        Exception? lastError = null;
        foreach (var mirror in Chain())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _downloader.DownloadFileAsync(mirror.VersionManifest, cachePath, group: "manifest", expectJson: true, ct: ct);
                if (result.Success)
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cachePath, ct), JsonOpts);
                        if (parsed is { Versions.Count: > 0 } && parsed.Latest is not null)
                        {
                            parsed.CacheWrittenUtc = DateTime.UtcNow;
                            return parsed;
                        }
                        QuarantineCache(cachePath, parsed is null ? "null-json" : "missing-fields");
                        lastError = new InvalidOperationException("版本清单缺少 latest/versions 必要字段");
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
                    {
                        if (ex is OperationCanceledException) throw;
                        QuarantineCache(cachePath, ex is JsonException ? "corrupt-json" : "io-error");
                        lastError = ex;
                    }
                }
                else if (!result.Canceled)
                {
                    lastError = new InvalidOperationException(result.Error ?? "下载失败");
                }
            }
            catch (Exception ex) { lastError = ex; }
        }

        if (staleFallback is not null)
        {
            staleFallback.IsStale = true;
            return staleFallback;
        }
        throw new InvalidOperationException($"无法获取版本清单: {lastError?.Message}");
    }

    /// <summary>将损坏/无效的缓存移入诊断目录（保留证据，避免被反复误用）</summary>
    private static void QuarantineCache(string cachePath, string reason)
    {
        try
        {
            if (!File.Exists(cachePath)) return;
            var dir = Path.Combine(LoyaPaths.Cache, "diagnostic");
            Directory.CreateDirectory(dir);
            var name = Path.GetFileName(cachePath);
            var dest = Path.Combine(dir, $"{name}.{DateTime.UtcNow:yyyyMMddHHmmss}.{reason}.bad");
            File.Move(cachePath, dest, overwrite: true);
        }
        catch { /* 移动失败不影响主流程 */ }
    }

    /// <summary>下载并解析版本 JSON（含继承链合并）</summary>
    public async Task<VersionJson> GetVersionJsonAsync(string versionId, CancellationToken ct = default)
    {
        var jsonPath = Path.Combine(LoyaPaths.Versions, versionId, $"{versionId}.json");
        var root = await ReadOrDownloadVersionJsonAsync(versionId, jsonPath, ct);

        // 合并继承链（如快照版继承正式版）
        if (!string.IsNullOrEmpty(root.InheritsFrom))
        {
            var parentPath = Path.Combine(LoyaPaths.Versions, root.InheritsFrom, $"{root.InheritsFrom}.json");
            var parent = await ReadOrDownloadVersionJsonAsync(root.InheritsFrom, parentPath, ct);
            root = MergeVersion(root, parent);
        }
        return root;
    }

    /// <summary>
    /// 从缓存读取版本 JSON；缓存损坏时删除并重新下载。官方版本 JSON 的真实地址
    /// 来自 version_manifest_v2.json 的每个版本 url（piston-meta 的 SHA-1 路径），
    /// 不能把版本 ID 直接拼到官方 URL 中。
    /// </summary>
    private async Task<VersionJson> ReadOrDownloadVersionJsonAsync(string versionId, string jsonPath, CancellationToken ct)
    {
        if (File.Exists(jsonPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(jsonPath, ct), JsonOpts);
                if (cached is not null) return cached;
                QuarantineCache(jsonPath, "empty-json");
            }
            catch (JsonException) { QuarantineCache(jsonPath, "corrupt-json"); }
            catch (IOException) { try { File.Delete(jsonPath); } catch { /* 下载阶段会报告实际错误 */ } }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        var candidates = new List<string>();
        foreach (var mirror in Chain())
        {
            try { candidates.Add(mirror.VersionJson(versionId)); }
            catch (InvalidOperationException) { /* 空的自定义源由后续源兜底 */ }
        }

        // 从官方清单读取准确 URL，作为通用回退；对 BMCLAPI 不可用的旧版本尤其重要。
        try
        {
            var manifest = await GetManifestAsync(ct);
            var officialUrl = manifest.Versions.FirstOrDefault(v => v.Id.Equals(versionId, StringComparison.Ordinal))?.Url;
            if (!string.IsNullOrWhiteSpace(officialUrl)) candidates.Add(officialUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 保留已有镜像候选；下方会汇总下载失败 */ }

        var errors = new List<string>();
        foreach (var url in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var result = await _downloader.DownloadFileAsync(url, jsonPath, group: "version", expectJson: true, ct: ct);
            if (result.Success)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(jsonPath, ct), JsonOpts);
                    if (parsed is not null) return parsed;
                    errors.Add($"{new Uri(url).Host}: JSON 内容为空");
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    errors.Add($"{new Uri(url).Host}: {ex.Message}");
                }
                try { File.Delete(jsonPath); } catch { }
            }
            else errors.Add($"{new Uri(url).Host}: {result.Error}");
        }

        throw new InvalidOperationException($"无法下载或解析版本 {versionId} 的 JSON。" +
            (errors.Count == 0 ? "没有可用的下载源。" : "尝试结果：" + string.Join("；", errors.Take(4))));
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
            await DownloadWithChainAsync(
                [(clientJar, client.Sha1, "client", m => m.ClientJar(versionId, client.Sha1), false)], ct);
        }

        // 2. libraries（过滤 Rules，区分普通库与 natives）
        progress?.Report(new(versionId, "下载依赖库", "libraries", 0.2));
        var nativeDir = Path.Combine(versionDir, "natives");
        var artifacts = new List<(string Path, string? Sha1, string MavenPath)>();
        var nativeJars = new List<string>();

        foreach (var lib in json.Libraries)
        {
            if (!RulesAllow(lib.Rules)) continue;

            if (lib.Downloads?.Artifact is { } artifact)
            {
                var mavenPath = LibraryToMavenPath(lib.Name);
                var dest = Path.Combine(LoyaPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
                artifacts.Add((dest, artifact.Sha1, mavenPath));
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
                            artifacts.Add((dest, info.Sha1, mavenPath));
                            nativeJars.Add(dest);
                        }
                    }
                }
            }
        }

        await DownloadWithChainAsync(
            artifacts.Select(a => (a.Path, a.Sha1, "libraries",
                (Func<Downloader.MirrorUrlSet, string>)(m => m.Library(a.MavenPath)), false)), ct);

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
            // 资源索引优先用版本 JSON 自带的官方 URL（BMCLAPI 系镜像基本不提供索引文件），
            // JSON 缺 url 时才回退到镜像约定路径
            await DownloadWithChainAsync(
                [(indexPath, ai.Sha1, "assets-index",
                    m => string.IsNullOrEmpty(ai.Url) ? m.AssetsIndex(assetIndexId) : ai.Url, true)], ct);
        }

        var assetIndex = JsonSerializer.Deserialize<AssetIndex>(await File.ReadAllTextAsync(indexPath, ct), JsonOpts);
        if (assetIndex is { Objects.Count: > 0 })
        {
            var objects = assetIndex.Objects
                .Select(o => (
                    Path: Path.Combine(LoyaPaths.Assets, "objects", o.Value.Hash[..2], o.Value.Hash),
                    Sha1: (string?)o.Value.Hash,
                    Group: "assets",
                    UrlFor: (Func<Downloader.MirrorUrlSet, string>)(m => m.AssetObject(o.Value.Hash)),
                    ExpectJson: false))
                .ToList();
            try
            {
                await DownloadWithChainAsync(objects, ct);
            }
            catch
            {
                // 资源对象允许部分失败（少数非关键文件），不阻断安装
            }
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

    /// <summary>
    /// 按镜像链依次下载目标文件：每个文件优先走链首源；失败的文件自动切换到链上
    /// 下一个源重试（DownloadFileAsync 幂等——已存在且 SHA1 校验通过的文件自动跳过，
    /// 因此重试只会补齐缺口，不会重复传输）。
    /// 全部源都失败时抛出聚合错误。
    /// </summary>
    private async Task DownloadWithChainAsync(
        IEnumerable<(string Path, string? Sha1, string Group, Func<Downloader.MirrorUrlSet, string> UrlFor, bool ExpectJson)> targets,
        CancellationToken ct)
    {
        var chain = Chain();
        var pending = targets.ToList();
        string? lastError = null;

        foreach (var mirror in chain)
        {
            if (pending.Count == 0) break;
            var items = pending.Select(t => (t.UrlFor(mirror), t.Path, t.Sha1, t.Group, t.ExpectJson)).ToArray();
            var result = await _downloader.DownloadManyAsync(items, ct);
            if (result.Success)
            {
                pending.Clear();
                break;
            }
            lastError = result.Error;
            // 缩小待重试集合：只保留仍未落盘/校验未过的文件
            pending = pending.Where(t => !IsValidOnDisk(t.Path, t.Sha1)).ToList();
        }

        if (pending.Count > 0)
            throw new InvalidOperationException(
                $"下载失败（所有源均未成功，共 {pending.Count} 个文件）：" +
                string.Join("、", pending.Take(3).Select(t => Path.GetFileName(t.Path))) +
                (pending.Count > 3 ? " 等" : "") + $"；最后错误：{lastError}");
    }

    /// <summary>文件已存在且 SHA1 与期望一致（无期望值时只要求存在）</summary>
    private static bool IsValidOnDisk(string path, string? sha1)
    {
        if (!File.Exists(path)) return false;
        if (sha1 is null) return true;
        try
        {
            return string.Equals(
                Downloader.Sha1Util.ComputeFileHashAsync(path).GetAwaiter().GetResult(),
                sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
        // 新快照/预发布格式（25w14craftmine、25w30a 等）：对应 1.21.6+，按 Java 21 处理
        if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{2}w")) return 21;
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

    /// <summary>解压 natives jar（排除 META-INF 等非库文件；条目路径经 SafePath 校验，防目录穿越）</summary>
    private static void ExtractNatives(string jarPath, string targetDir)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(jarPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.FullName.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) continue;

            // 目录穿越防护（清单 §10）：跳过非法条目而非抛出，单个坏 jar 不中断整个安装
            var dest = Core.SafePath.ResolveEntry(targetDir, entry.FullName);
            if (dest is null) continue;

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
