using System.Windows;
using System.Windows.Controls;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 设置页面。
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // 每次显示设置页时同步一次「dsh 安装位置（锁定）」：
        // 首页扫描 / 搬运也会更新锁定位置，本页缓存的副本可能已过期，重新读取可保证展示与配置一致。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.RefreshInstallLocationInfo();
    }
}
