using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// AppLogService 单元测试。
/// 每个测试用独立实例（构造函数公开即为便于隔离），不触碰真实文件系统与 UI，全部离线。
/// 覆盖：写入 / 读取顺序、环形裁剪上限、Clear、展示格式、并发写入不丢不崩。
/// </summary>
public class AppLogServiceTests
{
    /// <summary>构造被测服务（独立实例，避免测试间互相污染）。</summary>
    private static AppLogService CreateService() => new();

    // ==================== 写入 & 读取顺序 ====================

    /// <summary>按写入顺序返回，来源与正文一一对应。</summary>
    [Fact]
    public void Write_AppendsInWriteOrder()
    {
        var service = CreateService();

        service.Write("面板", "第一条");
        service.Write("dsh", "第二条");
        service.Write("环境", "第三条");

        Assert.Equal(3, service.Entries.Count);
        Assert.Equal(new[] { "第一条", "第二条", "第三条" }, service.Entries.Select(e => e.Message));
        Assert.Equal(new[] { "面板", "dsh", "环境" }, service.Entries.Select(e => e.Source));
    }

    /// <summary>来源为空 / 空白时归入「面板」，保证展示格式完整。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_BlankSource_FallsBackToPanel(string? source)
    {
        var service = CreateService();

        service.Write(source!, "hello");

        Assert.Equal("面板", Assert.Single(service.Entries).Source);
    }

    /// <summary>Info 等价于以「面板」为来源写入。</summary>
    [Fact]
    public void Info_UsesPanelSource()
    {
        var service = CreateService();

        service.Info("面板消息");

        var entry = Assert.Single(service.Entries);
        Assert.Equal("面板", entry.Source);
        Assert.Equal("面板消息", entry.Message);
    }

    /// <summary>message 传 null 时退化为空字符串，不产生 null 引用。</summary>
    [Fact]
    public void Write_NullMessage_BecomesEmpty()
    {
        var service = CreateService();

        service.Write("面板", null!);

        Assert.Equal(string.Empty, Assert.Single(service.Entries).Message);
    }

    // ==================== 环形缓冲 ====================

    /// <summary>超过 MaxEntries 后丢弃最旧条目，最新的始终保留。</summary>
    [Fact]
    public void Write_ExceedingMaxEntries_DropsOldest()
    {
        var service = CreateService();
        const int overflow = 50;

        for (var i = 0; i < AppLogService.MaxEntries + overflow; i++)
        {
            service.Write("面板", $"第 {i} 条");
        }

        Assert.Equal(AppLogService.MaxEntries, service.Entries.Count);
        // 最旧的 overflow 条被丢弃 → 首条应为第 overflow 条
        Assert.Equal($"第 {overflow} 条", service.Entries[0].Message);
        Assert.Equal($"第 {AppLogService.MaxEntries + overflow - 1} 条", service.Entries[^1].Message);
    }

    // ==================== Clear ====================

    /// <summary>Clear 后列表为空，且可继续写入。</summary>
    [Fact]
    public void Clear_RemovesAllEntries_AndAllowsFurtherWrites()
    {
        var service = CreateService();
        service.Write("面板", "a");
        service.Write("dsh", "b");

        service.Clear();

        Assert.Empty(service.Entries);

        service.Info("c");
        Assert.Equal("c", Assert.Single(service.Entries).Message);
    }

    // ==================== 展示格式 ====================

    /// <summary>TimeText 为 HH:mm:ss（8 位），DisplayText 为「[时间] [来源] 内容」。</summary>
    [Fact]
    public void DisplayText_And_TimeText_UseExpectedFormat()
    {
        var service = CreateService();

        service.Write("dsh", "启动完成");

        var entry = Assert.Single(service.Entries);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", entry.TimeText);
        Assert.Equal($"[{entry.TimeText}] [dsh] 启动完成", entry.DisplayText);
    }

    /// <summary>错误类日志（含 失败 / 错误 / Error）被标记，用于界面粉色高亮。</summary>
    [Theory]
    [InlineData("启动失败：端口占用")]
    [InlineData("发生错误")]
    [InlineData("Unhandled Error occurred")]
    public void IsError_DetectsErrorKeywords(string message)
    {
        var service = CreateService();

        service.Write("dsh", message);

        Assert.True(Assert.Single(service.Entries).IsError);
    }

    /// <summary>普通日志不标记为错误。</summary>
    [Fact]
    public void IsError_FalseForNormalMessage()
    {
        var service = CreateService();

        service.Write("dsh", "dsh Web 端口 3080 已就绪");

        Assert.False(Assert.Single(service.Entries).IsError);
    }

    // ==================== 事件 ====================

    /// <summary>每次写入都触发一次 EntryAdded，且携带的就是该条日志。</summary>
    [Fact]
    public void EntryAdded_RaisedOncePerWrite()
    {
        var service = CreateService();
        var received = new List<LogEntry>();
        service.EntryAdded += (_, entry) => received.Add(entry);

        service.Write("面板", "x");
        service.Write("dsh", "y");

        Assert.Equal(2, received.Count);
        Assert.Equal(new[] { "x", "y" }, received.Select(e => e.Message));
    }

    // ==================== 并发 ====================

    /// <summary>多线程并发写入：不丢条目不崩溃，条目总数与唯一性均正确。</summary>
    [Fact]
    public void ConcurrentWrites_DoNotLoseEntriesOrThrow()
    {
        var service = CreateService();
        const int threads = 8;
        const int perThread = 200;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                service.Write("并发", $"T{t}-{i}");
            }
        });

        var entries = service.Entries;
        Assert.Equal(threads * perThread, entries.Count);
        // 每条消息唯一 → 用去重数验证"不丢不重"
        Assert.Equal(threads * perThread, entries.Select(e => e.Message).Distinct().Count());
    }
}
