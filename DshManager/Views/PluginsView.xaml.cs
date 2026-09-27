using System.Windows.Controls;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 插件管理页：列出 / 安装 / 卸载 dsh profile 插件。
/// </summary>
public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        DataContext = new PluginsViewModel();
    }
}
