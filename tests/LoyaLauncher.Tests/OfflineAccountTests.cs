using LoyaLauncher.Auth;

namespace LoyaLauncher.Tests;

/// <summary>
/// 离线账户测试：重点是 Mojang 离线 UUID 生成规范的严格符合。
/// 规范：UUID = MD5("OfflinePlayer:" + name)，并设置 version(3) 与 variant(RFC4122) 位。
/// </summary>
public class OfflineAccountTests
{
    [Fact]
    public void GenerateOfflineUuid_IsDeterministic()
    {
        var a = OfflineAccount.GenerateOfflineUuid("Steve");
        var b = OfflineAccount.GenerateOfflineUuid("Steve");
        Assert.Equal(a, b);
    }

    [Fact]
    public void GenerateOfflineUuid_DifferentNames_DifferentUuids()
    {
        Assert.NotEqual(
            OfflineAccount.GenerateOfflineUuid("Steve"),
            OfflineAccount.GenerateOfflineUuid("Alex"));
    }

    [Fact]
    public void GenerateOfflineUuid_FollowsMojangSpec()
    {
        // UUID v3 变体：第三段以 3 开头，第四段以 8/9/a/b 开头
        var uuid = OfflineAccount.GenerateOfflineUuid("TestPlayer");
        var parts = uuid.Split('-');
        Assert.Equal(5, parts.Length);
        Assert.StartsWith("3", parts[2]);
        Assert.Contains(parts[3][0], "89ab");
    }

    [Fact]
    public void GenerateOfflineUuid_MatchesKnownValue()
    {
        // 已知向量：Mojang 规范计算的 "Steve" 离线 UUID
        Assert.Equal("5627dd98-e6be-3c21-b8a8-e92344183641", OfflineAccount.GenerateOfflineUuid("Steve"));
        Assert.Equal("36532b5e-c442-3dbb-a24c-c7e55d0f979a", OfflineAccount.GenerateOfflineUuid("Alex"));
    }

    [Fact]
    public void OfflineAccount_Session_Defaults()
    {
        var account = new OfflineAccount { PlayerName = "Player" };
        Assert.Equal("0", account.AccessToken);
        Assert.Equal("legacy", account.UserType);
        Assert.Null(account.AuthLibInjectorArg);
        Assert.False(string.IsNullOrWhiteSpace(account.PlayerUuid));
    }
}
