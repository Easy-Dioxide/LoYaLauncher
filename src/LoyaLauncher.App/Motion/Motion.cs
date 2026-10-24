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
using Avalonia.Interactivity;
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
/// 设计约束（Avalonia 12，均有运行时实测背书）：
/// - <b>RenderTransform 没有注册动画器</b>：<c>Animation.RunAsync</c> 对
///   <see cref="Visual.RenderTransformProperty"/> 直接抛
///   「No animator registered for the property RenderTransform」；
///   且动画目标必须是 <see cref="Visual"/>（不能是 Transform 实例，会 InvalidCast）。
///   → 一次性 transform 动画一律走 <see cref="TransformOperationsTransition"/> 声明式过渡
///   （实测插值平滑，CubicEaseOut 曲线标准）；
/// - <b>挂载当口播放过渡会静默失效</b>：在 <see cref="Visual.AttachedToVisualTree"/>
///   处理函数里当场给控件挂 <see cref="Control.Transitions"/> 并改属性，过渡<b>不报错但也不播放</b>
///   （属性直接落终态，实测 op 首采样即 1.00）；推迟到布局后（DispatcherPriority.Loaded）
///   再设值即恢复平滑插值。→ 一切入场播放必须经 <see cref="ScheduleReveal"/> 调度，
///   绝不在 attach / Opened 事件里同步设终态；
/// - 循环动画同样不允许 RunAsync → 用 DispatcherTimer 手动驱动（timer 直接写属性，
///   天然逐帧平滑）；
/// - 只动画 Opacity / RenderTransform（合成层属性），绝不触碰布局属性，保证 60fps；
/// - 所有播放路径均有容错：失败时目标控件直接落到可见终态，绝不留下隐形内容；
/// - Avalonia 12 的类型布局注意：TransformOperations 在 Avalonia.Media.Transformation；
///   Shape 在 Avalonia.Controls.Shapes；hover 事件为 PointerEntered/PointerExited。
/// </summary>
public static class Motion
{
    // ==================== 时序参数（高雅丝滑的关键：缓动与节奏） ====================

    /// <summary>交错入场：相邻一项的启动间隔</summary>
    public static readonly TimeSpan StaggerInterval = TimeSpan.FromMilliseconds(45);

    /// <summary>交错入场：单项时长</summary>
    public static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(320);

    /// <summary>交错入场：上浮距离（px）</summary>
    public const double RevealOffsetY = 14;

    /// <summary>涟漪扩散时长</summary>
    private static readonly TimeSpan RippleDuration = TimeSpan.FromMilliseconds(520);

    /// <summary>扫光掠过时长</summary>
    private static readonly TimeSpan SheenDuration = TimeSpan.FromMilliseconds(560);

    /// <summary>扫光节流：宿主内 PointerMoved 的最小重播间隔</summary>
    private static readonly TimeSpan SheenThrottle = TimeSpan.FromMilliseconds(800);

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

    /// <summary>宿主上次播放扫光的时间（节流，避免 PointerMoved 每帧重播）</summary>
    private static readonly StyledProperty<DateTime> LastSheenAtProperty =
        AvaloniaProperty.Register<MotionHost, DateTime>("LastSheenAt");

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

    /// <summary>
    /// 瞬移设置属性值：临时摘掉控件自身 Transitions，赋值后还原。
    /// 用于动画起点（初始态）——必须无过渡生效，否则会先「滑向」起点再归位。
    /// </summary>
    private static void SetInstant(Control control, Action apply)
    {
        var saved = control.Transitions;
        if (saved is { Count: > 0 })
            control.Transitions = null;
        apply();
        if (saved is { Count: > 0 })
            control.Transitions = saved;
    }

    /// <summary>给控件挂过渡（替换同属性的旧条目，避免两条过渡抢同一属性）。</summary>
    private static void EnsureTransitions(Control control, params TransitionBase[] transitions)
    {
        var list = control.Transitions;
        if (list is null)
        {
            var fresh = new Transitions();
            fresh.AddRange(transitions);
            control.Transitions = fresh;
            return;
        }
        foreach (var t in transitions)
            for (var i = list.Count - 1; i >= 0; i--)
                if (list[i] is TransitionBase existing && existing.Property == t.Property)
                    list.RemoveAt(i);
        list.AddRange(transitions);
    }

    // ==================== 交错入场 ====================

