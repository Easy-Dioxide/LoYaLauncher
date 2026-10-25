using System.Net;
using System.Text.Json;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.Mods;

/// <summary>
/// Modrinth API 异常：携带 HTTP 状态码，UI 可区分限流（429）、
/// 未找到（404）、服务端错误（5xx）与离线（0）并给出不同提示（清单 §5）。
/// </summary>
public sealed class ModrinthApiException : Exception
{
    public HttpStatusCode Status { get; }

    public ModrinthApiException(HttpStatusCode status, string message, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
    }

    /// <summary>是否因 API 限流失败（重试已耗尽）</summary>
    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;
}

/// <summary>
/// Modrinth API v2 客户端。公共搜索与项目端点无需 API Key。
///
/// 清单 §5 修复点：
/// - API 请求统一走 <see cref="DownloadManager"/>（有限重试、停滞看门狗、网络诊断日志），
///   不再自建裸 HttpClient（§2.1 统一 HttpClient，避免 socket 耗尽）；
/// - API（api.modrinth.com）与文件 CDN（项目版本返回的 URL）严格分离，
///   日志按 "modrinth-api" / "mod" 分组，诊断页可分辨是哪一侧故障；
/// - 429/Retry-After 由重试策略处理，耗尽后抛出带状态码的
///   <see cref="ModrinthApiException"/>，错误不再是无状态字符串；
/// - 文件下载保持使用版本数据里的原始 URL，不替换域名。
/// </summary>
public sealed class ModrinthClient : IDisposable
{
    private const string BaseUrl = "https://api.modrinth.com/v2/";
    private const string ApiGroup = "modrinth-api";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient? _http;
    private readonly bool _ownsHttp;
    private readonly DownloadManager? _downloads;

    /// <summary>
    /// 兼容构造：调用方自带 HttpClient（BaseAddress 需指向 api.modrinth.com/v2/）。
    /// 单次请求、无重试——生产代码请使用 <see cref="ModrinthClient(DownloadManager)"/>。
    /// </summary>
    public ModrinthClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _ownsHttp = http is null;
        _http.BaseAddress ??= new Uri(BaseUrl);
        // Modrinth requires a descriptive User-Agent. Keep it stable and avoid a misleading
        // third-party project identifier so API errors are easier to diagnose.
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LoyaLauncher/0.7.1 (https://github.com/Easy-Dioxide/LoyaLauncher)");
    }

    /// <summary>
    /// 生产构造：API 请求与文件下载都经由统一的 <see cref="DownloadManager"/>
    /// （重试 / 停滞看门狗 / 网络日志 / 镜像健康统计）。
    /// </summary>
    /// <param name="apiBaseUrl">API 基地址覆盖（测试注入本地 mock；null 用官方地址）</param>
    public ModrinthClient(DownloadManager downloads, string? apiBaseUrl = null)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            BaseUrlOverride = apiBaseUrl.TrimEnd('/') + "/";
    }

    /// <summary>API 基地址（测试可覆盖；生产恒为 https://api.modrinth.com/v2/）</summary>
    private string ApiBase => BaseUrlOverride ?? BaseUrl;
    private string? BaseUrlOverride { get; }

    /// <summary>搜索 Mod。facets 格式：[["project_type:mod"],["categories:fabric"]]</summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query,
        string gameVersion,
        string loader,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default)
    {
        // Modrinth's facets schema is an array of arrays of "key:value" strings.
        // Objects here return HTTP 400 and made every mod search fail.
        List<string[]> facetGroups = [["project_type:mod"]];
        if (!string.IsNullOrWhiteSpace(loader) && !loader.Equals("all", StringComparison.OrdinalIgnoreCase))
            facetGroups.Add([$"categories:{loader.Trim()}"]);
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facetGroups.Add([$"versions:{gameVersion.Trim()}"]);
        var facets = JsonSerializer.Serialize(facetGroups);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var safeOffset = Math.Max(0, offset);
        var url = $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&limit={safeLimit}&offset={safeOffset}";
        return await GetAsync<ModrinthSearchResult>(url, ct) ?? new ModrinthSearchResult();
    }

    /// <summary>获取项目的版本列表（按游戏版本与加载器过滤）</summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string gameVersion,
        string loader,
        CancellationToken ct = default)
    {
        // /project/{id}/version uses game_versions and loaders query parameters, not facets.
        // Both parameters are JSON-encoded arrays per the Modrinth API v2 contract.
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion.Trim() }))}");
        if (!string.IsNullOrWhiteSpace(loader) && !loader.Equals("all", StringComparison.OrdinalIgnoreCase))
            query.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.Trim() }))}");
        var project = Uri.EscapeDataString(projectIdOrSlug.Trim());
        var url = $"project/{project}/version" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return await GetAsync<List<ModrinthVersion>>(url, ct) ?? [];
    }

    /// <summary>下载 Mod 文件到实例 mods 目录（文件 URL 用版本数据原始值，不替换域名，清单 §5）</summary>
    public async Task<string> DownloadModAsync(
        ModrinthFile file,
        string modsDirectory,
        DownloadManager downloader,
        CancellationToken ct = default)
    {
        // Modrinth 返回的文件名来自远端数据：清单 §10 要求拒绝（而非净化）
        // 含目录分隔符 / ".." / 保留设备名的文件名，防止穿越出 mods 目录
        if (!SafePath.IsSafeFileName(file.Filename))
            throw new InvalidOperationException($"Mod 文件名不安全，已拒绝下载: {file.Filename}");
        var dest = Path.Combine(modsDirectory, file.Filename);
        var sha1 = file.Hashes.TryGetValue("sha1", out var h) ? h : null;
        var result = await downloader.DownloadFileAsync(file.Url, dest, sha1, group: "mod", ct: ct);
        if (!result.Success)
            throw new InvalidOperationException($"Mod 下载失败: {result.Error}");
        return dest;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        if (_downloads is not null)
        {
            // 生产路径：绝对 URL 走统一下载基础设施（重试 + 日志 + 停滞看门狗）
            var fetch = await _downloads.GetStringResultAsync(ApiBase + url, group: ApiGroup, ct: ct);
            if (!fetch.Ok)
                throw new ModrinthApiException(
                    (HttpStatusCode)fetch.StatusCode,
                    DescribeFailure(fetch.StatusCode, fetch.Error));
            return Deserialize<T>(fetch.Content!);
        }

        // 兼容路径：注入的裸 HttpClient（测试用），单次请求
        using var response = await _http!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            if (detail.Length > 400) detail = detail[..400];
            throw new ModrinthApiException(
                response.StatusCode,
                $"Modrinth API 返回 {(int)response.StatusCode} ({response.ReasonPhrase})：{detail}");
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        return Deserialize<T>(json);
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new ModrinthApiException(default, $"Modrinth 响应解析失败: {ex.Message}", ex);
        }
    }

    /// <summary>把传输层失败翻译成用户可理解、且按状态码分类的信息</summary>
    private static string DescribeFailure(int status, string? error)
    {
        var code = (HttpStatusCode)status;
        return code switch
        {
            HttpStatusCode.TooManyRequests => "Modrinth API 限流（429），请稍后再试",
            HttpStatusCode.NotFound => "Modrinth 上未找到对应项目或版本",
            HttpStatusCode.BadRequest => "搜索参数不被 Modrinth 接受（400）",
            _ => status == 0
                ? $"无法连接 Modrinth（网络不可用或超时）：{error}"
                : $"Modrinth API 返回 {status}：{error}",
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http?.Dispose();
    }
}
