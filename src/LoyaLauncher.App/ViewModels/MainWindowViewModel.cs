using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.App.Services;
using LoyaLauncher.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LoyaLauncher.App.ViewModels;

/// <summary>启动器页面枚举</summary>
public enum LauncherPage
{
    Home,
    Versions,
    Mods,
    Modpacks,
    Accounts,
    Settings,
    Downloads,
}

/// <summary>侧边导航项</summary>
public sealed partial class NavItem : ObservableObject
{
    public required string LabelKey { get; init; }
    public required StreamGeometry IconGeometry { get; init; }
    public required LauncherPage Page { get; init; }

    /// <summary>是否当前选中（由 MainWindowViewModel 同步）</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>角标数量（下载中心显示进行中任务数）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private int _badge;

    public bool HasBadge => Badge > 0;

    /// <summary>本地化后的标签</summary>
    public string Label => LangService.T(LabelKey, LabelKey);
}

/// <summary>
/// 主窗口 ViewModel：侧边导航 + 页面切换。
/// 页面实例由 DI 容器提供（单例，切换不重建，保留滚动位置与状态）。
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _sp;
    private readonly Dictionary<LauncherPage, ViewModelBase> _pages = new();
    private readonly Dictionary<LauncherPage, NavItem> _navMap = new();
    private readonly Auth.AccountManager _accounts;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private NavItem _selectedNav = null!;

    /// <summary>当前账户名（侧栏胶囊展示）</summary>
    [ObservableProperty]
    private string _currentAccountName = "离线账户";

    /// <summary>全局状态栏文本</summary>
    [ObservableProperty]
    private string _statusText = "就绪";

    public ObservableCollection<NavItem> NavItems { get; } = [];

    public MainWindowViewModel(IServiceProvider sp)
    {
        _sp = sp;
        _accounts = sp.GetRequiredService<Auth.AccountManager>();
        CurrentAccountName = _accounts.CurrentAccount?.PlayerName ?? "离线账户";
        _accounts.CurrentAccountChanged += () =>
            CurrentAccountName = _accounts.CurrentAccount?.PlayerName ?? "离线账户";

        // 图标取自 Glass.axaml 中定义的矢量资源
        var icons = new (string Key, StreamGeometry Geometry)[]
        {
            ("IconHome", (StreamGeometry)Application.Current!.Resources["IconHome"]!),
            ("IconCube", (StreamGeometry)Application.Current!.Resources["IconCube"]!),
            ("IconGrid", (StreamGeometry)Application.Current!.Resources["IconGrid"]!),
            ("IconPackage", (StreamGeometry)Application.Current!.Resources["IconPackage"]!),
            ("IconUser", (StreamGeometry)Application.Current!.Resources["IconUser"]!),
            ("IconGear", (StreamGeometry)Application.Current!.Resources["IconGear"]!),
            ("IconDownload", (StreamGeometry)Application.Current!.Resources["IconDownload"]!),
        };

        foreach (var (page, labelKey, iconKey) in new[]
        {
            (LauncherPage.Home, "Nav.Home", "IconHome"),
            (LauncherPage.Versions, "Nav.Versions", "IconCube"),
            (LauncherPage.Mods, "Nav.Mods", "IconGrid"),
            (LauncherPage.Modpacks, "Nav.Modpacks", "IconPackage"),
            (LauncherPage.Accounts, "Nav.Accounts", "IconUser"),
            (LauncherPage.Downloads, "Nav.Downloads", "IconDownload"),
            (LauncherPage.Settings, "Nav.Settings", "IconGear"),
        })
        {
            var geometry = icons.First(i => i.Key == iconKey).Geometry;
            var item = new NavItem { LabelKey = labelKey, IconGeometry = geometry, Page = page };
            NavItems.Add(item);
            _navMap[page] = item;
        }

        // 默认打开主页
        Navigate(LauncherPage.Home);
    }

    /// <summary>页面工厂：延迟创建并缓存页面实例</summary>
    private ViewModelBase GetPage(LauncherPage page)
    {
        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = page switch
            {
                LauncherPage.Home => _sp.GetRequiredService<HomeViewModel>(),
                LauncherPage.Versions => _sp.GetRequiredService<VersionsViewModel>(),
                LauncherPage.Mods => _sp.GetRequiredService<ModsViewModel>(),
                LauncherPage.Modpacks => _sp.GetRequiredService<ModpacksViewModel>(),
                LauncherPage.Accounts => _sp.GetRequiredService<AccountsViewModel>(),
                LauncherPage.Settings => _sp.GetRequiredService<SettingsViewModel>(),
                LauncherPage.Downloads => _sp.GetRequiredService<DownloadCenterViewModel>(),
                _ => throw new ArgumentOutOfRangeException(nameof(page)),
            };
            _pages[page] = vm;
        }
        return vm;
    }

    /// <summary>切换页面（触发过渡动画）</summary>
    public void Navigate(LauncherPage page)
    {
        var vm = GetPage(page);
        if (CurrentPage == vm) return;

        foreach (var nav in NavItems)
            nav.IsSelected = false;

        CurrentPage?.OnNavigatedFrom();
        CurrentPage = vm;
        SelectedNav = _navMap[page];
        SelectedNav.IsSelected = true;
        vm.OnNavigatedTo();
    }

    /// <summary>导航命令（供侧边栏绑定）</summary>
    [RelayCommand]
    private void NavigateTo(LauncherPage page) => Navigate(page);

    /// <summary>设置下载中心角标</summary>
    public void SetDownloadBadge(int count)
    {
        if (_navMap.TryGetValue(LauncherPage.Downloads, out var nav))
            nav.Badge = count;
    }
}
