using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LoyaLauncher.App.Views;

public partial class MainWindow : Window
{
    /// <summary>入场收尾是否已应用（幂等，防止重复触发）</summary>
    private bool _revealed;

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
}
