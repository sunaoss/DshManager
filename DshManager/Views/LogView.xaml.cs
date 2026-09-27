using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 「日志」页面：实时展示 <see cref="Services.AppLogService"/> 中的统一日志（面板 / dsh）。
/// 由 MainViewModel 以 new LogView() 创建，DataContext 在本构造函数内自行绑定。
/// </summary>
public partial class LogView : UserControl
{
    private readonly LogViewModel _viewModel;

    /// <summary>是否已有一次待执行的滚动回调，用于合并连续快速追加（避免堆叠大量 Dispatcher 回调）。</summary>
    private bool _scrollPending;

    public LogView()
    {
        InitializeComponent();

        // MainViewModel 以无参方式创建本页，这里自行绑定视图模型
        _viewModel = new LogViewModel();
        DataContext = _viewModel;

        // 列表追加新行时自动滚动到底部（模拟实时终端效果）
        _viewModel.Entries.CollectionChanged += OnEntriesCollectionChanged;

        // 首次布局完成后滚动到底部，展示已有日志的末尾
        Loaded += OnLoaded;

        // 页面卸载时退订事件并释放 VM，避免导航重建页面后残留订阅导致泄漏
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RequestScrollToEnd();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        _viewModel.Dispose();
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 只在追加行时滚动；清空 / 重置（筛选切换）时不主动滚，避免视觉跳动
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        // 用户手动关掉「自动滚动」后，新行不再打断其翻看历史
        if (!_viewModel.IsAutoScrollEnabled)
            return;

        RequestScrollToEnd();
    }

    /// <summary>
    /// 请求滚动到底部。
    /// 【为什么要延迟到布局之后】日志集合的 CollectionChanged 是同步触发的，
    /// 此时新行尚未参与布局测量，直接调用 ScrollToEnd() 只能滚到「旧内容的底部」，
    /// 结果永远滞后于最新一行（WPF 经典坑，本项目的已知问题）。
    /// 故用 Dispatcher.BeginInvoke 把滚动推迟到 Background 优先级（布局完成）再执行。
    /// 另外用 _scrollPending 合并连续快速追加：多行同时到达时只需滚一次。
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
