using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Java;
using LoyaLauncher.Mods;

namespace LoyaLauncher.App.ViewModels;

/// <summary>下载任务 UI 模型</summary>
public sealed partial class DownloadTaskItem : ObservableObject
{
    [ObservableProperty]
    private string _taskId = "";

    [ObservableProperty]
    private string _group = "";

    [ObservableProperty]
    private string _statusText = "等待中";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private bool _isCompleted;
}

/// <summary>镜像源选项（下拉框展示用）</summary>
public sealed record MirrorOption(string LabelKey, MirrorMode Value)
{
    public string Label => Services.LangService.T(LabelKey, LabelKey);
}

/// <summary>版本类型筛选项（下拉框展示用）</summary>
public sealed record VersionFilterOption(string LabelKey, string Value)
{
    public string Label => Services.LangService.T(LabelKey, LabelKey);
}

/// <summary>
/// 下载中心 ViewModel（PCL2 式三页签）：
/// 游戏本体下载 / Mod 下载 / 下载任务列表，顶部可快捷切换下载镜像源。
/// </summary>
public sealed partial class DownloadCenterViewModel : ViewModelBase
{
    private readonly JavaEditionService _java;
    private readonly ModrinthClient _modrinth;
    private readonly Core.EventBus _bus;
    private readonly Core.ConfigStore _config;
    private readonly Dictionary<string, DownloadTaskItem> _items = new();
    private readonly Dictionary<string, DownloadTaskItem> _active = new();
    private CancellationTokenSource? _installCts;
    private IDisposable? _progressSub;
    private IDisposable? _completedSub;

    public DownloadCenterViewModel(
        JavaEditionService java,
        ModrinthClient modrinth,
        Core.EventBus bus,
        Core.ConfigStore config)
    {
        _java = java;
        _modrinth = modrinth;
        _bus = bus;
        _config = config;
        _selectedMirror = MirrorOptions.FirstOrDefault(m => m.Value == config.Config.Mirror)
                          ?? MirrorOptions[0];
        _customMirrorUrl = config.Config.CustomMirrorUrl;
    }

    // ======================= 页签 =======================

