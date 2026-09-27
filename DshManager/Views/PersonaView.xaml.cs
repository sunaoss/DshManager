using System.Windows.Controls;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 人格卡页面：管理 dsh 官方人格预设目录（&lt;DSH_HOME&gt;\.agent-presets\），
/// 支持新建 / 编辑 / 删除 / 另存为，并通过「设为默认」写 agent-presets.default 启用。
/// </summary>
public partial class PersonaView : UserControl
{
    public PersonaView()
    {
        InitializeComponent();
        DataContext = new PersonaViewModel();
    }
}
