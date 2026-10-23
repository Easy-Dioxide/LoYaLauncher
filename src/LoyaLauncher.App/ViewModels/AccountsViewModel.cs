using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoyaLauncher.Auth;

namespace LoyaLauncher.App.ViewModels;

/// <summary>
/// 账户管理 ViewModel：离线账户增删改、切换、皮肤设置；
/// 微软正版账户设备码登录（登录后加入列表，由用户手动切换）。
/// </summary>
public sealed partial class AccountsViewModel : ViewModelBase
{
    private readonly AccountManager _accounts;
    private readonly MicrosoftAuthService _msAuth;
    private CancellationTokenSource? _msCts;

    public AccountsViewModel(AccountManager accounts, MicrosoftAuthService msAuth)
    {
        _accounts = accounts;
        _msAuth = msAuth;
        _accounts.CurrentAccountChanged += Refresh;
    }

    public ObservableCollection<GameAccount> Accounts { get; } = [];

    [ObservableProperty]
    private GameAccount? _selectedAccount;

    [ObservableProperty]
    private string _newPlayerName = "";

    [ObservableProperty]
    private string _statusText = "";

    // ======================= 微软登录状态 =======================

    /// <summary>设备码面板是否可见（已取得设备码、等待授权）</summary>
    [ObservableProperty]
    private bool _isMicrosoftLoginActive;

    /// <summary>登录流程进行中（按钮禁用态）</summary>
    [ObservableProperty]
    private bool _isMicrosoftBusy;

    /// <summary>用户需在浏览器输入的验证码</summary>
    [ObservableProperty]
    private string _microsoftUserCode = "";

    /// <summary>验证页面地址</summary>
    [ObservableProperty]
    private string _microsoftVerificationUri = "";

    /// <summary>流程状态文案（等待授权 / 已复制 / 已取消…）</summary>
    [ObservableProperty]
    private string _microsoftStatus = "";

    /// <summary>错误文案（失败时显示，成功/进行中为空）</summary>
    [ObservableProperty]
    private string _microsoftError = "";

    /// <summary>剪贴板写入委托（由 View 挂接，避免 VM 直接依赖 UI 层）</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

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

    // ======================= 离线账户命令 =======================

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
    private void RemoveAccount(GameAccount account)
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
    private void UseAccount(GameAccount account)
    {
        _accounts.SetCurrent(account.Id);
        SelectedAccount = account;
        StatusText = string.Format(Lang("Accounts.Switched"), account.PlayerName);
    }

    /// <summary>修改用户名（仅离线账户）</summary>
    [RelayCommand]
    private void RenameAccount(string newName)
    {
        if (SelectedAccount is not OfflineAccount { } offline || string.IsNullOrWhiteSpace(newName)) return;
        offline.PlayerName = newName.Trim();
        _accounts.Update(offline);
        Refresh();
    }

    /// <summary>设置皮肤（从本地文件拷贝，仅离线账户）</summary>
    [RelayCommand]
    private void SetSkin(string skinFilePath)
    {
        if (SelectedAccount is not OfflineAccount { } offline) return;
        try
        {
            var skinsDir = System.IO.Path.Combine(Core.LoyaPaths.Config, "skins");
            System.IO.Directory.CreateDirectory(skinsDir);
            var dest = System.IO.Path.Combine(skinsDir, $"{offline.Id}.png");
            System.IO.File.Copy(skinFilePath, dest, overwrite: true);
            offline.SkinPath = dest;
            _accounts.Update(offline);
            StatusText = Lang("Accounts.SkinSet");
        }
        catch (Exception ex)
        {
            StatusText = $"{Lang("Accounts.SkinFailed")}: {ex.Message}";
        }
    }

    /// <summary>切换皮肤模型（经典/纤细，仅离线账户）</summary>
    [RelayCommand]
    private void ToggleSkinModel()
    {
        if (SelectedAccount is not OfflineAccount { } offline) return;
        offline.SkinModel = offline.SkinModel == SkinModel.Classic ? SkinModel.Slim : SkinModel.Classic;
        _accounts.Update(offline);
    }

    // ======================= 微软正版登录 =======================

    /// <summary>
    /// 发起微软设备码登录：申请设备码 → 展示验证码面板 → 轮询直至授权完成。
    /// 成功后将账户加入列表（按产品决策不自动切换，由用户手动「使用」）。
    /// </summary>
    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        if (IsMicrosoftBusy) return;

        IsMicrosoftBusy = true;
        MicrosoftError = "";
        _msCts = new CancellationTokenSource();
        try
        {
            var info = await _msAuth.RequestDeviceCodeAsync(_msCts.Token);
            MicrosoftUserCode = info.UserCode;
            MicrosoftVerificationUri = info.VerificationUri;
            MicrosoftStatus = Lang("Accounts.Microsoft.Waiting");
            IsMicrosoftLoginActive = true;

            var account = await _msAuth.PollAndCompleteAsync(info, _msCts.Token);
            _accounts.AddMicrosoft(account);
            IsMicrosoftLoginActive = false;
            MicrosoftStatus = string.Format(Lang("Accounts.Microsoft.Success"), account.PlayerName);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：静默收尾，状态文案由取消命令设置
        }
        catch (MicrosoftAuthException ex)
        {
            IsMicrosoftLoginActive = false;
            MicrosoftError = MicrosoftErrorMessage(ex.Code);
        }
        catch (Exception ex)
        {
            IsMicrosoftLoginActive = false;
            MicrosoftError = $"{Lang("Accounts.Microsoft.Failed")}: {ex.Message}";
        }
        finally
        {
            IsMicrosoftBusy = false;
            _msCts?.Dispose();
            _msCts = null;
        }
    }

    /// <summary>取消等待中的微软登录</summary>
    [RelayCommand]
    private void CancelMicrosoftLogin()
    {
        _msCts?.Cancel();
        IsMicrosoftBusy = false;
        IsMicrosoftLoginActive = false;
        MicrosoftStatus = Lang("Accounts.Microsoft.Canceled");
    }

    /// <summary>复制验证码到剪贴板</summary>
    [RelayCommand]
    private async Task CopyMicrosoftCodeAsync()
    {
        if (CopyToClipboard is null || string.IsNullOrEmpty(MicrosoftUserCode)) return;
        try
        {
            await CopyToClipboard(MicrosoftUserCode);
            MicrosoftStatus = Lang("Accounts.Microsoft.Copied");
        }
        catch (Exception ex)
        {
            MicrosoftError = ex.Message;
        }
    }

    /// <summary>在系统浏览器打开验证页面</summary>
    [RelayCommand]
    private void OpenMicrosoftVerification()
    {
        if (string.IsNullOrEmpty(MicrosoftVerificationUri)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MicrosoftVerificationUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MicrosoftError = $"{Lang("Accounts.Microsoft.OpenFailed")}: {ex.Message}";
        }
    }

    private static string MicrosoftErrorMessage(string code) => Lang(code switch
    {
        "declined" => "Accounts.Microsoft.Declined",
        "expired" => "Accounts.Microsoft.Expired",
        "no_xbox" => "Accounts.Microsoft.NoXbox",
        "child_account" => "Accounts.Microsoft.Child",
        "no_game" => "Accounts.Microsoft.NoGame",
        _ => "Accounts.Microsoft.Failed",
    });

    private static string Lang(string key) => Services.LangService.T(key, key);
}