    /// <summary>当前页签：0=游戏本体 1=Mod 2=任务</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>页签选中态（计算属性，初始状态即可正确求值；点击经 SelectTabCommand 切换）</summary>
    public bool IsTabGame => SelectedTab == 0;
    public bool IsTabMods => SelectedTab == 1;
    public bool IsTabTasks => SelectedTab == 2;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsTabGame));
        OnPropertyChanged(nameof(IsTabMods));
        OnPropertyChanged(nameof(IsTabTasks));
    }

    /// <summary>切换页签（XAML CommandParameter 为字符串，自行解析）</summary>
    [RelayCommand]
    private void SelectTab(string indexText)
    {
        if (int.TryParse(indexText, out var index) && index is >= 0 and <= 2)
            SelectedTab = index;
    }

    // ======================= 下载源（快捷切换，写回配置即时生效） =======================

    public MirrorOption[] MirrorOptions { get; } =
    [
        new("Mirror.Auto", MirrorMode.BmclapiFirst),
        new("Mirror.Official", MirrorMode.OfficialOnly),
        new("Mirror.Bmclapi", MirrorMode.BmclapiOnly),
        new("Mirror.CustomFirst", MirrorMode.CustomFirst),
        new("Mirror.CustomOnly", MirrorMode.CustomOnly),
    ];

    [ObservableProperty]
    private MirrorOption _selectedMirror;

    /// <summary>是否展示自定义源地址输入框（选中含 Custom 的模式时）</summary>
    public bool IsCustomMirrorVisible =>
        SelectedMirror.Value is MirrorMode.CustomFirst or MirrorMode.CustomOnly;

    [ObservableProperty]
    private string _customMirrorUrl = "";

    partial void OnSelectedMirrorChanged(MirrorOption value)
    {
        _config.Config.Mirror = value.Value;
        _config.Save();
        OnPropertyChanged(nameof(IsCustomMirrorVisible));
    }

    partial void OnCustomMirrorUrlChanged(string value)
    {
        _config.Config.CustomMirrorUrl = value;
        _config.Save();
    }

    // ======================= 游戏本体 =======================

    public ObservableCollection<ManifestVersion> Versions { get; } = [];

    [ObservableProperty]
    private ManifestVersion? _selectedVersion;

    [ObservableProperty]
    private string _versionSearchText = "";

    /// <summary>版本类型筛选项</summary>
    public ObservableCollection<VersionFilterOption> VersionFilters { get; } =
    [
        new("Downloads.FilterRelease", "release"),
        new("Downloads.FilterSnapshot", "snapshot"),
        new("Downloads.FilterAll", "all"),
    ];

    [ObservableProperty]
    private VersionFilterOption _selectedVersionFilter = new("Downloads.FilterRelease", "release");

    [ObservableProperty]
    private bool _isLoadingVersions;

    partial void OnIsLoadingVersionsChanged(bool value) => OnPropertyChanged(nameof(ShowVersionEmptyHint));

    /// <summary>版本列表为空提示（加载完成且无结果时展示，避免加载失败时区域全空白）</summary>
    public bool ShowVersionEmptyHint => !IsLoadingVersions && Versions.Count == 0;

    [ObservableProperty]
    private string _versionsStatusText = "";

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    /// <summary>过滤后的版本列表（搜索 + 类型筛选）</summary>
    public IEnumerable<ManifestVersion> FilteredVersions
    {
        get
        {
            var q = Versions.AsEnumerable();
            if (SelectedVersionFilter.Value != "all")
                q = q.Where(v => v.Type == SelectedVersionFilter.Value);
            if (!string.IsNullOrWhiteSpace(VersionSearchText))
                q = q.Where(v => v.Id.Contains(VersionSearchText, StringComparison.OrdinalIgnoreCase));
            return q;
        }
    }

    partial void OnVersionSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredVersions));
    partial void OnSelectedVersionFilterChanged(VersionFilterOption value) => OnPropertyChanged(nameof(FilteredVersions));

    /// <summary>选中版本详情：类型（本地化）</summary>
    public string SelectedVersionTypeLabel => SelectedVersion?.Type switch
    {
        "release" => Lang("Downloads.TypeRelease"),
        "snapshot" => Lang("Downloads.TypeSnapshot"),
        "old_beta" => Lang("Downloads.TypeOldBeta"),
        "old_alpha" => Lang("Downloads.TypeOldAlpha"),
        _ => SelectedVersion?.Type ?? "",
    };

    /// <summary>选中版本详情：发布日期</summary>
    public string SelectedVersionTimeLabel
    {
        get
        {
            var t = SelectedVersion?.ReleaseTime;
            if (string.IsNullOrEmpty(t)) return "";
            return DateTime.TryParse(t, out var dt) ? dt.ToString("yyyy-MM-dd") : t;
        }
    }

    /// <summary>选中版本详情：所需 Java 主版本（按版本号经验推断，与安装时一致）</summary>
    public int SelectedVersionJavaMajor =>
        SelectedVersion is null ? 0 :
        JavaInstaller.GetRequiredJavaMajor(new VersionJson { Id = SelectedVersion.Id });

    /// <summary>选中版本是否已安装</summary>
    public bool IsSelectedVersionInstalled =>
        SelectedVersion is not null && _java.IsInstalled(SelectedVersion.Id);

    partial void OnSelectedVersionChanged(ManifestVersion? value)
    {
        OnPropertyChanged(nameof(SelectedVersionTypeLabel));
        OnPropertyChanged(nameof(SelectedVersionTimeLabel));
        OnPropertyChanged(nameof(SelectedVersionJavaMajor));
        OnPropertyChanged(nameof(IsSelectedVersionInstalled));
    }

    // ======================= Mod =======================

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<ModrinthProject> ModResults { get; } = [];

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    public string[] Loaders { get; } = ["fabric", "forge", "quilt", "neoforged"];

    [ObservableProperty]
    private string _selectedLoader = "fabric";

    [ObservableProperty]
    private string _modSearchQuery = "";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _modsStatusText = "";

    /// <summary>是否存在可用实例（无实例时禁用 Mod 安装）</summary>
    public bool HasInstances => Instances.Count > 0;

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        if (value is not null && !string.IsNullOrEmpty(value.Loader))
            SelectedLoader = value.Loader;
    }

    // ======================= 任务列表 =======================

    public ObservableCollection<DownloadTaskItem> Tasks { get; } = [];

    [ObservableProperty]
    private string _totalSpeedText = "0 B/s";

    [ObservableProperty]
    private bool _hasActiveTasks;

    [ObservableProperty]
    private int _activeCount;

    // ======================= 生命周期 =======================

    public override void OnNavigatedTo()
    {
        // 订阅句柄持有 + 进入时幂等重建：避免重复进入页面导致同一回调注册多份
        _progressSub?.Dispose();
        _completedSub?.Dispose();
        _progressSub = _bus.Subscribe<DownloadProgressEvent>(OnProgress);
        _completedSub = _bus.Subscribe<DownloadCompletedEvent>(OnCompleted);

        if (Versions.Count == 0)
            _ = LoadVersionsAsync();
        if (Instances.Count == 0)
            RefreshInstances();
    }

    public override void OnNavigatedFrom()
    {
        _progressSub?.Dispose();
        _progressSub = null;
        _completedSub?.Dispose();
        _completedSub = null;
    }

    private void RefreshInstances()
    {
        Instances.Clear();
        foreach (var inst in JavaEditionService.ListInstances())
            Instances.Add(inst);
        SelectedInstance ??= Instances.FirstOrDefault();
        OnPropertyChanged(nameof(HasInstances));
    }

    // ======================= 游戏本体：加载与安装 =======================

    private async Task LoadVersionsAsync()
    {
        IsLoadingVersions = true;
        try
        {
            var versions = await _java.GetAvailableVersionsAsync();
            Versions.Clear();
            foreach (var v in versions)
                Versions.Add(v);
            OnPropertyChanged(nameof(FilteredVersions));
            OnPropertyChanged(nameof(ShowVersionEmptyHint));
            VersionsStatusText = string.Format(Lang("Downloads.VersionsLoaded"), versions.Count);
        }
        catch (Exception ex)
        {
            VersionsStatusText = $"{Lang("Downloads.VersionsLoadFailed")}: {ex.Message}";
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync() => await LoadVersionsAsync();

    /// <summary>
    /// 安装选中版本（PCL2 式「下载即可玩」）：安装完成后自动创建同名实例。
    /// </summary>
    [RelayCommand]
    private async Task InstallVersionAsync()
    {
        var version = SelectedVersion;
        if (version is null || IsInstalling) return;

        IsInstalling = true;
        InstallProgress = 0;
        _installCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<JavaInstallProgress>(p =>
            {
                InstallProgress = p.Percent * 100;
                VersionsStatusText = $"{Lang("Downloads.Installing")} {version.Id} — {p.Stage} {p.Detail}".Trim();
            });
            await _java.InstallAsync(version.Id, progress, _installCts.Token);

            // 安装完成即创建实例（幂等：已存在同名实例时跳过创建逻辑由 CreateInstance 保证新 ID）
            var instance = await _java.CreateInstanceAsync(version.Id, version.Id, _installCts.Token);
            OnPropertyChanged(nameof(IsSelectedVersionInstalled));
            RefreshInstances();
            VersionsStatusText = string.Format(Lang("Downloads.InstalledWithInstance"), version.Id, instance.Name);
        }
        catch (OperationCanceledException)
        {
            VersionsStatusText = Lang("Downloads.InstallCanceled");
        }
        catch (Exception ex)
        {
            VersionsStatusText = $"{Lang("Downloads.InstallFailed")}: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    // ======================= Mod：搜索与安装 =======================

    [RelayCommand]
    private async Task SearchModsAsync()
    {
        if (IsSearching) return;
        IsSearching = true;
        try
        {
            // 未选实例时用空版本号搜索（不过滤游戏版本，展示全部结果）
            var gameVersion = SelectedInstance?.VersionId ?? "";
            var result = await _modrinth.SearchAsync(ModSearchQuery, gameVersion, SelectedLoader);
            ModResults.Clear();
            foreach (var hit in result.Hits)
                ModResults.Add(hit);
            ModsStatusText = string.Format(Lang("Downloads.ModsFound"), result.Total);
        }
        catch (Exception ex)
        {
            ModsStatusText = $"{Lang("Downloads.ModsSearchFailed")}: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task InstallModAsync(ModrinthProject project)
    {
        if (SelectedInstance is null)
        {
            ModsStatusText = Lang("Downloads.NoInstance");
            return;
        }
        try
        {
            var versions = await _modrinth.GetVersionsAsync(
                project.ProjectId, SelectedInstance.VersionId, SelectedLoader);
            var version = versions.FirstOrDefault(v => v.Files.Any(f => f.Primary)) ?? versions.FirstOrDefault();
            var file = version?.Files.FirstOrDefault(f => f.Primary) ?? version?.Files.FirstOrDefault();
            if (file is null)
            {
                ModsStatusText = string.Format(Lang("Downloads.NoCompatibleMod"), project.Title);
                return;
            }
            await _modrinth.DownloadModAsync(file, SelectedInstance.ModsDirectory, GetDownloader());
            ModsStatusText = string.Format(Lang("Downloads.ModInstalled"), project.Title, version!.VersionNumber);
        }
        catch (Exception ex)
        {
            ModsStatusText = $"{Lang("Downloads.ModInstallFailed")}: {ex.Message}";
        }
    }

    // ======================= 任务事件 =======================

    private void OnProgress(DownloadProgressEvent e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_items.TryGetValue(e.TaskId, out var item))
            {
                item = new DownloadTaskItem { TaskId = e.TaskId, Group = e.Group };
                _items[e.TaskId] = item;
                Tasks.Add(item);
            }

            if (e.Group == "verify")
            {
                item.StatusText = Lang("Downloads.Verifying");
                return;
            }

            if (e.TotalBytes > 0)
            {
                item.Percent = e.ReceivedBytes * 100.0 / e.TotalBytes;
                item.StatusText = $"{FormatBytes(e.ReceivedBytes)} / {FormatBytes(e.TotalBytes)}";
            }
            else
            {
                item.StatusText = FormatBytes(e.ReceivedBytes);
            }
            item.SpeedText = FormatSpeed(e.BytesPerSecond);

            _active[e.TaskId] = item;
            UpdateTotals();
        });
    }

    private void OnCompleted(DownloadCompletedEvent e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_items.TryGetValue(e.TaskId, out var item))
            {
                item.IsCompleted = e.Success;
                item.Percent = 100;
                item.SpeedText = "";
                item.StatusText = e.Success
                    ? Lang("Downloads.TaskDone")
                    : $"{Lang("Downloads.TaskFailed")}: {e.Error}";
            }
            _active.Remove(e.TaskId);
            UpdateTotals();
        });
    }

    private void UpdateTotals()
    {
        ActiveCount = _active.Count;
        HasActiveTasks = _active.Count > 0;
        TotalSpeedText = _active.Count > 0
            ? string.Format(Lang("Downloads.ActiveTasks"), _active.Count)
            : Lang("Downloads.NoActiveTasks");
    }

    // ======================= 工具 =======================

    private static Downloader.DownloadManager GetDownloader()
    {
        // 与 AppServices 共用同一个下载管理器实例
        return (Downloader.DownloadManager)Services.AppServices.Provider
            .GetService(typeof(Downloader.DownloadManager))!;
    }

    private static string Lang(string key) => Services.LangService.T(key, key);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        return $"{FormatBytes((long)bytesPerSecond)}/s";
    }
}
