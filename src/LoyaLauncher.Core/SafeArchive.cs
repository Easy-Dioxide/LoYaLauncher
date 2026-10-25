using System.Formats.Tar;
using System.IO.Compression;

namespace LoyaLauncher.Core;

/// <summary>
/// 安全归档解压（清单 §10）：为 zip/tar.gz 解压统一提供目录穿越与
/// 符号链接防护。与框架的 ExtractToDirectory 不同，这里逐条目校验
/// <see cref="SafePath.IsSafeEntry"/> 并跳过链接条目，绝不依赖
/// 框架版本的默认行为。
/// </summary>
public static class SafeArchive
{
    /// <summary>安全解压 zip 到目标目录（覆盖已存在文件）</summary>
    /// <exception cref="InvalidDataException">归档包含非法路径</exception>
    public static void ExtractZipSafely(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            // 目录条目（名字以 / 结尾）跳过：写文件时会自动创建目录
            if (entry.FullName.EndsWith('/')) continue;
            // Unix 模式下符号链接条目（S_IFLNK）跳过，防止链接逃逸
            if (IsZipSymlink(entry)) continue;
            // 空文件也跳过（无内容）
            if (entry.Length == 0 && entry.Name.EndsWith('/')) continue;

            var dest = SafePath.ResolveEntry(destDir, entry.FullName)
                ?? throw new InvalidDataException($"归档包含非法路径: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>安全解压 tar 流到目标目录（覆盖已存在文件）</summary>
    /// <exception cref="InvalidDataException">归档包含非法路径</exception>
    public static void ExtractTarSafely(Stream tarStream, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var reader = new TarReader(tarStream);
        while (reader.GetNextEntry() is { } entry)
        {
            // 跳过目录、符号链接、硬链接与非文件条目
            if (entry.EntryType is not (TarEntryType.V7RegularFile or TarEntryType.RegularFile
                or TarEntryType.ContiguousFile))
                continue;

            var dest = SafePath.ResolveEntry(destDir, entry.Name)
                ?? throw new InvalidDataException($"归档包含非法路径: {entry.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>zip 条目的 Unix 模式是否为符号链接（external_attributes 高 16 位）</summary>
    private static bool IsZipSymlink(ZipArchiveEntry entry)
    {
        const int s_iflnk = 0xA000;
        const int s_ifmt = 0xF000;
        try
        {
            var unixMode = entry.ExternalAttributes >> 16;
            return unixMode != 0 && (unixMode & s_ifmt) == s_iflnk;
        }
        catch
        {
            return false;
        }
    }
}
