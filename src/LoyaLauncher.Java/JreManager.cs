using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>
/// Java 运行时管理。策略：
/// 1. 优先检测系统已安装 Java（java -version 解析主版本）
/// 2. 不足时自动下载：清华大学 Adoptium 镜像优先（国内快），Adoptium 官方 API 次之，
///    BMCLAPI Java 镜像兜底（当前 /java/* 整条 403，保留仅为将来恢复时的国内加速）
/// </summary>
public sealed class JreManager
{
    private readonly Downloader.DownloadManager _downloader;
    private readonly Func<int, CancellationToken, Task<List<(string Url, string Ext)>>>? _candidateFactory;
    private readonly Func<CancellationToken, Task<(string? Path, int Major)>>? _systemJavaProbe;
    private readonly Func<string, CancellationToken, Task<int>>? _javaProbe;

    public JreManager(
        Downloader.DownloadManager downloader,
        Func<int, CancellationToken, Task<List<(string Url, string Ext)>>>? candidateFactory = null,
        Func<CancellationToken, Task<(string? Path, int Major)>>? systemJavaProbe = null,
        Func<string, CancellationToken, Task<int>>? javaProbe = null)
    {
        _downloader = downloader;
        _candidateFactory = candidateFactory;
        _systemJavaProbe = systemJavaProbe;
        _javaProbe = javaProbe;
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

        // 2. 系统 Java 满足版本（含上限约束，见 IsAcceptable）
        var (sysPath, sysMajor) = _systemJavaProbe is not null
            ? await _systemJavaProbe(ct)
            : await DetectSystemJavaAsync(ct);
        if (sysPath is not null && IsAcceptable(sysMajor, majorVersion))
            return sysPath;

        // 3. 下载托管 JRE
        return await DownloadJreAsync(majorVersion, ct);
    }

    /// <summary>
    /// 系统 Java 能否担此任：主版本必须 ≥ 要求值；且当要求为 Java 8 时只接受 Java 8——
    /// LWJGL 2.x（MC 1.13 以前依赖）与 Java 9+ 不兼容（libjawt.so 的
    /// SUNWprivate_1.1 符号自 JDK 9 起被移除），用高版本 Java 将就会在
    /// 加载本地库时直接崩溃。要求 Java 16/17/21 的版本则接受不低于它的系统 Java。
    /// </summary>
    private static bool IsAcceptable(int sysMajor, int required) =>
        required <= 8 ? sysMajor == 8 : sysMajor >= required;

