using System.Runtime.InteropServices;

namespace LoyaLauncher.Core;

/// <summary>
/// LoyaLauncher 全局路径管理。所有游戏数据集中在用户目录下的 .loya 文件夹，
/// 与官方启动器 (.minecraft) 完全隔离，互不污染。
/// </summary>
public static class LoyaPaths
{
    /// <summary>数据根目录：Windows %APPDATA%\.loya / macOS、Linux ~/.loya。测试可重定向以隔离数据。</summary>
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ".loya");

    /// <summary>Java 版版本 JSON 与客户端 jar 存放目录（按版本 ID 分文件夹）</summary>
    public static string Versions => Sub("versions");

    /// <summary>libraries 公共库目录</summary>
    public static string Libraries => Sub("libraries");

    /// <summary>资源索引与对象目录</summary>
    public static string Assets => Sub("assets");

    /// <summary>启动器管理的 Java 运行时（按主版本号分目录）</summary>
    public static string JavaRuntimes => Sub("java");

    /// <summary>实例（游戏隔离目录）根目录，每个实例拥有独立的 gameDir/mods/configs/saves</summary>
    public static string Instances => Sub("instances");

    /// <summary>启动器自身配置</summary>
    public static string Config => Sub("config");

    /// <summary>日志目录</summary>
    public static string Logs => Sub("logs");

    /// <summary>临时缓存（下载中断的 .part 文件等）</summary>
    public static string Cache => Sub("cache");

    /// <summary>基岩版版本数据目录</summary>
    public static string Bedrock => Sub("bedrock");

    private static string Sub(string name) => Path.Combine(Root, name);

    /// <summary>创建全部数据目录（幂等）</summary>
    public static void EnsureDirectories()
    {
        foreach (var dir in new[] { Root, Versions, Libraries, Assets, JavaRuntimes, Instances, Config, Logs, Cache, Bedrock })
            Directory.CreateDirectory(dir);
    }

    /// <summary>当前平台的 Java 可执行文件名</summary>
    public static string JavaExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "java.exe" : "java";

    /// <summary>当前平台的归档/架构描述，用于 JRE 下载 URL</summary>
    public static string PlatformId =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac" : "linux";

    /// <summary>当前 CPU 架构（x64 / aarch64）</summary>
    public static string ArchId =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
}
