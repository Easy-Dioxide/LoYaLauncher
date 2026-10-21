using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;
using LoyaLauncher.Java;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// Java 管理页：系统 Java 检测、托管 JRE 列表/删除、按主版本手动下载。
/// </summary>
public sealed partial class JavaManagerViewModel : ViewModelBase
{
    private readonly JreManager _jre;
    private readonly Core.EventBus _bus;
    private readonly IDisposable _progressSubscription;

    public JavaManagerViewModel(JreManager jre, Core.EventBus bus)
    {
        _jre = jre;
        _bus = bus;

        _progressSubscription = bus.Subscribe<DownloadProgressEvent>(e =>
        {
            if (e.Group != "java") return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (e.TotalBytes > 0)
                {
                    Progress = (double)e.ReceivedBytes / e.TotalBytes;
                    StatusText = $"{Lang("Java.Downloading")} {Progress * 100:F0}%";
                }
                else
                {
                    Progress = 0;
                    StatusText = Lang("Java.Downloading");
                }
            });
        });
    }

    // ======================= 数据 =======================

    public ObservableCollection<JavaRuntimeItem> ManagedRuntimes { get; } = [];

    /// <summary>没有任何托管 JRE（显示空状态）</summary>
    public bool HasNoRuntimes => ManagedRuntimes.Count == 0;

    /// <summary>系统检测到的 Java 描述（未检测到为空）</summary>
    [ObservableProperty]
    private string _systemJavaText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _progress;

    /// <summary>手动下载目标主版本输入框</summary>
    [ObservableProperty]
    private string _majorVersionInput = "21";

    [ObservableProperty]
    private JavaRuntimeItem? _selectedRuntime;

    private static string Lang(string key) => Services.LangService.T(key, key);

    public override void OnNavigatedTo() => Refresh();

    /// <summary>刷新系统 Java 检测 + 托管 JRE 列表</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var (path, major) = await JreManager.DetectSystemJavaAsync();
            SystemJavaText = path is null
                ? Lang("Java.SystemNone")
                : $"{Lang("Java.SystemFound")}: Java {major} — {path}";

            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh()
    {
        ManagedRuntimes.Clear();
        foreach (var rt in _jre.ListManagedRuntimes())
            ManagedRuntimes.Add(new JavaRuntimeItem(rt));
        OnPropertyChanged(nameof(HasNoRuntimes));
    }

    // ======================= 下载 =======================

    /// <summary>下载指定主版本的托管 JRE（输入框）</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!int.TryParse(MajorVersionInput.Trim(), out var major) || major is < 8 or > 64)
        {
            StatusText = Lang("Java.InvalidVersion");
            return;
        }
        await DownloadMajorAsync(major);
    }

    /// <summary>下载快捷版本（页面按钮直接传主版本）</summary>
    [RelayCommand]
    private async Task DownloadPresetAsync(int major) => await DownloadMajorAsync(major);

    private async Task DownloadMajorAsync(int major)
    {
        IsBusy = true;
        Progress = 0;
        StatusText = Lang("Java.Downloading");
        try
        {
            await _jre.EnsureJavaAsync(major);
            Refresh();
            Progress = 1;
            StatusText = string.Format(Lang("Java.DownloadDone"), major);
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Java.DownloadFailed")}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ======================= 删除 =======================

    /// <summary>删除选中的托管 JRE（占用中的会拒绝）</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedRuntime is null) return;
        var major = SelectedRuntime.Info.Major;

        var result = _jre.RemoveManagedRuntime(major);
        StatusText = result switch
        {
            RemoveRuntimeResult.Removed => string.Format(Lang("Java.Removed"), major),
            RemoveRuntimeResult.NotFound => string.Format(Lang("Java.NotFound"), major),
            RemoveRuntimeResult.InUse => string.Format(Lang("Java.InUse"), major),
            _ => StatusText,
        };
        Refresh();
    }
}

/// <summary>托管 JRE 列表项（UI 包装：附带格式化文本）</summary>
public sealed class JavaRuntimeItem(ManagedJavaInfo info)
{
    public ManagedJavaInfo Info { get; } = info;

    public int Major => Info.Major;

    public string Version => Info.Version ?? "—";

    public string Path => Info.Path;

    /// <summary>格式化的体积（MB/GB）</summary>
    public string SizeText => Info.SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{Info.SizeBytes / (1024.0 * 1024 * 1024):F1} GB",
        _ => $"{Info.SizeBytes / (1024.0 * 1024):F0} MB",
    };
}