    private async Task<string> DownloadJreAsync(int majorVersion, CancellationToken ct)
    {
        var targetDir = Path.Combine(LoyaPaths.JavaRuntimes, $"java{majorVersion}");
        Directory.CreateDirectory(targetDir);

        var candidates = _candidateFactory is not null
            ? await _candidateFactory(majorVersion, ct)
            : await BuildJreCandidatesAsync(majorVersion, ct);

        var errors = new List<string>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (url, ext) = candidates[i];
            var host = new Uri(url).Host;
            // 每个候选独立归档名：避免上一源残留的 .part 被下一源断点续传拼成损坏文件
            var archive = Path.Combine(LoyaPaths.Cache, $"jre-{majorVersion}-{i}{ext}");
            try
            {
                var r = await _downloader.DownloadFileAsync(url, archive, group: "java", ct: ct);
                if (!r.Success)
                {
                    errors.Add($"{host}: {r.Error}");
                    continue;
                }

                var extractDir = Path.Combine(LoyaPaths.Cache, $"jre-{majorVersion}-extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                // TarFile.ExtractToDirectory 不像 ZipFile.ExtractToDirectory 那样会自动
                // 创建目标目录，必须显式创建，否则 Linux/macOS 的 tar.gz 解压必抛
                // DirectoryNotFoundException
                Directory.CreateDirectory(extractDir);
                await Task.Run(() =>
                {
                    // 统一走 SafeArchive：逐条目校验目录穿越并跳过符号链接条目
                    // （清单 §10：Java 解压必须防目录穿越）
                    if (ext == ".zip")
                        SafeArchive.ExtractZipSafely(archive, extractDir);
                    else
                    {
                        // tgz.gz：先 gzip 解压再 tar 展开
                        using var inStream = File.OpenRead(archive);
                        using var gzStream = new GZipStream(inStream, CompressionMode.Decompress);
                        using var tarStream = new MemoryStream();
                        gzStream.CopyTo(tarStream);
                        tarStream.Position = 0;
                        SafeArchive.ExtractTarSafely(tarStream, extractDir);
                    }
                }, ct);

                // 清空目标目录再搬入：上一候选可能留下了不完整的解压残留
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                Directory.CreateDirectory(targetDir);

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

                // 清单 §65：解压后必须实际运行 java -version 验证可用性，
                // 并核对主版本——下载到错误架构/损坏包时文件可能"存在但跑不起来"，
                // 只在启动时才暴露会更难排查。验证失败按候选失败继续尝试下一源。
                // 探针可注入（与 systemJavaProbe 同模式）：测试在无真实 JRE 可执行的
                // 平台上（如 Windows 上的假 java 脚本）也能验证完整链路；默认走真实探测。
                var detected = await (_javaProbe?.Invoke(javaPath, ct) ?? ProbeJavaMajorAsync(javaPath, ct));
                if (detected == 0)
                    throw new InvalidOperationException("JRE 校验失败: java -version 无法执行（架构不匹配或文件损坏）");
                if (detected != majorVersion)
                    throw new InvalidOperationException(
                        $"JRE 校验失败: java -version 报告主版本 {detected}，期望 {majorVersion}");

                return javaPath;
            }
            catch (Exception ex) { errors.Add($"{host}: {ex.Message}"); }
        }
        throw new InvalidOperationException(
            $"Java {majorVersion} 下载失败（已尝试 {candidates.Count} 个源）: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// 运行 &lt;c&gt;java -version&lt;/c&gt; 并解析主版本号（清单 §65 下载后验证）。
    /// 无法启动 / 非零退出 / 无法解析一律返回 0，由调用方按候选失败处理。
    /// </summary>
    public static async Task<int> ProbeJavaMajorAsync(string javaExecutable, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(javaExecutable, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return 0;
            var output = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return 0;
            var m = Regex.Match(output, @"version ""(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 构建 JRE 下载候选源（按优先级排序）。附带归档扩展名：
    /// Windows 为 .zip，Linux/macOS 为 .tar.gz（Adoptium 系发布物约定）。
    /// </summary>
    private async Task<List<(string Url, string Ext)>> BuildJreCandidatesAsync(int majorVersion, CancellationToken ct)
    {
        var ext = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
        var os = LoyaPaths.PlatformId;
        var arch = LoyaPaths.ArchId;
        var candidates = new List<(string, string)>();

        // 1. 清华大学 Adoptium 镜像（国内快）。JRE 优先（体积约为 JDK 一半），
        //    无 JRE 构建时（如 Java 16）自动退 JDK。
        //    目录 HTML 解析失败（清单 §64：不得作为唯一来源）时记录原因并继续，
        //    由下方 Adoptium 官方 API 兜底。
        foreach (var kind in new[] { "jre", "jdk" })
        {
            var listing = await _downloader.GetStringAsync(
                $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{majorVersion}/{kind}/{arch}/{os}/", ct);
            var file = PickLatestAsset(listing, $"OpenJDK{majorVersion}U-{kind}_{arch}_{os}_hotspot_", ext);
            if (file is not null)
                candidates.Add((
                    $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{majorVersion}/{kind}/{arch}/{os}/{file}", ext));
            else if (listing is null)
                Debug.WriteLine($"[JreManager] 清华 Adoptium 目录不可读（java{majorVersion}/{kind}），改走官方 API");
        }

        // 2. Adoptium 官方 API（latest 别名，全球可达）
        foreach (var kind in new[] { "jre", "jdk" })
            candidates.Add((
                $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/{os}/{arch}/{kind}/hotspot/normal/eclipse", ext));

        // 3. BMCLAPI Java 镜像（当前整条 403，保留仅为将来恢复时的国内加速）
        candidates.Add(($"https://bmclapi2.bangbang93.com/java/{majorVersion}/{os}/{arch}", ext));

        return candidates;
    }

    /// <summary>
    /// 从镜像目录列表 HTML 中挑选指定前缀 + 扩展名的最新版本文件名。
    /// 版本号形如 <c>8u504b01</c> 或 <c>17.0.9_9</c>，按数字段逐段比较。
    /// 列表为空/无匹配返回 null（目录不存在或尚未发布构建）。
    /// </summary>
    public static string? PickLatestAsset(string? listingHtml, string filePrefix, string ext)
    {
        if (string.IsNullOrEmpty(listingHtml)) return null;
        var matches = Regex.Matches(
            listingHtml, $@"href=""{Regex.Escape(filePrefix)}(?<ver>[^""]+?){Regex.Escape(ext)}""");

        string? best = null;
        int[] bestKey = [];
        foreach (Match m in matches)
        {
            var ver = m.Groups["ver"].Value;
            var key = Regex.Matches(ver, @"\d+").Select(x => int.Parse(x.Value)).ToArray();
            if (key.Length == 0) continue;
            if (best is null || CompareVersionKeys(key, bestKey) > 0)
            {
                best = filePrefix + ver + ext;
                bestKey = key;
            }
        }
        return best;
    }

    /// <summary>版本号数字段逐段比较（短的一方补 0），用于同主版本内排序</summary>
    private static int CompareVersionKeys(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            var cmp = x.CompareTo(y);
            if (cmp != 0) return cmp;
        }
        return 0;
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
