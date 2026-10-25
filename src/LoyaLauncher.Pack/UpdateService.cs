using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.Pack;

/// <summary>
/// 更新流程各阶段状态（清单 §8）。
/// 六种失败/边界状态必须可区分，不得全部显示成"无更新"。
/// </summary>
public enum UpdateStatus
{
    /// <summary>检查成功，当前已是最新版本</summary>
    UpToDate,
    /// <summary>发现新版本，且找到了适配平台的安装包</summary>
    UpdateAvailable,
    /// <summary>发现新版本，但 Release 中没有可用安装包资产</summary>
    AssetMissing,
    /// <summary>检查过程失败（网络错误 / 响应非法）</summary>
    CheckFailed,
    /// <summary>GitHub API 限流（403/429），与普通失败区分</summary>
    RateLimited,
    /// <summary>安装包下载失败</summary>
    DownloadFailed,
    /// <summary>安装包已下载但 SHA-256 校验失败（已删除，不会应用）</summary>
    VerifyFailed,
    /// <summary>下载/解压准备阶段失败（如解压报错）</summary>
    PrepareFailed,
    /// <summary>更新包已就绪，等待下次启动应用</summary>
    Prepared,
    /// <summary>应用更新失败（已尝试回滚到旧版本）</summary>
    ApplyFailed,
}

/// <summary>更新检查结果</summary>
public sealed record UpdateInfo
{
    /// <summary>是否有可用新版本</summary>
    public bool HasUpdate { get; }

    /// <summary>结构化状态（UI 依此显示不同文案，清单 §8）</summary>
    public UpdateStatus Status { get; }

    /// <summary>当前启动器版本</summary>
    public string CurrentVersion { get; }

    /// <summary>最新版本（无更新时与当前相同）</summary>
    public string LatestVersion { get; }

    /// <summary>下载地址（无更新或找不到匹配 asset 时为 null）</summary>
    public string? DownloadUrl { get; }

    /// <summary>安装包大小（字节）</summary>
    public long DownloadSize { get; }

    /// <summary>Release Notes</summary>
    public string? Notes { get; }

    /// <summary>检查失败/边界情况的说明信息</summary>
    public string? Error { get; }

    /// <summary>
    /// 发布方提供的期望 SHA-256（小写十六进制）。
    /// 来自同名 .sha256 资产 / SHA256SUMS 资产 / Release Notes；
    /// 为 null 表示发布者未提供，客户端会记录警告（清单 §8）。
    /// </summary>
    public string? ExpectedSha256 { get; }

    public UpdateInfo(bool hasUpdate, string currentVersion, string latestVersion,
        string? downloadUrl = null, long downloadSize = 0, string? notes = null, string? error = null,
        UpdateStatus? status = null, string? expectedSha256 = null)
    {
        HasUpdate = hasUpdate;
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion;
        DownloadUrl = downloadUrl;
        DownloadSize = downloadSize;
        Notes = notes;
        Error = error;
        Status = status ?? (hasUpdate ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate);
        ExpectedSha256 = expectedSha256;
    }
}

/// <summary>更新准备（下载 + 校验 + 解压）结果</summary>
public sealed record UpdatePrepareResult
{
    public UpdateStatus Status { get; }
    /// <summary>就绪的暂存目录（仅 Status == Prepared 时非空）</summary>
    public string? StagingDir { get; }
    /// <summary>非致命警告（如"发布者未提供 SHA-256"）</summary>
    public string? Warning { get; }
    /// <summary>失败原因</summary>
    public string? Error { get; }

    public UpdatePrepareResult(UpdateStatus status, string? stagingDir = null, string? warning = null, string? error = null)
    {
        Status = status;
        StagingDir = stagingDir;
        Warning = warning;
        Error = error;
    }

    public static UpdatePrepareResult Prepared(string stagingDir, string? warning = null) =>
        new(UpdateStatus.Prepared, stagingDir, warning);

    public static UpdatePrepareResult Failed(UpdateStatus status, string error) =>
        new(status, error: error);
}

