using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 聊天消息模型：标记发言方、正文、表情包路径（绝对路径，可为空）与时间文本。
/// </summary>
public sealed class ChatMessage
{
    /// <summary>是否用户消息（false = 鲸鱼娘助手消息）。</summary>
    public bool IsUser { get; set; }

    /// <summary>消息正文。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>表情包绝对路径；为空时界面退化为纯文字气泡。</summary>
    public string? StickerPath { get; set; }

    /// <summary>发送时间文本（HH:mm）。</summary>
    public string TimeText { get; set; } = string.Empty;
}

/// <summary>
/// 鲸鱼娘聊天视图模型：
/// 维护消息集合与输入框，提供「发送 / 点表情包 / 快捷问题」三条命令；
/// 发送后先追加用户消息，置 IsTyping，延迟约 600ms 再取 WhaleChatService 回复。
/// 命令均在 UI 线程执行：async/await 在 UI 上下文捕获 SynchronizationContext，
/// Task.Delay 之后自动回到 UI 线程，无需额外 Dispatcher 调度。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly WhaleChatService _chat;
    private readonly StickerService _stickers;

    /// <summary>聊天消息集合（用户与助手消息按时间顺序追加）。</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>输入框内容；变化时刷新发送命令可用性。</summary>
    [ObservableProperty]
    private string input = string.Empty;

    /// <summary>表情包文件路径集合（用户目录优先 + 内置目录，由 StickerService.ListAll 提供）。</summary>
    public ObservableCollection<string> Stickers { get; } = [];

    /// <summary>表情包操作结果提示（添加/删除后的中文反馈，界面用一行浅色文字展示）。</summary>
    [ObservableProperty]
    private string stickerStatus = string.Empty;

    /// <summary>快捷问题列表（点击后走同一回复流程）。</summary>
    public IReadOnlyList<string> QuickQuestions { get; } =
    [
        "这个软件是干嘛的？",
        "dsh 是什么？",
        "怎么启动 dsh？",
        "怎么换壁纸？",
        "什么是记忆库？",
        "怎么卸载 dsh？",
    ];

    /// <summary>鲸鱼娘是否「正在输入」（控制界面打字气泡显示）。</summary>
    [ObservableProperty]
    private bool isTyping;

    /// <summary>页面默认构造：使用内置知识库服务与默认表情服务，并发送开场白。</summary>
    public ChatViewModel() : this(new WhaleChatService(), new StickerService())
    {
    }

    /// <summary>注入聊天服务（便于测试）；表情服务可选（缺省用默认目录实现）。构造时加载表情并发送娘化开场白。</summary>
    public ChatViewModel(WhaleChatService chat, StickerService? stickers = null)
    {
        _chat = chat;
        _stickers = stickers ?? new StickerService();
        LoadStickers();

        // 开场白：带傲娇表情（若图片缺失会自动退化为文字气泡）
        AddAssistantMessage(
            "欢迎鱼片～ 窝是 DeepSeek 娘，dsh 的问题随便问，不许说窝胖哦！(￣▽￣)",
            "Assets/Stickers/tsundere.png");
    }

    /// <summary>输入变化时刷新发送命令（CanExecute 由 Input 非空驱动）。</summary>
    partial void OnInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    // ==================== 命令 ====================

    /// <summary>发送消息：追加用户消息 → 模拟打字延迟 → 取鲸鱼娘回复。</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 追加用户消息并清空输入框（清空后发送按钮自动禁用）
        Messages.Add(new ChatMessage { IsUser = true, Text = text, TimeText = NowTime() });
        Input = string.Empty;

        // 模拟「正在输入」，延迟后取回复（await 自动回到 UI 线程）
        IsTyping = true;
        await Task.Delay(600);
        IsTyping = false;

        var reply = _chat.Reply(text);
        AddAssistantMessage(reply.Text, reply.StickerPath);
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Input);

    /// <summary>点击表情包：直接追加一张鲸鱼娘表情消息（配一句简短台词）。</summary>
    [RelayCommand]
    private void SendSticker(string stickerPath)
    {
        if (string.IsNullOrWhiteSpace(stickerPath))
        {
            return;
        }

        AddAssistantMessage("（贴贴～这张送给鱼片！）", stickerPath);
    }

    /// <summary>点击快捷问题：回填输入框并走同一回复流程。</summary>
    [RelayCommand]
    private async Task AskAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        // 回填输入框（可让用户看到自己问了什么），再走统一发送流程
        Input = question;
        await SendAsync();
    }

    /// <summary>「＋ 添加表情」：多选本地图片 → 逐个复制进用户表情目录 → 刷新列表并提示结果。</summary>
    [RelayCommand]
    private void AddStickers()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要添加的表情图片（可多选）",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var successCount = 0;
        string? firstError = null;
        foreach (var file in dialog.FileNames)
        {
            if (_stickers.AddSticker(file, out var message) is not null)
            {
                successCount++;
            }
            else
            {
                // 只保留第一条失败原因，避免提示文字过长
                firstError ??= message;
            }
        }

        ReloadStickers();

        var failedCount = dialog.FileNames.Length - successCount;
        StickerStatus = failedCount == 0
            ? $"已添加 {successCount} 张表情～"
            : $"成功 {successCount} 张，{failedCount} 张失败：{firstError}";
    }

    /// <summary>「📂 表情包目录」：用资源管理器打开用户表情目录，方便用户自行放图。</summary>
    [RelayCommand]
    private void OpenStickerDirectory() => _stickers.OpenUserDirectory();

    /// <summary>右键菜单「删除表情」：删除用户目录下的表情；内置表情会被拒绝并给出中文提示。</summary>
    [RelayCommand]
    private void DeleteSticker(string? stickerPath)
    {
        if (string.IsNullOrWhiteSpace(stickerPath))
        {
            return;
        }

        if (_stickers.DeleteSticker(stickerPath, out var message))
        {
            ReloadStickers();
        }

        StickerStatus = message;
    }

    // ==================== 内部工具 ====================

    /// <summary>追加一条助手消息（表情路径解析失败时传 null，界面显示文字气泡）。</summary>
    private void AddAssistantMessage(string text, string? stickerRelative)
    {
        Messages.Add(new ChatMessage
        {
            IsUser = false,
            Text = text,
            StickerPath = ResolveSticker(stickerRelative),
            TimeText = NowTime(),
        });
    }

    /// <summary>清空后重新加载表情列表（添加/删除后调用，保证界面与磁盘一致）。</summary>
    private void ReloadStickers()
    {
        Stickers.Clear();
        LoadStickers();
    }

    /// <summary>通过 StickerService 加载全部表情（用户目录优先 + 内置目录）；失败时静默留空，聊天不受影响。</summary>
    private void LoadStickers()
    {
        try
        {
            foreach (var file in _stickers.ListAll())
            {
                Stickers.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 扫描失败不阻塞聊天，表情面板留空即可
        }
    }

    /// <summary>把表情包相对路径解析为程序目录下的绝对路径；文件不存在时返回 null（降级为文字气泡）。</summary>
    private static string? ResolveSticker(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var full = Path.Combine(AppContext.BaseDirectory, relative);
        return File.Exists(full) ? full : null;
    }

    /// <summary>当前时间文本（HH:mm）。</summary>
    private static string NowTime() => DateTime.Now.ToString("HH:mm");
}
