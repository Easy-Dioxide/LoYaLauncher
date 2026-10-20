using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>Java 安装器工具函数测试</summary>
public class JavaInstallerTests
{
    [Theory]
    [InlineData("com.mojang:patchy:1.3.9", "com/mojang/patchy/1.3.9/patchy-1.3.9.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1", "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-windows", "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar")]
    public void LibraryToMavenPath_ConvertsCorrectly(string name, string expected)
    {
        Assert.Equal(expected, JavaInstaller.LibraryToMavenPath(name));
    }

    [Fact]
    public void LibraryToMavenPath_Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => JavaInstaller.LibraryToMavenPath("invalid"));
    }

    [Fact]
    public void RulesAllow_Null_ReturnsTrue()
    {
        Assert.True(JavaInstaller.RulesAllow(null));
        Assert.True(JavaInstaller.RulesAllow([]));
    }

    [Fact]
    public void RulesAllow_OsSpecificRule_Evaluates()
    {
        // allow + os=windows：在 Windows 上测试通过（CI 三平台中仅 Windows 分支生效）
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows" } } };
        if (OperatingSystem.IsWindows())
            Assert.True(JavaInstaller.RulesAllow(rules));
        else
            Assert.False(JavaInstaller.RulesAllow(rules));
    }

    [Fact]
    public void RulesAllow_DisallowTakesEffect()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow" },
            new() { Action = "disallow", Os = new OsRule { Name = "osx" } },
        };
        if (OperatingSystem.IsMacOS())
            Assert.False(JavaInstaller.RulesAllow(rules));
        else
            Assert.True(JavaInstaller.RulesAllow(rules));
    }

    [Theory]
    [InlineData("1.12.2", 8)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.19", 17)]
    [InlineData("1.20.1", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21", 21)]
    [InlineData("1.21.4", 21)]
    public void GetRequiredJavaMajor_MapsVersionsCorrectly(string versionId, int expectedMajor)
    {
        var json = new VersionJson { Id = versionId };
        Assert.Equal(expectedMajor, JavaInstaller.GetRequiredJavaMajor(json));
    }

    [Fact]
    public void GetRequiredJavaMajor_ExplicitDeclaration_Wins()
    {
        var json = new VersionJson { Id = "1.21.4", JavaVersion = new JavaVersionRef { MajorVersion = 25 } };
        Assert.Equal(25, JavaInstaller.GetRequiredJavaMajor(json));
    }

    [Theory]
    [InlineData("rd-132211")]
    [InlineData("1.7.10")]
    public void GetRequiredJavaMajor_LegacyVersions_DefaultTo8(string versionId)
    {
        var json = new VersionJson { Id = versionId };
        Assert.Equal(8, JavaInstaller.GetRequiredJavaMajor(json));
    }
}
