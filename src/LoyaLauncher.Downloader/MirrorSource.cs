namespace LoyaLauncher.Downloader;

/// <summary>下载源类型</summary>
public enum MirrorKind
{
    /// <summary>Mojang 官方源</summary>
    Official,
    /// <summary>BMCLAPI 国内镜像（bangbang93）</summary>
    Bmclapi,
    /// <summary>自定义源：任意兼容 BMCLAPI 路径规范的镜像（含自建/内网镜像）</summary>
    Custom,
}

/// <summary>
/// 下载源 URL 映射。官方 Mojang 与 BMCLAPI 系镜像的路径结构不同，
/// 由本类统一封装，上层无需关心差异。
/// </summary>
public static class MirrorSource
{
    public static MirrorUrlSet Create(MirrorKind kind, string? customBaseUrl = null) => kind switch
    {
        MirrorKind.Bmclapi => new MirrorUrlSet(
            VersionManifest: "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            VersionJson: id => $"https://bmclapi2.bangbang93.com/version/{id}/json",
            // 客户端 jar 必须走 /version/{id}/client：/objects/{sha1} 对老版本返回 404
            ClientJar: (id, _) => $"https://bmclapi2.bangbang93.com/version/{id}/client",
            Library: path => $"https://bmclapi2.bangbang93.com/maven/{path}",
            AssetsIndex: id => $"https://bmclapi2.bangbang93.com/assets/indexes/{id}.json",
            // 资源对象在 /objects/{2}/{hash}（/assets/objects/ 路径不存在）
            AssetObject: hash => $"https://bmclapi2.bangbang93.com/objects/{hash[..2]}/{hash}"),
        MirrorKind.Custom => new MirrorUrlSet(
            VersionManifest: $"{TrimBase(customBaseUrl)}/mc/game/version_manifest_v2.json",
            VersionJson: id => $"{TrimBase(customBaseUrl)}/version/{id}/json",
            ClientJar: (id, _) => $"{TrimBase(customBaseUrl)}/version/{id}/client",
            Library: path => $"{TrimBase(customBaseUrl)}/maven/{path}",
            AssetsIndex: id => $"{TrimBase(customBaseUrl)}/assets/indexes/{id}.json",
            AssetObject: hash => $"{TrimBase(customBaseUrl)}/objects/{hash[..2]}/{hash}"),
        _ => new MirrorUrlSet(
            VersionManifest: "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
            // 官方版本 JSON 地址由 manifest 直接给出（piston-meta），此处为兜底
            VersionJson: id => $"https://piston-meta.mojang.com/v1/packages/{id}",
            ClientJar: (_, sha1) => $"https://piston-data.mojang.com/v1/objects/{sha1}/client.jar",
            Library: path => $"https://libraries.minecraft.net/{path}",
            AssetsIndex: id => $"https://piston-meta.mojang.com/v1/packages/{id}",
            AssetObject: hash => $"https://resources.download.minecraft.net/{hash[..2]}/{hash}"),
    };

    /// <summary>按配置的镜像模式返回源列表（优先级从高到低）</summary>
    public static IReadOnlyList<MirrorUrlSet> ResolveChain(Core.MirrorMode mode, string? customBaseUrl = null) => mode switch
    {
        Core.MirrorMode.OfficialOnly => [Create(MirrorKind.Official)],
        Core.MirrorMode.BmclapiOnly => [Create(MirrorKind.Bmclapi)],
        Core.MirrorMode.CustomOnly => [Create(MirrorKind.Custom, customBaseUrl)],
        Core.MirrorMode.CustomFirst =>
            [Create(MirrorKind.Custom, customBaseUrl), Create(MirrorKind.Bmclapi), Create(MirrorKind.Official)],
        _ => [Create(MirrorKind.Bmclapi), Create(MirrorKind.Official)],
    };

    private static string TrimBase(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("未配置自定义下载源地址");
        return url.TrimEnd('/');
    }
}

/// <summary>一组下载源 URL 构建委托</summary>
/// <param name="ClientJar">(versionId, sha1) → 客户端 jar URL。BMCLAPI 系走 /version/{id}/client；官方走 piston-data objects 路径。</param>
public sealed record MirrorUrlSet(
    string VersionManifest,
    Func<string, string> VersionJson,
    Func<string, string, string> ClientJar,
    Func<string, string> Library,
    Func<string, string> AssetsIndex,
    Func<string, string> AssetObject);
