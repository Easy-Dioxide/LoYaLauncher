using System.Text.Json;
using LoyaLauncher.Auth;

namespace LoyaLauncher.Tests;

/// <summary>
/// GameAccount 多态持久化测试：判别器往返、旧版无判别器文件迁移、微软账户字段完整性。
/// </summary>
public class GameAccountTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new GameAccountConverter() },
    };

    [Fact]
    public void OfflineAccount_RoundTrip_PreservesFields()
    {
        var account = new OfflineAccount
        {
            PlayerName = "Steve",
            SkinPath = "/tmp/skin.png",
            SkinModel = SkinModel.Slim,
        };

        var json = JsonSerializer.Serialize<GameAccount>(account, Opts);
        var back = JsonSerializer.Deserialize<GameAccount>(json, Opts);

        var offline = Assert.IsType<OfflineAccount>(back);
        Assert.Equal("Steve", offline.PlayerName);
        Assert.Equal("/tmp/skin.png", offline.SkinPath);
        Assert.Equal(SkinModel.Slim, offline.SkinModel);
        Assert.Equal(account.Id, offline.Id);
        // 计算属性不得进入持久化
        Assert.DoesNotContain("PlayerUuid", json);
        Assert.DoesNotContain("AccessToken", json);
    }

    [Fact]
    public void MicrosoftAccount_RoundTrip_PreservesTokens()
    {
        var account = new MicrosoftAccount
        {
            PlayerName = "Notch",
            PlayerUuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5",
            AccessToken = "mc-access-token",
            RefreshToken = "ms-refresh-token",
            ExpiresAtUtc = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize<GameAccount>(account, Opts);
        var back = JsonSerializer.Deserialize<GameAccount>(json, Opts);

        var ms = Assert.IsType<MicrosoftAccount>(back);
        Assert.Equal("Notch", ms.PlayerName);
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", ms.PlayerUuid);
        Assert.Equal("mc-access-token", ms.AccessToken);
        Assert.Equal("ms-refresh-token", ms.RefreshToken);
        Assert.Equal(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), ms.ExpiresAtUtc);
        Assert.Equal(AccountKind.Microsoft, ms.Kind);
        Assert.Equal("msa", ms.UserType);
        Assert.Null(ms.AuthLibInjectorArg);
        // 判别器必须落盘
        Assert.Contains("\"microsoft\"", json);
    }

    [Fact]
    public void MixedAccounts_RoundTrip_KeepsTypes()
    {
        GameAccount[] accounts =
        [
            new OfflineAccount { PlayerName = "Steve" },
            new MicrosoftAccount { PlayerName = "Notch", AccessToken = "t", RefreshToken = "r" },
        ];

        var json = JsonSerializer.Serialize(accounts, Opts);
        var back = JsonSerializer.Deserialize<GameAccount[]>(json, Opts)!;

        Assert.IsType<OfflineAccount>(back[0]);
        Assert.IsType<MicrosoftAccount>(back[1]);
    }

    [Fact]
    public void LegacyFile_WithoutDiscriminator_LoadsAsOffline()
    {
        // v0.4.0 及以前的平铺格式：无 kind 判别器、无 data 包装
        const string legacy = """
        {
          "Id": "abc123def456",
          "PlayerName": "Legacy",
          "CustomUuid": null,
          "SkinPath": null,
          "SkinModel": 1
        }
        """;

        var back = JsonSerializer.Deserialize<GameAccount>(legacy, Opts);

        var offline = Assert.IsType<OfflineAccount>(back);
        Assert.Equal("Legacy", offline.PlayerName);
        Assert.Equal("abc123def456", offline.Id);
        Assert.Equal(SkinModel.Slim, offline.SkinModel);
    }

    [Fact]
    public void MicrosoftAccount_Expiry_Window()
    {
        var fresh = new MicrosoftAccount { ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
        var stale = new MicrosoftAccount { ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2) };
        var dead = new MicrosoftAccount { ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) };

        Assert.False(fresh.IsExpired);
        Assert.True(stale.IsExpired);  // 提前 5 分钟即视为需刷新
        Assert.True(dead.IsExpired);
    }

    [Fact]
    public void KindLabel_CoversAllKinds()
    {
        Assert.Equal("离线", new OfflineAccount().KindLabel);
        Assert.Equal("正版", new MicrosoftAccount().KindLabel);
        Assert.Equal("外置", new YggdrasilAccount().KindLabel);
    }
}
