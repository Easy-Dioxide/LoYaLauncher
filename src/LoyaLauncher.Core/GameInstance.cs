namespace LoyaLauncher.Core;

/// <summary>游戏版本类型</summary>
public enum GameEdition
{
    /// <summary>Java 版</summary>
    Java,
    /// <summary>基岩版</summary>
    Bedrock,
}

/// <summary>
/// 游戏实例：一套隔离的游戏环境（独立 gameDir/mods/configs/saves）。
/// versions/libraries/assets 在实例间共享，实例目录只存世界与 mod。
/// </summary>
public sealed class GameInstance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public GameEdition Edition { get; set; } = GameEdition.Java;

    /// <summary>Java 版：版本 ID（如 1.21.4）；基岩版：版本号</summary>
    public string VersionId { get; set; } = "";

    /// <summary>Mod 加载器（fabric / forge / quilt / neoforged），空为原版</summary>
    public string? Loader { get; set; }

    /// <summary>加载器版本</summary>
    public string? LoaderVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastPlayed { get; set; }

    // ---------- 派生路径 ----------
    public string RootDirectory => Path.Combine(LoyaPaths.Instances, Id);
    public string MetaPath => Path.Combine(RootDirectory, "instance.json");
    public string GameDirectory => Path.Combine(RootDirectory, ".minecraft");
    public string ModsDirectory => Path.Combine(GameDirectory, "mods");
    public string ConfigsDirectory => Path.Combine(GameDirectory, "config");
    public string SavesDirectory => Path.Combine(GameDirectory, "saves");
    public string ResourcePacksDirectory => Path.Combine(GameDirectory, "resourcepacks");
    public string OptionsPath => Path.Combine(GameDirectory, "options.txt");
}

/// <summary>游戏会话（账户抽象）：离线 / 正版 / 外置登录统一接口</summary>
public interface IGameSession
{
    string PlayerName { get; }
    string PlayerUuid { get; }
    string AccessToken { get; }
    string UserType { get; }
    /// <summary>authlib-injector 参数（外置登录用），空表示官方登录</summary>
    string? AuthLibInjectorArg { get; }
}