/// <summary>更新应用失败（替换过程中回滚后抛出/记录）</summary>
public sealed class UpdateApplyException : Exception
{
    public UpdateApplyException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 启动器自动更新服务（GitHub Releases）。
///
/// 更新策略（三平台一致的"下次启动应用"方案，清单 §8）：
/// 1. <see cref="CheckAsync"/> 查询 GitHub Releases latest，与当前版本比较；
///    检查失败 / 限流 / 资产缺失 / 校验缺失均为独立状态，不混淆为"无更新"；
/// 2. <see cref="PrepareUpdateAsync"/> 经 <see cref="DownloadManager"/>（重试 + 停滞看门狗 +
///    网络日志）下载平台对应 asset，校验 SHA-256（发布方提供时强制匹配），
///    安全解压到 updates/&lt;版本&gt;/，写入 pending 标记；
/// 3. 用户重启启动器后，Program.cs 调用 <see cref="ApplyPendingUpdate"/>：
///    先备份将被替换的旧文件，再逐个替换；任一失败即从备份回滚；
///    成功后写 applied 标记，等待 <see cref="ConfirmStartup"/> 确认；
/// 4. 若应用更新后启动从未成功确认（下次启动时 <see cref="RollbackIfUnconfirmed"/>
///    检测到 applied 标记仍在），自动从备份恢复旧版本。
/// 这样做避免了 Windows 上运行中 exe/dll 无法覆盖的问题，也无需辅助脚本。
/// </summary>
public sealed class UpdateService
{
    private const string RepoOwner = "Easy-Dioxide";
    private const string RepoName = "LoyaLauncher";
    private const string PendingMarkerName = "pending.json";
    private const string AppliedMarkerName = "applied.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly DownloadManager _downloads;

    /// <summary>GitHub API 基地址（测试可覆盖为本地 mock 服务器）</summary>
    public string ApiBaseUrl { get; }

    public UpdateService(DownloadManager downloads, string? apiBaseUrl = null)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        ApiBaseUrl = (apiBaseUrl ?? "https://api.github.com").TrimEnd('/');
    }

