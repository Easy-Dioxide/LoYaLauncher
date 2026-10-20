using System.Text.Json.Serialization;

namespace LoyaLauncher.Pack;

/// <summary>GitHub Release API 响应模型</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>更新检查结果</summary>
public sealed record UpdateInfo
{
    /// <summary>是否有可用新版本</summary>
    public bool HasUpdate { get; }

    /// <summary>当前启动器版本</summary>
    public string CurrentVersion { get; }

    /// <summary>最新版本（无更新时与当前相同）</summary>
    public string LatestVersion { get; }

    /// <summary>下载地址（无更新或找不到匹配 asset 时为 null）</summary>
    public string? DownloadUrl { get; }

    /// <summary>安装包大小（字节）</summary>
    public long DownloadSize { get; }

    /// <summary>Release Notes</summary>
    public string? Notes { get; }

    /// <summary>检查失败时的错误信息</summary>
    public string? Error { get; }

    public UpdateInfo(bool hasUpdate, string currentVersion, string latestVersion,
        string? downloadUrl = null, long downloadSize = 0, string? notes = null, string? error = null)
    {
        HasUpdate = hasUpdate;
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion;
        DownloadUrl = downloadUrl;
        DownloadSize = downloadSize;
        Notes = notes;
        Error = error;
    }
}
