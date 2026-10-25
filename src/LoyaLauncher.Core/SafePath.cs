namespace LoyaLauncher.Core;

/// <summary>
/// 路径安全工具（清单 §10）：为 natives 解压、更新包解压、整合包导入、
/// Java 解压、Mod 文件落盘提供统一的目录穿越防护。
/// 拒绝：空路径、绝对路径（含盘符/UNC）、含 .. 的父目录穿越、解析后越界、
/// 经由已存在符号链接逃逸。
/// </summary>
public static class SafePath
{
    /// <summary>平台相关的路径比较方式（Windows 大小写不敏感，Linux 敏感）</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 校验归档条目/相对路径能否安全解析到 <paramref name="baseDir"/> 内。
    /// </summary>
    public static bool IsSafeEntry(string baseDir, string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        if (Path.IsPathRooted(entryName)) return false;

        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/')) return false;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        if (segments.Any(s => s == ".." || s == ".")) return false;

        var root = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));
        return IsWithin(root, full);
    }

    /// <summary>安全解析条目到完整路径；越界返回 null</summary>
    public static string? ResolveEntry(string baseDir, string entryName)
    {
        if (!IsSafeEntry(baseDir, entryName)) return null;
        var normalized = entryName.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));
        return EscapesViaSymlink(root, full) ? null : full;
    }

    /// <summary>
    /// 目标路径的某一级已存在父目录是符号链接且指向 baseDir 外时返回 true。
    /// （Path.GetFullPath 只做词法解析，识别不了符号链接逃逸。）
    /// 注意：ResolveLinkTarget 对不存在的路径抛 DirectoryNotFoundException，
    /// 因此只检查已存在的目录——不存在的目录不可能是符号链接。
    /// </summary>
    public static bool EscapesViaSymlink(string baseDir, string fullDest)
    {
        try
        {
            var root = Path.GetFullPath(baseDir);
            var dir = Path.GetDirectoryName(fullDest);
            while (dir is not null && dir.Length > root.Length)
            {
                if (Directory.Exists(dir))
                {
                    var target = Directory.ResolveLinkTarget(dir, returnFinalTarget: true);
                    if (target is not null && !IsWithin(root, target.FullName))
                        return true;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return false;
        }
        catch
        {
            // 探测失败按不安全处理
            return true;
        }
    }

    /// <summary>
    /// 校验普通文件名（如 Modrinth 返回的 mod 文件名）是否可直接用于
    /// Path.Combine 落盘：不含目录分隔符、不是保留设备名、无穿越。
    /// </summary>
    public static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName != fileName.Trim()) return false;
        if (fileName.Contains('/') || fileName.Contains('\\')) return false;
        if (fileName.Contains("..")) return false;
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (fileName is "." or "..") return false;
        // Windows 保留设备名（CON、PRN、AUX、NUL、COM1-9、LPT1-9）
        var stem = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return !reserved.Contains(stem);
    }

    /// <summary>把外部输入净化为安全文件名（去掉目录部分与非法字符）；无法净化返回 null</summary>
    public static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName.Replace('\\', '/').Trim());
        if (!IsSafeFileName(name)) return null;
        return name;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        var cmp = PathComparison;
        return full.Equals(root, cmp) ||
               full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cmp);
    }
}
