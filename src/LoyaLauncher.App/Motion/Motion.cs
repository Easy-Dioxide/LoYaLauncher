using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

// Avalonia 12 将 TransformOperations 移入 Avalonia.Media.Transformation，
// 且与命名空间段同名易冲突，统一用别名引用
using TransformOps = Avalonia.Media.Transformation.TransformOperations;

namespace LoyaLauncher.App.Motion;

/// <summary>
/// LoyaLauncher 动效基础设施（v0.7.0 视觉升级）。
/// 提供四类能力：
/// <list type="bullet">
///   <item><see cref="RevealChildrenProperty"/>：容器子元素交错入场（淡入 + 上浮归位）</item>
///   <item><see cref="RippleHostProperty"/>：按压涟漪（从命中点扩散的圆形高亮）</item>
///   <item><see cref="SheenProperty"/>：hover 扫光（一道斜向高光掠过）</item>
///   <item><see cref="StartDrift"/>/<see cref="StartPulse"/>/<see cref="StartSpin"/>：
///         循环氛围动画（背景漂移 / 呼吸 / 光环旋转）</item>
/// </list>
/// 设计约束（Avalonia 12）：
/// - XAML 的 Animations 附加属性已移除 → 一次性动画一律用 <c>Animation.RunAsync</c>；
/// - 循环动画不允许 RunAsync → 用 DispatcherTimer 手动驱动（见 SplashWindow 既有模式）；
/// - 只动画 Opacity / RenderTransform（合成层属性），绝不触碰布局属性，保证 60fps；
/// - 所有播放路径均有容错：动画失败时目标控件直接落到可见终态，绝不留下隐形内容；
/// - Avalonia 12 的类型布局注意：TransformOperations 在 Avalonia.Media.Transformation；
///   Shape 在 Avalonia.Controls.Shapes；hover 事件为 PointerEntered/PointerExited。
/// </summary>
public static class Motion
{
    // ==================== 全局开关 ====================

    /// <summary>
    /// 动效总开关（清单 §9 P1「减少动效」）：false 时所有动画路径直接跳过，
    /// 内容立即呈现可见终态。由 App 启动时按 <c>AppConfig.ReduceMotion</c> 初始化，
    /// 设置页切换时即时生效。
    /// </summary>
    public static bool Enabled { get; set; } = true;

    // ==================== 时序参数（高雅丝滑的关键：缓动与节奏） ====================

    /// <summary>交错入场：相邻两项的启动间隔</summary>
    public static readonly TimeSpan StaggerInterval = TimeSpan.FromMilliseconds(45);

    /// <summary>交错入场：单项时长</summary>
    public static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(320);

    /// <summary>交错入场：上浮距离（px）</summary>
    public const double RevealOffsetY = 14;

    /// <summary>涟漪扩散时长</summary>
    private static readonly TimeSpan RippleDuration = TimeSpan.FromMilliseconds(520);

    /// <summary>扫光掠过时长</summary>
    private static readonly TimeSpan SheenDuration = TimeSpan.FromMilliseconds(560);

    /// <summary>循环动画帧间隔（约 60fps）</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);

    // ==================== 附加属性：交错入场 ====================

    /// <summary>附加属性的宿主类型（AvaloniaProperty.Register 的 owner 须为 AvaloniaObject 子类）</summary>
    private sealed class MotionHost : AvaloniaObject;

    public static readonly StyledProperty<bool> RevealChildrenProperty =
        AvaloniaProperty.Register<MotionHost, bool>("RevealChildren");

    public static bool GetRevealChildren(Panel host) => host.GetValue(RevealChildrenProperty);
    public static void SetRevealChildren(Panel host, bool value) => host.SetValue(RevealChildrenProperty, value);

    /// <summary>容器是否已播放过入场（幂等，防止重复触发）</summary>
    private static readonly StyledProperty<bool> RevealedProperty =
        AvaloniaProperty.Register<MotionHost, bool>("Revealed");

    /// <summary>容器当前入场动画的取消源（快速切页时取消未播完的队列）</summary>
    private static readonly StyledProperty<CancellationTokenSource?> RevealCtsProperty =
        AvaloniaProperty.Register<MotionHost, CancellationTokenSource?>("RevealCts");

    // ==================== 附加属性：涟漪宿主 ====================

    public static readonly StyledProperty<bool> RippleHostProperty =
        AvaloniaProperty.Register<MotionHost, bool>("RippleHost");

    public static bool GetRippleHost(Panel host) => host.GetValue(RippleHostProperty);
    public static void SetRippleHost(Panel host, bool value) => host.SetValue(RippleHostProperty, value);

    // ==================== 附加属性：扫光 ====================

    public static readonly StyledProperty<bool> SheenProperty =
        AvaloniaProperty.Register<MotionHost, bool>("Sheen");

    public static bool GetSheen(Border host) => host.GetValue(SheenProperty);
    public static void SetSheen(Border host, bool value) => host.SetValue(SheenProperty, value);

