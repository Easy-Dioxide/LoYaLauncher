using System.Text.Json;
using System.Text.Json.Serialization;
using LoyaLauncher.Core;

namespace LoyaLauncher.Auth;

/// <summary>账户类型（同时作为 accounts.json 的持久化判别器）</summary>
public enum AccountKind
{
    /// <summary>离线账户</summary>
    Offline,
    /// <summary>微软正版账户</summary>
    Microsoft,
    /// <summary>外置登录（authlib-injector）</summary>
    Yggdrasil,
}

/// <summary>
/// 账户统一基类：离线 / 微软正版 / 外置登录均实现 <see cref="IGameSession"/>。
/// 会话成员（PlayerName / PlayerUuid / AccessToken / UserType / AuthLibInjectorArg）
/// 在此声明为抽象成员，由各派生类按自身持久化语义重写：
/// 计算得出的属性（如离线 UUID）在派生类上加 [JsonIgnore]，真实令牌则正常序列化。
/// </summary>
public abstract class GameAccount : IGameSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>账户类型</summary>
    [JsonIgnore]
    public abstract AccountKind Kind { get; }

    /// <summary>玩家名（将显示在游戏中）</summary>
    public abstract string PlayerName { get; set; }

    /// <summary>玩家 UUID</summary>
    public abstract string PlayerUuid { get; set; }

    /// <summary>访问令牌（离线恒为 "0"）</summary>
    public abstract string AccessToken { get; set; }

    /// <summary>账户类型标识（写入游戏参数 --userType）</summary>
    [JsonIgnore]
    public abstract string UserType { get; }

    /// <summary>authlib-injector 参数（外置登录用），空表示官方登录</summary>
    [JsonIgnore]
    public abstract string? AuthLibInjectorArg { get; }

    /// <summary>账户类型展示名（UI 徽标，不持久化）</summary>
    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        AccountKind.Microsoft => "正版",
        AccountKind.Yggdrasil => "外置",
        _ => "离线",
    };
}

/// <summary>
/// 微软正版账户：设备码 OAuth 登录，令牌链 MS → Xbox Live → XSTS → Minecraft Services。
/// 持久化 MC 访问令牌 + MS 刷新令牌 + 过期时间，启动前按需静默续期。
/// </summary>
public sealed class MicrosoftAccount : GameAccount
{
    public override AccountKind Kind => AccountKind.Microsoft;

    /// <summary>Minecraft 角色名（登录后由 profile 拉取）</summary>
    public override string PlayerName { get; set; } = "";

    /// <summary>玩家 UUID（带横线，由 profile id 规范化）</summary>
    public override string PlayerUuid { get; set; } = "";

    /// <summary>Minecraft Services 访问令牌</summary>
    public override string AccessToken { get; set; } = "";

    /// <summary>微软刷新令牌（整条令牌链续期用）</summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>MC 访问令牌过期时间（UTC）</summary>
    public DateTime ExpiresAtUtc { get; set; }

    [JsonIgnore]
    public override string UserType => "msa";

    [JsonIgnore]
    public override string? AuthLibInjectorArg => null;

    /// <summary>令牌是否即将过期（提前 5 分钟视为需刷新）</summary>
    [JsonIgnore]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc.AddMinutes(-5);
}

/// <summary>
/// GameAccount 多态 JSON 转换器。
/// 写入：附加 kind 判别器，派生对象序列化进 data 字段；
/// 读取：按 kind 路由到具体派生类；判别器缺失（v0.4.0 及以前的旧文件）一律按离线账户解析。
/// </summary>
public sealed class GameAccountConverter : JsonConverter<GameAccount>
{
    // 序列化/反序列化派生对象时使用不带本转换器的内部选项：
    // 否则 STJ 对派生类型做 CanConvert 查找时会再次命中本转换器，造成无限递归。
    private static readonly JsonSerializerOptions Inner = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public override GameAccount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // 新格式数据嵌在 data 字段；旧格式（无判别器）整个对象即账户本身
        var data = root.TryGetProperty("data", out var d) ? d : root;

        var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;

        return kind?.ToLowerInvariant() switch
        {
            "microsoft" => data.Deserialize<MicrosoftAccount>(Inner)!,
            "yggdrasil" => data.Deserialize<YggdrasilAccount>(Inner)!,
            _ => data.Deserialize<OfflineAccount>(Inner)!,
        };
    }

    public override void Write(Utf8JsonWriter writer, GameAccount value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        writer.WritePropertyName("data");
        // 按运行时类型序列化，走 Inner 选项（无 GameAccount 转换器）
        JsonSerializer.Serialize(writer, value, value.GetType(), Inner);
        writer.WriteEndObject();
    }
}
