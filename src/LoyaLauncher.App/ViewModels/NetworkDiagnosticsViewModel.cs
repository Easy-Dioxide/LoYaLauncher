using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.App.Services;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.App.ViewModels;

/// <summary>单个源的探测状态</summary>
public enum DiagState
{
    /// <summary>尚未测试</summary>
    Pending,
    /// <summary>正在测试</summary>
    Testing,
    /// <summary>可达（收到成功响应）</summary>
    Ok,
    /// <summary>不可达（网络错误或非成功状态码）</summary>
    Failed,
}

/// <summary>一个下载源的探测结果行（诊断页表格数据源）</summary>
public sealed partial class DiagItem : ObservableObject
{
    public DiagItem(string nameKey, string nameFallback, string url, string group)
    {
        NameKey = nameKey;
        NameFallback = nameFallback;
        Url = url;
        Group = group;
    }

    /// <summary>本地化名称的资源键</summary>
    public string NameKey { get; }

    /// <summary>本地化名称的中文兜底（无 Avalonia 资源时使用，如单元测试）</summary>
    public string NameFallback { get; }

    /// <summary>探测地址（展示时脱敏）</summary>
    public string Url { get; }

    /// <summary>NetLog 归属模块名</summary>
    public string Group { get; }

    /// <summary>本地化名称</summary>
    public string Name => LangService.T(NameKey, NameFallback);

    /// <summary>脱敏后的展示地址（丢弃查询串，避免泄露令牌）</summary>
    public string DisplayUrl => NetLog.SanitizeUrl(Url);

    [ObservableProperty]
    private DiagState _state = DiagState.Pending;

    /// <summary>HTTP 状态码（0 表示未收到响应）</summary>
    [ObservableProperty]
    private int _statusCode;

    /// <summary>耗时（毫秒）</summary>
    [ObservableProperty]
    private double _latencyMs;

    /// <summary>失败原因（成功时为空）</summary>
    [ObservableProperty]
    private string _errorText = "";

    /// <summary>状态文案（本地化）</summary>
    public string StateText => State switch
    {
        DiagState.Ok => LangService.T("Diagnostics.StateOk", "可达"),
        DiagState.Failed => LangService.T("Diagnostics.StateFail", "不可达"),
        DiagState.Testing => LangService.T("Diagnostics.StateTesting", "测试中…"),
        _ => LangService.T("Diagnostics.StatePending", "待测试"),
    };

    /// <summary>结果摘要（如 "HTTP 200 · 215 ms"）</summary>
    public string ResultText
    {
        get
        {
            if (State is DiagState.Pending) return "—";
            var status = StatusCode > 0 ? $"HTTP {StatusCode}" : LangService.T("Diagnostics.NoResponse", "无响应");
            var latency = LatencyMs > 0 ? $" · {LatencyMs:F0} ms" : "";
            return status + latency;
        }
    }

    partial void OnStateChanged(DiagState value) => OnPropertyChanged(nameof(StateText));
    partial void OnStatusCodeChanged(int value) => OnPropertyChanged(nameof(ResultText));
    partial void OnLatencyMsChanged(double value) => OnPropertyChanged(nameof(ResultText));

    /// <summary>应用一次探测结果</summary>
    public void Apply(ProbeResult probe)
    {
        StatusCode = probe.StatusCode;
        LatencyMs = probe.LatencyMs;
        ErrorText = probe.Error ?? "";
        State = probe.Ok ? DiagState.Ok : DiagState.Failed;
    }
}

/// <summary>近期网络请求的展示行（NetEntry 的 UI 友好投影）</summary>
public sealed class NetLogItem(NetEntry entry)
{
    public string Time => entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Module => entry.Module;
    public string Stage => entry.Stage.ToString();
    public string Result => entry.Result;
    public string Host => entry.Host;
    public string Status => entry.StatusCode == 0 ? "—" : entry.StatusCode.ToString();
    public string Duration => entry.DurationMs > 0 ? $"{entry.DurationMs:F0} ms" : "—";
    public string Retry => entry.RetryCount > 0 ? entry.RetryCount.ToString() : "—";
}

