using LoyaLauncher.Java;

namespace LoyaLauncher.Tests;

/// <summary>
/// JreManager 下载源候选构建之目录解析逻辑测试（纯函数，不联网）。
/// 覆盖清华大学 Adoptium 镜像（nginx autoindex）的真实 HTML 形态。
/// </summary>
public class JreDownloadSourceTests
{
    private const string Tuna8Listing = """
        <!DOCTYPE html>
        <html>
        <head><title>Index of /Adoptium/8/jre/x64/linux/</title></head>
        <body>
        <h1>Index of /Adoptium/8/jre/x64/linux/</h1><hr><pre><a href="../">../</a>
        <a href="OpenJDK8U-jre_x64_linux_hotspot_8u412b08.tar.gz">OpenJDK8U-jre_x64_linux_hotspot_8u412b08.tar.gz</a>
        <a href="OpenJDK8U-jre_x64_linux_hotspot_8u452b09.tar.gz">OpenJDK8U-jre_x64_linux_hotspot_8u452b09.tar.gz</a>
        <a href="OpenJDK8U-jre_x64_linux_hotspot_8u504b01.tar.gz">OpenJDK8U-jre_x64_linux_hotspot_8u504b01.tar.gz</a>
        <a href="OpenJDK8U-jre_x64_linux_hotspot_8u504b01.tar.gz.sha256.txt">OpenJDK8U-jre_x64_linux_hotspot_8u504b01.tar.gz.sha256.txt</a>
        </pre><hr></body>
        </html>
        """;

    private const string Tuna17Listing = """
        <html><body><pre>
        <a href="OpenJDK17U-jre_x64_linux_hotspot_17.0.8_7.tar.gz">OpenJDK17U-jre_x64_linux_hotspot_17.0.8_7.tar.gz</a>
        <a href="OpenJDK17U-jre_x64_linux_hotspot_17.0.9_9.tar.gz">OpenJDK17U-jre_x64_linux_hotspot_17.0.9_9.tar.gz</a>
        </pre></body></html>
        """;

    [Fact]
    public void PickLatestAsset_Java8Style_PicksHighestBuild()
    {
        var file = JreManager.PickLatestAsset(
            Tuna8Listing, "OpenJDK8U-jre_x64_linux_hotspot_", ".tar.gz");

        Assert.Equal("OpenJDK8U-jre_x64_linux_hotspot_8u504b01.tar.gz", file);
    }

    [Fact]
    public void PickLatestAsset_Java17Style_PicksHighestBuild()
    {
        var file = JreManager.PickLatestAsset(
            Tuna17Listing, "OpenJDK17U-jre_x64_linux_hotspot_", ".tar.gz");

        Assert.Equal("OpenJDK17U-jre_x64_linux_hotspot_17.0.9_9.tar.gz", file);
    }

    [Fact]
    public void PickLatestAsset_IgnoresChecksumSidecars_AndDirectoryLinks()
    {
        // .sha256.txt 旁文件与 ../ 目录链接都不得被选中
        var file = JreManager.PickLatestAsset(
            Tuna8Listing, "OpenJDK8U-jre_x64_linux_hotspot_", ".tar.gz");

        Assert.NotNull(file);
        Assert.DoesNotContain("sha256", file);
        Assert.DoesNotContain("../", file);
    }

    [Fact]
    public void PickLatestAsset_NoMatchForOtherKind_ReturnsNull()
    {
        // 目录里只有 jre 文件，按 jdk 前缀查找应无结果
        var file = JreManager.PickLatestAsset(
            Tuna8Listing, "OpenJDK8U-jdk_x64_linux_hotspot_", ".tar.gz");

        Assert.Null(file);
    }

    [Fact]
    public void PickLatestAsset_WrongExtension_ReturnsNull()
    {
        // Linux 构建是 .tar.gz；按 .zip 前缀查找应无结果（Windows 专用）
        var file = JreManager.PickLatestAsset(
            Tuna8Listing, "OpenJDK8U-jre_x64_linux_hotspot_", ".zip");

        Assert.Null(file);
    }

    [Fact]
    public void PickLatestAsset_EmptyOrNullListing_ReturnsNull()
    {
        Assert.Null(JreManager.PickLatestAsset(null, "OpenJDK8U-jre_x64_linux_hotspot_", ".tar.gz"));
        Assert.Null(JreManager.PickLatestAsset("", "OpenJDK8U-jre_x64_linux_hotspot_", ".tar.gz"));
    }

    [Fact]
    public void PickLatestAsset_ZeroPaddedBuildNumbers_CompareNumerically()
    {
        // b09 与 b10：字符串比较会把 b09 判大，数字比较必须 b10 胜出
        var listing = """
            <pre>
            <a href="OpenJDK8U-jre_x64_linux_hotspot_8u504b09.tar.gz">a</a>
            <a href="OpenJDK8U-jre_x64_linux_hotspot_8u504b10.tar.gz">b</a>
            </pre>
            """;

        var file = JreManager.PickLatestAsset(
            listing, "OpenJDK8U-jre_x64_linux_hotspot_", ".tar.gz");

        Assert.Equal("OpenJDK8U-jre_x64_linux_hotspot_8u504b10.tar.gz", file);
    }
}
