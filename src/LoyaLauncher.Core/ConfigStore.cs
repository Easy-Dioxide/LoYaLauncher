using System.Text.Json;

namespace LoyaLauncher.Core;

/// <summary>
/// JSON 配置持久化。读写 .loya/config/config.json，采用原子写入（临时文件 + 替换）。
/// </summary>
public sealed class ConfigStore
{
    private static readonly Lazy<ConfigStore> _instance = new(() => new ConfigStore());
    public static ConfigStore Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private AppConfig? _config;

    private ConfigStore()
    {
        _filePath = Path.Combine(LoyaPaths.Config, "config.json");
    }

    /// <summary>测试/多配置场景：在指定目录构造独立实例（不触碰全局单例）</summary>
    public ConfigStore(string configDirectory)
    {
        _filePath = Path.Combine(configDirectory, "config.json");
    }

    /// <summary>当前配置（首次访问时从磁盘加载，损坏则回退默认值）</summary>
    public AppConfig Config => _config ??= LoadFromDisk();

    /// <summary>从磁盘重新加载配置</summary>
    public AppConfig LoadFromDisk()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            // 配置损坏时回退默认值，保证启动器始终能启动
        }
        return _config ??= new AppConfig();
    }

    /// <summary>保存配置到磁盘（原子写入）</summary>
    public void Save()
    {
        var cfg = _config ??= new AppConfig();
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
