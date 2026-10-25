namespace LoyaLauncher.Core;

/// <summary>下载镜像模式</summary>
public enum MirrorMode
{
    /// <summary>BMCLAPI 镜像优先，失败自动回退官方源（默认）</summary>
    BmclapiFirst,
    /// <summary>仅官方 Mojang 源</summary>
    OfficialOnly,
    /// <summary>仅 BMCLAPI 镜像</summary>
    BmclapiOnly,
    /// <summary>自定义源优先，失败依次回退 BMCLAPI、官方源</summary>
    CustomFirst,
    /// <summary>仅自定义源</summary>
    CustomOnly,
}

/// <summary>启动器全局配置（config.json）</summary>
public sealed class AppConfig
{
    /// <summary>当前选中的账户 ID</summary>
    public string CurrentAccountId { get; set; } = "";

    /// <summary>当前选中的实例 ID</summary>
    public string CurrentInstanceId { get; set; } = "";

    /// <summary>镜像模式</summary>
    public MirrorMode Mirror { get; set; } = MirrorMode.BmclapiFirst;

    /// <summary>
    /// 自定义下载源地址（需兼容 BMCLAPI 路径规范，如 https://bmclapi2.bangbang93.com）。
    /// 仅 CustomFirst / CustomOnly 模式使用；为空时这两种模式自动退化为 BMCLAPI。
    /// </summary>
    public string CustomMirrorUrl { get; set; } = "";

    /// <summary>最大并发下载数</summary>
    public int MaxConcurrentDownloads { get; set; } = 8;

    /// <summary>游戏最大堆内存（MB）</summary>
    public int JavaMaxMemoryMb { get; set; } = 4096;

    /// <summary>附加 JVM 参数（PVP 优化默认值）</summary>
    public string JvmArguments { get; set; } =
        "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 " +
        "-XX:+UnlockExperimentalVMOptions -XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 " +
        "-XX:G1HeapRegionSize=8M -XX:G1ReservePercent=20 -XX:G1HeapWastePercent=5 " +
        "-XX:G1MixedGCCountTarget=4 -XX:InitiatingHeapOccupancyPercent=15 " +
        "-XX:G1MixedGCLiveThresholdPercent=90 -XX:G1RSetUpdatingPauseTimePercent=5 " +
        "-XX:SurvivorRatio=32 -XX:MaxTenuringThreshold=1 -Dusing.aikars.flags=true";

    /// <summary>启动游戏后最小化启动器到托盘</summary>
    public bool MinimizeAfterLaunch { get; set; } = true;

    /// <summary>界面语言（资源键，如 zh-CN）</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>主题：dark / light</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>主色调（十六进制，如 #7C5CFF），空则用默认渐变</summary>
    public string AccentColor { get; set; } = "#7C5CFF";

    /// <summary>窗口几何信息</summary>
    public WindowConfig Window { get; set; } = new();

    /// <summary>同意用户协议（首次启动需勾选）</summary>
    public bool EulaAccepted { get; set; }

    /// <summary>是否开启实验性功能（基岩版 Mac/Linux 等）</summary>
    public bool ExperimentalEnabled { get; set; }

    /// <summary>
    /// 减少动效（清单 §9 P1）：关闭页面过渡、交错入场、涟漪/扫光与背景漂移，
    /// 适合低性能设备或晕动敏感用户。设置后即时生效（Motion 全局开关），无需重启。
    /// </summary>
    public bool ReduceMotion { get; set; }
}

/// <summary>窗口几何信息</summary>
public sealed class WindowConfig
{
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;
    /// <summary>窗口左上角 X；null = 由系统/居中定位（NaN 无法序列化为合法 JSON，故用可空类型）</summary>
    public double? X { get; set; }
    /// <summary>窗口左上角 Y；null = 由系统/居中定位</summary>
    public double? Y { get; set; }
    public bool Maximized { get; set; }
}
