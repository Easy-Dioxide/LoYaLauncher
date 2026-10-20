using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Auth;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// 账户管理 ViewModel：离线账户增删改、切换、皮肤设置。
/// </summary>
public sealed partial class AccountsViewModel : ViewModelBase
{
    private readonly AccountManager _accounts;

    public AccountsViewModel(AccountManager accounts)
    {
        _accounts = accounts;
        _accounts.CurrentAccountChanged += Refresh;
    }

    public ObservableCollection<OfflineAccount> Accounts { get; } = [];

    [ObservableProperty]
    private OfflineAccount? _selectedAccount;

    [ObservableProperty]
    private string _newPlayerName = "";

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>当前账户名（主页显示）</summary>
    public string CurrentAccountName => _accounts.CurrentAccount?.PlayerName ?? "无";

    public override void OnNavigatedTo() => Refresh();

    private void Refresh()
    {
        Accounts.Clear();
        foreach (var a in _accounts.Accounts)
            Accounts.Add(a);
        SelectedAccount = _accounts.CurrentAccount;
        OnPropertyChanged(nameof(CurrentAccountName));
    }

    // ======================= 命令 =======================

    /// <summary>添加离线账户</summary>
    [RelayCommand]
    private void AddAccount()
    {
        var name = NewPlayerName.Trim();
        if (name.Length == 0)
        {
            StatusText = Lang("Accounts.Error.EmptyName");
            return;
        }
        if (name.Length > 16)
        {
            StatusText = Lang("Accounts.Error.NameTooLong");
            return;
        }
        var account = _accounts.Add(name);
        NewPlayerName = "";
        StatusText = string.Format(Lang("Accounts.Added"), account.PlayerName);
    }

    /// <summary>删除账户</summary>
    [RelayCommand]
    private void RemoveAccount(OfflineAccount account)
    {
        if (_accounts.Accounts.Count <= 1)
        {
            StatusText = Lang("Accounts.Error.KeepOne");
            return;
        }
        _accounts.Remove(account.Id);
        StatusText = string.Format(Lang("Accounts.Removed"), account.PlayerName);
    }

    /// <summary>切换当前账户</summary>
    [RelayCommand]
    private void UseAccount(OfflineAccount account)
    {
        _accounts.SetCurrent(account.Id);
        SelectedAccount = account;
        StatusText = string.Format(Lang("Accounts.Switched"), account.PlayerName);
    }

    /// <summary>修改用户名</summary>
    [RelayCommand]
    private void RenameAccount(string newName)
    {
        if (SelectedAccount is null || string.IsNullOrWhiteSpace(newName)) return;
        SelectedAccount.PlayerName = newName.Trim();
        _accounts.Update(SelectedAccount);
        Refresh();
    }

    /// <summary>设置皮肤（从本地文件拷贝）</summary>
    [RelayCommand]
    private void SetSkin(string skinFilePath)
    {
        if (SelectedAccount is null) return;
        try
        {
            var skinsDir = System.IO.Path.Combine(Core.LoyaPaths.Config, "skins");
            System.IO.Directory.CreateDirectory(skinsDir);
            var dest = System.IO.Path.Combine(skinsDir, $"{SelectedAccount.Id}.png");
            System.IO.File.Copy(skinFilePath, dest, overwrite: true);
            SelectedAccount.SkinPath = dest;
            _accounts.Update(SelectedAccount);
            StatusText = Lang("Accounts.SkinSet");
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Accounts.SkinFailed")}: {ex.Message}";
        }
    }

    /// <summary>切换皮肤模型（经典/纤细）</summary>
    [RelayCommand]
    private void ToggleSkinModel()
    {
        if (SelectedAccount is null) return;
        SelectedAccount.SkinModel = SelectedAccount.SkinModel == SkinModel.Classic ? SkinModel.Slim : SkinModel.Classic;
        _accounts.Update(SelectedAccount);
    }

    private static string Lang(string key) => Services.LangService.T(key, key);
}
