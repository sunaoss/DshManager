using System.Windows.Controls;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 记忆库页面（三个分区）：
/// A) 全局记忆 —— &lt;DSH_HOME&gt;\AGENTS.md（"##" 小节 + 条目）；
/// B) 项目级记忆 —— 项目根到工作目录的 AGENTS.md / CLAUDE.md 指令链与记忆预算；
/// C) 扩展记忆（大容量）—— dsh-long-memory 插件的安装 / 卸载、MEMORY.md 编辑与容量设置。
/// </summary>
public partial class MemoryView : UserControl
{
    public MemoryView()
    {
        InitializeComponent();
        DataContext = new MemoryViewModel();
    }
}