    /// <summary>当前启动器版本（来自程序集 InformationalVersion，形如 1.2.3）</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0.0.0";
            // 去掉 +commit 后缀
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
    }

    /// <summary>
    /// 检查更新。所有失败路径都返回带 <see cref="UpdateStatus"/> 的结果而不抛异常，
    /// 调用方依据 Status 展示不同文案（清单 §8：不得全部显示成无更新）。
    /// </summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        var url = $"{ApiBaseUrl}/repos/{RepoOwner}/{RepoName}/releases/latest";

        var fetch = await _downloads.GetStringResultAsync(url, group: "update-api", ct: ct);
        if (!fetch.Ok)
        {
            // GitHub API 对未认证请求有严格限流（403/429），与普通网络失败区分提示
            var status = fetch.StatusCode is 403 or 429 ? UpdateStatus.RateLimited : UpdateStatus.CheckFailed;
            var reason = status == UpdateStatus.RateLimited
                ? "GitHub API 访问受限（403/429），请稍后再试"
                : $"检查更新失败: {fetch.Error}";
            return new UpdateInfo(false, current, current, error: reason, status: status);
        }

        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize<GitHubRelease>(fetch.Content!);
        }
        catch (JsonException ex)
        {
            return new UpdateInfo(false, current, current,
                error: $"检查更新失败: 响应不是合法 JSON（{ex.Message}）", status: UpdateStatus.CheckFailed);
        }

        if (release is null || release.Draft)
            return new UpdateInfo(false, current, current);

        var latest = release.TagName.TrimStart('v', 'V');
        if (string.IsNullOrEmpty(latest))
            return new UpdateInfo(false, current, current,
                error: "检查更新失败: Release 缺少版本号（tag_name）", status: UpdateStatus.CheckFailed);

        if (CompareVersions(latest, current) <= 0)
            return new UpdateInfo(false, current, latest, notes: release.Body);

        var asset = PickAssetForPlatform(release.Assets);
        if (asset is null)
            return new UpdateInfo(true, current, latest,
                error: $"发现新版本 {latest}，但 Release 中没有适配当前平台的安装包",
                notes: release.Body, status: UpdateStatus.AssetMissing);

        // 期望 SHA-256：优先发布方随 Release 提供的校验资产，其次从 Release Notes 提取
        string? expectedSha = null;
        var checksumAsset = PickChecksumAsset(release.Assets, asset.Name);
        if (checksumAsset is not null)
        {
            var text = await _downloads.GetStringResultAsync(checksumAsset.BrowserDownloadUrl, group: "update-api", ct: ct);
            if (text.Ok)
                expectedSha = ParseChecksumText(text.Content!, asset.Name);
            else
                Debug.WriteLine($"[UpdateService] 校验资产下载失败: {text.Error}");
        }
        expectedSha ??= ExtractShaFromNotes(release.Body, asset.Name);

        // 注意：ExpectedSha256 为 null 只表示发布方未提供校验值，
        // 不设 Error——这是准备阶段的警告（由 UI 追加提示），不是检查失败。
        return new UpdateInfo(
            true, current, latest,
            asset.BrowserDownloadUrl, asset.Size,
            release.Body,
            error: null,
            UpdateStatus.UpdateAvailable,
            expectedSha);
    }

    /// <summary>
    /// 下载更新包并解压就位（写入 pending 标记），等待下次启动应用。
    /// 下载走 <see cref="DownloadManager"/>：自动重试、停滞看门狗、网络诊断日志。
    /// </summary>
    public async Task<UpdatePrepareResult> PrepareUpdateAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (info.DownloadUrl is null)
            return UpdatePrepareResult.Failed(UpdateStatus.DownloadFailed, "没有可下载的更新地址");

        var updatesDir = Path.Combine(LoyaPaths.Root, "updates");
        var targetDir = Path.Combine(updatesDir, SafeVersionDirName(info.LatestVersion));
        var packagePath = Path.Combine(LoyaPaths.Cache, $"LoyaLauncher-{SafeVersionDirName(info.LatestVersion)}.zip");

        Directory.CreateDirectory(updatesDir);
        Directory.CreateDirectory(LoyaPaths.Cache);

        // 1. 下载安装包（重试 + 停滞看门狗由 DownloadManager 负责）
        progress?.Report(0.1);
        var dl = await _downloads.DownloadFileAsync(
            info.DownloadUrl, packagePath, expectedSha1: null, expectJson: false, ct: ct);
        if (!dl.Success)
        {
            return UpdatePrepareResult.Failed(UpdateStatus.DownloadFailed,
                dl.Error ?? "下载更新包失败");
        }

        // 2. SHA-256 校验（清单 §8：发布方提供期望值时必须匹配，否则不应用）
        progress?.Report(0.5);
        string? actualSha;
        try
        {
            actualSha = await ComputeSha256Async(packagePath, ct);
        }
        catch (Exception ex)
        {
            TryDelete(packagePath);
            return UpdatePrepareResult.Failed(UpdateStatus.VerifyFailed, $"计算安装包哈希失败: {ex.Message}");
        }

        string? warning = null;
        if (info.ExpectedSha256 is not null &&
            !string.Equals(info.ExpectedSha256, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            // 校验失败：删除安装包，绝不进入解压/应用阶段
            TryDelete(packagePath);
            return UpdatePrepareResult.Failed(UpdateStatus.VerifyFailed,
                $"安装包 SHA-256 校验失败（期望 {info.ExpectedSha256[..12]}…，实际 {actualSha[..12]}…），已删除安装包");
        }
        if (info.ExpectedSha256 is null)
        {
            warning = "发布者未提供 SHA-256 清单，本次更新跳过了完整性校验";
        }

        // 3. 安全解压到版本暂存目录（SafeArchive 拒绝目录穿越与符号链接条目）
        progress?.Report(0.7);
        try
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);
            SafeArchive.ExtractZipSafely(packagePath, targetDir);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(targetDir);
            TryDelete(packagePath);
            return UpdatePrepareResult.Failed(UpdateStatus.PrepareFailed, $"解压更新包失败: {ex.Message}");
        }
        finally
        {
            TryDelete(packagePath);
        }

        // 4. 写 pending 标记（含校验信息，供应用阶段审计）
        progress?.Report(0.9);
        var marker = new
        {
            Version = info.LatestVersion,
            SourceDir = targetDir,
            PreparedAt = DateTime.UtcNow,
            Sha256 = actualSha,
            Sha256Verified = info.ExpectedSha256 is not null,
        };
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, PendingMarkerName),
            JsonSerializer.Serialize(marker, JsonOpts), ct);

        progress?.Report(1.0);
        return UpdatePrepareResult.Prepared(targetDir, warning);
    }

    /// <summary>
    /// 应用已就绪的更新（在启动早期、旧进程完全退出后调用）。
    /// 流程：备份将被替换的旧文件 → 逐个复制替换 → 任一失败从备份回滚 →
    /// 成功则写 applied 标记（等待 <see cref="ConfirmStartup"/> 确认）。
    /// </summary>
    /// <param name="appDirOverride">测试注入的应用目录；null 时取真实运行目录</param>
    public static bool ApplyPendingUpdate(string? appDirOverride = null)
    {
        var updatesDir = Path.Combine(LoyaPaths.Root, "updates");
        var markerPath = Path.Combine(updatesDir, PendingMarkerName);
        if (!File.Exists(markerPath)) return false;

        try
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            var sourceDir = marker.RootElement.GetProperty("SourceDir").GetString();
            var version = marker.RootElement.TryGetProperty("Version", out var v) ? v.GetString() : null;
            if (sourceDir is null || !Directory.Exists(sourceDir))
            {
                File.Delete(markerPath);
                return false;
            }

            // 防御：pending 标记里的 SourceDir 必须位于 updates 目录下，
            // 防止标记被篡改后把任意目录的文件复制进应用目录
            var updatesRoot = Path.GetFullPath(updatesDir);
            if (!Path.GetFullPath(sourceDir).StartsWith(updatesRoot, (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                File.Delete(markerPath);
                Debug.WriteLine("[UpdateService] pending 标记 SourceDir 越界，已忽略");
                return false;
            }

            // 应用目录：优先取真实 exe 路径（单文件模式下 Assembly.Location 为空、
            // AppContext.BaseDirectory 指向临时解压目录，均不可信）
            var appDir = appDirOverride
                ?? Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
                ?? Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                ?? AppContext.BaseDirectory;

            var running = Path.GetFullPath(Process.GetCurrentProcess().MainModule?.FileName ?? "");

            // 收集替换计划（跳过当前正在运行的可执行文件——它无法被覆盖，
            // 且本次重启后加载的仍是旧 exe，下次重启自然生效）
            var plan = new List<(string Source, string Dest, string Relative)>();
            foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, source);
                var dest = Path.Combine(appDir, relative);
                if (!string.IsNullOrEmpty(running) && Path.GetFullPath(dest) == running) continue;
                plan.Add((source, dest, relative));
            }

            // 1. 备份：仅备份"已存在且将被覆盖"的旧文件（新增文件无需备份，
            //    回滚时删除即可），备份目录带版本与时间戳
            var backupDir = Path.Combine(updatesDir, "backup", $"{SafeVersionDirName(version ?? "unknown")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
            var backedUp = new List<string>();
            foreach (var (_, dest, relative) in plan)
            {
                if (!File.Exists(dest)) continue;
                var backupPath = Path.Combine(backupDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(dest, backupPath, overwrite: true);
                backedUp.Add(relative);
            }

            // 2. 替换：任一文件失败 → 从备份回滚已替换部分，保留旧版本
            try
            {
                foreach (var (source, dest, relative) in plan)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(source, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                var restored = RestoreFromBackup(backupDir, appDir, plan);
                TryDeleteDirectory(backupDir);
                File.Delete(markerPath);
                Debug.WriteLine($"[UpdateService] 应用更新失败（已回滚 {restored} 个文件）: {ex.Message}");
                return false;
            }

            // 3. 成功：写 applied 标记，等待启动成功确认后清理备份。
            //    AwaitingFirstStart 标记这是"应用更新后的第一次启动"——
            //    重启进程应在启动早期将其置 false；若之后再看到 applied 标记，
            //    说明第一次启动也失败了，才触发回滚（见 RollbackIfUnconfirmed）。
            var applied = new
            {
                Version = version,
                BackupDir = backupDir,
                AppliedAt = DateTime.UtcNow,
                Files = plan.Select(p => p.Relative).ToArray(),
                AwaitingFirstStart = true,
            };
            File.WriteAllText(
                Path.Combine(updatesDir, AppliedMarkerName),
                JsonSerializer.Serialize(applied, JsonOpts));

            File.Delete(markerPath);
            Debug.WriteLine($"[UpdateService] 已应用 {plan.Count} 个文件，备份于 {backupDir}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] 应用更新失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动成功确认：删除 applied 标记与备份目录。
    /// 由 App 主窗口加载后调用——走到这里说明新版本已正常运行。
    /// </summary>
    public static void ConfirmStartup()
    {
        try
        {
            var updatesDir = Path.Combine(LoyaPaths.Root, "updates");
            var appliedPath = Path.Combine(updatesDir, AppliedMarkerName);
            if (!File.Exists(appliedPath)) return;

            string? backupDir = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(appliedPath));
                backupDir = doc.RootElement.TryGetProperty("BackupDir", out var b) ? b.GetString() : null;
            }
            catch (JsonException)
            {
                // 标记损坏：当作无备份处理，仅删除标记
            }

            if (backupDir is not null)
            {
                var updatesRoot = Path.GetFullPath(updatesDir);
                var full = Path.GetFullPath(backupDir);
                // 只允许删除 updates/backup 下的目录，防止标记被篡改导致误删
                if (full.StartsWith(Path.Combine(updatesRoot, "backup"), (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    TryDeleteDirectory(full);
            }
            File.Delete(appliedPath);
        }
        catch (Exception ex)
        {
            // 确认失败不能影响启动，仅记录
            Debug.WriteLine($"[UpdateService] 启动确认清理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 回滚检查（启动早期调用）：
    /// - applied 标记带 <c>AwaitingFirstStart</c>：这是应用更新后的第一次启动
    ///   （由 ApplyPendingUpdate 触发的重启），属预期流程——置位后放行；
    /// - applied 标记无该位：说明第一次启动也未成功走到
    ///   <see cref="ConfirmStartup"/>，新版本起不来，从备份恢复旧版本。
    /// 返回 true 表示发生了回滚。
    /// </summary>
    public static bool RollbackIfUnconfirmed(string? appDirOverride = null)
    {
        var updatesDir = Path.Combine(LoyaPaths.Root, "updates");
        var appliedPath = Path.Combine(updatesDir, AppliedMarkerName);
        if (!File.Exists(appliedPath)) return false;

        try
        {
            var json = File.ReadAllText(appliedPath);
            using var doc = JsonDocument.Parse(json);

            // 应用更新后的第一次启动：放行，仅清除宽限位
            if (doc.RootElement.TryGetProperty("AwaitingFirstStart", out var awaitFirst) &&
                awaitFirst.ValueKind == JsonValueKind.True)
            {
                var cleared = JsonSerializer.Serialize(new
                {
                    Version = doc.RootElement.TryGetProperty("Version", out var v) ? v.GetString() : null,
                    BackupDir = doc.RootElement.GetProperty("BackupDir").GetString(),
                    AppliedAt = doc.RootElement.TryGetProperty("AppliedAt", out var a) ? a.GetString() : null,
                    Files = doc.RootElement.TryGetProperty("Files", out var f)
                        ? f.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                        : [],
                    AwaitingFirstStart = false,
                }, JsonOpts);
                File.WriteAllText(appliedPath, cleared);
                return false;
            }

            var backupDir = doc.RootElement.GetProperty("BackupDir").GetString();
            var version = doc.RootElement.TryGetProperty("Version", out var ver) ? ver.GetString() : "unknown";
            var files = doc.RootElement.TryGetProperty("Files", out var filesEl)
                ? filesEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                : [];

            var appDir = appDirOverride
                ?? Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
                ?? Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                ?? AppContext.BaseDirectory;

            if (backupDir is null || !Directory.Exists(backupDir))
            {
                // 没有备份可恢复：只能删掉标记，避免每次启动都尝试回滚
                File.Delete(appliedPath);
                Debug.WriteLine("[UpdateService] applied 标记存在但备份缺失，无法回滚");
                return false;
            }

            var restored = 0;
            foreach (var relative in files)
            {
                var dest = Path.Combine(appDir, relative);
                var backupPath = Path.Combine(backupDir, relative);
                try
                {
                    if (File.Exists(backupPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(backupPath, dest, overwrite: true);
                        restored++;
                    }
                    else if (File.Exists(dest))
                    {
                        // 更新新增的文件（旧版本不存在）：直接删除
                        File.Delete(dest);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UpdateService] 回滚 {relative} 失败: {ex.Message}");
                }
            }

            TryDeleteDirectory(backupDir);
            File.Delete(appliedPath);
            Debug.WriteLine($"[UpdateService] 版本 {version} 启动未确认，已回滚 {restored} 个文件");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] 回滚检查失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>从备份目录恢复 plan 中列出的文件，返回恢复的文件数</summary>
    private static int RestoreFromBackup(string backupDir, string appDir,
        List<(string Source, string Dest, string Relative)> plan)
    {
        var restored = 0;
        foreach (var (_, _, relative) in plan)
        {
            var backupPath = Path.Combine(backupDir, relative);
            var dest = Path.Combine(appDir, relative);
            try
            {
                if (File.Exists(backupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(backupPath, dest, overwrite: true);
                    restored++;
                }
                else if (File.Exists(dest))
                {
                    File.Delete(dest);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] 恢复 {relative} 失败: {ex.Message}");
            }
        }
        return restored;
    }

    /// <summary>计算文件 SHA-256（小写十六进制）</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// 语义化版本比较：按点分数字段逐个比较，缺省字段视为 0。
    /// 返回 >0 表示 a 更新，0 表示相同，&lt;0 表示 a 更旧。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = ParseVersionParts(a);
        var pb = ParseVersionParts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            var cmp = va.CompareTo(vb);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int[] ParseVersionParts(string version)
    {
        var clean = version.Trim().TrimStart('v', 'V');
        // 去掉预发布标签（如 -rc.1）后再拆分
        var dash = clean.IndexOf('-');
        if (dash > 0) clean = clean[..dash];
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return result;
    }

    /// <summary>
    /// 从 Release assets 中选择当前平台对应的安装包。
    /// 约定命名：LoyaLauncher-{rid}.zip（如 win-x64 / linux-x64 / osx-x64 / osx-arm64）。
    /// 回退到任意平台 zip 时排除源码包（LoyaLauncher-src-*.zip），
    /// 防止自动更新把源码压缩包误当安装包下载。
    /// 找不到时返回 null。
    /// </summary>
    public static GitHubAsset? PickAssetForPlatform(List<GitHubAsset> assets)
    {
        var rid = LoyaPaths.PlatformId switch
        {
            "windows" => "win",
            "mac" => "osx",
            _ => "linux",
        } + "-" + (LoyaPaths.ArchId == "aarch64" ? "arm64" : "x64");

        var isInstallableZip = new Func<GitHubAsset, bool>(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("-src-", StringComparison.OrdinalIgnoreCase));

        return assets.FirstOrDefault(a =>
            a.Name.Equals($"LoyaLauncher-{rid}.zip", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(isInstallableZip);
    }

    /// <summary>
    /// 查找发布方提供的校验资产：
    /// 1. 与安装包同名的 .sha256 资产（如 LoyaLauncher-win-x64.zip.sha256）；
    /// 2. 通用 SHA256SUMS 资产。
    /// 内容需另行下载解析（<see cref="ParseChecksumText"/>）。
    /// </summary>
    public static GitHubAsset? PickChecksumAsset(List<GitHubAsset> assets, string assetName)
    {
        return assets.FirstOrDefault(a =>
            a.Name.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a =>
                a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析校验资产文本（.sha256 / SHA256SUMS 格式），提取目标安装包的期望哈希。
    /// 行格式："&lt;hash&gt;  &lt;filename&gt;" 或纯 "&lt;hash&gt;"；
    /// SHA256SUMS 多文件场景下仅接受提到目标文件名的行。
    /// </summary>
    public static string? ParseChecksumText(string text, string assetName)
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var hash = line.Length >= 64 ? line[..64] : line;
            if (!IsHex64(hash)) continue;
            // 行内包含其他文件名（SHA256SUMS 多文件场景）时必须匹配目标
            if (line.Length > 64 && !line.Contains(assetName, StringComparison.OrdinalIgnoreCase))
                continue;
            return hash.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// 从 Release Notes 提取 SHA-256：优先包含安装包名的行；
    /// 否则仅当全文只有一个 64 位十六进制串时采用（保守，避免误取）。
    /// </summary>
    public static string? ExtractShaFromNotes(string? body, string assetName)
    {
        if (string.IsNullOrEmpty(body)) return null;

        string? withName = null;
        var all = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(raw, "[0-9a-fA-F]{64}"))
            {
                all.Add(m.Value);
                if (withName is null && raw.Contains(assetName, StringComparison.OrdinalIgnoreCase))
                    withName = m.Value;
            }
        }

        if (withName is not null) return withName.ToLowerInvariant();
        return all.Count == 1 ? all[0].ToLowerInvariant() : null;
    }

    private static bool IsHex64(string s) =>
        s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>版本号用作目录名：剔除路径分隔符等危险字符</summary>
    private static string SafeVersionDirName(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(version.Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Debug.WriteLine($"[UpdateService] 删除 {path} 失败: {ex.Message}"); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"[UpdateService] 删除目录 {path} 失败: {ex.Message}"); }
    }
}
