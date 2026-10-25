using System.IO.Compression;
using System.Formats.Tar;
using LoyaLauncher.Core;

namespace LoyaLauncher.Tests;

/// <summary>SafePath 路径穿越防护测试（清单 §10/§11）</summary>
public sealed class SafePathTests
{
    private readonly string _baseDir;

    public SafePathTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "loya-safepath", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    [Theory]
    [InlineData("mods/foo.jar", true)]
    [InlineData("a/b/c/d.txt", true)]
    [InlineData("中文模组.jar", true)]
    [InlineData("../outside.txt", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("mods/../../outside.jar", false)]
    [InlineData("config/../config/options.txt", false)]
    [InlineData("./relative.txt", false)]
    [InlineData("/absolute/path.txt", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsSafeEntry_按规则判定(string entry, bool expected)
    {
        Assert.Equal(expected, SafePath.IsSafeEntry(_baseDir, entry));
    }

    [Fact]
    public void IsSafeEntry_Windows盘符路径_仅Windows拒绝()
    {
        // Linux 上反斜杠不是路径分隔符（属普通文件名，落点仍在基目录内）；
        // Windows 上 IsPathRooted 识别盘符，必须拒绝。
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(SafePath.IsSafeEntry(_baseDir, "C:\\Windows\\system32\\evil.dll"));
    }

    [Fact]
    public void ResolveEntry_合法路径返回基目录内完整路径()
    {
        var resolved = SafePath.ResolveEntry(_baseDir, "mods/foo.jar");
        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(_baseDir), resolved);
        Assert.EndsWith("foo.jar", resolved);
    }

    [Fact]
    public void ResolveEntry_越界返回null()
    {
        Assert.Null(SafePath.ResolveEntry(_baseDir, "../evil.txt"));
    }

    [Theory]
    [InlineData("sodium.jar", true)]
    [InlineData("my mod v1.2.jar", true)]
    [InlineData("中文模组.jar", true)]
    [InlineData("../evil.jar", false)]
    [InlineData("dir/evil.jar", false)]
    [InlineData("dir\\evil.jar", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("com1.jar", false)]
    [InlineData("", false)]
    public void IsSafeFileName_按规则判定(string name, bool expected)
    {
        Assert.Equal(expected, SafePath.IsSafeFileName(name));
    }

    [Fact]
    public void SanitizeFileName_剥离目录部分()
    {
        Assert.Equal("evil.jar", SafePath.SanitizeFileName("/some/dir/evil.jar"));
        Assert.Equal("evil.jar", SafePath.SanitizeFileName("C:\\dir\\evil.jar"));
        Assert.Null(SafePath.SanitizeFileName("../.."));
    }

    [Fact]
    public void EscapesViaSymlink_链接逃逸被识别()
    {
        // baseDir/inside -> baseDir 外的 outside：经 inside 落盘即逃逸
        var outside = Path.Combine(Path.GetTempPath(), "loya-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        var insideLink = Path.Combine(_baseDir, "inside");
        try
        {
            Directory.CreateSymbolicLink(insideLink, outside);
            var dest = Path.Combine(insideLink, "evil.txt");
            Assert.True(SafePath.EscapesViaSymlink(_baseDir, dest));
            // 普通路径不误报
            Assert.False(SafePath.EscapesViaSymlink(_baseDir, Path.Combine(_baseDir, "normal.txt")));
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }
}

/// <summary>SafeArchive 恶意归档解压测试</summary>
public sealed class SafeArchiveTests : IDisposable
{
    private readonly string _baseDir;

    public SafeArchiveTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "loya-safearchive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    [Fact]
    public void ExtractZipSafely_正常归档全部解出()
    {
        var src = Path.Combine(_baseDir, "src.txt");
        File.WriteAllText(src, "hello");
        var zipPath = Path.Combine(_baseDir, "ok.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("dir/"); // 目录条目
            zip.CreateEntryFromFile(src, "dir/hello.txt");
        }
        var dest = Path.Combine(_baseDir, "out-ok");

        SafeArchive.ExtractZipSafely(zipPath, dest);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "dir", "hello.txt")));
    }

    [Fact]
    public void ExtractZipSafely_拒绝穿越条目()
    {
        var zipPath = Path.Combine(_baseDir, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escaped.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("pwned");
        }
        var dest = Path.Combine(_baseDir, "out-evil");

        var ex = Assert.Throws<InvalidDataException>(() => SafeArchive.ExtractZipSafely(zipPath, dest));
        Assert.Contains("非法路径", ex.Message);
        Assert.False(File.Exists(Path.Combine(_baseDir, "escaped.txt")));
    }

    [Fact]
    public void ExtractZipSafely_跳过符号链接条目()
    {
        var zipPath = Path.Combine(_baseDir, "link.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("evil-link");
            // 设置 Unix 符号链接模式（S_IFLNK = 0xA000）
            entry.ExternalAttributes = unchecked((int)(0xA1FF << 16));
            using var w = new StreamWriter(entry.Open());
            w.Write("/etc/passwd");
        }
        var dest = Path.Combine(_baseDir, "out-link");

        // 不抛异常：链接条目被跳过
        SafeArchive.ExtractZipSafely(zipPath, dest);

        Assert.False(File.Exists(Path.Combine(dest, "evil-link")));
    }

    [Fact]
    public void ExtractTarSafely_拒绝穿越条目()
    {
        var tarPath = Path.Combine(_baseDir, "evil.tar");
        File.WriteAllBytes(tarPath, BuildTar("../escaped.txt", "pwned"));
        var dest = Path.Combine(_baseDir, "out-tar-evil");

        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            using var fs = File.OpenRead(tarPath);
            SafeArchive.ExtractTarSafely(fs, dest);
        });
        Assert.Contains("非法路径", ex.Message);
        Assert.False(File.Exists(Path.Combine(_baseDir, "escaped.txt")));
    }

