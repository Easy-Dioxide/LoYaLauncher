using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace LoyaLauncher.App.Services;

/// <summary>
/// 语言服务：语言资源以 EmbeddedResource 内置于程序集（单文件 exe 的唯一来源），
/// 同时支持用户在 exe 旁边的 Assets/Lang/{lang}.json 放置文件覆盖（自制语言包）。
/// 资源写入 Application 级 ResourceDictionary，XAML 用 {DynamicResource key} 访问。
/// 切换语言时替换字典，无需重启。
/// </summary>
public static class LangService
{
    private const string DefaultLanguage = "zh-CN";

    /// <summary>当前语言</summary>
    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary>应用语言（幂等）</summary>
    public static void ApplyLanguage(string? language)
    {
        language ??= DefaultLanguage;
        var dict = LoadDictionary(language) ?? LoadDictionary(DefaultLanguage);
        if (dict is null) return;

        CurrentLanguage = language;

        if (Application.Current is { } app)
        {
            // 移除旧语言字典，插入新字典
            var existing = app.Resources.MergedDictionaries
                .Where(d => d is ResourceDictionary rd && rd.ContainsKey("__LoyaLang__"))
                .ToList();
            foreach (var d in existing)
                app.Resources.MergedDictionaries.Remove(d);

            app.Resources.MergedDictionaries.Insert(0, dict);
        }
    }

    /// <summary>获取本地化文本（代码侧使用）</summary>
    public static string T(string key, string fallback = "")
    {
        if (Application.Current?.TryFindResource(key, out var value) == true && value is string s)
            return s;
        return fallback;
    }

    /// <summary>exe 所在目录（单文件模式下 BaseDirectory 指向临时解压目录，不可信）</summary>
    private static string AppDirectory =>
        Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName)
        ?? AppContext.BaseDirectory;

    private static ResourceDictionary? LoadDictionary(string language)
    {
        try
        {
            var dict = new ResourceDictionary { ["__LoyaLang__"] = true };
            var found = false;

            // 1. 用户覆盖：exe 旁边的 Assets/Lang/{lang}.json（自制语言包优先）
            var overridePath = Path.Combine(AppDirectory, "Assets", "Lang", $"{language}.json");
            if (File.Exists(overridePath))
            {
                using var fs = File.OpenRead(overridePath);
                LoadInto(dict, fs);
                found = true;
            }
            else
            {
                // 2. 内置资源（嵌入程序集，单文件 exe 的唯一来源）
                var resName = $"LoyaLauncher.App.Assets.Lang.{language}.json";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
                if (stream is not null)
                {
                    LoadInto(dict, stream);
                    found = true;
                }
            }

            return found ? dict : null;
        }
        catch
        {
            return null;
        }
    }

    private static void LoadInto(ResourceDictionary dict, Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString() ?? "";
        }
    }
}
