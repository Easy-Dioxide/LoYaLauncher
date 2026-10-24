using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
// 命名空间 LoyaLauncher.App.Motion 与类名 Motion 同名，直接用会解析为命名空间，须起别名
using LoyaMotion = LoyaLauncher.App.Motion.Motion;

namespace LoyaLauncher.App.Views;

public partial class MainWindow : Window
{
    /// <summary>入场收尾是否已应用（幂等，防止重复触发）</summary>
    private bool _revealed;

    /// <summary>背景柔光漂移句柄（窗口关闭时统一停止）</summary>
    private readonly List<DispatcherTimer> _ambient = new();

    public MainWindow()
    {
        InitializeComponent();
        // 激活兜底：个别平台/时序下 OnOpened 可能不触发或晚于首次渲染，
        // 这里保证内容一定会离开"透明 + 缩小"的初始态，不会呈现永久空窗口
        Activated += (_, _) => ApplyReveal();
    }

    /// <summary>窗口打开：播放入场过渡（淡入 + 微缩放归位）</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyReveal();
        StartAmbient();
    }

    /// <summary>
    /// 激活兜底说明：见构造函数中的 Activated 订阅。
    /// </summary>
    /// <summary>入场收尾：淡入到 1、缩放归位（Transitions 负责补间，幂等）</summary>
    private void ApplyReveal()
    {
        if (_revealed) return;
        _revealed = true;
        RootGrid.Opacity = 1;
        RootGrid.RenderTransform = new Avalonia.Media.ScaleTransform(1, 1);
    }

    /// <summary>
    /// 背景氛围（v0.7.0）：两团柔光异相缓慢漂移 + 透明度呼吸。
    /// 只动 RenderTransform / Opacity（合成层），不触发布局。
    /// </summary>
    private void StartAmbient()
    {
        if (_ambient.Count > 0) return; // 幂等
        // 主光斑：18s 周期，幅度较大，相位 0
        _ambient.Add(LoyaMotion.StartDrift(
            GlowA, amplitudeX: 26, amplitudeY: 18,
            opacityFrom: 0.72, opacityTo: 1.0,
            period: TimeSpan.FromSeconds(18), phase: 0));
        // 副光斑：24s 周期，反向漂移，相位错开约 2/3 周期，亮度低于主光斑
        _ambient.Add(LoyaMotion.StartDrift(
            GlowB, amplitudeX: -22, amplitudeY: 14,
            opacityFrom: 0.38, opacityTo: 0.62,
            period: TimeSpan.FromSeconds(24), phase: Math.PI * 1.35));
    }

    /// <summary>自定义标题栏拖动</summary>
    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>双击标题栏切换最大化</summary>
    private void TitleBar_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ToggleMaximize();
    }

    /// <summary>点击侧栏底部账户胶囊 → 跳转账户管理页</summary>
    private void AccountCapsule_OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.Navigate(ViewModels.LauncherPage.Accounts);
    }

    private void OnMinimizeWindow(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnToggleMaximize(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseWindow(object? sender, RoutedEventArgs e)
    {
        // 停止背景漂移，避免关闭后 timer 仍持有窗口引用
        foreach (var t in _ambient)
            LoyaMotion.Stop(t);
        _ambient.Clear();

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
        else
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var t in _ambient)
            LoyaMotion.Stop(t);
        _ambient.Clear();
        base.OnClosed(e);
    }
}
