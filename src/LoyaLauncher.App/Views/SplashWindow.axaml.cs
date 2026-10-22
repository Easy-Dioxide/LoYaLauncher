using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using LoyaLauncher.Pack;

namespace LoyaLauncher.App.Views;

/// <summary>
/// 启动画面。动画全部通过 code-behind 播放：
/// - Avalonia 12 已移除 XAML 的 <c>Animations</c> 附加属性；
/// - 一次性过渡用 <see cref="Animation.RunAsync(Animatable, CancellationToken)"/>；
/// - 循环呼吸用 DispatcherTimer 手动驱动 Opacity（Avalonia 12 的循环动画
///   不允许走 RunAsync，且窗口只存活数百毫秒，16ms tick 开销可忽略）。
/// 窗口不透明：窗口级透明合成在无 compositor 的平台（部分 Linux / 远程桌面）
/// 会导致整个窗口不渲染。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>兜底超时：主窗口迟迟不出现时（10 秒）自动关闭，避免永远卡在启动画面</summary>
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(10);

    /// <summary>呼吸动画帧间隔（约 60fps）</summary>
    private static readonly TimeSpan PulseTick = TimeSpan.FromMilliseconds(16);

    private readonly DispatcherTimer _fallbackTimer;
    private readonly List<DispatcherTimer> _pulseTimers = new();
    private readonly CancellationTokenSource _animCts = new();
    private bool _closing;

    public SplashWindow()
    {
        InitializeComponent();
        DataContext = new SplashViewModel(UpdateService.CurrentVersion);

        // 兜底定时器：无论主窗口是否到达，10 秒后必须让出舞台
        _fallbackTimer = new DispatcherTimer { Interval = FallbackTimeout };
        _fallbackTimer.Tick += (_, _) =>
        {
            _fallbackTimer.Stop();
            _ = CloseWithFadeAsync();
        };

        // 初始透明，OnOpened 时播放淡入；提前设置可避免首帧全亮闪烁
        Opacity = 0;
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _fallbackTimer.Start();

        // 一次性淡入 0.35s
        _ = FadeAsync(this, 0, 1, TimeSpan.FromSeconds(0.35), _animCts.Token);

        // 循环呼吸：光晕、字标、加载点（各自独立，互不阻塞）
        StartPulse(Glow, 0.45, 0.85, TimeSpan.FromSeconds(2.6));
        StartPulse(Logo, 0.92, 1.0, TimeSpan.FromSeconds(2.6));
        StartPulse(Dots, 0.3, 1.0, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>
    /// 正弦呼吸：Opacity 在 [from, to] 间平滑往复，周期为 <paramref name="period"/>。
    /// 直接写 Opacity 而不是动画对象——避免 Avalonia 12 对循环动画的限制。
    /// </summary>
    private void StartPulse(Control target, double from, double to, TimeSpan period)
    {
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = PulseTick };
        timer.Tick += (_, _) =>
        {
            var phase = (DateTime.UtcNow - start).TotalSeconds % period.TotalSeconds / period.TotalSeconds;
            // sin(0)=0 → sin(π/2)=1 → sin(π)=0：半周期到达峰值，整周期回到起点
            target.Opacity = from + (to - from) * Math.Sin(phase * Math.PI);
        };
        timer.Start();
        _pulseTimers.Add(timer);
    }

    private static async Task FadeAsync(Animatable target, double from, double to, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var fade = new Animation
            {
                Duration = duration,
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(OpacityProperty, from) },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(OpacityProperty, to) },
                    },
                },
            };
            await fade.RunAsync(target, ct);
        }
        catch (OperationCanceledException) { /* 关闭时取消 */ }
    }

    /// <summary>淡出后关闭（主窗口就绪时调用）</summary>
    public async Task CloseWithFadeAsync()
    {
        if (_closing) return;
        _closing = true;
        _fallbackTimer.Stop();
        StopPulses();
        _animCts.Cancel();
        try
        {
            await FadeAsync(this, 1, 0, TimeSpan.FromSeconds(0.25), CancellationToken.None);
        }
        catch
        {
            // 动画失败也要保证关闭
        }
        Close();
    }

    private void StopPulses()
    {
        foreach (var t in _pulseTimers)
            t.Stop();
        _pulseTimers.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPulses();
        _animCts.Cancel();
        _animCts.Dispose();
        base.OnClosed(e);
    }

    private sealed record SplashViewModel(string VersionText);
}
