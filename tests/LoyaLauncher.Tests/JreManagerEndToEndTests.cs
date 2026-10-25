using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>
/// JreManager 端到端测试（清单 §6/§65/§11）：
/// - EnsureJavaAsync：已托管零请求、下载→安全解压→java -version 验证→主版本核对；
/// - 校验失败（版本不符 / 无法执行 / 架构不匹配）按候选失败处理，不静默；
/// - 恶意 tar 条目（目录穿越）拒绝。
/// 候选源经工厂注入指向本地 mock，绝不真实访问清华/Adoptium。
/// </summary>
public sealed class JreManagerEndToEndTests : IDisposable
{
    private readonly MockHttpServer _server = new();
    private readonly string _tempDir;
    private readonly string _originalRoot;
    private readonly EventBus _bus = new();

    public JreManagerEndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "loya-jre-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalRoot = LoyaPaths.Root;
        LoyaPaths.Root = Path.Combine(_tempDir, "root");
        LoyaPaths.EnsureDirectories();
    }

    public void Dispose()
    {
        LoyaPaths.Root = _originalRoot;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private DownloadManager CreateManager(int maxAttempts = 2)
        => new(_bus, maxConcurrency: 2, maxAttempts: maxAttempts,
            headersTimeoutSeconds: 15, stallTimeoutSeconds: 15);

    /// <summary>构造含指定 java 版本输出的假 JRE tar.gz（jdk-{ver}/bin/java 脚本 + release 文件）</summary>
    private static byte[] MakeFakeJreTgz(string versionOutput, bool exitZero = true, string? extraEntry = null)
    {
        using var tarMs = new MemoryStream();
        using (var tarWriter = new TarWriter(tarMs, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var script = exitZero
                ? $"#!/bin/sh\necho 'openjdk version \"{versionOutput}\" 2025-01-01' >&2\n"
                : "#!/bin/sh\nexit 3\n";

            AddFile(tarWriter, "jdk-17.0.2+8/bin/java", script);
            // Windows 上 GetManagedJavaPath 查找的是 java.exe。补一个同名条目，
            // 使"文件已就位"的路径在 Windows 上同样能走到（内容非 PE，执行必然失败——
            // 正好用于验证"无法执行"的失败分支；成功路径经注入探针验证）。
            if (OperatingSystem.IsWindows())
                AddFile(tarWriter, "jdk-17.0.2+8/bin/java.exe", script);
            AddFile(tarWriter, "jdk-17.0.2+8/release", "JAVA_VERSION=\"17.0.2\"\nIMPLEMENTOR_VERSION=\"Temurin\"\n");
            if (extraEntry is not null)
                AddFile(tarWriter, extraEntry, "escaped");
        }
        tarMs.Position = 0;

        using var gzMs = new MemoryStream();
        using (var gz = new GZipStream(gzMs, CompressionMode.Compress, leaveOpen: true))
            tarMs.CopyTo(gz);
        return gzMs.ToArray();
    }

    private static void AddFile(TarWriter writer, string name, string content)
    {
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        };
        writer.WriteEntry(entry);
    }

    /// <summary>模拟无系统 Java 的探测结果（隔离测试机已安装的 JDK）</summary>
    private static Task<(string? Path, int Major)> NoSystemJava(CancellationToken _)
        => Task.FromResult<(string?, int)>((null, 0));

    [Fact]
    public async Task EnsureJava_系统java满足要求_直接复用零请求()
    {
        using var dm = CreateManager();
        var manager = new JreManager(dm,
            candidateFactory: null,
            systemJavaProbe: _ => Task.FromResult<(string?, int)>(("/usr/bin/java", 21)));

        var result = await manager.EnsureJavaAsync(17);

        Assert.Equal("/usr/bin/java", result);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task EnsureJava_已托管则直接返回_零网络请求()
    {
        var binDir = Path.Combine(LoyaPaths.JavaRuntimes, "java17", "bin");
        Directory.CreateDirectory(binDir);
        var javaPath = Path.Combine(binDir, LoyaPaths.JavaExecutableName);
        await File.WriteAllTextAsync(javaPath, "#!/bin/sh\necho 'openjdk version \"17\"' >&2\n");
        // Unix 文件模式 API 在 Windows 抛 PlatformNotSupportedException（CI 实锤），
        // 且 Windows 不需要执行位——按平台跳过
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(javaPath,
                File.GetUnixFileMode(javaPath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        using var dm = CreateManager();
        var manager = new JreManager(dm);

        var result = await manager.EnsureJavaAsync(17);

        Assert.Equal(javaPath, result);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task EnsureJava_下载解压并验证java版本_成功返回可执行路径()
    {
        var tgz = MakeFakeJreTgz("17.0.2");
        _server.Handler = _ => new MockResponse(200, "application/gzip", tgz);

        using var dm = CreateManager();
        // 注入版本探针：假 java 是 shell 脚本，Windows 上无法执行。
        // 探针注入后，"下载→安全解压→查找可执行文件（Windows 为 java.exe）→
        // 核对主版本→搬正 release 文件"的完整链路在三平台都可真实验证。
        var manager = new JreManager(dm,
            (_, _) => Task.FromResult(new List<(string, string)> { (_server.Url("jre17.tar.gz"), ".tar.gz") }),
            NoSystemJava,
            (_, _) => Task.FromResult(17));

        var javaPath = await manager.EnsureJavaAsync(17);

        Assert.True(File.Exists(javaPath));
        Assert.Contains("java17", javaPath.Replace('\\', '/'));

        // Unix：解压出的 java 真实可执行且报告 17。
        // Windows 上假 java 非 PE 映像、必然无法执行，真实探测断言仅限 Unix。
        if (!OperatingSystem.IsWindows())
            Assert.Equal(17, await JreManager.ProbeJavaMajorAsync(javaPath));

        // release 文件被正确搬入，ListManagedRuntimes 能读到详细版本
        var runtimes = manager.ListManagedRuntimes();
        var info = Assert.Single(runtimes);
        Assert.Equal(17, info.Major);
        Assert.Equal("17.0.2", info.Version);
    }

    [Fact]
    public async Task EnsureJava_下载的java主版本不符_校验失败并报错()
    {
        var tgz = MakeFakeJreTgz("21.0.1"); // 请求 17，实际 21
        _server.Handler = _ => new MockResponse(200, "application/gzip", tgz);

        using var dm = CreateManager();
        var manager = new JreManager(dm,
            (_, _) => Task.FromResult(new List<(string, string)> { (_server.Url("jre17.tar.gz"), ".tar.gz") }),
            NoSystemJava,
            (_, _) => Task.FromResult(21)); // 探针报告 21：请求 17，实际 21

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureJavaAsync(17));

        Assert.Contains("校验失败", ex.Message);
        Assert.Contains("21", ex.Message);
    }

    [Fact]
    public async Task EnsureJava_下载的java无法执行_校验失败并报错()
    {
        var tgz = MakeFakeJreTgz("17.0.2", exitZero: false);
        _server.Handler = _ => new MockResponse(200, "application/gzip", tgz);

        using var dm = CreateManager();
        var manager = new JreManager(dm,
            (_, _) => Task.FromResult(new List<(string, string)> { (_server.Url("jre17.tar.gz"), ".tar.gz") }),
            NoSystemJava);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureJavaAsync(17));

        Assert.Contains("无法执行", ex.Message);
    }

    [Fact]
    public async Task EnsureJava_下载404_报错含主机且不静默()
    {
        _server.Handler = _ => new MockResponse(404, "text/plain", "not found"u8.ToArray());

        using var dm = CreateManager(maxAttempts: 1);
        var manager = new JreManager(dm,
            (_, _) => Task.FromResult(new List<(string, string)> { (_server.Url("jre17.tar.gz"), ".tar.gz") }),
            NoSystemJava);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureJavaAsync(17));

        Assert.Contains("127.0.0.1", ex.Message);
        Assert.Contains("下载失败", ex.Message);
    }

    [Fact]
    public async Task EnsureJava_恶意tar条目穿越_拒绝且不落盘()
    {
        var tgz = MakeFakeJreTgz("17.0.2", extraEntry: "../escaped-jre.txt");
        _server.Handler = _ => new MockResponse(200, "application/gzip", tgz);

        using var dm = CreateManager();
        var manager = new JreManager(dm,
            (_, _) => Task.FromResult(new List<(string, string)> { (_server.Url("jre17.tar.gz"), ".tar.gz") }),
            NoSystemJava);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureJavaAsync(17));

        Assert.False(File.Exists(Path.Combine(_tempDir, "escaped-jre.txt")));
    }

    [Fact]
    public async Task EnsureJava_首个候选失败_自动尝试下一个候选()
    {
        var tgz = MakeFakeJreTgz("17.0.2");
        var attempt = 0;
        _server.Handler = _ =>
        {
            attempt++;
            // 第一个 URL 404，第二个成功
            return attempt == 1
                ? new MockResponse(404, "text/plain", "no"u8.ToArray())
                : new MockResponse(200, "application/gzip", tgz);
        };

        using var dm = CreateManager(maxAttempts: 1);
        var manager = new JreManager(dm,
            (_, _) => Task.FromResult(new List<(string, string)>
            {
                (_server.Url("bad.tar.gz"), ".tar.gz"),
                (_server.Url("good.tar.gz"), ".tar.gz"),
            }),
            NoSystemJava,
            (_, _) => Task.FromResult(17)); // 注入探针：链路在三平台可验证（Windows 假 java 无法真实执行）

        var javaPath = await manager.EnsureJavaAsync(17);

        Assert.True(File.Exists(javaPath));
        Assert.Equal(2, attempt);
    }

    // ======================= ProbeJavaMajorAsync =======================

    [Fact]
    public async Task ProbeJava_解析stderr版本串()
    {
        var script = Path.Combine(_tempDir, "java17.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho 'openjdk version \"17.0.2\" 2025-01-01' >&2\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
            Assert.Equal(17, await JreManager.ProbeJavaMajorAsync(script));
        }
        else
        {
            // Windows 上 .sh 不是可执行映像（CreateProcess 直接失败）：
            // 探测必须安全返回 0 而不是把异常抛给调用方
            Assert.Equal(0, await JreManager.ProbeJavaMajorAsync(script));
        }
    }

    [Fact]
    public async Task ProbeJava_非零退出返回0()
    {
        var script = Path.Combine(_tempDir, "java-bad.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nexit 1\n");
        // Unix 文件模式 API 在 Windows 抛 PlatformNotSupportedException（CI 实锤）
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);

        Assert.Equal(0, await JreManager.ProbeJavaMajorAsync(script));
    }

    [Fact]
    public async Task ProbeJava_不存在的路径返回0()
    {
        Assert.Equal(0, await JreManager.ProbeJavaMajorAsync(Path.Combine(_tempDir, "no-such-java")));
    }

    // ======================= GetRequiredJavaMajor（主版本推断）=======================

    [Theory]
    [InlineData("rd-132211", 8)]        // 远古版本无声明
    [InlineData("1.12.2", 8)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17", 16)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.20", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.1", 21)]
    [InlineData("25w14craftmine", 21)] // 新版本号格式（无 1.x 前缀）
    public void 主版本推断_覆盖各代Minecraft(string versionId, int expected)
    {
        var json = new VersionJson { Id = versionId };
        Assert.Equal(expected, JavaInstaller.GetRequiredJavaMajor(json));
    }

    [Fact]
    public void 主版本推断_版本清单声明优先()
    {
        var json = new VersionJson
        {
            Id = "1.20.4",
            JavaVersion = new JavaVersionRef { Component = "jre-legacy", MajorVersion = 17 },
        };
        Assert.Equal(17, JavaInstaller.GetRequiredJavaMajor(json));
    }
}
