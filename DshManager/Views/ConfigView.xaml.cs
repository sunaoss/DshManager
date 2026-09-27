using System.Windows.Controls;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 配置页面：按 6 个分组可视化编辑 DSH 官方 settings.yaml（33 项官方参数）。
/// 无参构造函数保留，由 MainViewModel 通过 new ConfigView() 创建。
/// </summary>
public partial class ConfigView : UserControl
{
    public ConfigView()
    {
        InitializeComponent();

        // 页面自管理视图模型：加载、保存、重置均由 ConfigViewModel 驱动
        DataContext = new ConfigViewModel();
    }
}
