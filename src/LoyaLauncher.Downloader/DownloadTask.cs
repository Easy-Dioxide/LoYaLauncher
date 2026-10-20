namespace LoyaLauncher.Downloader;

/// <summary>下载任务状态</summary>
public enum DownloadStatus
{
    Queued,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// 单个文件下载任务。由 DownloadManager 驱动状态流转：
/// Queued → Downloading → Verifying → Completed / Failed
/// </summary>
public sealed class DownloadTask
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string DestinationPath { get; init; }

    /// <summary>期望的 SHA1（小写十六进制），为空则跳过校验</summary>
    public string? ExpectedSha1 { get; init; }

    /// <summary>UI 显示名称</summary>
    public string? DisplayName { get; init; }

    /// <summary>业务分组（如 "libraries" / "assets" / "natives"），用于统计与 UI 归类</summary>
    public string Group { get; init; } = "misc";

    public long TotalBytes { get; internal set; }
    public long ReceivedBytes { get; internal set; }
    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;
    public string? Error { get; internal set; }

    /// <summary>瞬时速度（字节/秒）</summary>
    public double BytesPerSecond { get; internal set; }
}

/// <summary>下载进度事件（经 EventBus 广播）</summary>
public sealed record DownloadProgressEvent(
    string TaskId, long ReceivedBytes, long TotalBytes, double BytesPerSecond, string Group);

/// <summary>下载完成事件</summary>
public sealed record DownloadCompletedEvent(string TaskId, bool Success, string? Error, string Group);
