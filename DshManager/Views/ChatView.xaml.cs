using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DshManager.ViewModels;

namespace DshManager.Views;

/// <summary>
/// 鲸鱼娘聊天页面：对话区 / 表情包面板 / 输入行与快捷问题。
/// 由 MainViewModel 以 new ChatView() 创建，构造函数内自行绑定视图模型；
/// 负责两件界面层的事：消息变化时自动滚动到底部、输入框回车直接发送。
/// </summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();

        // 无参构造里自行绑定视图模型（与 HomeView 等页面保持一致）
        var viewModel = new ChatViewModel();
        DataContext = viewModel;

        // 消息追加 / 正在输入状态变化时，把对话区滚动到最新一条
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 首次布局完成后滚到底部，让开场白完整可见
        Loaded += (_, _) => ChatScrollViewer.ScrollToEnd();
    }

    /// <summary>消息集合变化时延迟到布局完成后再滚动（立即滚动拿到的还是旧尺寸）。</summary>
    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollChatToBottom();
        }
    }

    /// <summary>「正在输入…」气泡出现 / 消失时也滚动一次，保证输入气泡可见。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsTyping))
        {
            ScrollChatToBottom();
        }
    }

    /// <summary>调度到布局完成后滚动到底部（模拟聊天软件跟随最新消息的效果）。</summary>
    private void ScrollChatToBottom()
    {
        // Loaded 优先级在布局之后执行，确保 ScrollToEnd 拿到的是渲染后的高度
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            ChatScrollViewer.ScrollToEnd());
    }

    /// <summary>输入框回车直接发送（TextBox 默认不换行，Enter 全部走发送）。</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ChatViewModel viewModel)
        {
            return;
        }

        if (viewModel.SendCommand.CanExecute(null))
        {
            viewModel.SendCommand.Execute(null);
        }

        // 标记已处理，避免回车事件继续冒泡触发页面级快捷键
        e.Handled = true;
    }
}
