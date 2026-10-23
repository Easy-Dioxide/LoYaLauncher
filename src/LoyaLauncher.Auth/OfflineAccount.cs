using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LoyaLauncher.Core;

namespace LoyaLauncher.Auth;

/// <summary>皮肤模型</summary>
public enum SkinModel
{
    /// <summary>经典模型（宽手臂，Steve）</summary>
    Classic,
    /// <summary>纤细模型（Alex）</summary>
    Slim,
}

/// <summary>
/// 离线账户：自定义用户名 + 皮肤，无需任何联网认证。
/// UUID 严格遵循 Mojang 离线 UUID 生成规范（MD5("OfflinePlayer:" + name)）。
/// </summary>
public sealed class OfflineAccount : GameAccount
{
    public override AccountKind Kind => AccountKind.Offline;

    /// <summary>玩家名（将显示在游戏中）</summary>
    public override string PlayerName { get; set; } = "Steve";

    /// <summary>覆盖默认 UUID（留空则按 Mojang 规范生成）</summary>
    public string? CustomUuid { get; set; }

    /// <summary>本地皮肤文件路径（64x64 PNG）</summary>
    public string? SkinPath { get; set; }

    public SkinModel SkinModel { get; set; } = SkinModel.Classic;

    // ---------- IGameSession ----------
    [JsonIgnore]
    public override string PlayerUuid
    {
        // 计算得出：未显式覆盖时按 Mojang 规范生成；写入等价于设置 CustomUuid
        get => string.IsNullOrEmpty(CustomUuid) ? GenerateOfflineUuid(PlayerName) : CustomUuid;
        set => CustomUuid = value;
    }

    [JsonIgnore]
    public override string AccessToken
    {
        // 离线模式无真实令牌，游戏端以 "0" 占位；无写入语义
        get => "0";
        set { }
    }

    [JsonIgnore]
    public override string UserType => "legacy";

    [JsonIgnore]
    public override string? AuthLibInjectorArg => null;

    /// <summary>Mojang 规范的离线 UUID 生成算法</summary>
    public static string GenerateOfflineUuid(string playerName)
    {
        var input = Encoding.UTF8.GetBytes($"OfflinePlayer:{playerName}");
        var hash = MD5.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant RFC 4122
        // 手工按字节序格式化为 8-4-4-4-12。
        // 注意：不能用 new Guid(byte[]) —— .NET 会把前 4/2/2 字节按小端重排，
        // 导致 UUID 前三个字段字节颠倒，与 Mojang 规范不符。
        var sb = new StringBuilder(36);
        for (var i = 0; i < 16; i++)
        {
            sb.Append(hash[i].ToString("x2"));
            if (i is 3 or 5 or 7 or 9) sb.Append('-');
        }
        return sb.ToString();
    }
}

/// <summary>
/// 外置登录账户（authlib-injector，如 LittleSkin）。
/// 模型已就位，完整流程在路线图中排期。
/// </summary>
public sealed class YggdrasilAccount : GameAccount
{
    public override AccountKind Kind => AccountKind.Yggdrasil;

    public override string PlayerName { get; set; } = "";
    public override string PlayerUuid { get; set; } = "";
    public override string AccessToken { get; set; } = "";

    public string ServerUrl { get; set; } = "";

    [JsonIgnore]
    public override string UserType => "mojang";

    [JsonIgnore]
    public override string? AuthLibInjectorArg => null; // 注入 -javaagent authlib-injector.jar -Dauthlibinjector.yggdrasil.server
}