/// <summary>
/// 网络诊断 ViewModel（清单 §2.2/§9 P1）：
/// 一键测试各下载源（Mojang / BMCLAPI / Modrinth / GitHub / Adoptium / 自定义源），
/// 展示 HTTPS 状态码、耗时与失败原因；近期请求列表来自 NetLog；
/// 「复制诊断报告」生成脱敏文本（NetLog.ExportReport + 探测结果汇总），
/// 不含任何令牌/设备码/查询串。
/// </summary>
public sealed partial class NetworkDiagnosticsViewModel : ViewModelBase
{
    /// <summary>单源探测超时：避免某源挂起时整个诊断页卡住</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    private readonly DownloadManager _downloads;
    private readonly ConfigStore _config;

    public NetworkDiagnosticsViewModel(DownloadManager downloads, ConfigStore config)
    {
        _downloads = downloads;
        _config = config;
        RebuildTargets();
    }

    /// <summary>探测目标列表（自定义源随配置变化，进入页面时重建）</summary>
    public ObservableCollection<DiagItem> Targets { get; } = [];

    /// <summary>近期网络请求（NetLog 环形缓冲的投影）</summary>
    public ObservableCollection<NetLogItem> RecentLogs { get; } = [];

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private DiagItem? _selectedTarget;

    /// <summary>选中行的失败原因（无选中或无错误时为空，供错误横幅展示）</summary>
    public string SelectedErrorText =>
        SelectedTarget is { State: DiagState.Failed } && !string.IsNullOrEmpty(SelectedTarget.ErrorText)
            ? SelectedTarget.ErrorText
            : "";

    /// <summary>是否有近期请求记录（空状态展示）</summary>
    public bool HasRecentLogs => RecentLogs.Count > 0;

    partial void OnSelectedTargetChanged(DiagItem? value) =>
        OnPropertyChanged(nameof(SelectedErrorText));

    /// <summary>按当前镜像配置重建目标列表（保留已有结果，重置状态）</summary>
    private void RebuildTargets()
    {
        var previous = Targets.ToDictionary(t => t.Url, t => t);
        Targets.Clear();
        foreach (var (nameKey, nameFallback, url, group) in BuildTargetList())
        {
            if (previous.TryGetValue(url, out var old))
            {
                Targets.Add(old);
            }
            else
            {
                Targets.Add(new DiagItem(nameKey, nameFallback, url, group));
            }
        }
    }