    [Fact]
    public void ExtractTarSafely_正常归档全部解出()
    {
        var tarPath = Path.Combine(_baseDir, "ok.tar");
        File.WriteAllBytes(tarPath, BuildTar("jdk/bin/java", "hello tar"));
        var dest = Path.Combine(_baseDir, "out-tar-ok");

        using (var fs = File.OpenRead(tarPath))
            SafeArchive.ExtractTarSafely(fs, dest);

        Assert.Equal("hello tar", File.ReadAllText(Path.Combine(dest, "jdk", "bin", "java")));

        // 回归防护：条目 mode 必须可读。曾因 BuildTar 漏写 mode 字段（全零），
        // 提取出的文件无读权限，CI 非 root 用户读取即 UnauthorizedAccessException。
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(dest, "jdk", "bin", "java"));
            Assert.True(mode.HasFlag(UnixFileMode.UserRead), "提取出的文件必须可读");
        }
    }

    /// <summary>手工构造 tar 归档（测试需要含 ../ 的恶意条目，框架 API 会拒绝构造）</summary>
    private static byte[] BuildTar(string entryName, string content)
    {
        var ms = new MemoryStream();
        var header = new byte[512];
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(entryName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));
        // mode 字段（偏移 100，8 字节八进制）必须给 0644：全零模式会让
        // TarEntry.ExtractToFile 产出无任何权限位的文件，非 root 用户读取即
        // UnauthorizedAccessException——本地 root 开发环境会掩盖该问题（CI 实锤）。
        System.Text.Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        var sizeStr = Convert.ToString(content.Length, 8).PadLeft(11, '0');
        System.Text.Encoding.ASCII.GetBytes(sizeStr + "\0").CopyTo(header, 124);
        header[156] = (byte)'0'; // typeflag: 普通文件
        for (var i = 148; i < 156; i++) header[i] = (byte)' '; // checksum 字段先置空格
        var sum = header.Sum(b => (int)b);
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        ms.Write(header);
        var data = System.Text.Encoding.ASCII.GetBytes(content);
        ms.Write(data);
        var pad = (512 - (data.Length % 512)) % 512;
        ms.Write(new byte[pad]);
        ms.Write(new byte[1024]); // 结束块
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { }
    }
}
