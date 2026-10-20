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
public sealed class OfflineAccount : IGameSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>玩家名（将显示在游戏中）</summary>
    public string PlayerName { get; set; } = "Steve";

    /// <summary>覆盖默认 UUID（留空则按 Mojang 规范生成）</summary>
    public string? CustomUuid { get; set; }

    /// <summary>本地皮肤文件路径（64x64 PNG）</summary>
    public string? SkinPath { get; set; }

    public SkinModel SkinModel { get; set; } = SkinModel.Classic;

    // ---------- IGameSession ----------
    [JsonIgnore]
    public string PlayerUuid => string.IsNullOrEmpty(CustomUuid) ? GenerateOfflineUuid(PlayerName) : CustomUuid;

    [JsonIgnore]
    public string AccessToken => "0";

    [JsonIgnore]
    public string UserType => "legacy";

    [JsonIgnore]
    public string? AuthLibInjectorArg => null;

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
/// 二期实现，当前保留模型占位。
/// </summary>
public sealed class YggdrasilAccount : IGameSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string PlayerName { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string PlayerUuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserType => "mojang";
    public string? AuthLibInjectorArg => null; // 二期：注入 -javaagent authlib-injector.jar -Dauthlibinjector.yggdrasil.server
}
