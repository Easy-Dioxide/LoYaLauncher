using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace LoyaLauncher.App.Services;

/// <summary>
/// 语言服务：从输出目录 Assets/Lang/{lang}.json 加载资源，
/// 写入 Application 级 ResourceDictionary，XAML 用 {DynamicResource key} 访问。
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

    private static ResourceDictionary? LoadDictionary(string language)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Lang", $"{language}.json");
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);

            var dict = new ResourceDictionary { ["__LoyaLang__"] = true };
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? "";
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }
}
