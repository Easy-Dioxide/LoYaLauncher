using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>预检问题严重级别</summary>
public enum PreflightSeverity
{
    /// <summary>全部通过</summary>
    Ok,
    /// <summary>提示信息（如"将自动下载 Java"），不阻止启动</summary>
    Info,
    /// <summary>警告，放行但提示风险</summary>
    Warning,
    /// <summary>阻断性问题，无法启动</summary>
    Error,
}

/// <summary>
/// 单项预检结果。<see cref="Code"/> 为本地化键（UI 层查语言包），
/// <see cref="Args"/> 为格式化参数。消息文本不在此层拼装，避免引擎层依赖界面层。
/// </summary>
public sealed record PreflightIssue(PreflightSeverity Severity, string Code, object?[] Args)
{
    /// <summary>是否为阻断级问题</summary>
    public bool BlocksLaunch => Severity == PreflightSeverity.Error;
}

/// <summary>预检报告</summary>
public sealed record PreflightReport
{
    public IReadOnlyList<PreflightIssue> Issues { get; init; } = Array.Empty<PreflightIssue>();

    /// <summary>最高严重级别（无问题时为 Ok）</summary>
    public PreflightSeverity Level
    {
        get
        {
            if (Issues.Any(i => i.Severity == PreflightSeverity.Error)) return PreflightSeverity.Error;
            if (Issues.Any(i => i.Severity == PreflightSeverity.Warning)) return PreflightSeverity.Warning;
            if (Issues.Count > 0) return PreflightSeverity.Info;
            return PreflightSeverity.Ok;
        }
    }

    /// <summary>是否可以启动（无 Error 级问题）</summary>
    public bool CanLaunch => !Issues.Any(i => i.BlocksLaunch);
}

/// <summary>
/// 启动前环境自检。检查项：
/// 1. 版本是否选择 / 已安装（未安装不阻断——启动流程会自动安装）
/// 2. Java 匹配情况（托管 / 系统 / 将自动下载，均不阻断）
/// 3. 磁盘可用空间（数据盘 &lt;1GB 阻断，&lt;3GB 警告）
/// 4. 内存配置（堆 ≥ 物理内存阻断，余量 &lt;2GB 警告）
/// </summary>
public sealed class LaunchPreflight
{
    /// <summary>磁盘阻断阈值（字节）</summary>
    private const long DiskErrorBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>磁盘警告阈值（字节）</summary>
    private const long DiskWarnBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>给系统和其他程序预留的内存（MB）</summary>
    private const long MemoryReserveMb = 2048;

    /// <summary>自动下载的 JRE 约 40MB（仅用于提示文案）</summary>
    private const int ApproxJreSizeMb = 40;

    private readonly JavaInstaller _installer;
    private readonly JreManager _jre;
    private readonly Func<string, long?> _diskProbe;
    private readonly Func<long?> _memoryProbe;
    private readonly Func<CancellationToken, Task<(string? Path, int Major)>> _systemJavaProbe;

    public LaunchPreflight(
        JavaInstaller installer,
        JreManager jre,
        Func<string, long?>? diskProbe = null,
        Func<long?>? memoryProbe = null,
        Func<CancellationToken, Task<(string? Path, int Major)>>? systemJavaProbe = null)
    {
        _installer = installer;
        _jre = jre;
        _diskProbe = diskProbe ?? DefaultDiskProbe;
        _memoryProbe = memoryProbe ?? (() => SystemMemoryInfo.TotalBytes);
        _systemJavaProbe = systemJavaProbe ?? (ct => JreManager.DetectSystemJavaAsync(ct));
    }

    /// <summary>执行预检</summary>
    public async Task<PreflightReport> CheckAsync(
        GameInstance instance, int maxHeapMb, CancellationToken ct = default)
    {
        var issues = new List<PreflightIssue>();

        // 1. 版本选择
        if (string.IsNullOrWhiteSpace(instance.VersionId))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error, "Preflight.NoVersion", Array.Empty<object?>()));
            return new PreflightReport { Issues = issues };
        }

        var installed = _installer.IsInstalled(instance.VersionId);
        if (!installed)
        {
            // 不阻断：JavaEditionService.LaunchAsync 会自动安装
            issues.Add(new PreflightIssue(
                PreflightSeverity.Info, "Preflight.VersionNotInstalled", new object?[] { instance.VersionId }));
        }
        else
        {
            // 2. Java 匹配（版本 JSON 已就绪才能确定所需主版本）
            try
            {
                var json = await _installer.GetVersionJsonAsync(instance.VersionId, ct);
                var required = JavaInstaller.GetRequiredJavaMajor(json);
                await CheckJavaAsync(required, issues, ct);
            }
            catch { /* JSON 读取失败留给启动流程报错，预检不重复阻断 */ }
        }

        // 3. 磁盘空间（数据盘）
        CheckDisk(issues);

        // 4. 内存配置
        CheckMemory(maxHeapMb, issues);

        return new PreflightReport { Issues = issues };
    }

    private async Task CheckJavaAsync(int requiredMajor, List<PreflightIssue> issues, CancellationToken ct)
    {
        if (JreManager.GetManagedJavaPath(requiredMajor) is not null)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Info, "Preflight.JavaManaged", new object?[] { requiredMajor }));
            return;
        }

        var (sysPath, sysMajor) = await _systemJavaProbe(ct);
        if (sysPath is not null && sysMajor >= requiredMajor)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Info, "Preflight.JavaSystem", new object?[] { sysMajor, requiredMajor }));
            return;
        }

        // 不阻断：JreManager.EnsureJavaAsync 会自动下载
        issues.Add(new PreflightIssue(
            PreflightSeverity.Info, "Preflight.JavaWillDownload",
            new object?[] { requiredMajor, ApproxJreSizeMb }));
    }

    private void CheckDisk(List<PreflightIssue> issues)
    {
        var available = _diskProbe(LoyaPaths.Root);
        if (available is null) return; // 探测失败不误报
        var availableGb = available.Value / (1024.0 * 1024 * 1024);

        if (available.Value < DiskErrorBytes)
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error, "Preflight.DiskCritical", new object?[] { availableGb.ToString("F1") }));
        else if (available.Value < DiskWarnBytes)
            issues.Add(new PreflightIssue(
                PreflightSeverity.Warning, "Preflight.DiskLow", new object?[] { availableGb.ToString("F1") }));
    }

    private void CheckMemory(int maxHeapMb, List<PreflightIssue> issues)
    {
        var total = _memoryProbe();
        if (total is null) return; // 探测失败不误报
        var totalMb = total.Value / (1024 * 1024);

        if (maxHeapMb >= totalMb)
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error, "Preflight.MemoryExceeds", new object?[] { maxHeapMb, totalMb }));
        else if (totalMb - maxHeapMb < MemoryReserveMb)
            issues.Add(new PreflightIssue(
                PreflightSeverity.Warning, "Preflight.MemoryTight", new object?[] { totalMb - maxHeapMb }));
    }

    /// <summary>默认磁盘探测：数据目录所在盘的可用空间</summary>
    private static long? DefaultDiskProbe(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch { return null; }
    }
}
