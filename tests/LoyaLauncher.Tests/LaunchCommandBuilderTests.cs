using System.Text.Json;
using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>启动参数构建测试：新版 arguments 与旧版 minecraftArguments 双兼容</summary>
public class LaunchCommandBuilderTests
{
    private static LaunchContext CreateContext() => new()
    {
        VersionId = "1.21.4",
        GameDirectory = "/tmp/game dir",
        JavaExecutable = "/usr/bin/java",
        MainClass = "net.minecraft.client.main.Main",
        Classpath = "/libs/a.jar:/libs/b.jar",
        NativesDirectory = "/tmp/natives",
        AssetsDirectory = "/tmp/assets",
        AssetIndexId = "24",
        PlayerName = "LoyaPlayer",
        PlayerUuid = "2ef39de5-963d-306a-a8bd-9d79619ef29d",
        AccessToken = "0",
    };

    [Fact]
    public void Build_ModernArguments_ContainsAccountParameters()
    {
        var json = new VersionJson
        {
            Id = "1.21.4",
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new Arguments
            {
                Game = new List<ArgumentElement>
                {
                    new() { Value = JsonDocument.Parse("\"--username\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"${auth_player_name}\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"--uuid\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"${auth_uuid}\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"--accessToken\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"${auth_access_token}\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"--gameDir\"").RootElement },
                    new() { Value = JsonDocument.Parse("\"${game_directory}\"").RootElement },
                },
            },
        };

        var cmd = LaunchCommandBuilder.Build(CreateContext(), json);

        Assert.Equal("/usr/bin/java", cmd[0]);
        Assert.Contains("net.minecraft.client.main.Main", cmd);
        Assert.Contains("-cp", cmd);
        Assert.Contains("/libs/a.jar:/libs/b.jar", cmd);
        Assert.Contains("--username", cmd);
        Assert.Contains("LoyaPlayer", cmd);
        Assert.Contains("2ef39de5-963d-306a-a8bd-9d79619ef29d", cmd);
        Assert.Contains("--accessToken", cmd);
        Assert.Contains("0", cmd);
    }

    [Fact]
    public void Build_LegacyMinecraftArguments_ExpandsTemplate()
    {
        var ctx = CreateContext();
        ctx.VersionId = "1.12.2"; // 与 json.Id 保持一致（真实流程中二者恒等）
        var json = new VersionJson
        {
            Id = "1.12.2",
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments = "--username ${auth_player_name} --version ${version_name} " +
                                 "--gameDir ${game_directory} --assetsDir ${assets_root} " +
                                 "--assetIndex ${assets_index_name} --uuid ${auth_uuid} " +
                                 "--accessToken ${auth_access_token} --userType ${user_type}",
        };

        var cmd = LaunchCommandBuilder.Build(ctx, json);

        Assert.Contains("--username", cmd);
        Assert.Contains("LoyaPlayer", cmd);
        Assert.Contains("1.12.2", cmd);
        Assert.Contains("24", cmd);
        Assert.Contains("--userType", cmd);
        Assert.Contains("legacy", cmd);
    }

    [Fact]
    public void Build_IncludesMemorySetting()
    {
        var ctx = CreateContext();
        ctx.MaxHeapMb = 4096;
        var json = new VersionJson
        {
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments = "--username ${auth_player_name}",
        };

        var cmd = LaunchCommandBuilder.Build(ctx, json);
        Assert.Contains("-Xmx4096M", cmd);
    }

    [Fact]
    public void Build_QuotesPathsWithSpaces()
    {
        var json = new VersionJson
        {
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments = "--gameDir ${game_directory}",
        };

        var cmd = LaunchCommandBuilder.Build(CreateContext(), json);
        // "/tmp/game dir" 含空格，应被引号包裹
        Assert.Contains("\"/tmp/game dir\"", cmd);
    }

    [Fact]
    public void Build_ConditionalArguments_FilteredByRules()
    {
        var json = new VersionJson
        {
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new Arguments
            {
                Game = new List<ArgumentElement>
                {
                    new() { Value = JsonDocument.Parse("\"--quickPlayPath\"").RootElement },
                    // 仅 Windows 允许的参数：在非 Windows 平台应被过滤
                    new()
                    {
                        Rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows" } } },
                        Value = JsonDocument.Parse("\"--windowsOnly\"").RootElement,
                    },
                },
            },
        };

        var cmd = LaunchCommandBuilder.Build(CreateContext(), json);
        Assert.Contains("--quickPlayPath", cmd);
        if (OperatingSystem.IsWindows())
            Assert.Contains("--windowsOnly", cmd);
        else
            Assert.DoesNotContain("--windowsOnly", cmd);
    }
}