    /// <summary>扫光层当前动画取消源</summary>
    private static readonly StyledProperty<CancellationTokenSource?> SheenCtsProperty =
        AvaloniaProperty.Register<MotionHost, CancellationTokenSource?>("SheenCts");

    // ==================== 静态构造：属性变更接线 ====================

    static Motion()
    {
        RevealChildrenProperty.Changed.AddClassHandler<Panel>(OnRevealChildrenChanged);
        RippleHostProperty.Changed.AddClassHandler<Panel>(OnRippleHostChanged);
        SheenProperty.Changed.AddClassHandler<Border>(OnSheenChanged);
    }

    // ==================== 工具：TransformOperations 构建 ====================

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>平移变换</summary>
    private static TransformOps Translate(double x, double y) =>
        TransformOps.Parse($"translate({N(x)}px, {N(y)}px)");

    /// <summary>旋转变换（度）</summary>
    private static TransformOps Rotate(double degrees) =>
        TransformOps.Parse($"rotate({N(degrees)}deg)");

    // ==================== 交错入场 ====================

    /// <summary>
    /// 交错入场：属性挂载后，等容器附加到视觉树（子元素已就位）再编排。
    /// </summary>
    private static void OnRevealChildrenChanged(Panel host, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        if (host.GetValue(RevealedProperty)) return;

        if (host.IsAttachedToVisualTree())
            BeginReveal(host);
        else
        {
            // 只订一次：附加即播，播完置幂等标记
            void Once(object? sender, VisualTreeAttachmentEventArgs args)
            {
                host.AttachedToVisualTree -= Once;
                BeginReveal(host);
            }
            host.AttachedToVisualTree += Once;
        }
    }

    /// <summary>
    /// 编排交错入场：先把所有可见子元素压到「未揭示」初始态（透明 + 下移），
    /// 再按索引错峰播放归位。初始态同步设置（无 transition），不会出现先闪一帧再消失。
    /// </summary>
    private static void BeginReveal(Panel host)
    {
        if (host.GetValue(RevealedProperty)) return;
        host.SetValue(RevealedProperty, true);

        // 减少动效：不播放入场，内容保持自然可见态
        if (!Enabled) return;

        var targets = host.Children
            .OfType<Control>()
            .Where(c => c.IsVisible && c.Opacity > 0)
            .ToList();
        if (targets.Count == 0) return;

        // 取消上一次未播完的队列（容器复用时）
        host.GetValue(RevealCtsProperty)?.Cancel();
        var cts = new CancellationTokenSource();
        host.SetValue(RevealCtsProperty, cts);
        var ct = cts.Token;

        // 初始态：立即生效，不经过渡
        foreach (var t in targets)
        {
            t.Opacity = 0;
            t.RenderTransform = Translate(0, RevealOffsetY);
        }

        for (var i = 0; i < targets.Count; i++)
        {
            var control = targets[i];
            var delay = StaggerInterval * i;
            _ = RevealOneAsync(control, delay, ct);
        }
    }

    /// <summary>单项归位：delay 后播放 opacity 0→1 + translateY →0。</summary>
    /// <remarks>
    /// 线程纪律：本方法在 UI 线程被调用；await 不得带 ConfigureAwait(false)，
    /// 否则续延落到线程池后再访问控件即抛「线程不能访问对象」。
    /// </remarks>
    private static async Task RevealOneAsync(Control control, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);   // 捕获 SynchronizationContext，续延回 UI 线程

