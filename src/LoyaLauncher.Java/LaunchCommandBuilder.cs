using System.Text;

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
                result.Add(ReplacePlaceholders(el.Value.GetString() ?? "", ctx));
            }
            else if (el.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in el.Value.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        result.Add(ReplacePlaceholders(item.GetString() ?? "", ctx));
                }
            }
        }
        return result;
    }

    /// <summary>展开旧版 minecraftArguments 模板</summary>
    private static List<string> ExpandLegacyArguments(string template, LaunchContext ctx)
    {
        // 占位符可能展开为带引号的路径（如 "C:\Users\John Doe\..."），
        // 拆分时必须感知引号，否则路径会在空格处被拆碎。
        return SplitRespectingQuotes(ReplacePlaceholders(template, ctx));
    }

    /// <summary>按空格拆分命令行，但保持双引号内的内容完整（引号本身保留）</summary>
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
                current.Append(c);
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

    /// <summary>占位符替换表（新旧版通用）</summary>
    private static string ReplacePlaceholders(string input, LaunchContext ctx)
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
            .Replace("${game_directory}", Quote(ctx.GameDirectory))
            .Replace("${assets_root}", Quote(ctx.AssetsDirectory))
            .Replace("${assets_index_name}", ctx.AssetIndexId)
            .Replace("${game_assets}", Quote(ctx.AssetsDirectory))
            .Replace("${natives_directory}", Quote(ctx.NativesDirectory))
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
        args.Add("--username"); args.Add(ctx.PlayerName);
        args.Add("--version"); args.Add(ctx.VersionId);
        args.Add("--gameDir"); args.Add(Quote(ctx.GameDirectory));
        args.Add("--assetsDir"); args.Add(Quote(ctx.AssetsDirectory));
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

    /// <summary>含空格路径加引号（Windows 常见）</summary>
    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
