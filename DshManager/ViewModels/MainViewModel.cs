using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Controls;
using DshManager.Views;

namespace DshManager.ViewModels;

/// <summary>
/// 主窗口视图模型：
/// 1) 维护左侧 9 个导航项列表；
/// 2) 通过 CurrentView 属性驱动右侧内容区（ContentControl）切换页面。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>当前显示的内容视图（UserControl），由 Navigate 命令更新</summary>
    [ObservableProperty]
    private UserControl? currentView;

    /// <summary>当前选中的导航项（驱动导航按钮高亮）</summary>
    [ObservableProperty]
    private NavigationItem? selectedItem;

    /// <summary>左侧导航项列表（共 9 个页面）</summary>
    public IReadOnlyList<NavigationItem> NavItems { get; }

    public MainViewModel(SettingsViewModel? settingsViewModel = null)
    {
        // 集中定义 9 个导航项：名称、占位图标、对应视图的创建工厂
        NavItems =
        [
            new("首页", "🏠", () => new HomeView()),
            new("配置", "⚙️", () => new ConfigView()),
            new("插件", "🧩", () => new PluginsView()),
            new("记忆库", "🧠", () => new MemoryView()),
            new("人格", "🎭", () => new PersonaView()),
            new("文件", "📁", () => new FilesView()),
            new("聊天", "💬", () => new ChatView()),
            // 设置页复用共享 SettingsViewModel，保证设置跨页面导航保留并即时生效
            new("设置", "🛠️", () =>
            {
                var view = new SettingsView();
                if (settingsViewModel is not null)
                {
                    view.DataContext = settingsViewModel;
                }
                return view;
            }),
            // 日志页放在最后，用于实时查看面板与 dsh 的统一日志
            new("日志", "📜", () => new LogView()),
        ];

        // 启动时默认选中首页
        Navigate(NavItems[0]);
    }

    /// <summary>
    /// 切换当前页面：更新选中态后创建对应视图并放入内容区。
    /// 由 CommunityToolkit.Mvvm 的源码生成器自动生成 NavigateCommand。
    /// </summary>
    [RelayCommand]
    private void Navigate(NavigationItem item)
    {
        if (SelectedItem is not null)
        {
            SelectedItem.IsSelected = false;
        }

        item.IsSelected = true;
        SelectedItem = item;
        CurrentView = item.CreateView();
    }
}
