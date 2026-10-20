using System.Diagnostics;
using System.IO.Compression;
using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>
/// Java 运行时管理。策略：
/// 1. 优先检测系统已安装 Java（java -version 解析主版本）
/// 2. 不足时自动下载：BMCLAPI Java 镜像优先，Adoptium Temurin 回退
/// </summary>
public sealed class JreManager
{
    private readonly Downloader.DownloadManager _downloader;

    public JreManager(Downloader.DownloadManager downloader)
    {
        _downloader = downloader;
    }

    /// <summary>已托管的 JRE 可执行文件路径（不存在返回 null）</summary>
    public static string? GetManagedJavaPath(int majorVersion)
    {
        var dir = Path.Combine(LoyaPaths.JavaRuntimes, $"java{majorVersion}");
        if (!Directory.Exists(dir)) return null;

        // 解压后的目录名不确定（jdk-17.0.2+8 / jre-17 等），递归查找可执行文件
        var exeName = LoyaPaths.JavaExecutableName;
        var binDir = Path.Combine(dir, "bin", exeName);
        if (File.Exists(binDir)) return binDir;

        foreach (var candidate in Directory.EnumerateFiles(dir, exeName, SearchOption.AllDirectories))
            return candidate;
        return null;
    }

    /// <summary>检测系统 Java 主版本（未安装返回 0）</summary>
    public static async Task<(string? Path, int Major)> DetectSystemJavaAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("java", "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (null, 0);
            var output = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var m = System.Text.RegularExpressions.Regex.Match(output, @"version ""(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var major))
            {
                var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
                var path = !string.IsNullOrEmpty(javaHome)
                    ? Path.Combine(javaHome, "bin", LoyaPaths.JavaExecutableName)
                    : "java";
                return (path, major);
            }
        }
        catch { /* 系统无 Java 或不在 PATH */ }
        return (null, 0);
    }

    /// <summary>确保存在满足要求的 Java，返回可执行文件路径</summary>
    public async Task<string> EnsureJavaAsync(int majorVersion, CancellationToken ct = default)
    {
        // 1. 已托管的
        var managed = GetManagedJavaPath(majorVersion);
        if (managed is not null) return managed;

        // 2. 系统 Java 满足版本
        var (sysPath, sysMajor) = await DetectSystemJavaAsync(ct);
        if (sysPath is not null && sysMajor >= majorVersion)
            return sysPath;

        // 3. 下载托管 JRE
        return await DownloadJreAsync(majorVersion, ct);
    }

    private async Task<string> DownloadJreAsync(int majorVersion, CancellationToken ct)
    {
        var targetDir = Path.Combine(LoyaPaths.JavaRuntimes, $"java{majorVersion}");
        Directory.CreateDirectory(targetDir);

        var candidates = new List<(string Url, string Ext)>
        {
            // BMCLAPI Java 镜像（国内快）
            ($"https://bmclapi2.bangbang93.com/java/{majorVersion}/{LoyaPaths.PlatformId}/{LoyaPaths.ArchId}", ".zip"),
            // Adoptium Temurin 官方 API
            ($"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/{LoyaPaths.PlatformId}/{LoyaPaths.ArchId}/jre/hotspot/normal/eclipse", ".zip"),
        };

        Exception? lastError = null;
        foreach (var (url, ext) in candidates)
        {
            var archive = Path.Combine(LoyaPaths.Cache, $"jre-{majorVersion}{ext}");
            try
            {
                var r = await _downloader.DownloadFileAsync(url, archive, group: "java", ct: ct);
                if (!r.Success) continue;

                var extractDir = Path.Combine(LoyaPaths.Cache, $"jre-{majorVersion}-extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                await Task.Run(async () =>
                {
                    if (ext == ".zip")
                        ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
                    else
                    {
                        // tgz.gz：先 gzip 解压再 tar 展开
                        await using var inStream = File.OpenRead(archive);
                        await using var gzStream = new System.IO.Compression.GZipStream(inStream, CompressionMode.Decompress);
                        using var tarStream = new MemoryStream();
                        await gzStream.CopyToAsync(tarStream, ct);
                        tarStream.Position = 0;
                        System.Formats.Tar.TarFile.ExtractToDirectory(tarStream, extractDir, overwriteFiles: true);
                    }
                }, ct);

                // 将解压内容移动到目标目录
                var inner = Directory.EnumerateDirectories(extractDir).FirstOrDefault();
                var sourceRoot = inner ?? extractDir;
                MoveDirectory(sourceRoot, targetDir);

                var javaPath = GetManagedJavaPath(majorVersion)
                    ?? throw new InvalidOperationException("JRE 解压后未找到可执行文件");
                // Linux/macOS 需要执行权限
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(javaPath,
                        File.GetUnixFileMode(javaPath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
                return javaPath;
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw new InvalidOperationException($"Java {majorVersion} 下载失败: {lastError?.Message}");
    }

    private static void MoveDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
    }
}
