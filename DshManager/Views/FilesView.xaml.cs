using System.Windows.Controls;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 文件页面：展示 dsh 相关文件清单，支持打开位置与一键卸载。
/// </summary>
public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
        // 页面自带独立 ViewModel（每次进入页面重新扫描 dsh 相关文件）
        DataContext = new FilesViewModel();
    }
}
