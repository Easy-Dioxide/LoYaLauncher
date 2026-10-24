using System.Diagnostics;
using System.Text.Json;
using LoyaLauncher.Core;

namespace LoyaLauncher.Java;

/// <summary>安装进度回调</summary>
public sealed record JavaInstallProgress(string VersionId, string Stage, string Detail, double Percent);

/// <summary>
/// Java 版门面服务：版本清单、安装、实例创建、启动。
/// UI 层只与这个类交互。
/// </summary>
public sealed class JavaEditionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly JavaInstaller _installer;
    private readonly JreManager _jre;
    private readonly Downloader.DownloadManager _downloader;
    private readonly Core.EventBus _bus;

    /// <summary>当前正在运行的游戏进程（P1-5：持有引用，避免"启动后无人跟踪"）</summary>
    private GameProcessRunner? _activeRunner;

    /// <summary>是否有游戏正在运行</summary>
    public bool HasActiveGame => _activeRunner is { IsRunning: true };

    /// <summary>请求结束当前游戏进程（无运行时为空操作）</summary>
    public void StopActiveGame()
    {
        _activeRunner?.Stop();
    }

    public JavaEditionService(
        JavaInstaller installer,
        JreManager jre,
        Downloader.DownloadManager downloader,
        Core.EventBus bus)
    {
        _installer = installer;
        _jre = jre;
        _downloader = downloader;
        _bus = bus;
    }

    // ======================= 版本清单 =======================

    /// <summary>获取可下载版本列表</summary>
    public async Task<List<ManifestVersion>> GetAvailableVersionsAsync(CancellationToken ct = default)
    {
        var manifest = await _installer.GetManifestAsync(ct);
        return manifest.Versions;
    }

    /// <summary>获取最新版本 ID</summary>
    public async Task<(string Release, string Snapshot)> GetLatestAsync(CancellationToken ct = default)
    {
        var manifest = await _installer.GetManifestAsync(ct);
        return (manifest.Latest?.Release ?? "", manifest.Latest?.Snapshot ?? "");
    }

    // ======================= 安装 =======================

    /// <summary>安装版本（带阶段进度）。安装完成后预下载该版本所需的 Java。</summary>
    public async Task InstallAsync(string versionId, IProgress<JavaInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var adapter = new ProgressAdapter(versionId, progress);
        await _installer.InstallAsync(versionId, adapter, ct);

        // 预下载 Java：安装完成即就绪，启动时零等待。
        // 失败不阻断安装——启动时 EnsureJavaAsync 还会再试一次。
        try
        {
            var json = await _installer.GetVersionJsonAsync(versionId, ct);
            var major = JavaInstaller.GetRequiredJavaMajor(json);
            progress?.Report(new JavaInstallProgress(versionId, "Java", $"Java {major}", 0.95));
            await _jre.EnsureJavaAsync(major, ct);
            progress?.Report(new JavaInstallProgress(versionId, "Java", $"Java {major}", 1.0));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 预下载失败可容忍：启动路径上的 EnsureJavaAsync 会兜底
        }
    }

    private sealed class ProgressAdapter(string versionId, IProgress<JavaInstallProgress>? target)
        : IProgress<JavaInstaller.InstallProgressEvent>
    {
        public void Report(JavaInstaller.InstallProgressEvent value) =>
            target?.Report(new JavaInstallProgress(versionId, value.Stage, value.Detail, value.Percent));
    }

    /// <summary>是否已安装</summary>
    public bool IsInstalled(string versionId) => _installer.IsInstalled(versionId);

    /// <summary>已安装版本列表</summary>
    public List<InstalledVersionMeta> ListInstalled() => _installer.ListInstalled();

    // ======================= 实例管理 =======================

    /// <summary>创建实例（自动安装底层版本）</summary>
    public async Task<GameInstance> CreateInstanceAsync(
        string name, string versionId, CancellationToken ct = default)
    {
        var instance = new GameInstance
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name,
            Edition = GameEdition.Java,
            VersionId = versionId,
        };

        Directory.CreateDirectory(instance.GameDirectory);
        Directory.CreateDirectory(instance.ModsDirectory);
        Directory.CreateDirectory(instance.ConfigsDirectory);
        Directory.CreateDirectory(instance.SavesDirectory);
        await SaveInstanceAsync(instance, ct);
        return instance;
    }

    /// <summary>保存实例元数据</summary>
    public static async Task SaveInstanceAsync(GameInstance instance, CancellationToken ct = default)
    {
        Directory.CreateDirectory(instance.RootDirectory);
        await File.WriteAllTextAsync(
            instance.MetaPath,
            JsonSerializer.Serialize(instance, JsonOpts), ct);
    }

    /// <summary>列出全部实例</summary>
    public static List<GameInstance> ListInstances()
    {
        var result = new List<GameInstance>();
        if (!Directory.Exists(LoyaPaths.Instances)) return result;
        foreach (var dir in Directory.EnumerateDirectories(LoyaPaths.Instances))
        {
            var metaPath = Path.Combine(dir, "instance.json");
            if (!File.Exists(metaPath)) continue;
            try
            {
                var inst = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(metaPath), JsonOpts);
                if (inst is not null) result.Add(inst);
            }
            catch { /* 跳过损坏记录 */ }
        }
        return result.OrderByDescending(i => i.LastPlayed).ToList();
    }

    /// <summary>删除实例（保留已安装版本）</summary>
    public static void DeleteInstance(string instanceId)
    {
        var dir = Path.Combine(LoyaPaths.Instances, instanceId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    // ======================= 启动 =======================

    /// <summary>
    /// 启动实例。流程：确保版本安装 → 确保 Java → 构建 classpath 与参数 → 启动进程。
    /// </summary>
    public async Task<GameProcessRunner> LaunchAsync(
        GameInstance instance,
        IGameSession session,
        int maxHeapMb = 4096,
        string extraJvmArgs = "",
        CancellationToken ct = default)
    {
        // 取消边界：调用方传入已取消的令牌时，在任何安装/下载动作之前停下，
        // 避免半途启动后状态不一致
        ct.ThrowIfCancellationRequested();

        // 1. 确保版本已安装
        if (!_installer.IsInstalled(instance.VersionId))
            await _installer.InstallAsync(instance.VersionId, ct: ct);

        // 2. 版本 JSON
        var json = await _installer.GetVersionJsonAsync(instance.VersionId, ct);

        // 3. 确保 Java
        var requiredMajor = JavaInstaller.GetRequiredJavaMajor(json);
        var javaPath = await _jre.EnsureJavaAsync(requiredMajor, ct);

        // 4. classpath：实例版本声明的 libraries + 客户端 jar
        var classpathEntries = new List<string>();
        foreach (var lib in json.Libraries)
        {
            if (!JavaInstaller.RulesAllow(lib.Rules)) continue;
            if (lib.Downloads?.Artifact is null) continue;
            var mavenPath = JavaInstaller.LibraryToMavenPath(lib.Name);
            var fullPath = Path.Combine(LoyaPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                classpathEntries.Add(fullPath);
        }
        classpathEntries.Add(Path.Combine(LoyaPaths.Versions, instance.VersionId, $"{instance.VersionId}.jar"));

        // 加载器注入的 jar（fabric/forge 等）
        var loaderJar = GetLoaderJar(instance);
        if (loaderJar is not null)
            classpathEntries.Add(loaderJar);

        var classpath = string.Join(Path.PathSeparator, classpathEntries);

        // 5. 构建启动命令
        var ctx = new LaunchContext
        {
            VersionId = instance.VersionId,
            GameDirectory = instance.GameDirectory,
            JavaExecutable = javaPath,
            MainClass = json.MainClass,
            Classpath = classpath,
            NativesDirectory = Path.Combine(LoyaPaths.Versions, instance.VersionId, "natives"),
            AssetsDirectory = LoyaPaths.Assets,
            AssetIndexId = json.AssetIndex?.Id ?? "legacy",
            PlayerName = session.PlayerName,
            PlayerUuid = session.PlayerUuid,
            AccessToken = session.AccessToken,
            UserType = session.UserType,
            MaxHeapMb = maxHeapMb,
            ExtraJvmArgs = extraJvmArgs,
        };

        var command = LaunchCommandBuilder.Build(ctx, json);
        _bus.Publish(new GameLogEvent(instance.VersionId, $"[LoyaLauncher] Java: {javaPath}", false));
        _bus.Publish(new GameLogEvent(instance.VersionId, $"[LoyaLauncher] 启动命令: {string.Join(' ', command)}", false));

        // 6. 启动。监控任务不丢弃引用： finally 中释放进程句柄，
        //    并按"取消 / 异常"分流记录，取消不当作崩溃。
        var runner = new GameProcessRunner(_bus);
        _activeRunner = runner;
        _ = Task.Run(async () =>
        {
            try
            {
                await runner.StartAsync(command, instance.GameDirectory, instance.VersionId, ct);
            }
            catch (OperationCanceledException)
            {
                // 取消是预期路径：进程已被 runner 终止，GameExitedEvent 已发布，UI 自行复位
                _bus.Publish(new GameLogEvent(instance.VersionId, "[LoyaLauncher] 启动已取消", false));
            }
            catch (Exception ex)
            {
                _bus.Publish(new GameLogEvent(instance.VersionId, $"[LoyaLauncher] 异常: {ex.Message}", true));
            }
            finally
            {
                // 进程已退出：释放 Process 句柄，避免每次启动泄漏一个内核对象
                runner.Dispose();
                if (ReferenceEquals(_activeRunner, runner))
                    _activeRunner = null;
            }
        }, ct);

        instance.LastPlayed = DateTime.UtcNow;
        await SaveInstanceAsync(instance, ct);
        return runner;
    }

    /// <summary>查找加载器注入的 jar（由 Mods 模块安装到实例目录）</summary>
    private static string? GetLoaderJar(GameInstance instance)
    {
        var loaderDir = Path.Combine(instance.RootDirectory, "loader");
        if (!Directory.Exists(loaderDir)) return null;
        return Directory.EnumerateFiles(loaderDir, "*.jar").FirstOrDefault();
    }
}
