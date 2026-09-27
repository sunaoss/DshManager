using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 仪表盘首页：展示安装 / 进程状态与实时日志，并提供快捷操作。
/// 由 MainViewModel 以 new HomeView() 创建，DataContext 在本构造函数内自行绑定。
/// </summary>
public partial class HomeView : UserControl
{
    private readonly HomeViewModel _viewModel;

    /// <summary>是否已有一次待执行的滚动回调，用于合并连续快速追加（避免堆叠大量 Dispatcher 回调）。</summary>
    private bool _scrollPending;

    public HomeView()
    {
        InitializeComponent();

        // MainViewModel 以无参方式创建本页，这里自行绑定视图模型
        _viewModel = new HomeViewModel();
        DataContext = _viewModel;

        // 日志集合追加新行时自动滚动到底部（模拟实时终端效果）
        _viewModel.Logs.CollectionChanged += OnLogsCollectionChanged;

        // 首次布局完成后滚动到底部，展示历史日志的末尾
        Loaded += OnLoaded;

        // 页面卸载时退订事件并释放 VM（与 LogView 保持一致）。
        // 【为什么必须有】MainViewModel 每次导航到「首页」都会 new HomeView()，
        // 若此处不释放，会有两处泄漏：
        // 1) HomeViewModel 构造时启动的 2 秒 DispatcherTimer 被 Dispatcher 强引用 → 旧 VM 永不回收；
        // 2) 旧 VM 对静态单例 AppServices.Process/Install 的订阅继续触发 → 订阅数随导航次数线性增长。
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RequestScrollToEnd();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
        _viewModel.Dispose();
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 只在追加行时滚动；清空 / 重置时保持原位，避免视觉跳动
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        // 用户手动关掉「自动滚动」后，不再打断其向上翻看历史
        if (!_viewModel.IsAutoScrollEnabled)
            return;

        RequestScrollToEnd();
    }

    /// <summary>
    /// 请求滚动到底部。
    /// 【为什么要延迟到布局之后】日志集合的 CollectionChanged 是同步触发的，
    /// 此时新行尚未完成布局测量，直接调用 ScrollToEnd() 只能滚到「旧内容的底部」，
    /// 于是日志区看起来永远停在上一行（用户实测：dsh 已「运行中」，日志却还停在「正在启动 dsh…」）。
    /// 用 Dispatcher.BeginInvoke 推迟到 Background 优先级（布局完成）再滚动即可解决。
    /// 另外用 _scrollPending 合并连续快速追加：多行同时到达时只需滚一次，避免堆积回调。
    /// </summary>
    private void RequestScrollToEnd()
    {
        if (_scrollPending)
            return;

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;
            LogScrollViewer.ScrollToEnd();
        }));
    }
}