    /// <summary>
    /// 交错入场：属性挂载后，等容器附加到视觉树（子元素已就位）再编排。
    /// </summary>
    private static void OnRevealChildrenChanged(Panel host, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        if (host.GetValue(RevealedProperty)) return;

        if (host.IsAttachedToVisualTree())
            ScheduleReveal(host);
        else
        {
            // 只订一次：附加即排播，播完置幂等标记
            void Once(object? sender, VisualTreeAttachmentEventArgs args)
            {
                host.AttachedToVisualTree -= Once;
                ScheduleReveal(host);
            }
            host.AttachedToVisualTree += Once;
        }
    }

    /// <summary>
    /// 把入场播放推迟到布局帧之后（<see cref="DispatcherPriority.Loaded"/>）。
    /// 实测（animtest r2 vs r3 对照）：在 AttachedToVisualTree 当口挂 Transitions
    /// 并设终态，过渡静默不播放；推迟一帧后 CubicEaseOut 插值恢复正常。
    /// </summary>
    private static void ScheduleReveal(Panel host)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 排队期间被卸载（快速切页）：无可播放，直接放弃
            if (!host.IsAttachedToVisualTree()) return;
            BeginReveal(host);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 编排交错入场：先把所有可见子元素压到「未揭示」初始态（透明 + 下移，
    /// 瞬移生效不触发过渡），再按索引错峰触发归位过渡。
    /// </summary>
    private static void BeginReveal(Panel host)
    {
        if (host.GetValue(RevealedProperty)) return;
        host.SetValue(RevealedProperty, true);

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

        // 初始态：瞬移生效，不经过渡（否则会先「滑向」起点再归位，闪一帧）
        foreach (var t in targets)
            SetInstant(t, () =>
            {
                t.Opacity = 0;
                t.RenderTransform = Translate(0, RevealOffsetY);
            });

        for (var i = 0; i < targets.Count; i++)
        {
            var control = targets[i];
            var delay = StaggerInterval * i;
            _ = RevealOneAsync(control, delay, ct);
        }
    }

    /// <summary>单项归位：delay 后触发 opacity 0→1 + translateY →0 的过渡。</summary>
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