    /// <summary>静态源 + 配置中的自定义源</summary>
    private List<(string NameKey, string NameFallback, string Url, string Group)> BuildTargetList()
    {
        var list = new List<(string, string, string, string)>
        {
            ("Diagnostics.TargetMojang", "Mojang 版本清单", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", "diag-mojang"),
            ("Diagnostics.TargetBmclapi", "BMCLAPI 镜像", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", "diag-bmclapi"),
            ("Diagnostics.TargetModrinth", "Modrinth API", "https://api.modrinth.com/v2/search?query=a&limit=1", "diag-modrinth"),
            ("Diagnostics.TargetGithub", "GitHub API（更新检查）", "https://api.github.com", "diag-github"),
            ("Diagnostics.TargetAdoptium", "Adoptium API（Java 下载）", "https://api.adoptium.net/v3/info/available_releases", "diag-adoptium"),
        };

        var custom = _config.Config.CustomMirrorUrl;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var baseUrl = custom.TrimEnd('/');
            list.Add(("Diagnostics.TargetCustom", "自定义下载源", $"{baseUrl}/mc/game/version_manifest_v2.json", "diag-custom"));
        }
        return list;
    }

    public override void OnNavigatedTo()
    {
        RebuildTargets();
        RefreshRecentLogs();
    }

    /// <summary>从 NetLog 刷新近期请求列表</summary>
    private void RefreshRecentLogs()
    {
        RecentLogs.Clear();
        foreach (var entry in NetLog.Recent(60))
            RecentLogs.Add(new NetLogItem(entry));
        OnPropertyChanged(nameof(HasRecentLogs));
    }

    /// <summary>一键测试全部源（顺序执行，避免相互抢带宽干扰结果）</summary>
    [RelayCommand]
    private async Task RunAllAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        StatusText = Lang("Diagnostics.Testing", "测试中…");
        try
        {
            foreach (var target in Targets)
                await ProbeOneAsync(target);

            var ok = Targets.Count(t => t.State == DiagState.Ok);
            var failed = Targets.Count(t => t.State == DiagState.Failed);
            StatusText = string.Format(
                Lang("Diagnostics.SummaryDone", "测试完成：{0} 个源，{1} 个可达，{2} 个不可达"),
                Targets.Count, ok, failed);
            RefreshRecentLogs();
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>单独测试一个源（行内「测试」按钮）</summary>
    [RelayCommand]
    private async Task TestOneAsync(DiagItem? target)
    {
        if (target is null || IsRunning) return;
        IsRunning = true;
        try
        {
            await ProbeOneAsync(target);
            RefreshRecentLogs();
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task ProbeOneAsync(DiagItem target)
    {
        target.State = DiagState.Testing;
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var probe = await _downloads.HeadProbeAsync(target.Url, cts.Token);
            target.Apply(probe);

            // 诊断探测也记入 NetLog：复制报告时可见完整时间线
            if (probe.Ok)
                NetLog.Response(target.Group, target.Url, probe.StatusCode, probe.LatencyMs);
            else
                NetLog.Fail(target.Group, target.Url, probe.StatusCode > 0 ? "http-status" : "network",
                    probe.Error, probe.LatencyMs);
        }
        catch (Exception ex)
        {
            target.State = DiagState.Failed;
            target.StatusCode = 0;
            target.ErrorText = ex.Message;
            NetLog.Fail(target.Group, target.Url, "network", ex.Message);
        }
    }

    /// <summary>
    /// 生成脱敏诊断报告：环境/代理/镜像链 + 本次探测结果 + 近期请求。
    /// 绝不包含令牌、设备码、完整查询串（NetLog.SanitizeUrl 已剥离）。
    /// </summary>
    public string GenerateReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(NetLog.ExportReport(MirrorChainHosts()));

        sb.AppendLine();
        sb.AppendLine("—— 一键测试结果 ——");
        foreach (var t in Targets)
        {
            var state = t.State switch
            {
                DiagState.Ok => "OK",
                DiagState.Failed => "FAIL",
                DiagState.Testing => "TESTING",
                _ => "PENDING",
            };
            sb.AppendLine($"[{state,-7}] {t.Name} ({t.DisplayUrl}) {t.ResultText}" +
                          (string.IsNullOrEmpty(t.ErrorText) ? "" : $" error={t.ErrorText}"));
        }

        sb.AppendLine();
        sb.AppendLine("—— 镜像配置 ——");
        sb.AppendLine($"模式: {_config.Config.Mirror}");
        if (!string.IsNullOrWhiteSpace(_config.Config.CustomMirrorUrl))
            sb.AppendLine($"自定义源: {NetLog.SanitizeUrl(_config.Config.CustomMirrorUrl)}");

        return sb.ToString();
    }

    /// <summary>当前镜像链主机名（仅主机，供报告展示）</summary>
    private List<string> MirrorChainHosts()
    {
        try
        {
            return MirrorSource.ResolveChain(_config.Config.Mirror, _config.Config.CustomMirrorUrl)
                .Select(m => new Uri(m.VersionManifest).Host)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>日志目录（诊断页「打开日志目录」按钮目标）</summary>
    public string LogDirectory => LoyaPaths.Logs;

    private static string Lang(string key, string fallback) => LangService.T(key, fallback);
}
