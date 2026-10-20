using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Core;
using LoyaLauncher.Java;
using LoyaLauncher.Mods;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// Mod 管理 ViewModel：Modrinth 搜索、四加载器安装、实例 mod 管理。
/// </summary>
public sealed partial class ModsViewModel : ViewModelBase
{
    private readonly JavaEditionService _java;
    private readonly ModrinthClient _modrinth;
    private readonly LoaderInstaller _loader;
    private readonly Core.EventBus _bus;

    public ModsViewModel(
        JavaEditionService java,
        ModrinthClient modrinth,
        LoaderInstaller loader,
        Core.EventBus bus)
    {
        _java = java;
        _modrinth = modrinth;
        _loader = loader;
        _bus = bus;
    }

    // ======================= 实例与加载器 =======================

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<InstalledMod> InstalledMods { get; } = [];
    public ObservableCollection<ModrinthProject> SearchResults { get; } = [];

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _selectedLoader = "fabric";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    public string[] Loaders { get; } = ["fabric", "forge", "quilt", "neoforged"];

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        RefreshMods();
        if (value is not null && value.Loader is not null)
            SelectedLoader = value.Loader;
    }

    // ======================= 生命周期 =======================

    public override void OnNavigatedTo()
    {
        if (Instances.Count == 0)
        {
            foreach (var inst in JavaEditionService.ListInstances())
                Instances.Add(inst);
            SelectedInstance = Instances.FirstOrDefault();
        }
    }

    /// <summary>刷新实例 mod 列表</summary>
    public void RefreshMods()
    {
        InstalledMods.Clear();
        if (SelectedInstance is null) return;
        foreach (var mod in ModManager.ScanMods(SelectedInstance))
            InstalledMods.Add(mod);
    }

    // ======================= 命令 =======================

    /// <summary>搜索 Modrinth</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SelectedInstance is null || IsSearching) return;
        IsSearching = true;
        try
        {
            var result = await _modrinth.SearchAsync(SearchQuery, SelectedInstance.VersionId, SelectedLoader);
            SearchResults.Clear();
            foreach (var hit in result.Hits)
                SearchResults.Add(hit);
            StatusText = string.Format(Lang("Mods.SearchDone"), result.Total);
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Mods.SearchFailed")}: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>安装 Mod（最新兼容版本）</summary>
    [RelayCommand]
    private async Task InstallModAsync(ModrinthProject project)
    {
        if (SelectedInstance is null) return;
        IsLoading = true;
        try
        {
            var versions = await _modrinth.GetVersionsAsync(project.ProjectId, SelectedInstance.VersionId, SelectedLoader);
            var version = versions.FirstOrDefault(v => v.Files.Any(f => f.Primary)) ?? versions.FirstOrDefault();
            var file = version?.Files.FirstOrDefault(f => f.Primary) ?? version?.Files.FirstOrDefault();
            if (file is null)
            {
                StatusText = string.Format(Lang("Mods.NoCompatible"), project.Title);
                return;
            }
            await _modrinth.DownloadModAsync(file, SelectedInstance.ModsDirectory, GetDownloader());
            RefreshMods();
            StatusText = string.Format(Lang("Mods.Installed"), project.Title, version!.VersionNumber);
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Mods.InstallFailed")}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>给当前实例安装 Mod 加载器</summary>
    [RelayCommand]
    private async Task InstallLoaderAsync()
    {
        if (SelectedInstance is null) return;
        IsLoading = true;
        try
        {
            var kind = Enum.Parse<LoaderKind>(SelectedLoader, ignoreCase: true);
            var newVersionId = await _loader.InstallAsync(kind, SelectedInstance.VersionId, null, null, CancellationToken.None);

            // 切换实例到加载器版本并写入元数据
            SelectedInstance.VersionId = newVersionId;
            SelectedInstance.Loader = SelectedLoader;
            await JavaEditionService.SaveInstanceAsync(SelectedInstance);
            StatusText = string.Format(Lang("Mods.LoaderInstalled"), SelectedLoader, newVersionId);
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Mods.LoaderFailed")}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>启用/禁用 Mod</summary>
    [RelayCommand]
    private void ToggleMod(InstalledMod mod)
    {
        if (mod.Enabled) ModManager.Disable(mod.FilePath);
        else ModManager.Enable(mod.FilePath);
        RefreshMods();
    }

    /// <summary>删除 Mod</summary>
    [RelayCommand]
    private void DeleteMod(InstalledMod mod)
    {
        ModManager.Delete(mod.FilePath);
        RefreshMods();
    }

    /// <summary>导入本地 Mod jar</summary>
    [RelayCommand]
    private void ImportMod(string filePath)
    {
        if (SelectedInstance is null) return;
        ModManager.ImportLocalJar(SelectedInstance, filePath);
        RefreshMods();
        StatusText = string.Format(Lang("Mods.Imported"), System.IO.Path.GetFileName(filePath));
    }

    private static Downloader.DownloadManager GetDownloader()
    {
        // 与 AppServices 共用同一个下载管理器实例
        return (Downloader.DownloadManager)Services.AppServices.Provider
            .GetService(typeof(Downloader.DownloadManager))!;
    }

    private static string Lang(string key) => Services.LangService.T(key, key);
}
