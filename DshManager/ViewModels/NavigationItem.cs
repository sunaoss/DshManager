using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DshManager.ViewModels;

/// <summary>
/// 导航项：左侧导航栏中的单个条目，包含显示名称、图标与页面创建工厂。
/// </summary>
public sealed partial class NavigationItem : ObservableObject
{
    /// <summary>导航项显示名称</summary>
    public string Name { get; }

    /// <summary>导航项图标（emoji 字符，萌系风格点缀）</summary>
    public string Icon { get; }

    /// <summary>创建对应页面的工厂方法（延迟创建，点击时才实例化视图）</summary>
    public Func<UserControl> CreateView { get; }

    /// <summary>是否被选中（导航按钮高亮依据）</summary>
    [ObservableProperty]
    private bool isSelected;

    public NavigationItem(string name, string icon, Func<UserControl> createView)
    {
        Name = name;
        Icon = icon;
        CreateView = createView;
    }
}
