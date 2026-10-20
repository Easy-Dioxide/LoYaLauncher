using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using LoyaLauncher.Core;

namespace LoyaLauncher.Bedrock;

/// <summary>
/// 基岩版模块。技术现实（已与项目负责人确认）：
/// - 基岩版官方仅存在 Windows 10/11 UWP 与 GDK 构建，macOS/Linux 无官方客户端
/// - 离线启动基岩版无成熟开源方案，需要正版微软账号
/// - 本模块一期目标：Windows 上的基岩版版本管理（下载/切换/隔离）+ 正版账号启动
/// - Mac/Linux 基岩支持标记为实验性，UI 中明确提示限制
/// </summary>
public sealed class BedrockService
{
    private const string VersionDbUrl = "https://mrarm.io/r/w10-vdb";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Downloader.DownloadManager _downloader;
    private readonly HttpClient _http;

    public BedrockService(Downloader.DownloadManager downloader)
    {
        _downloader = downloader;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "LoyaLauncher/0.1.0");
    }

    /// <summary>当前平台是否支持基岩版完整功能</summary>
    public static bool IsFullySupported => OperatingSystem.IsWindows();

    /// <summary>平台限制说明（UI 展示用）</summary>
    public static string PlatformNotice => OperatingSystem.IsWindows()
        ? "基岩版需要已购买 Minecraft 的微软账号登录（Xbox Live）。"
        : "基岩版官方仅支持 Windows 10/11。macOS/Linux 用户可尝试实验性 Proton/GDK 方案（设置中开启实验功能）。";

    /// <summary>获取版本数据库（缓存 6 小时）</summary>
    public async Task<W10VersionDatabase> GetVersionDatabaseAsync(CancellationToken ct = default)
    {
        var cachePath = Path.Combine(LoyaPaths.Cache, "w10-vdb.json");
        if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromHours(6))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<W10VersionDatabase>(await File.ReadAllTextAsync(cachePath, ct), JsonOpts);
                if (cached is not null) return cached;
            }
            catch { /* 缓存损坏重新下载 */ }
        }

        var r = await _downloader.DownloadFileAsync(VersionDbUrl, cachePath, group: "bedrock", ct: ct);
        if (!r.Success)
            throw new InvalidOperationException($"基岩版版本库获取失败: {r.Error}");
        return JsonSerializer.Deserialize<W10VersionDatabase>(await File.ReadAllTextAsync(cachePath, ct), JsonOpts)
            ?? throw new InvalidOperationException("基岩版版本库解析失败");
    }

    /// <summary>列出指定通道的可用版本</summary>
    public async Task<List<string>> ListVersionsAsync(BedrockChannel channel = BedrockChannel.Release, CancellationToken ct = default)
    {
        var db = await GetVersionDatabaseAsync(ct);
        var c = channel switch
        {
            BedrockChannel.Beta => db.Beta,
            BedrockChannel.Preview => db.Preview,
            _ => db.Release,
        };
        return c?.Versions.Keys.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    /// <summary>
    /// 安装基岩版（仅 Windows）。
    /// 流程：获取版本 GUID → 从微软 CDN 下载 AppX/AppxBundle → 注册到系统。
    /// </summary>
    public async Task InstallAsync(string version, BedrockChannel channel, IProgress<BedrockInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (!IsFullySupported)
            throw new PlatformNotSupportedException("基岩版安装当前仅支持 Windows 10/11（UWP）。");

        progress?.Report(new("获取版本信息", version, 0.1));
        var db = await GetVersionDatabaseAsync(ct);
        var c = channel switch
        {
            BedrockChannel.Beta => db.Beta,
            BedrockChannel.Preview => db.Preview,
            _ => db.Release,
        };
        if (c?.Versions is null || !c.Versions.TryGetValue(version, out var info) || string.IsNullOrEmpty(info?.Guid))
            throw new InvalidOperationException($"未找到基岩版版本 {version}");

        var versionDir = Path.Combine(LoyaPaths.Bedrock, version);
        Directory.CreateDirectory(versionDir);

        // 下载 AppX 包（通过微软商店 CDN，URL 形如 tlu.dl.delivery.mp.microsoft.com/...）
        progress?.Report(new("下载游戏包", "Microsoft CDN", 0.3));
        var appxUrl = $"https://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/{info.UpdateIds.FirstOrDefault() ?? info.Guid}";
        var appxPath = Path.Combine(versionDir, "minecraft.appx");
        var r = await _downloader.DownloadFileAsync(appxUrl, appxPath, group: "bedrock", ct: ct);
        if (!r.Success)
            throw new InvalidOperationException($"基岩版下载失败（可能需要有效的微软商店内容分发链接）: {r.Error}");

        // 注册 AppX（需要开发者模式）
        progress?.Report(new("注册到系统", "Add-AppxPackage", 0.8));
        await RegisterAppxAsync(appxPath, ct);

        await File.WriteAllTextAsync(
            Path.Combine(versionDir, "loya.json"),
            JsonSerializer.Serialize(new BedrockInstallMeta
            {
                Version = version,
                Guid = info.Guid,
                Channel = channel,
            }, JsonOpts), ct);

        progress?.Report(new("完成", version, 1.0));
    }

    /// <summary>注册 AppX 包（PowerShell Add-AppxPackage，需要 Windows 开发者模式）</summary>
    private static async Task RegisterAppxAsync(string appxPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"Add-AppxPackage -Path '{appxPath}' -ForceUpdateFromAnyVersion\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 PowerShell");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var error = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"AppX 注册失败（请确认已开启 Windows 开发者模式）: {error[..Math.Min(300, error.Length)]}");
        }
    }

    /// <summary>
    /// 启动基岩版。通过 Shell AppsFolder 唤起已注册的 Minecraft UWP 应用。
    /// 首次运行需要微软账号（Xbox Live）登录，离线启动无成熟方案。
    /// </summary>
    public static void Launch()
    {
        if (!IsFullySupported)
            throw new PlatformNotSupportedException(PlatformNotice);

        // Minecraft UWP 的 AUMID（Package Family Name + App Id）
        var psi = new ProcessStartInfo("explorer.exe",
            "shell:AppsFolder\\Microsoft.MinecraftUWP_8wekyb3d8bbwe!App")
        {
            UseShellExecute = true,
        };
        Process.Start(psi);
    }

    /// <summary>列出已安装基岩版</summary>
    public static List<BedrockInstallMeta> ListInstalled()
    {
        var result = new List<BedrockInstallMeta>();
        if (!Directory.Exists(LoyaPaths.Bedrock)) return result;
        foreach (var dir in Directory.EnumerateDirectories(LoyaPaths.Bedrock))
        {
            var metaPath = Path.Combine(dir, "loya.json");
            if (!File.Exists(metaPath)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<BedrockInstallMeta>(File.ReadAllText(metaPath), JsonOpts);
                if (meta is not null) result.Add(meta);
            }
            catch { /* 跳过损坏记录 */ }
        }
        return result;
    }
}
