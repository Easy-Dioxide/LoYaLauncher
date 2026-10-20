using CommunityToolkit.Mvvm.ComponentModel;

namespace LoyaLauncher.App.ViewModels;

/// <summary>ViewModel 基类：导航生命周期钩子</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>页面切入时调用（数据刷新）</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>页面切出时调用（暂停定时器等）</summary>
    public virtual void OnNavigatedFrom() { }
}
