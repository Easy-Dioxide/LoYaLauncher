namespace LoyaLauncher.Downloader;

/// <summary>下载源类型</summary>
public enum MirrorKind
{
    /// <summary>BMCLAPI 国内镜像（bangbang93）</summary>
    Bmclapi,
    /// <summary>Mojang 官方源</summary>
    Official,
}

/// <summary>
/// 下载源 URL 映射。官方 Mojang 与 BMCLAPI 镜像的路径结构不同，
/// 由本类统一封装，上层无需关心差异。
/// </summary>
public static class MirrorSource
{
    public static MirrorUrlSet Create(MirrorKind kind) => kind switch
    {
        MirrorKind.Bmclapi => new MirrorUrlSet(
            VersionManifest: "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            VersionJson: id => $"https://bmclapi2.bangbang93.com/version/{id}/json",
            ClientJar: sha1 => $"https://bmclapi2.bangbang93.com/objects/{sha1}",
            Library: path => $"https://bmclapi2.bangbang93.com/maven/{path}",
            LibraryObject: path => $"https://bmclapi2.bangbang93.com/objects/{path}",
            AssetsIndex: id => $"https://bmclapi2.bangbang93.com/assets/indexes/{id}.json",
            AssetObject: hash => $"https://bmclapi2.bangbang93.com/assets/objects/{hash[..2]}/{hash}"),
        _ => new MirrorUrlSet(
            VersionManifest: "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
            // 官方版本 JSON 地址由 manifest 直接给出（piston-meta），此处为兜底
            VersionJson: id => $"https://piston-meta.mojang.com/v1/packages/{id}",
            ClientJar: sha1 => $"https://piston-data.mojang.com/v1/objects/{sha1}",
            Library: path => $"https://libraries.minecraft.net/{path}",
            LibraryObject: path => $"https://libraries.minecraft.net/{path}",
            AssetsIndex: id => $"https://piston-meta.mojang.com/v1/packages/{id}",
            AssetObject: hash => $"https://resources.download.minecraft.net/{hash[..2]}/{hash}"),
    };

    /// <summary>按配置的镜像模式返回源列表（优先级从高到低）</summary>
    public static IReadOnlyList<MirrorUrlSet> ResolveChain(Core.MirrorMode mode) => mode switch
    {
        Core.MirrorMode.OfficialOnly => [Create(MirrorKind.Official)],
        Core.MirrorMode.BmclapiOnly => [Create(MirrorKind.Bmclapi)],
        _ => [Create(MirrorKind.Bmclapi), Create(MirrorKind.Official)],
    };
}

/// <summary>一组下载源 URL 构建委托</summary>
public sealed record MirrorUrlSet(
    string VersionManifest,
    Func<string, string> VersionJson,
    Func<string, string> ClientJar,
    Func<string, string> Library,
    Func<string, string> LibraryObject,
    Func<string, string> AssetsIndex,
    Func<string, string> AssetObject);
