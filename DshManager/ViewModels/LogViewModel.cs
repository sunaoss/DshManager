using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 「日志」页视图模型：把 <see cref="AppLogService"/> 的统一日志实时呈现到界面。
/// - 构造时灌入历史快照，并订阅 EntryAdded（后台线程触发 → 调度到 UI 线程后追加）；
/// - 支持来源过滤（全部 / 面板 / dsh），过滤即刷新本地列表，不做任何联网操作；
/// - 页面卸载时由视图调用 <see cref="Dispose"/> 退订，避免长期持有导致内存泄漏
///   （导航会重建页面，不退订则每次进入日志页都会残留一个订阅者）。
/// </summary>
public partial class LogViewModel : ObservableObject, IDisposable
{
    /// <summary>来源过滤的「全部」选项文本。</summary>
    public const string AllSourcesFilter = "全部";

    private readonly AppLogService _service = AppLogService.Instance;
    private bool _disposed;

    /// <summary>日志区是否自动滚动到底部（默认开启；用户向上翻看历史时可关闭）。</summary>
    [ObservableProperty]
    private bool isAutoScrollEnabled = true;

    /// <summary>当前来源过滤项（全部 / 面板 / dsh）。</summary>
    [ObservableProperty]
    private string selectedSourceFilter = AllSourcesFilter;

    /// <summary>状态行文本：条目数 + dsh 日志文件路径。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>界面展示的日志集合（已按来源过滤，最多保留 <see cref="AppLogService.MaxEntries"/> 条）。</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>来源过滤下拉的可选项。</summary>
    public IReadOnlyList<string> SourceFilters { get; } = [AllSourcesFilter, "面板", "dsh"];

    /// <summary>dsh 输出重定向的日志文件路径（与进程服务保持同一处定义）。</summary>
    public string LogFilePath { get; } = DshProcessService.DefaultLogFilePath;

    /// <summary>dsh 日志文件所在目录（「打开日志目录」按钮的目标）。</summary>
    public string LogDirectory { get; } = Path.GetDirectoryName(DshProcessService.DefaultLogFilePath) ?? string.Empty;

    public LogViewModel()
    {
        // 先订阅再灌入历史：避免两者之间新产生的日志被漏掉
        _service.EntryAdded += OnEntryAdded;
        // 初始展示：把服务缓冲的历史日志按当前过滤条件灌入
        Reload();
    }

    /// <summary>过滤条件变化时重刷列表（纯内存操作，不联网）。</summary>
    partial void OnSelectedSourceFilterChanged(string value) => Reload();

    /// <summary>按当前过滤条件重建列表并刷新状态行。</summary>
    private void Reload()
    {
        Entries.Clear();
        foreach (var entry in _service.Entries)
        {
            if (Matches(entry))
                Entries.Add(entry);
        }
        TrimToMax();
        UpdateStatus();
        // 历史里可能已经有日志文件 → 同步「打开 dsh 日志文件」按钮可用性
        RefreshOpenFileCommand();
    }

    /// <summary>后台线程收到新日志：调度到 UI 线程后追加。</summary>
    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Append(entry);
            return;
        }

        // 事件来自后台线程（dsh 输出 / 状态回调），必须回到 UI 线程再改集合
        dispatcher.BeginInvoke(new Action(() => Append(entry)));
    }

    /// <summary>追加一条日志到界面集合（仅当通过来源过滤）。</summary>
    private void Append(LogEntry entry)
    {
        if (_disposed || !Matches(entry))
            return;

        Entries.Add(entry);
        TrimToMax();
        UpdateStatus();
        RefreshOpenFileCommand();
    }

    /// <summary>该日志是否应显示在当前过滤条件下。</summary>
    private bool Matches(LogEntry entry) =>
        SelectedSourceFilter == AllSourcesFilter || entry.Source == SelectedSourceFilter;

    /// <summary>界面集合同样按上限裁剪（过滤「全部」时会累积全部来源）。</summary>
    private void TrimToMax()
    {
        while (Entries.Count > AppLogService.MaxEntries)
            Entries.RemoveAt(0);
    }

    /// <summary>刷新状态行：条目数 + dsh 日志文件路径。</summary>
    private void UpdateStatus() =>
        StatusText = string.IsNullOrEmpty(LogFilePath)
            ? $"{Entries.Count} 条日志"
            : $"{Entries.Count} 条日志 · dsh 日志文件：{LogFilePath}";

    /// <summary>清空全部日志（服务缓冲 + 界面列表）。</summary>
    [RelayCommand]
    private void ClearServiceLogs()
    {
        _service.Clear();
        Reload();
    }

    /// <summary>用资源管理器打开日志目录（目录不存在时先创建，保证一定能打开）。</summary>
    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            if (!string.IsNullOrEmpty(LogDirectory))
                Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{LogDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // 打开失败不影响页面其它功能，仅记录到统一日志
            _service.Info($"打开日志目录失败：{ex.Message}");
        }
    }

    /// <summary>用默认程序打开 dsh 日志文件；文件不存在时按钮禁用。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLogFile))]
    private void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = LogFilePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _service.Info($"打开 dsh 日志文件失败：{ex.Message}");
        }
    }

    private bool CanOpenLogFile() => File.Exists(LogFilePath);

    /// <summary>刷新「打开 dsh 日志文件」按钮的可用性（文件出现 / 消失时）。</summary>
    private void RefreshOpenFileCommand() => OpenLogFileCommand.NotifyCanExecuteChanged();

    /// <summary>退订统一日志事件，避免页面切换后残留订阅导致泄漏。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _service.EntryAdded -= OnEntryAdded;
    }
}
