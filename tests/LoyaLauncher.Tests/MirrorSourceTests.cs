using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.Tests;

public class MirrorSourceTests
{
    [Fact]
    public void ResolveChain_Auto_优先BMCLAPI并回退官方()
    {
        var chain = MirrorSource.ResolveChain(MirrorMode.BmclapiFirst);
        Assert.Equal(2, chain.Count);
        Assert.Contains("bmclapi2.bangbang93.com", chain[0].VersionManifest);
        Assert.Contains("launchermeta.mojang.com", chain[1].VersionManifest);
    }

    [Fact]
    public void ResolveChain_OfficialOnly_仅官方源()
    {
        var chain = MirrorSource.ResolveChain(MirrorMode.OfficialOnly);
        Assert.Single(chain);
        Assert.Contains("launchermeta.mojang.com", chain[0].VersionManifest);
    }

    [Fact]
    public void ResolveChain_BmclapiOnly_仅BMCLAPI()
    {
        var chain = MirrorSource.ResolveChain(MirrorMode.BmclapiOnly);
        Assert.Single(chain);
        Assert.Contains("bmclapi2.bangbang93.com", chain[0].VersionManifest);
    }

    [Fact]
    public void ResolveChain_CustomFirst_自定义优先且带回退链()
    {
        var chain = MirrorSource.ResolveChain(MirrorMode.CustomFirst, "https://example.com/mirror/");
        Assert.Equal(3, chain.Count);
        Assert.Equal("https://example.com/mirror/mc/game/version_manifest_v2.json", chain[0].VersionManifest);
        Assert.Contains("bmclapi2.bangbang93.com", chain[1].VersionManifest);
        Assert.Contains("launchermeta.mojang.com", chain[2].VersionManifest);
    }

    [Fact]
    public void ResolveChain_CustomOnly_仅自定义源()
    {
        var chain = MirrorSource.ResolveChain(MirrorMode.CustomOnly, "https://example.com/mirror");
        Assert.Single(chain);
        Assert.Equal("https://example.com/mirror/version/1.21/json", chain[0].VersionJson("1.21"));
    }

    [Fact]
    public void Custom源_URL构造遵循BMCLAPI路径规范()
    {
        var set = MirrorSource.Create(MirrorKind.Custom, "https://mirror.example.com/base/");
        Assert.Equal("https://mirror.example.com/base/version/1.21/client", set.ClientJar("1.21", "abc123"));
        Assert.Equal("https://mirror.example.com/base/maven/a/b/c.jar", set.Library("a/b/c.jar"));
        Assert.Equal("https://mirror.example.com/base/assets/indexes/pre-1.6.json", set.AssetsIndex("pre-1.6"));
        Assert.Equal("https://mirror.example.com/base/objects/ab/abcdef", set.AssetObject("abcdef"));
    }

    [Fact]
    public void Custom源_未配置地址_抛出明确异常()
    {
        Assert.Throws<InvalidOperationException>(
            () => MirrorSource.ResolveChain(MirrorMode.CustomOnly, ""));
    }

    [Fact]
    public void Bmclapi源_URL构造保持不变()
    {
        var set = MirrorSource.Create(MirrorKind.Bmclapi);
        Assert.Equal("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", set.VersionManifest);
        Assert.Equal("https://bmclapi2.bangbang93.com/version/1.20.1/json", set.VersionJson("1.20.1"));
        Assert.Equal("https://bmclapi2.bangbang93.com/version/1.20.1/client", set.ClientJar("1.20.1", "deadbeef"));
        Assert.Equal("https://bmclapi2.bangbang93.com/objects/de/deadbeef", set.AssetObject("deadbeef"));
    }

    [Fact]
    public void 官方源_客户端Jar走pistonData完整路径()
    {
        var set = MirrorSource.Create(MirrorKind.Official);
        Assert.Equal(
            "https://piston-data.mojang.com/v1/objects/deadbeef/client.jar",
            set.ClientJar("1.20.1", "deadbeef"));
    }
}
