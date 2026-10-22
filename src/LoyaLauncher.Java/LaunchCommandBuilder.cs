using System.Text;
using System.Text.RegularExpressions;

namespace LoyaLauncher.Java;

/// <summary>构建启动命令所需的上下文</summary>
public sealed class LaunchContext
{
    public required string VersionId { get; set; }
    public required string GameDirectory { get; set; }
    public required string JavaExecutable { get; set; }
    public required string MainClass { get; set; }
    public required string Classpath { get; set; }
    public required string NativesDirectory { get; set; }
    public required string AssetsDirectory { get; set; }
    public required string AssetIndexId { get; set; }
    public required string PlayerName { get; set; }
    public required string PlayerUuid { get; set; }
    public string AccessToken { get; set; } = "offline";
    public string UserType { get; set; } = "legacy";
    public int MaxHeapMb { get; set; } = 4096;
    public string ExtraJvmArgs { get; set; } = "";
    public string LauncherName { get; set; } = "LoyaLauncher";
    public string LauncherVersion { get; set; } = "0.1.0";
}

/// <summary>
/// 启动参数构建器。兼容三种格式：
/// - 1.13+ arguments（含 JVM/游戏条件参数）
/// - 旧版 minecraftArguments 字符串模板
/// - 占位符替换（${auth_player_name} 等）
/// </summary>
public static class LaunchCommandBuilder
{
    /// <summary>构建完整 java 命令参数列表</summary>
    public static List<string> Build(LaunchContext ctx, VersionJson json)
    {
        var jvmArgs = new List<string>();
        var gameArgs = new List<string>();

        // ---------- JVM 参数 ----------
        jvmArgs.Add($"-Xmx{ctx.MaxHeapMb}M");
        jvmArgs.Add($"-Xms256M");

        // 版本 JSON 声明的 JVM 参数（1.19+ 起出现，可能含条件规则）
        if (json.Arguments?.Jvm is { Count: > 0 } jvmElements)
        {
            foreach (var arg in ExpandElements(jvmElements, ctx))
                jvmArgs.Add(arg);
        }
        else
        {
            // 旧版无 JVM 参数声明时，补齐关键参数
            jvmArgs.Add($"-Djava.library.path={ctx.NativesDirectory}");
        }

        jvmArgs.Add($"-Dminecraft.launcher.brand={ctx.LauncherName}");
        jvmArgs.Add($"-Dminecraft.launcher.version={ctx.LauncherVersion}");
        jvmArgs.Add("-Djava.rmi.server.useCodebaseOnly=true");

        // 用户自定义优化参数
        if (!string.IsNullOrWhiteSpace(ctx.ExtraJvmArgs))
            jvmArgs.AddRange(ctx.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        jvmArgs.Add("-cp");
        jvmArgs.Add(ctx.Classpath);

        // ---------- 游戏参数 ----------
        if (json.Arguments?.Game is { Count: > 0 } gameElements)
        {
            gameArgs.AddRange(ExpandElements(gameElements, ctx));
        }
        else if (!string.IsNullOrEmpty(json.MinecraftArguments))
        {
            gameArgs.AddRange(ExpandLegacyArguments(json.MinecraftArguments, ctx));
        }
        else
        {
            // 兜底：直接拼装标准参数
            AppendStandardGameArgs(gameArgs, ctx);
        }

        // 兼容性：确保核心账户参数存在（部分旧版/整合包缺失）
        EnsureArgument(gameArgs, "--username", ctx.PlayerName);
        EnsureArgument(gameArgs, "--uuid", ctx.PlayerUuid);
        EnsureArgument(gameArgs, "--accessToken", ctx.AccessToken);

        return [ctx.JavaExecutable, .. jvmArgs, ctx.MainClass, .. gameArgs];
    }

    /// <summary>展开带 rules 的参数元素（rules 不满足则跳过）</summary>
    private static List<string> ExpandElements(List<ArgumentElement> elements, LaunchContext ctx)
    {
        var result = new List<string>();
        foreach (var el in elements)
        {
            if (el.Rules is { Count: > 0 } && !JavaInstaller.RulesAllow(el.Rules))
                continue;

            if (el.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                // 新版 arguments 每项独立传递，路径值保持裸值
                result.Add(ReplacePlaceholders(el.Value.GetString() ?? "", ctx, p => p));
            }
            else if (el.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in el.Value.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        result.Add(ReplacePlaceholders(item.GetString() ?? "", ctx, p => p));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 展开旧版 minecraftArguments 模板。
    /// 旧版模板是单行文本，替换后还要按空格拆分——路径值含空格时必须
    /// 先用引号界定分组，拆分时再<em>剥离引号</em>输出裸值。
    /// 引号只存在于拆分阶段内部：最终参数列表统一为裸值，
    /// 由 GameProcessRunner 的 ArgumentList 按需自动加引号。
    /// </summary>
    private static List<string> ExpandLegacyArguments(string template, LaunchContext ctx)
    {
        // 部分整合包模板自带引号包裹路径占位符（"--gameDir \"${game_directory}\""）。
        // 这些引号只是分组界定，先归一化去掉，再由 QuoteIfNeeded 统一界定——
        // 否则双层引号叠加会让分组判定失效，含空格路径仍被拆碎。
        var normalized = QuotedPathPlaceholder.Replace(template, m => m.Value.Trim('"'));
        return SplitRespectingQuotes(ReplacePlaceholders(normalized, ctx, QuoteIfNeeded));
    }

    /// <summary>紧贴路径占位符的引号（含占位符自身两侧）</summary>
    private static readonly Regex QuotedPathPlaceholder = new(
        "\"?\\$\\{(game_directory|assets_root|game_assets|natives_directory|classpath)\\}\"?",
        RegexOptions.Compiled);

    /// <summary>含空格路径加引号（仅用于旧版模板拆分阶段的分组界定，不会进入输出）</summary>
    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// 按空格拆分命令行：双引号内的空格不拆分，引号分组本身不进入输出。
    /// </summary>
    private static List<string> SplitRespectingQuotes(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// 占位符替换表（新旧版通用）。
    /// <paramref name="wrapPath"/> 控制路径型值的包裹方式：
    /// 新版 arguments 传 identity（每项独立传递，无需引号）；
    /// 旧版单行模板传 <see cref="QuoteIfNeeded"/>（拆分前界定分组，拆分后剥离）。
    /// 两种情况最终输出都是裸值，引号统一由 ArgumentList 处理。
    /// </summary>
    private static string ReplacePlaceholders(string input, LaunchContext ctx, Func<string, string> wrapPath)
    {
        return input
            .Replace("${auth_player_name}", ctx.PlayerName)
            .Replace("${auth_uuid}", ctx.PlayerUuid)
            .Replace("${auth_access_token}", ctx.AccessToken)
            .Replace("${auth_session}", ctx.AccessToken)
            .Replace("${auth_xuid}", "0")
            .Replace("${user_type}", ctx.UserType)
            .Replace("${version_type}", "release")
            .Replace("${version_name}", ctx.VersionId)
            .Replace("${game_directory}", wrapPath(ctx.GameDirectory))
            .Replace("${assets_root}", wrapPath(ctx.AssetsDirectory))
            .Replace("${assets_index_name}", ctx.AssetIndexId)
            .Replace("${game_assets}", wrapPath(ctx.AssetsDirectory))
            .Replace("${natives_directory}", wrapPath(ctx.NativesDirectory))
            .Replace("${classpath}", ctx.Classpath)
            .Replace("${launcher_name}", ctx.LauncherName)
            .Replace("${launcher_version}", ctx.LauncherVersion)
            .Replace("${clientid}", "LoyaLauncher")
            .Replace("${user_properties}", "{}")
            .Replace("${profile_name}", ctx.PlayerName)
            .Replace("${quickPlayPath}", "")
            .Replace("${quickPlaySingleplayer}", "")
            .Replace("${resolution_width}", "854")
            .Replace("${resolution_height}", "480");
    }

    private static void AppendStandardGameArgs(List<string> args, LaunchContext ctx)
    {
        // 所有值均为裸值：GameProcessRunner 通过 ArgumentList 逐项传递，
        // .NET 会为含空格的项自动加引号；此处预加引号会变成双重引号
        args.Add("--username"); args.Add(ctx.PlayerName);
        args.Add("--version"); args.Add(ctx.VersionId);
        args.Add("--gameDir"); args.Add(ctx.GameDirectory);
        args.Add("--assetsDir"); args.Add(ctx.AssetsDirectory);
        args.Add("--assetIndex"); args.Add(ctx.AssetIndexId);
        args.Add("--uuid"); args.Add(ctx.PlayerUuid);
        args.Add("--accessToken"); args.Add(ctx.AccessToken);
        args.Add("--userType"); args.Add(ctx.UserType);
    }

    /// <summary>确保某参数存在（不存在则追加）</summary>
    private static void EnsureArgument(List<string> args, string name, string value)
    {
        if (args.Contains(name)) return;
        args.Add(name);
        args.Add(value);
    }
}
