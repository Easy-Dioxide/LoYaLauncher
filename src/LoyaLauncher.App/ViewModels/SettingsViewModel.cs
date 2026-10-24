using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Core;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// 设置 ViewModel：镜像源、并发、内存、JVM 参数、主题、数据目录、启动器更新。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ConfigStore _config;
    private readonly UpdateService _updates;

    public SettingsViewModel(ConfigStore config, UpdateService updates)
    {
        _config = config;
        _updates = updates;
    }

    // ======================= 镜像与下载 =======================

    public MirrorOption[] MirrorModes { get; } =
    [
        new("Mirror.Auto", MirrorMode.BmclapiFirst),
        new("Mirror.Official", MirrorMode.OfficialOnly),
        new("Mirror.Bmclapi", MirrorMode.BmclapiOnly),
        new("Mirror.CustomFirst", MirrorMode.CustomFirst),
        new("Mirror.CustomOnly", MirrorMode.CustomOnly),
    ];

    [ObservableProperty]
    private MirrorOption _mirror = new("Mirror.Auto", MirrorMode.BmclapiFirst);

    /// <summary>是否展示自定义源地址输入行</summary>
    public bool IsCustomMirrorVisible =>
        Mirror.Value is MirrorMode.CustomFirst or MirrorMode.CustomOnly;

    [ObservableProperty]
    private string _customMirrorUrl = "";

    partial void OnMirrorChanged(MirrorOption value) =>
        OnPropertyChanged(nameof(IsCustomMirrorVisible));

    [ObservableProperty]
    private int _maxConcurrentDownloads;

    // ======================= 游戏 =======================

    [ObservableProperty]
    private int _javaMaxMemoryMb;

    [ObservableProperty]
    private string _jvmArguments = "";

    // ======================= 界面 =======================

    [ObservableProperty]
    private bool _minimizeAfterLaunch;

    [ObservableProperty]
    private bool _experimentalEnabled;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>数据目录路径（展示 + 打开）</summary>
    public string DataDirectory => LoyaPaths.Root;

    // ======================= 启动器更新 =======================

    /// <summary>当前启动器版本</summary>
    public string CurrentVersion => UpdateService.CurrentVersion;

    [ObservableProperty]
    private string _updateStatusText = "";

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private double _updateProgress;

    /// <summary>待安装的更新信息（有更新且未下载时可用）</summary>
    private UpdateInfo? _pendingUpdate;

    /// <summary>检查更新</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        IsCheckingUpdate = true;
        UpdateStatusText = Lang("Settings.Checking");
        try
        {
            var info = await _updates.CheckAsync();
            if (info.Error is not null)
            {
                UpdateStatusText = info.Error;
                HasUpdate = false;
            }
            else if (info.HasUpdate)
            {
                _pendingUpdate = info;
                HasUpdate = info.DownloadUrl is not null;
                UpdateStatusText = string.Format(Lang("Settings.UpdateAvailable"), info.LatestVersion);
            }
            else
            {
                HasUpdate = false;
                UpdateStatusText = Lang("Settings.UpToDate");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"{Lang("Settings.UpdateFailed")}: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>下载更新（就绪后下次重启自动安装）</summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate is null) return;
        IsCheckingUpdate = true;
        UpdateStatusText = Lang("Settings.Downloading");
        UpdateProgress = 0;
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            await _updates.PrepareUpdateAsync(_pendingUpdate, progress);
            UpdateStatusText = Lang("Settings.UpdateReady");
            HasUpdate = false;
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"{Lang("Settings.UpdateFailed")}: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    public override void OnNavigatedTo()
    {
        var c = _config.Config;
        Mirror = MirrorModes.FirstOrDefault(m => m.Value == c.Mirror) ?? MirrorModes[0];
        CustomMirrorUrl = c.CustomMirrorUrl;
        OnPropertyChanged(nameof(IsCustomMirrorVisible));
        MaxConcurrentDownloads = c.MaxConcurrentDownloads;
        JavaMaxMemoryMb = c.JavaMaxMemoryMb;
        JvmArguments = c.JvmArguments;
        MinimizeAfterLaunch = c.MinimizeAfterLaunch;
        ExperimentalEnabled = c.ExperimentalEnabled;
    }

    /// <summary>保存设置</summary>
    [RelayCommand]
    private void Save()
    {
        var c = _config.Config;
        c.Mirror = Mirror.Value;
        c.CustomMirrorUrl = CustomMirrorUrl;
        c.MaxConcurrentDownloads = MaxConcurrentDownloads;
        c.JavaMaxMemoryMb = JavaMaxMemoryMb;
        c.JvmArguments = JvmArguments;
        c.MinimizeAfterLaunch = MinimizeAfterLaunch;
        c.ExperimentalEnabled = ExperimentalEnabled;
        _config.Save();
        StatusText = Lang("Settings.Saved");
    }

    /// <summary>恢复默认设置</summary>
    [RelayCommand]
    private void Reset()
    {
        var defaults = new AppConfig();
        Mirror = MirrorModes.FirstOrDefault(m => m.Value == defaults.Mirror) ?? MirrorModes[0];
        CustomMirrorUrl = defaults.CustomMirrorUrl;
        OnPropertyChanged(nameof(IsCustomMirrorVisible));
        MaxConcurrentDownloads = defaults.MaxConcurrentDownloads;
        JavaMaxMemoryMb = defaults.JavaMaxMemoryMb;
        JvmArguments = defaults.JvmArguments;
        MinimizeAfterLaunch = defaults.MinimizeAfterLaunch;
        ExperimentalEnabled = defaults.ExperimentalEnabled;
        Save();
        StatusText = Lang("Settings.Reset");
    }

    /// <summary>打开数据目录</summary>
    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            LoyaPaths.EnsureDirectories();
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", LoyaPaths.Root);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", LoyaPaths.Root);
            else
                System.Diagnostics.Process.Start("xdg-open", LoyaPaths.Root);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static string Lang(string key) => Services.LangService.T(key, key);
}
