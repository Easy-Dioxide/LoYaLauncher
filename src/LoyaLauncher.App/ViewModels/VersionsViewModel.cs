using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Core;
using LoyaLauncher.Java;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// 版本管理 ViewModel：浏览 Mojang 全部历史版本、安装、创建实例。
/// </summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly JavaEditionService _java;
    private readonly Core.EventBus _bus;
    private CancellationTokenSource? _installCts;

    public VersionsViewModel(JavaEditionService java, Core.EventBus bus)
    {
        _java = java;
        _bus = bus;
    }

    // ======================= 可下载版本 =======================

    public ObservableCollection<ManifestVersion> AvailableVersions { get; } = [];
    public ObservableCollection<InstalledVersionMeta> InstalledVersions { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>版本类型筛选选项</summary>
    public sealed record TypeFilterOption(string Label, string Value);

    public ObservableCollection<TypeFilterOption> TypeFilterOptions { get; } =
    [
        new("正式版", "release"),
        new("快照", "snapshot"),
        new("全部", "all"),
    ];

    [ObservableProperty]
    private TypeFilterOption _selectedTypeFilter = new("正式版", "release");

    [ObservableProperty]
    private InstalledVersionMeta? _selectedInstalled;

    [ObservableProperty]
    private ManifestVersion? _selectedAvailable;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>过滤后的版本列表</summary>
    public IEnumerable<ManifestVersion> FilteredVersions
    {
        get
        {
            var q = AvailableVersions.AsEnumerable();
            if (SelectedTypeFilter.Value != "all")
                q = q.Where(v => v.Type == SelectedTypeFilter.Value);
            if (!string.IsNullOrWhiteSpace(SearchText))
                q = q.Where(v => v.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            return q;
        }
    }

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredVersions));
    partial void OnSelectedTypeFilterChanged(TypeFilterOption value) => OnPropertyChanged(nameof(FilteredVersions));

    // ======================= 生命周期 =======================

    public override async void OnNavigatedTo()
    {
        if (AvailableVersions.Count == 0)
            await LoadVersionsAsync();
        RefreshInstalled();
    }

    private async Task LoadVersionsAsync()
    {
        IsLoading = true;
        try
        {
            var versions = await _java.GetAvailableVersionsAsync();
            AvailableVersions.Clear();
            foreach (var v in versions)
                AvailableVersions.Add(v);
            OnPropertyChanged(nameof(FilteredVersions));
            StatusText = string.Format(Lang("Versions.Loaded"), versions.Count);
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Versions.LoadFailed")}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>刷新版本列表命令</summary>
    [RelayCommand]
    private async Task LoadVersions() => await LoadVersionsAsync();

    /// <summary>刷新已安装版本列表</summary>
    public void RefreshInstalled()
    {
        InstalledVersions.Clear();
        foreach (var v in _java.ListInstalled())
            InstalledVersions.Add(v);
    }

    // ======================= 命令 =======================

    /// <summary>安装指定版本</summary>
    [RelayCommand]
    private async Task InstallAsync(ManifestVersion version)
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallProgress = 0;
        _installCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<JavaInstallProgress>(p =>
            {
                InstallProgress = p.Percent;
                StatusText = $"{Lang("Versions.Installing")} {version.Id} — {p.Stage} {p.Detail}".Trim();
            });
            await _java.InstallAsync(version.Id, progress, _installCts.Token);
            RefreshInstalled();
            StatusText = string.Format(Lang("Versions.Installed"), version.Id);
        }
        catch (OperationCanceledException)
        {
            StatusText = Lang("Versions.InstallCanceled");
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Versions.InstallFailed")}: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    /// <summary>取消安装</summary>
    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    /// <summary>基于已安装版本创建实例</summary>
    [RelayCommand]
    private async Task CreateInstanceAsync(InstalledVersionMeta meta)
    {
        var instance = await _java.CreateInstanceAsync(meta.Id, meta.Id);
        StatusText = string.Format(Lang("Versions.InstanceCreated"), meta.Id, instance.Name);
    }

    /// <summary>删除已安装版本</summary>
    [RelayCommand]
    private void Uninstall(InstalledVersionMeta meta)
    {
        var dir = System.IO.Path.Combine(LoyaPaths.Versions, meta.Id);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, true);
        RefreshInstalled();
        StatusText = string.Format(Lang("Versions.Uninstalled"), meta.Id);
    }

    private static string Lang(string key) => Services.LangService.T(key, key);
}
