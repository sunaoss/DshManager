namespace DshManager.Services;

/// <summary>
/// 一条应用日志：不可变快照，界面直接绑定展示。
/// </summary>
public sealed class LogEntry
{
    /// <summary>写入时刻（用于排序 / 展示）。</summary>
    public DateTime Time { get; init; }

    /// <summary>来源标签，如「面板」「dsh」「环境」「插件」。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>日志正文。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>时间文本（HH:mm:ss），供界面单独展示。</summary>
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>整行展示文本：「[HH:mm:ss] [来源] 内容」。</summary>
    public string DisplayText => $"[{TimeText}] [{Source}] {Message}";

    /// <summary>
    /// 是否为错误类日志（正文含「失败 / 错误 / Error」）；界面据此做粉色高亮。
    /// 放在模型上而不是界面转换器里，是为了让「哪些算错误」这条规则可被单元测试直接断言。
    /// </summary>
    public bool IsError =>
        Message.Contains("失败", StringComparison.Ordinal)
        || Message.Contains("错误", StringComparison.Ordinal)
        || Message.Contains("Error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 统一应用日志服务（静态单例，供任意页面 / 服务写入）：
/// - 线程安全：可能被后台线程、子进程回调并发调用，内部以锁保护；
/// - 环形缓冲：超过 <see cref="MaxEntries"/> 丢弃最旧条目，长时间运行不会无限膨胀；
/// - 异常安全：任何情况下都不向调用方抛异常（日志本身绝不能成为故障源）。
/// 说明：构造函数保持公开，便于单元测试创建互相隔离的实例（生产用 <see cref="Instance"/> 单例）。
/// </summary>
public sealed class AppLogService
{
    /// <summary>全局单例（各页面 / 服务统一写入同一份日志）。</summary>
    public static AppLogService Instance { get; } = new();

    /// <summary>环形缓冲上限，超出后丢弃最旧条目。</summary>
    public const int MaxEntries = 2000;

    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();

    /// <summary>快照缓存：避免每次读取都做一次全量拷贝；写入时置脏，下次读取再重建。</summary>
    private LogEntry[] _snapshot = Array.Empty<LogEntry>();
    private bool _snapshotDirty = true;

    /// <summary>新日志写入时触发（在锁外触发，订阅方回调里可安全再调用本服务）。</summary>
    public event EventHandler<LogEntry>? EntryAdded;

    /// <summary>当前日志的线程安全快照（按写入顺序，最旧在前）。</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                if (_snapshotDirty)
                {
                    _snapshot = _entries.ToArray();
                    _snapshotDirty = false;
                }
                return _snapshot;
            }
        }
    }

    /// <summary>写入一行日志：自动补时间戳并按环形缓冲裁剪。</summary>
    public void Write(string source, string message)
    {
        try
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                // 来源为空时归入「面板」，保证展示格式永远完整
                Source = string.IsNullOrWhiteSpace(source) ? "面板" : source,
                Message = message ?? string.Empty,
            };

            lock (_gate)
            {
                _entries.Add(entry);
                // 环形裁剪：一次可能裁掉多行（并发写入时），统一用 RemoveRange
                if (_entries.Count > MaxEntries)
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                _snapshotDirty = true;
            }

            // 在锁外触发事件：避免订阅方回调里再调用 Write/Clear 造成死锁
            EntryAdded?.Invoke(this, entry);
        }
        catch
        {
            // 日志写入绝不允许把异常抛给调用方（可能正在关键业务流程中）
        }
    }

    /// <summary>写入一条来源为「面板」的日志。</summary>
    public void Info(string message) => Write("面板", message);

    /// <summary>清空全部日志（不影响订阅关系）。</summary>
    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                _entries.Clear();
                _snapshotDirty = true;
            }
        }
        catch
        {
            // 同上：清空失败也不抛
        }
    }
}
