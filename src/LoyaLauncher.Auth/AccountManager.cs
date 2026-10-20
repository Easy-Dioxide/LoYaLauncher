using System.Text.Json;
using LoyaLauncher.Core;

namespace LoyaLauncher.Auth;

/// <summary>
/// 账户管理器：JSON 持久化（.loya/config/accounts.json），
/// 维护当前选中账户。二期将在此扩展微软 OAuth 与外置登录。
/// </summary>
public sealed class AccountManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private List<OfflineAccount> _accounts = [];
    private string _currentId = "";

    public AccountManager()
    {
        _filePath = Path.Combine(LoyaPaths.Config, "accounts.json");
        Load();
    }

    /// <summary>全部账户</summary>
    public IReadOnlyList<OfflineAccount> Accounts => _accounts;

    /// <summary>当前账户（无账户时为 null）</summary>
    public OfflineAccount? CurrentAccount => _accounts.FirstOrDefault(a => a.Id == _currentId);

    /// <summary>当前账户变化事件</summary>
    public event Action? CurrentAccountChanged;

    /// <summary>从磁盘加载账户列表</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var data = JsonSerializer.Deserialize<AccountStore>(File.ReadAllText(_filePath), JsonOpts);
                if (data is not null)
                {
                    _accounts = data.Accounts;
                    _currentId = data.CurrentId;
                }
            }
        }
        catch { /* 损坏时使用空列表 */ }

        // 首次使用：自动创建一个默认账户，保证开箱即用
        if (_accounts.Count == 0)
        {
            var defaultAccount = new OfflineAccount { PlayerName = Environment.UserName is { Length: > 0 } n ? n : "Steve" };
            _accounts.Add(defaultAccount);
            _currentId = defaultAccount.Id;
            Save();
        }
    }

    /// <summary>保存到磁盘</summary>
    public void Save()
    {
        var data = new AccountStore { Accounts = _accounts, CurrentId = _currentId };
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data, JsonOpts));
    }

    /// <summary>添加账户</summary>
    public OfflineAccount Add(string playerName, string? skinPath = null, SkinModel skinModel = SkinModel.Classic)
    {
        var account = new OfflineAccount
        {
            PlayerName = playerName,
            SkinPath = skinPath,
            SkinModel = skinModel,
        };
        _accounts.Add(account);
        _currentId = account.Id;
        Save();
        CurrentAccountChanged?.Invoke();
        return account;
    }

    /// <summary>删除账户</summary>
    public void Remove(string accountId)
    {
        _accounts.RemoveAll(a => a.Id == accountId);
        if (_currentId == accountId)
            _currentId = _accounts.FirstOrDefault()?.Id ?? "";
        Save();
        CurrentAccountChanged?.Invoke();
    }

    /// <summary>设置当前账户</summary>
    public void SetCurrent(string accountId)
    {
        if (_accounts.Any(a => a.Id == accountId))
        {
            _currentId = accountId;
            Save();
            CurrentAccountChanged?.Invoke();
        }
    }

    /// <summary>更新账户信息（改名/换肤）</summary>
    public void Update(OfflineAccount account)
    {
        var idx = _accounts.FindIndex(a => a.Id == account.Id);
        if (idx >= 0)
        {
            _accounts[idx] = account;
            Save();
            CurrentAccountChanged?.Invoke();
        }
    }

    private sealed class AccountStore
    {
        public List<OfflineAccount> Accounts { get; set; } = [];
        public string CurrentId { get; set; } = "";
    }
}
