using LoyaLauncher.Java;
using Xunit;

namespace LoyaLauncher.Tests;

public class CrashAnalyzerTests
{
    [Fact]
    public void OutOfMemoryError_Detected()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "[main/INFO]: Loaded 42 mods",
            "java.lang.OutOfMemoryError: Java heap space",
            "\tat java.base/java.util.Arrays.copyOf(Arrays.java:3538)",
        });

        Assert.Equal(CrashCategory.OutOfMemory, d.Category);
        Assert.Equal(CrashSeverity.Error, d.Severity);
        Assert.Contains("OutOfMemoryError", d.Evidence);
    }

    [Fact]
    public void FailedToReserveMemory_TakesPriorityOverOom()
    {
        // 预留失败日志常同时出现 OOM 字样，但根因是预留失败
        var d = CrashAnalyzer.Analyze(new[]
        {
            "There is insufficient memory for the Java Runtime Environment to continue.",
            "Failed to reserve memory (assumed 4GB)",
        });

        Assert.Equal(CrashCategory.HeapReservationFailed, d.Category);
    }

    [Fact]
    public void GraphicsError_Detected()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "GLFW: Platform MK error",
            "Pixel format not accelerated",
        });

        Assert.Equal(CrashCategory.GraphicsDriver, d.Category);
        Assert.Equal(CrashSeverity.Error, d.Severity);
    }

    [Fact]
    public void ModConflict_ClassNotFound_Detected()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "Caused by: java.lang.ClassNotFoundException: net.fabricmc.loader.impl.launch.FabricLauncher",
        });

        Assert.Equal(CrashCategory.ModConflict, d.Category);
    }

    [Fact]
    public void JavaVersionMismatch_UnsupportedClassVersion_Detected()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "java.lang.UnsupportedClassVersionError: net/minecraft/client/main/Main has been compiled by a more recent version of Java",
        });

        Assert.Equal(CrashCategory.JavaVersionMismatch, d.Category);
    }

    [Fact]
    public void NetworkFailure_WarningSeverity()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "javax.net.ssl.SSLHandshakeException: PKIX path building failed",
        });

        Assert.Equal(CrashCategory.NetworkFailure, d.Category);
        Assert.Equal(CrashSeverity.Warning, d.Severity); // 可重试
    }

    [Fact]
    public void GameCrashReport_Detected()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "[Render thread/INFO]: Loaded 42 mods",
            "---- Minecraft Crash Report ----",
            "// Everything is going to plan.",
        });

        Assert.Equal(CrashCategory.GameCrash, d.Category);
    }

    [Fact]
    public void CleanLog_ReturnsUnknownInfo()
    {
        var d = CrashAnalyzer.Analyze(new[]
        {
            "[main/INFO]: Environment: authHost='https://authserver.mojang.com'",
            "[Render thread/INFO]: Creating: 1024x768 textures",
        });

        Assert.Equal(CrashCategory.Unknown, d.Category);
        Assert.Equal(CrashSeverity.Info, d.Severity);
        Assert.Null(d.Evidence);
    }

    [Fact]
    public void EmptyLog_ReturnsUnknownInfo()
    {
        var d = CrashAnalyzer.Analyze(Array.Empty<string>());
        Assert.Equal(CrashCategory.Unknown, d.Category);
    }

    [Fact]
    public void LongLine_IsTruncated()
    {
        var longPrefix = new string('x', 500);
        var d = CrashAnalyzer.Analyze(new[] { $"{longPrefix} java.lang.OutOfMemoryError: Java heap space" });

        Assert.Equal(CrashCategory.OutOfMemory, d.Category);
        Assert.True(d.Evidence!.Length <= 301);
    }

    [Fact]
    public void LocalizationKeys_DeriveFromCode()
    {
        var d = CrashAnalyzer.Analyze(new[] { "java.lang.OutOfMemoryError: Java heap space" });

        Assert.Equal("Crash.OOM.Title", CrashAnalyzer.TitleKeyFor(d));
        Assert.Equal("Crash.OOM.Suggestion", CrashAnalyzer.SuggestionKeyFor(d));
    }
}
