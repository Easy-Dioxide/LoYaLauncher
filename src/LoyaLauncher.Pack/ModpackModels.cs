namespace LoyaLauncher.Pack;

/// <summary>
/// 整合包清单（zip 内的 modpack.json）。
/// 描述这个包是什么、基于哪个 MC 版本与加载器，导入时据此重建实例。
/// </summary>
public sealed class ModpackManifest
{
    /// <summary>清单结构版本（当前 1）</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>整合包名称（导入后作为默认实例名）</summary>
    public string Name { get; set; } = "";

    /// <summary>整合包自身版本号（作者自定义，如 "1.2.0"）</summary>
    public string? Version { get; set; }

    /// <summary>作者</summary>
    public string? Author { get; set; }

    /// <summary>简介</summary>
    public string? Description { get; set; }

    /// <summary>Minecraft 版本 ID（如 1.21.4）</summary>
    public string MinecraftVersion { get; set; } = "";

    /// <summary>Mod 加载器（fabric / forge / quilt / neoforged），空为原版</summary>
    public string? Loader { get; set; }

    /// <summary>加载器版本</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>导出时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>包含的 mod 文件名列表（仅供导入前预览展示）</summary>
    public List<string> ModFiles { get; set; } = [];
}

/// <summary>整合包预览信息（读 zip 不落盘）</summary>
public sealed record ModpackInfo(string FilePath, ModpackManifest Manifest);

/// <summary>导入结果</summary>
public sealed record ModpackImportResult(Core.GameInstance Instance, string? Warning);
