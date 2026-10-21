using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// 列出所有已下载的托管 JRE。跳过无可执行文件的损坏目录（如下载中断残留）。
    /// 结果按主版本升序。
    /// </summary>
    public List<ManagedJavaInfo> ListManagedRuntimes()
    {
        var result = new List<ManagedJavaInfo>();
        var root = LoyaPaths.JavaRuntimes;
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var m = Regex.Match(Path.GetFileName(dir), @"^java(\d+)$");
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major)) continue;

            var exe = GetManagedJavaPath(major);
            if (exe is null) continue; // 目录存在但无可执行文件：损坏或下载中，跳过

            var version = ReadReleaseVersion(dir) ?? ParseVersionFromDirName(dir);
            result.Add(new ManagedJavaInfo(major, version, GetDirectorySize(dir), exe));
        }
        return result.OrderBy(x => x.Major).ToList();
    }

    /// <summary>
    /// 删除指定主版本的托管 JRE。正在被 <see cref="JavaUsageRegistry"/> 登记占用时拒绝删除。
    /// </summary>
    public RemoveRuntimeResult RemoveManagedRuntime(int major)
    {
        if (JavaUsageRegistry.IsInUse(major)) return RemoveRuntimeResult.InUse;

        var dir = Path.Combine(LoyaPaths.JavaRuntimes, $"java{major}");
        if (!Directory.Exists(dir)) return RemoveRuntimeResult.NotFound;

        try
        {
            Directory.Delete(dir, true);
            return RemoveRuntimeResult.Removed;
        }
        catch (IOException)
        {
            // Windows 上 java.exe 被进程占用时抛出
            return RemoveRuntimeResult.InUse;
        }
        catch (UnauthorizedAccessException)
        {
            return RemoveRuntimeResult.InUse;
        }
    }

    /// <summary>从 JRE 内的 release 文件读取详细版本（JAVA_VERSION="17.0.2"）</summary>
    private static string? ReadReleaseVersion(string runtimeDir)
    {
        // MoveDirectory 后 release 位于 java{N}/ 根下；兼容多套一层目录的归档
        var candidates = new List<string> { Path.Combine(runtimeDir, "release") };
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(runtimeDir)
                .Take(1)
                .Select(d => Path.Combine(d, "release")));
        }
        catch { /* 目录不可访问，仅尝试根路径 */ }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var text = File.ReadAllText(candidate);
                var m = Regex.Match(text, "JAVA_VERSION=\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { /* 读取失败时尝试下一个候选 */ }
        }
        return null;
    }

    /// <summary>从解压目录名解析版本（jdk-17.0.2+8 / jre-17.0.9+11）</summary>
    private static string? ParseVersionFromDirName(string runtimeDir)
    {
        // MoveDirectory 后内容直接位于 java{N}/ 下，但兼容多套一层的归档
        foreach (var dir in new[] { runtimeDir }.Concat(
                     Directory.EnumerateDirectories(runtimeDir).Take(1)))
        {
            var m = Regex.Match(Path.GetFileName(dir), @"\d+\.\d+\.\d+(\+\d+)?");
            if (m.Success) return m.Value;
        }
        return null;
    }

    private static long GetDirectorySize(string dir)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch { /* 文件已被移走等情况，跳过 */ }
            }
        }
        catch { /* 目录不可访问 */ }
        return size;
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

/// <summary>删除托管 JRE 的结果</summary>
/// <summary>托管 JRE 列表项</summary>
/// <param name="Major">Java 主版本号（目录名 java{Major}）</param>
/// <param name="Version">详细版本号（如 17.0.2+8），解析失败为 null</param>
/// <param name="SizeBytes">占用磁盘字节数</param>
/// <param name="Path">java 可执行文件完整路径</param>
public sealed record ManagedJavaInfo(int Major, string? Version, long SizeBytes, string Path);

public enum RemoveRuntimeResult
{
    /// <summary>已删除</summary>
    Removed,
    /// <summary>目录不存在（从未下载或已删除）</summary>
    NotFound,
    /// <summary>正被运行中的游戏占用，或文件被锁定（Windows 文件句柄）</summary>
    InUse,
}
