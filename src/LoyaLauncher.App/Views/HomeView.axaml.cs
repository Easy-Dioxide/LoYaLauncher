using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using LoyaLauncher.App.ViewModels;
using LoyaLauncher.Core;

namespace LoyaLauncher.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>点击实例卡片 = 选中该实例</summary>
    private void InstanceCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: GameInstance instance } &&
            DataContext is HomeViewModel vm)
        {
            vm.SelectInstanceCommand.Execute(instance);
        }
    }
}
