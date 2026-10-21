using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Auth;
using LoyaLauncher.Core;
using LoyaLauncher.Java;

namespace LoyaLauncher.App.ViewModels;

/// <summary>启动流程状态</summary>
public enum LaunchState
{
    Idle,
    Preparing,
    Launching,
    Running,
}

/// <summary>
/// 主页 ViewModel：实例总览、一键启动、实时日志。
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly JavaEditionService _java;
    private readonly AccountManager _accounts;
    private readonly Core.EventBus _bus;
    private readonly Core.ConfigStore _config;
    private readonly LaunchPreflight _preflight;
    private readonly IDisposable _logSubscription;
    private readonly IDisposable _exitSubscription;

    public HomeViewModel(
        JavaEditionService java,
        AccountManager accounts,
        Core.EventBus bus,
        Core.ConfigStore config,
        LaunchPreflight preflight)
    {
        _java = java;
        _accounts = accounts;
        _bus = bus;
        _config = config;
        _preflight = preflight;

        Logs.CollectionChanged += OnLogsCollectionChanged;

        _logSubscription = bus.Subscribe<GameLogEvent>(e =>
        {
            if (e.VersionId != SelectedInstance?.VersionId && SelectedInstance is not null)
                return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Logs.Add(e.Line);
                while (Logs.Count > 300)
                    Logs.RemoveAt(0);
            });
        });

        _exitSubscription = bus.Subscribe<GameExitedEvent>(e =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (e.ExitCode == 0)
                {
                    State = LaunchState.Idle;
                    StatusText = Lang("Home.Status.Ready");
                }
                else
                {
                    State = LaunchState.Idle;
                    StatusText = string.Format(Lang("Home.Status.Crashed"), e.ExitCode);
                }
                IsLaunching = false;
            });
        });
    }

    // ======================= 状态 =======================

    [ObservableProperty]
    private LaunchState _state = LaunchState.Idle;

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private double _launchProgress;

    [ObservableProperty]
    private string _currentVersionText = "未选择版本";

    [ObservableProperty]
    private string _currentAccountText = "离线账户";

    [ObservableProperty]
    private bool _hasInstances;

    [ObservableProperty]
    private bool _isEmpty;

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    private GameInstance? _selectedInstance;

    /// <summary>当前选中实例</summary>
    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetProperty(ref _selectedInstance, value) && value is not null)
            {
                _config.Config.CurrentInstanceId = value.Id;
                _config.Save();
                CurrentVersionText = value.VersionId;
                OnPropertyChanged(nameof(SelectedInstanceName));
            }
        }
    }

    public string SelectedInstanceName => SelectedInstance?.Name ?? "无实例";

    private static string Lang(string key) => Services.LangService.T(key, key);

    /// <summary>带格式化参数的本地化（{0} {1} … 占位符）</summary>
    private static string LangF(string key, params object?[] args)
    {
        var text = Services.LangService.T(key, key);
        return args.Length > 0 ? string.Format(text, args) : text;
    }

    // ======================= 生命周期 =======================

    public override void OnNavigatedTo()
    {
        RefreshInstances();
        var account = _accounts.CurrentAccount;
        CurrentAccountText = account is null ? "离线账户" : $"{account.PlayerName}";
    }

    /// <summary>刷新实例列表</summary>
    public void RefreshInstances()
    {
        Instances.Clear();
        foreach (var inst in JavaEditionService.ListInstances())
            Instances.Add(inst);

        HasInstances = Instances.Count > 0;
        IsEmpty = Instances.Count == 0;

        // 恢复上次选择的实例
        var savedId = _config.Config.CurrentInstanceId;
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == savedId) ?? Instances.FirstOrDefault();
    }

    // ======================= 命令 =======================

    /// <summary>一键启动</summary>
    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (SelectedInstance is null || IsLaunching) return;

        var account = _accounts.CurrentAccount;
        if (account is null)
        {
            StatusText = Lang("Home.Error.NoAccount");
            return;
        }

        // 启动前环境自检：磁盘空间 / 内存配置 / Java 匹配 / 版本安装状态。
        // Error 级问题阻断启动；Warning/Info 写入日志面板供用户参考。
        var report = await _preflight.CheckAsync(SelectedInstance, _config.Config.JavaMaxMemoryMb);
        foreach (var issue in report.Issues)
            Logs.Add(LangF(issue.Code, issue.Args));
        if (!report.CanLaunch)
        {
            var blocked = report.Issues.First(i => i.BlocksLaunch);
            StatusText = LangF(blocked.Code, blocked.Args);
            return;
        }

        IsLaunching = true;
        State = LaunchState.Preparing;
        LaunchProgress = 0;
        StatusText = Lang("Home.Status.Checking");
        Logs.Clear();

        try
        {
            var progress = new Progress<JavaInstallProgress>(p =>
            {
                LaunchProgress = p.Percent;
                StatusText = $"{p.Stage} {p.Detail}".Trim();
            });

            State = LaunchState.Launching;
            StatusText = Lang("Home.Status.Launching");

            await _java.LaunchAsync(
                SelectedInstance,
                account,
                _config.Config.JavaMaxMemoryMb,
                _config.Config.JvmArguments);

            State = LaunchState.Running;
            StatusText = Lang("Home.Status.Running");
            LaunchProgress = 1;
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Home.Error.LaunchFailed")}: {ex.Message}";
            State = LaunchState.Idle;
            IsLaunching = false;
        }
    }

    /// <summary>新建实例（简易：基于已安装版本）</summary>
    [RelayCommand]
    private void CreateInstance()
    {
        // 完整向导在版本管理页；主页提供快速创建入口
        NavigateToVersions?.Invoke();
    }

    /// <summary>请求跳转到版本页（由主窗口挂接）</summary>
    public Action? NavigateToVersions { get; set; }

    /// <summary>选择实例</summary>
    [RelayCommand]
    private void SelectInstance(GameInstance instance)
    {
        SelectedInstance = instance;
    }

    // ======================= 日志面板 =======================

    [ObservableProperty]
    private bool _logPanelExpanded;

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasLogs));
    }

    public bool HasLogs => Logs.Count > 0;
}
