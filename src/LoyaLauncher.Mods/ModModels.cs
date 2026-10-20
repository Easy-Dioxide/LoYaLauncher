using System.Text.Json.Serialization;

namespace LoyaLauncher.Mods;

// ======================= Modrinth 搜索 =======================

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")]
    public List<ModrinthProject> Hits { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = "mod";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = [];

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("date_modified")]
    public string DateModified { get; set; } = "";
}

// ======================= Modrinth 版本 =======================

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];

    [JsonPropertyName("date_published")]
    public string DatePublished { get; set; } = "";
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; set; } = [];
}

// ======================= 本地 Mod =======================

/// <summary>已安装的 Mod 信息</summary>
public sealed class InstalledMod
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public string ModId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long FileSize { get; set; }

    /// <summary>UI 状态点颜色（启用=绿，禁用=灰）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusBrush => Enabled ? "#4ADE80" : "#6B6E85";
}