            // 过渡播放：opacity 与 translateY 同步缓动归位（CubicEaseOut）
            EnsureTransitions(control,
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = RevealDuration,
                    Easing = new CubicEaseOut(),
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = RevealDuration,
                    Easing = new CubicEaseOut(),
                });
            control.Opacity = 1;
            control.RenderTransform = TransformOps.Identity;
        }
        catch (OperationCanceledException)
        {
            // 切页取消：立即落到终态，绝不留隐形内容
            control.Opacity = 1;
            control.RenderTransform = TransformOps.Identity;
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
    /// <remarks>
    /// 同一个宿主 Grid 还负责驱动 hover 扫光：指针在宿主内移动时 PointerMoved
    /// 冒泡到本 Grid，由此驱动同级的 sheen 层。
    /// （Avalonia 12 的 PointerEntered/Exited 是 Direct 路由，只达命中测试目标，
    /// 冒泡不到宿主 Grid；PointerPressed/PointerMoved 是 Bubble 路由，宿主必然收到。）
    /// <b>命中测试前提</b>：宿主 Grid 必须设 Background="Transparent"——null Background
    /// 的 Panel 不参与命中测试，指针事件的命中目标会是外层模板 Border（有 Background），
    /// 事件从 Border 向上冒泡，宿主 Grid 作为其<b>子节点</b>永远收不到。
    /// </remarks>
    private static void OnRippleHostChanged(Panel host, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        // Button.OnPointerPressed 在左键按下时置 e.Handled=true，且类处理器先于
        // 目标（宿主 Grid）执行（隧道阶段），普通订阅收不到本事件；
        // handledEventsToo:true 保证宿主仍能收到按压并叠加涟漪。
        host.RemoveHandler(InputElement.PointerPressedEvent, OnRipplePressed);
        host.AddHandler(
            InputElement.PointerPressedEvent,
            OnRipplePressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        host.PointerMoved -= OnHostMoved;
        host.PointerMoved += OnHostMoved;
    }

    /// <summary>宿主内移动：驱动其下挂 Sheen 标记的子层播放扫光（节流 800ms）。</summary>
    private static void OnHostMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Panel host) return;
        var now = DateTime.UtcNow;
        if (now - host.GetValue(LastSheenAtProperty) < SheenThrottle) return;
        host.SetValue(LastSheenAtProperty, now);
        foreach (var child in host.Children)
        {
            if (child is Border b && b.GetValue(SheenProperty))
                PlaySheen(b);
        }
    }

    private static void OnRipplePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Panel host) return;
        var point = e.GetCurrentPoint(host);
        if (!point.Properties.IsLeftButtonPressed) return;

        try
        {
            // 直径取对角线 1.15 倍：保证从任意命中点都能扩散覆盖整个宿主
            var size = Math.Sqrt(
                host.Bounds.Width * host.Bounds.Width + host.Bounds.Height * host.Bounds.Height) * 1.15;
            if (size <= 0) return;

            // 初始态（scale 0.25 + 不透明）在入树前设好：此时无过渡生效
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

            // 入树后挂过渡并改值 → scale 0.25→1 + opacity 1→0 平滑扩散淡出。
            // 时机纪律（同 BeginReveal，实测）：新建节点入树当口设终态，过渡静默
            // 不播放（直接跳终态 = 不可见）；必须 Post 到布局帧之后才恢复插值。
            Dispatcher.UIThread.Post(() =>
            {
                EnsureTransitions(ripple,
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = RippleDuration,
                        Easing = new SineEaseOut(),
                    },
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = RippleDuration,
                        Easing = new SineEaseOut(),
                    });
                ripple.RenderTransform = TransformOps.Parse("scale(1)");
                ripple.Opacity = 0;
            }, DispatcherPriority.Loaded);

            // 播完移除，避免在宿主上堆积不可命中的视觉节点
            _ = RemoveLaterAsync(host, ripple, RippleDuration);
        }
        catch
        {
            // 涟漪是纯锦上添花：任何失败都不影响宿主正常交互
        }
    }

    /// <summary>延迟移除涟漪节点（UI 线程续延，不得 ConfigureAwait(false)）。</summary>
    private static async Task RemoveLaterAsync(Panel host, Ellipse ripple, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            host.Children.Remove(ripple);
        }
        catch
        {
            // 宿主已卸载：无需移除
        }
    }

    // ==================== 扫光 ====================

    /// <summary>
    /// 扫光：宿主 Grid 进入时驱动挂此标记的子层（斜向渐变 Border）从左掠到右。
    /// 本属性只作标记（子层不可命中，无法自收指针事件）；事件由宿主 Grid 的
    /// <see cref="OnHostMoved"/> 转发。
    /// </summary>
    private static void OnSheenChanged(Border host, AvaloniaPropertyChangedEventArgs e)
    {
        // 仅作标记，无需接线
    }

    /// <summary>播放扫光：从宿主左侧外起扫，掠过整宽。</summary>
    private static void PlaySheen(Border host)
    {
        host.GetValue(SheenCtsProperty)?.Cancel();
        var cts = new CancellationTokenSource();
        host.SetValue(SheenCtsProperty, cts);

        // 宽度未知时退化为固定偏移
        var offset = host.Bounds.Width > 0 ? host.Bounds.Width : 120;

        // 1. 摘掉过渡，瞬移到起点（宿主左侧外）并显示
        host.Transitions = null;
        host.RenderTransform = Translate(-offset, 0);
        host.Opacity = 1;

        // 2. 挂上过渡，设终点 → 平滑扫到右侧外
        host.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = SheenDuration,
                Easing = new CubicEaseOut(),
            },
        };
        host.RenderTransform = Translate(offset, 0);

        // 3. 播完自动收起：瞬移回起点（无过渡，避免反向回扫）
        _ = HideSheenLaterAsync(host, cts.Token);
    }

    /// <summary>扫光播完延时收起（UI 线程续延；取消则让位给下一次播放）。</summary>
    private static async Task HideSheenLaterAsync(Border host, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SheenDuration, ct);
            HideSheen(host);
        }
        catch (OperationCanceledException)
        {
            // 已被新的播放取消：新播放自带起点归位
        }
        catch
        {
            // 宿主已卸载：无可补救
        }
    }

    /// <summary>收起扫光：立即隐藏并瞬移复位到起点，等下次移动再扫。</summary>
    private static void HideSheen(Border host)
    {
        host.Transitions = null;
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
            target.RenderTransform = new TranslateTransform(x, y);
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
            target.RenderTransform = new RotateTransform(angle);
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
