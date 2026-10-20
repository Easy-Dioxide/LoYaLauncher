using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Core;
using LoyaLauncher.Java;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// 整合包 ViewModel：导入 .zip 整合包（重建实例）、把本地实例导出为整合包。
/// </summary>
public sealed partial class ModpacksViewModel : ViewModelBase
{
    private readonly ModpackService _packService;

    public ModpacksViewModel(ModpackService packService)
    {
        _packService = packService;
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _progress;

    /// <summary>最近一次导出路径（供打开文件夹）</summary>
    [ObservableProperty]
    private string _lastExportPath = "";

    private static string Lang(string key) => Services.LangService.T(key, key);

    public override void OnNavigatedTo() => Refresh();

    /// <summary>刷新实例列表</summary>
    public void Refresh()
    {
        Instances.Clear();
        foreach (var inst in JavaEditionService.ListInstances())
            Instances.Add(inst);
        IsEmpty = Instances.Count == 0;
    }

    // ======================= 导入 =======================

    /// <summary>导入整合包 zip</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var file = await PickFileAsync([new FilePickerFileType("LoyaLauncher 整合包") { Patterns = ["*.zip"] }]);
        if (file is null) return;

        IsBusy = true;
        StatusText = Lang("Modpacks.Importing");
        try
        {
            var result = await _packService.ImportAsync(file.Path.LocalPath);
            Refresh();
            StatusText = result.Warning is null
                ? string.Format(Lang("Modpacks.ImportDone"), result.Instance.Name)
                : $"{string.Format(Lang("Modpacks.ImportDone"), result.Instance.Name)}（{result.Warning}）";
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Modpacks.ImportFailed")}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ======================= 导出 =======================

    /// <summary>导出实例为整合包 zip</summary>
    [RelayCommand]
    private async Task ExportAsync(GameInstance instance)
    {
        var safeName = string.Join("_", instance.Name.Split(Path.GetInvalidFileNameChars()));
        var suggested = $"{safeName}-{DateTime.Now:yyyyMMdd}.zip";
        var file = await PickSaveFileAsync(suggested);
        if (file is null) return;

        IsBusy = true;
        StatusText = Lang("Modpacks.Exporting");
        Progress = 0;
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var path = await _packService.ExportAsync(instance, file.Path.LocalPath, progress);
            LastExportPath = path;
            StatusText = string.Format(Lang("Modpacks.ExportDone"), path);
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Modpacks.ExportFailed")}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>打开最近导出文件所在目录</summary>
    [RelayCommand]
    private void OpenExportFolder()
    {
        if (string.IsNullOrEmpty(LastExportPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(LastExportPath)!;
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", dir);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", dir);
            else
                System.Diagnostics.Process.Start("xdg-open", dir);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    // ======================= 文件对话框 =======================

    private static async Task<IStorageFile?> PickFileAsync(FilePickerFileType[] types)
    {
        var window = GetMainWindow();
        if (window is null) return null;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Lang("Modpacks.ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.Count > 0 ? files[0] : null;
    }

    private static async Task<IStorageFile?> PickSaveFileAsync(string suggestedName)
    {
        var window = GetMainWindow();
        if (window is null) return null;
        return await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Lang("Modpacks.ChooseSave"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("LoyaLauncher 整合包") { Patterns = ["*.zip"] },
            ],
        });
    }

    private static Avalonia.Controls.Window? GetMainWindow() =>
        (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
