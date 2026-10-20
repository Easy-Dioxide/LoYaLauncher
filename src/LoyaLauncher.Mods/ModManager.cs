using System.IO.Compression;
using System.Text.Json;
using LoyaLauncher.Core;

namespace LoyaLauncher.Mods;

/// <summary>
/// 实例 Mod 管理器：扫描 mods 目录、解析 jar 元数据、
/// 启停（.disabled 后缀）、导入本地 jar。
/// </summary>
public sealed class ModManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>扫描实例的 mods 目录</summary>
    public static List<InstalledMod> ScanMods(GameInstance instance)
    {
        var result = new List<InstalledMod>();
        var dir = instance.ModsDirectory;
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.jar*"))
        {
            try
            {
                var info = new FileInfo(file);
                var mod = new InstalledMod
                {
                    FilePath = file,
                    FileName = info.Name,
                    Enabled = !info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    FileSize = info.Length,
                };
                ParseModMetadata(mod);
                result.Add(mod);
            }
            catch { /* 跳过无法读取的文件 */ }
        }
        return result.OrderBy(m => m.DisplayName).ToList();
    }

    /// <summary>
    /// 解析 mod jar 元数据。依次尝试：
    /// 1. fabric.mod.json（Fabric/Quilt）
    /// 2. META-INF/mods.toml（Forge/NeoForge）
    /// 3. neoforge.mods.toml
    /// 4. 回退：从文件名推断
    /// </summary>
    public static void ParseModMetadata(InstalledMod mod)
    {
        try
        {
            using var zip = ZipFile.OpenRead(mod.FilePath);

            var fabricEntry = zip.GetEntry("fabric.mod.json");
            if (fabricEntry is not null)
            {
                using var stream = fabricEntry.Open();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                mod.ModId = root.GetProperty("id").GetString() ?? "";
                mod.DisplayName = root.TryGetProperty("name", out var name) ? name.GetString() ?? mod.ModId : mod.ModId;
                mod.Version = root.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";
                mod.Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                return;
            }

            foreach (var tomlName in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
            {
                var entry = zip.GetEntry(tomlName);
                if (entry is null) continue;
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                var idMatch = System.Text.RegularExpressions.Regex.Match(content, @"modId\s*=\s*""([^""]+)""");
                var nameMatch = System.Text.RegularExpressions.Regex.Match(content, @"displayName\s*=\s*""([^""]+)""");
                var verMatch = System.Text.RegularExpressions.Regex.Match(content, @"version\s*=\s*""([^""]+)""");
                mod.ModId = idMatch.Success ? idMatch.Groups[1].Value : "";
                mod.DisplayName = nameMatch.Success ? nameMatch.Groups[1].Value : mod.ModId;
                mod.Version = verMatch.Success ? verMatch.Groups[1].Value : "";
                return;
            }
        }
        catch { /* 元数据解析失败时回退文件名 */ }

        mod.ModId = Path.GetFileNameWithoutExtension(mod.FileName);
        mod.DisplayName = mod.ModId;
        mod.Version = "";
    }

    /// <summary>启用 mod</summary>
    public static void Enable(string filePath)
    {
        if (!filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return;
        var target = filePath[..^".disabled".Length];
        File.Move(filePath, target, overwrite: true);
    }

    /// <summary>禁用 mod</summary>
    public static void Disable(string filePath)
    {
        if (filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return;
        File.Move(filePath, filePath + ".disabled", overwrite: true);
    }

    /// <summary>删除 mod</summary>
    public static void Delete(string filePath)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    /// <summary>导入本地 mod jar 到实例</summary>
    public static string ImportLocalJar(GameInstance instance, string sourcePath)
    {
        Directory.CreateDirectory(instance.ModsDirectory);
        var dest = Path.Combine(instance.ModsDirectory, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }
}
