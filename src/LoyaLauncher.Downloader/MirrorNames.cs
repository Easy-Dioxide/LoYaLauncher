namespace LoyaLauncher.Downloader;

/// <summary>
/// 下载源主机 → 友好名称（清单 §9 P1 下载中心展示「源名称」）。
/// 未识别的主机返回主机名本身，绝不返回空。
/// </summary>
public static class MirrorNames
{
    /// <summary>取主机友好名（传入 URL 或主机名均可）</summary>
    public static string Friendly(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return "未知源";
        var host = urlOrHost;
        try
        {
            if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri))
                host = uri.Host;
        }
        catch
        {
            // 无法解析：按原串匹配
        }

        if (host.EndsWith("mojang.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("minecraft.net", StringComparison.OrdinalIgnoreCase))
            return "Mojang 官方";
        if (host.EndsWith("bangbang93.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("bmclapi", StringComparison.OrdinalIgnoreCase))
            return "BMCLAPI";
        if (host.EndsWith("modrinth.com", StringComparison.OrdinalIgnoreCase))
            return "Modrinth";
        if (host.EndsWith("adoptium.net", StringComparison.OrdinalIgnoreCase))
            return "Adoptium";
        if (host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return "GitHub";
        return host;
    }
}
