using System.Text.Json;

namespace LoyaLauncher.Mods;

/// <summary>
/// Modrinth API v2 客户端。公共搜索与项目端点无需 API Key。
/// </summary>
public sealed class ModrinthClient : IDisposable
{
    private const string BaseUrl = "https://api.modrinth.com/v2";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public ModrinthClient()
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Add("User-Agent", "LoyaLauncher/0.1.0 (pvp-launcher)");
    }

    /// <summary>搜索 Mod。facets 格式：[["project_type:mod"],["categories:fabric"]]</summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query,
        string gameVersion,
        string loader,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default)
    {
        var facets = $"[{{\"project_type\":\"mod\"}},{{\"categories\":\"{loader}\"}},{{\"versions\":\"{gameVersion}\"}}]";
        var url = $"/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&limit={limit}&offset={offset}";
        return await GetAsync<ModrinthSearchResult>(url, ct) ?? new ModrinthSearchResult();
    }

    /// <summary>获取项目的版本列表（按游戏版本与加载器过滤）</summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string gameVersion,
        string loader,
        CancellationToken ct = default)
    {
        var facets = $"[{{\"game_versions\":\"{gameVersion}\"}},{{\"loaders\":\"{loader}\"}}]";
        var url = $"/project/{projectIdOrSlug}/version?facets={Uri.EscapeDataString(facets)}";
        return await GetAsync<List<ModrinthVersion>>(url, ct) ?? [];
    }

    /// <summary>下载 Mod 文件到实例 mods 目录</summary>
    public async Task<string> DownloadModAsync(
        ModrinthFile file,
        string modsDirectory,
        Downloader.DownloadManager downloader,
        CancellationToken ct = default)
    {
        var dest = Path.Combine(modsDirectory, file.Filename);
        var sha1 = file.Hashes.TryGetValue("sha1", out var h) ? h : null;
        var result = await downloader.DownloadFileAsync(file.Url, dest, sha1, group: "mod", ct: ct);
        if (!result.Success)
            throw new InvalidOperationException($"Mod 下载失败: {result.Error}");
        return dest;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public void Dispose() => _http.Dispose();
}