            var anim = new Animation
            {
                Duration = RevealDuration,
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 0d),
                            new Setter(Visual.RenderTransformProperty, Translate(0, RevealOffsetY)),
                        },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 1d),
                            new Setter(Visual.RenderTransformProperty, TransformOps.Identity),
                        },
                    },
                },
            };

            await anim.RunAsync(control, ct);

            // 双保险：FillMode 不可依赖，显式落到终态
            control.Opacity = 1;
            control.RenderTransform = TransformOps.Identity;
        }
        catch (OperationCanceledException)
        {
            // 切页取消：内容已处于自然状态，无需处理
        }
        catch
        {
            // 容错兜底：绝不留隐形内容；经 Post 保证在 UI 线程写
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    control.Opacity = 1;
                    control.RenderTransform = TransformOps.Identity;
                });
            }
            catch
            {
                // 控件已卸载：无可补救，忽略
            }
        }
    }

    // ==================== 涟漪 ====================

    /// <summary>涟漪：宿主 Panel 上挂载，按压时从命中点注入扩散圆。</summary>
    private static void OnRippleHostChanged(Panel host, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        host.PointerPressed -= OnRipplePressed;
        host.PointerPressed += OnRipplePressed;
    }

    private static void OnRipplePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Panel host) return;
        if (!Enabled) return; // 减少动效：无涟漪
        var point = e.GetCurrentPoint(host);
        if (!point.Properties.IsLeftButtonPressed) return;

        try
        {
            // 直径取对角线 1.15 倍：保证从任意命中点都能扩散覆盖整个宿主
            var size = Math.Sqrt(
                host.Bounds.Width * host.Bounds.Width + host.Bounds.Height * host.Bounds.Height) * 1.15;
            if (size <= 0) return;

            var ripple = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(point.Position.X - size / 2, point.Position.Y - size / 2, 0, 0),
                IsHitTestVisible = false,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = TransformOps.Parse("scale(0.25)"),
                Opacity = 1,
            };
            host.Children.Add(ripple);

            var anim = new Animation
            {
                Duration = RippleDuration,
                Easing = new SineEaseOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 1d),
                            new Setter(Visual.RenderTransformProperty, TransformOps.Parse("scale(0.25)")),
                        },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 0d),
                            new Setter(Visual.RenderTransformProperty, TransformOps.Parse("scale(1)")),
                        },
                    },
                },
            };
            _ = anim.RunAsync(ripple).ContinueWith(_ =>
            {
                // 回到 UI 线程移除，避免持有已卸载视觉树上的节点
                Dispatcher.UIThread.Post(() => host.Children.Remove(ripple));
            }, TaskScheduler.Default);
        }
        catch
        {
            // 涟漪是纯锦上添花：任何失败都不影响宿主正常交互
        }
    }

    // ==================== 扫光 ====================

    /// <summary>扫光：host 进入时一道斜向高光从左掠到右，离开即隐。</summary>
    private static void OnSheenChanged(Border host, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        host.PointerEntered -= OnSheenEnter;
        host.PointerEntered += OnSheenEnter;
        host.PointerExited -= OnSheenLeave;
        host.PointerExited += OnSheenLeave;
    }

    private static void OnSheenEnter(object? sender, PointerEventArgs e)
    {
        if (sender is not Border host) return;
        if (!Enabled) return; // 减少动效：无扫光
        host.GetValue(SheenCtsProperty)?.Cancel();
        var cts = new CancellationTokenSource();
        host.SetValue(SheenCtsProperty, cts);

        // 从宿主左侧外起扫；宽度未知时退化为固定偏移
        var offset = host.Bounds.Width > 0 ? host.Bounds.Width : 120;
        host.RenderTransform = Translate(-offset, 0);
        host.Opacity = 1;

        var anim = new Animation
        {
            Duration = SheenDuration,
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.RenderTransformProperty, Translate(-offset, 0)) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.RenderTransformProperty, Translate(offset, 0)) },
                },
            },
        };
        _ = anim.RunAsync(host, cts.Token);
    }

    private static void OnSheenLeave(object? sender, PointerEventArgs e)
    {
        if (sender is not Border host) return;
        host.GetValue(SheenCtsProperty)?.Cancel();
        host.Opacity = 0;
        var offset = host.Bounds.Width > 0 ? host.Bounds.Width : 120;
        host.RenderTransform = Translate(-offset, 0);
    }

    // ==================== 循环氛围动画 ====================

    /// <summary>
    /// 缓慢漂移：水平/垂直正弦平移叠加透明度呼吸。
    /// 返回的 timer 句柄由调用方保存；窗口关闭时调用 <see cref="Stop"/>。
    /// </summary>
    public static DispatcherTimer StartDrift(
        Control target,
        double amplitudeX,
        double amplitudeY,
        double opacityFrom,
        double opacityTo,
        TimeSpan period,
        double phase = 0)
    {
        var start = DateTime.UtcNow;
        var mid = (opacityFrom + opacityTo) / 2;
        var amp = (opacityTo - opacityFrom) / 2;
        var timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - start).TotalSeconds;
            var w = 2 * Math.PI * t / period.TotalSeconds;
            // 位移与呼吸异相：运动轨迹呈自然「∞」字形而非呆板直线
            var x = amplitudeX * Math.Sin(w + phase);
            var y = amplitudeY * Math.Sin(w * 0.8 + phase + Math.PI / 2);
            var o = mid + amp * Math.Sin(w * 0.6 + phase + Math.PI);
            target.RenderTransform = Translate(x, y);
            target.Opacity = o;
        };
        timer.Start();
        return timer;
    }

    /// <summary>正弦呼吸：Opacity 在 [from, to] 间往复（周期 <paramref name="period"/>）。</summary>
    public static DispatcherTimer StartPulse(Control target, double from, double to, TimeSpan period)
    {
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) =>
        {
            var phase = (DateTime.UtcNow - start).TotalSeconds % period.TotalSeconds / period.TotalSeconds;
            target.Opacity = from + (to - from) * Math.Sin(phase * Math.PI);
        };
        timer.Start();
        return timer;
    }

    /// <summary>匀速旋转：驱动 RotateTransform.Angle（光环 / 加载圈）。</summary>
    public static DispatcherTimer StartSpin(Control target, TimeSpan period, bool clockwise = true)
    {
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - start).TotalSeconds % period.TotalSeconds / period.TotalSeconds;
            var angle = (clockwise ? 360.0 : -360.0) * t;
            target.RenderTransform = Rotate(angle);
        };
        timer.Start();
        return timer;
    }

    /// <summary>停止循环动画句柄</summary>
    public static void Stop(DispatcherTimer? timer)
    {
        timer?.Stop();
    }
}
