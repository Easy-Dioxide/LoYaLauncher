using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LoyaLauncher.Core;

namespace LoyaLauncher.Pack;

/// <summary>
/// 启动器自动更新服务（GitHub Releases）。
///
/// 更新策略（三平台一致的"下次启动应用"方案）：
/// 1. CheckAsync 查询 GitHub Releases latest，与当前版本比较；
/// 2. PrepareUpdateAsync 下载平台对应的 asset 并解压到 updates/<版本>/，写入 pending 标记；
/// 3. 用户重启启动器后，Program.cs 调用 <see cref="ApplyPendingUpdate"/> 完成文件替换。
/// 这样做避免了 Windows 上运行中 exe/dll 无法覆盖的问题，也无需辅助脚本。
/// </summary>
public sealed class UpdateService
{
    private const string RepoOwner = "Easy-Dioxide";
    private const string RepoName = "LoyaLauncher";
    private const string PendingMarkerName = "pending.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        // GitHub API 强制要求 User-Agent
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "LoyaLauncher");
    }

    /// <summary>当前启动器版本（来自程序集 InformationalVersion，形如 1.2.3）</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";
            // 去掉 +commit 后缀
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
    }

    /// <summary>检查更新（失败不抛异常，错误信息封装进结果）</summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var release = await _http.GetFromJsonAsync<GitHubRelease>(url, ct);
            if (release is null || release.Draft)
                return new UpdateInfo(false, current, current);

            var latest = release.TagName.TrimStart('v', 'V');
            if (string.IsNullOrEmpty(latest))
                return new UpdateInfo(false, current, current, error: "Release 缺少版本号");

            var hasUpdate = CompareVersions(latest, current) > 0;
            if (!hasUpdate)
                return new UpdateInfo(false, current, latest, notes: release.Body);

            var asset = PickAssetForPlatform(release.Assets);
            return new UpdateInfo(
                true, current, latest,
                asset?.BrowserDownloadUrl, asset?.Size ?? 0,
                release.Body);
        }
        catch (Exception ex)
        {
            return new UpdateInfo(false, current, current, error: $"检查更新失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 下载更新包并解压就位（写入 pending 标记），等待下次启动应用。
    /// 返回 pending 目录路径。
    /// </summary>
    public async Task<string> PrepareUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!info.HasUpdate || info.DownloadUrl is null)
            throw new InvalidOperationException("没有可下载的更新");

        var updatesDir = Path.Combine(LoyaPaths.Root, "updates");
        var targetDir = Path.Combine(updatesDir, info.LatestVersion);
        var packagePath = Path.Combine(LoyaPaths.Cache, $"LoyaLauncher-{info.LatestVersion}.zip");

        Directory.CreateDirectory(updatesDir);
        Directory.CreateDirectory(LoyaPaths.Cache);

        // 1. 下载安装包
        progress?.Report(0.1);
        using (var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(packagePath);
            await source.CopyToAsync(dest, ct);
        }

        // 2. 解压到版本目录
        progress?.Report(0.6);
        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(packagePath, targetDir, overwriteFiles: true);
        File.Delete(packagePath);

        // 3. 写 pending 标记
        progress?.Report(0.9);
        var marker = new { Version = info.LatestVersion, SourceDir = targetDir, PreparedAt = DateTime.UtcNow };
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, PendingMarkerName),
            JsonSerializer.Serialize(marker, JsonOpts), ct);

        progress?.Report(1.0);
        return targetDir;
    }

    /// <summary>
    /// 应用已就绪的更新（在启动早期、旧进程完全退出后调用）。
    /// 有 pending 标记则替换应用目录文件并返回 true（调用方应随后重启）。
    /// </summary>
    public static bool ApplyPendingUpdate()
    {
        var updatesDir = Path.Combine(LoyaPaths.Root, "updates");
        var markerPath = Path.Combine(updatesDir, PendingMarkerName);
        if (!File.Exists(markerPath)) return false;

        try
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            var sourceDir = marker.RootElement.GetProperty("SourceDir").GetString();
            if (sourceDir is null || !Directory.Exists(sourceDir))
            {
                File.Delete(markerPath);
                return false;
            }

            // 应用目录：入口程序集所在目录（发布后即安装目录）
            var appDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;

            var copied = 0;
            foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, source);
                var dest = Path.Combine(appDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                // 当前正在运行的可执行文件无法覆盖，跳过（下次重启后自然生效）
                var running = Path.GetFullPath(Process.GetCurrentProcess().MainModule?.FileName ?? "");
                if (Path.GetFullPath(dest) == running) continue;

                File.Copy(source, dest, overwrite: true);
                copied++;
            }

            File.Delete(markerPath);
            Debug.WriteLine($"[UpdateService] 已应用 {copied} 个文件");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] 应用更新失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 语义化版本比较：按点分数字段逐个比较，缺省字段视为 0。
    /// 返回 >0 表示 a 更新，0 表示相同，&lt;0 表示 a 更旧。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = ParseVersionParts(a);
        var pb = ParseVersionParts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            var cmp = va.CompareTo(vb);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int[] ParseVersionParts(string version)
    {
        var clean = version.Trim().TrimStart('v', 'V');
        // 去掉预发布标签（如 -rc.1）后再拆分
        var dash = clean.IndexOf('-');
        if (dash > 0) clean = clean[..dash];
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return result;
    }

    /// <summary>
    /// 从 Release assets 中选择当前平台对应的安装包。
    /// 约定命名：LoyaLauncher-{rid}.zip（如 win-x64 / linux-x64 / osx-x64 / osx-arm64）。
    /// 找不到时返回 null。
    /// </summary>
    public static GitHubAsset? PickAssetForPlatform(List<GitHubAsset> assets)
    {
        var rid = LoyaPaths.PlatformId switch
        {
            "windows" => "win",
            "mac" => "osx",
            _ => "linux",
        } + "-" + (LoyaPaths.ArchId == "aarch64" ? "arm64" : "x64");

        return assets.FirstOrDefault(a =>
            a.Name.Equals($"LoyaLauncher-{rid}.zip", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}
