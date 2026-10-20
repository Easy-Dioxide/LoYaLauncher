using System.Text.Json.Serialization;

namespace LoyaLauncher.Bedrock;

/// <summary>
/// mrarm w10-vdb 基岩版版本数据库（https://mrarm.io/r/w10-vdb）。
/// 提供 UWP 各版本的 Update Identity GUID，供 AppX 注册与下载。
/// </summary>
public sealed class W10VersionDatabase
{
    [JsonPropertyName("release")]
    public W10Channel? Release { get; set; }

    [JsonPropertyName("beta")]
    public W10Channel? Beta { get; set; }

    [JsonPropertyName("preview")]
    public W10Channel? Preview { get; set; }
}

public sealed class W10Channel
{
    [JsonPropertyName("latest")]
    public string Latest { get; set; } = "";

    [JsonPropertyName("versions")]
    public Dictionary<string, W10VersionInfo> Versions { get; set; } = [];
}

public sealed class W10VersionInfo
{
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = "";

    [JsonPropertyName("updateIds")]
    public List<string> UpdateIds { get; set; } = [];
}

/// <summary>基岩版通道</summary>
public enum BedrockChannel
{
    Release,
    Beta,
    Preview,
}

/// <summary>基岩版安装进度</summary>
public sealed record BedrockInstallProgress(string Stage, string Detail, double Percent);

/// <summary>基岩版安装记录（bedrock/{version}/loya.json）</summary>
public sealed class BedrockInstallMeta
{
    public string Version { get; set; } = "";
    public string Guid { get; set; } = "";
    public BedrockChannel Channel { get; set; } = BedrockChannel.Release;
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}
