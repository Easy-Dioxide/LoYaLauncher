using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoyaLauncher.Java;

// ======================= 版本清单 =======================

/// <summary>Mojang version_manifest_v2.json 根对象</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions? Latest { get; set; }

    [JsonPropertyName("versions")]
    public List<ManifestVersion> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = "";
}

public sealed class ManifestVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "release";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    [JsonPropertyName("releaseTime")]
    public string ReleaseTime { get; set; } = "";
}

// ======================= 版本 JSON =======================

/// <summary>
/// versions/{id}/{id}.json 反序列化模型。兼容：
/// - 1.13+ arguments 结构
/// - 旧版 minecraftArguments 字符串模板
/// - inheritsFrom 继承链
/// </summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = "";

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = [];

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("arguments")]
    public Arguments? Arguments { get; set; }

    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionRef? JavaVersion { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "release";

    [JsonPropertyName("assets")]
    public string Assets { get; set; } = "legacy";
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadInfo? Client { get; set; }
}

public sealed class DownloadInfo
{
    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")]
    public string Component { get; set; } = "";

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

// ======================= Libraries =======================

public sealed class Library
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadInfo? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

/// <summary>allow/disallow 规则。无 rules 表示始终允许。</summary>
public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

// ======================= Arguments =======================

public sealed class Arguments
{
    [JsonPropertyName("game")]
    public List<ArgumentElement> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    public List<ArgumentElement> Jvm { get; set; } = [];
}

/// <summary>参数元素：纯字符串，或带 rules 的条件对象</summary>
public sealed class ArgumentElement
{
    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

// ======================= 资源索引 =======================

/// <summary>assets/indexes/{id}.json：资源对象清单</summary>
public sealed class AssetIndex
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = [];
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

// ======================= 安装记录 =======================

/// <summary>LoyaLauncher 写入 versions/{id}/loya.json 的安装元数据</summary>
public sealed class InstalledVersionMeta
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public string? InheritsFrom { get; set; }
    public string? MainClass { get; set; }
    public int JavaMajor { get; set; }
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
    public List<string> Libraries { get; set; } = [];
    public string AssetIndexId { get; set; } = "";
}
