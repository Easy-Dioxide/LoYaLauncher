namespace LoyaLauncher.Java;

/// <summary>
/// 崩溃日志分类。按可能性从高到低覆盖 Minecraft Java 版常见启动失败原因。
/// </summary>
public enum CrashCategory
{
    /// <summary>无法识别——日志中未命中已知模式</summary>
    Unknown,
    /// <summary>Java 堆内存不足（OutOfMemoryError: Java heap space）</summary>
    OutOfMemory,
    /// <summary>物理内存预留失败（Failed to reserve memory / 无法创建原生线程）</summary>
    HeapReservationFailed,
    /// <summary>显卡驱动/OpenGL 不支持（Pixel format not accelerated 等）</summary>
    GraphicsDriver,
    /// <summary>Mod 冲突（ClassNotFoundException / mixin apply failed 等）</summary>
    ModConflict,
    /// <summary>Java 版本与游戏不匹配（UnsupportedClassVersionError 等）</summary>
    JavaVersionMismatch,
    /// <summary>网络问题导致启动失败（SSL 握手失败 / DNS 解析失败 等）</summary>
    NetworkFailure,
    /// <summary>游戏本体崩溃（Minecraft Crash Report）</summary>
    GameCrash,
}

/// <summary>诊断结果的严重度。Warning 可重试，Error 需要用户干预。</summary>
public enum CrashSeverity
{
    /// <summary>提示（如干净退出）</summary>
    Info,
    /// <summary>警告——通常是可重试的环境问题</summary>
    Warning,
    /// <summary>错误——配置/硬件问题，需要修改后才能启动</summary>
    Error,
}

/// <summary>
/// 单条诊断结论。<see cref="Code"/> 是本地化键（如 "Crash.OOM.Title"），
/// <see cref="Evidence"/> 是命中的原始日志行（截断），供 UI 展示依据。
/// </summary>
public sealed record CrashDiagnosis(
    CrashCategory Category,
    CrashSeverity Severity,
    string Code,
    string? Evidence);

/// <summary>
/// Minecraft 崩溃日志模式匹配诊断器。纯函数、无 I/O、线程安全。
/// 分析游戏 stdout/stderr 合并后的日志行序列。
/// </summary>
public static class CrashAnalyzer
{
    // (正则, 分类, 严重度, 建议键)。顺序即优先级——先匹配先生效。
    private static readonly (string Pattern, CrashCategory Category, CrashSeverity Severity, string Code)[] Rules =
    [
        // 物理内存预留失败优先于 OOM：它表现为 JVM 启动即失败，与堆配置不同
        (@"Failed to reserve|unable to create native thread|cannot create native thread",
            CrashCategory.HeapReservationFailed, CrashSeverity.Error, "Crash.HeapReservation"),
        (@"java\.lang\.OutOfMemoryError|GC overhead limit exceeded|Metaspace",
            CrashCategory.OutOfMemory, CrashSeverity.Error, "Crash.OOM"),
        (@"UnsupportedClassVersionError|class file version \d+|has been compiled by a more recent version of Java",
            CrashCategory.JavaVersionMismatch, CrashSeverity.Error, "Crash.JavaVersion"),
        (@"ClassNotFoundException|NoClassDefFoundError|LoaderException|mixin apply failed|Mixin apply failed|DuplicateModsFoundException|MissingModException|ModResolutionException",
            CrashCategory.ModConflict, CrashSeverity.Error, "Crash.ModConflict"),
        (@"Pixel format not accelerated|OpenGL|GLFW|GL_INVALID|Failed to find GL|LWJGL",
            CrashCategory.GraphicsDriver, CrashSeverity.Error, "Crash.Graphics"),
        (@"Minecraft Crash Report|A crash occurred|Exception in thread ""main""",
            CrashCategory.GameCrash, CrashSeverity.Error, "Crash.Game"),
        (@"PKIX|SSLHandshakeException|Connection refused|UnknownHost|Couldn't connect to server|Connection reset|SSLException",
            CrashCategory.NetworkFailure, CrashSeverity.Warning, "Crash.Network"),
    ];

    /// <summary>
    /// 分析日志行，返回首个命中的诊断；全部未命中返回 Unknown（Info 级）。
    /// </summary>
    /// <param name="lines">按时间顺序的游戏输出行</param>
    public static CrashDiagnosis Analyze(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            foreach (var (pattern, category, severity, code) in Rules)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return new CrashDiagnosis(
                        category, severity, code, Truncate(line, 300));
                }
            }
        }

        return new CrashDiagnosis(CrashCategory.Unknown, CrashSeverity.Info, "Crash.Unknown", null);
    }

    /// <summary>诊断结果对应的本地化消息键（UI 层用 LangService 取值）</summary>
    public static string TitleKeyFor(CrashDiagnosis d) => d.Code + ".Title";

    /// <summary>诊断建议键（Crash.Xxx.Suggestion）</summary>
    public static string SuggestionKeyFor(CrashDiagnosis d) => d.Code + ".Suggestion";

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
