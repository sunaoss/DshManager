using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// MemoryLibraryService 单元测试：全部在临时目录里操作一份 AGENTS.md，
/// 绝不触碰真实的 ~/.dsh，测试结束自动清理。
/// </summary>
public class MemoryLibraryServiceTests : IDisposable
{
    private readonly string _dshHome;

    public MemoryLibraryServiceTests()
    {
        _dshHome = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dshHome))
        {
            Directory.Delete(_dshHome, recursive: true);
        }
    }

    /// <summary>真实 AGENTS.md 的结构样例：一级标题 + ">" 引用段 + 4 个 "##" 小节 + "- " 条目。</summary>
    private const string SampleMarkdown =
"""
# 我的长期记忆

> 这份文件由「贪吃鲸鱼管理系统」维护。
> 写在这里的事，我都会记得。

## 关于我

- 我用 Windows 11。
- 我的 DSH 数据目录在 C:\Users\me\.dsh

## 重要约定

- 直接说「全部否决」。

## 项目与背景

- 我们在做「贪吃鲸鱼管理系统」。

## 历史摘要

（以下由程序自动整理，可以随意删改）

- 想做一个一键启动的应用。
""";

    /// <summary>结构不标准的样例：散落文本 + 无 "-" 前缀的正文 + 四级标题。</summary>
    private const string MalformedMarkdown =
"""
开头一段散落文本
## 奇怪小节
这是没有前缀的一行
- 正常条目
#### 四级标题
""";

    /// <summary>临时目录下的记忆文件路径。</summary>
    private string MemoryPath => Path.Combine(_dshHome, MemoryLibraryService.MemoryFileName);

    /// <summary>基于临时目录创建服务实例（注入 dshHome）。</summary>
    private MemoryLibraryService CreateService() => new(_dshHome);

    /// <summary>预置一份 AGENTS.md。</summary>
    private void WriteMemoryFile(string content)
    {
        Directory.CreateDirectory(_dshHome);
        File.WriteAllText(MemoryPath, content);
    }

    private string ReadMemoryFile() => File.ReadAllText(MemoryPath);

    /// <summary>统计子串出现次数（用于断言小节标题未重复添加）。</summary>
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ==================== 解析 ====================

    /// <summary>文件不存在时返回空文档，不抛异常。</summary>
    [Fact]
    public void Load_WhenFileMissing_ReturnsEmptyDocument()
    {
        var document = CreateService().Load();

        Assert.NotNull(document);
        Assert.Empty(document.Sections);
        Assert.Equal(string.Empty, document.Title);
    }

    /// <summary>解析真实格式样例：一级标题、引用段、4 个小节及其条目数量与内容均正确。</summary>
    [Fact]
    public void Load_ParsesRealFormatSample()
    {
        WriteMemoryFile(SampleMarkdown);

        var document = CreateService().Load();

        Assert.Equal("我的长期记忆", document.Title);
        Assert.StartsWith(">", document.Intro);
        Assert.Contains("贪吃鲸鱼管理系统", document.Intro);

        Assert.Equal(4, document.Sections.Count);
        Assert.Equal(
            new[] { "关于我", "重要约定", "项目与背景", "历史摘要" },
            document.Sections.Select(s => s.Heading));

        Assert.Equal(2, document.Sections[0].Items.Count);
        Assert.Equal("我用 Windows 11。", document.Sections[0].Items[0]);
        Assert.Single(document.Sections[1].Items);
        Assert.Equal("直接说「全部否决」。", document.Sections[1].Items[0]);
        Assert.Single(document.Sections[2].Items);
        // 「历史摘要」下的说明段是无 "-" 前缀的整行，按条目处理（保留内容不丢失）
        Assert.Equal(2, document.Sections[3].Items.Count);
        Assert.Contains(document.Sections[3].Items, i => i.Contains("想做一个一键启动的应用"));
    }

    /// <summary>每个条目都记录原文行号（可定位标识），且与条目一一对应。</summary>
    [Fact]
    public void Load_RecordsSourceLinesForItems()
    {
        WriteMemoryFile(SampleMarkdown);

        var section = CreateService().Load().Sections[0];

        Assert.Equal(section.Items.Count, section.SourceLines.Count);
        Assert.All(section.SourceLines, line => Assert.True(line >= 0));
    }

    /// <summary>结构异常（缺标题、散落文本、多级标题、无前缀正文）时尽力解析，不崩。</summary>
    [Fact]
    public void Load_WhenStructureMalformed_DoesNotThrow()
    {
        WriteMemoryFile(MalformedMarkdown);

        var document = CreateService().Load();

        Assert.Contains("散落文本", document.Intro);            // 首个 "##" 之前的散落文本并入前言
        Assert.Equal(2, document.Sections.Count);               // "## 奇怪小节" 与 "#### 四级标题"
        Assert.Equal("奇怪小节", document.Sections[0].Heading);
        Assert.Equal(2, document.Sections[0].Items.Count);      // 无前缀正文 + 正常条目
        Assert.Equal("四级标题", document.Sections[1].Heading);
    }

    /// <summary>内容为纯噪声（无标题无小节）时也能安全解析。</summary>
    [Fact]
    public void Load_WhenOnlyNoise_DoesNotThrow()
    {
        WriteMemoryFile("    \n随便写的几行\n\n=== 分隔 ===\n");

        var document = CreateService().Load();

        Assert.NotNull(document);
        Assert.Empty(document.Sections);
        Assert.Contains("随便写的几行", document.Intro);
    }

    // ==================== 条目增删改 ====================

    /// <summary>向已存在的小节追加条目：小节标题不重复，原引用段与其它小节不受影响。</summary>
    [Fact]
    public void AddItem_ToExistingSection_AppendsWithoutDuplicateHeading()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.AddItem("关于我", "新增的一条"));

        var raw = ReadMemoryFile();
        Assert.Equal(1, CountOccurrences(raw, "## 关于我"));      // 标题已存在 → 不重复添加
        Assert.Contains("- 新增的一条", raw);
        Assert.Contains("这份文件由「贪吃鲸鱼管理系统」维护", raw); // 用户手写段落未被触碰

        var section = service.Load().Sections[0];
        Assert.Equal(3, section.Items.Count);
        Assert.Equal("新增的一条", section.Items[^1]);
    }

    /// <summary>小节不存在时自动新建，并追加到文件末尾。</summary>
    [Fact]
    public void AddItem_WhenSectionMissing_CreatesSectionAtEnd()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.AddItem("新的分组", "新分组里的条目"));

        var document = service.Load();
        Assert.Equal(5, document.Sections.Count);
        Assert.Equal("新的分组", document.Sections[^1].Heading);
        Assert.Equal("新分组里的条目", document.Sections[^1].Items[0]);
    }

    /// <summary>一次输入多行 → 逐行成为独立条目。</summary>
    [Fact]
    public void AddItem_MultiLine_AddsMultipleItems()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.AddItem("重要约定", "第一条\n第二条"));

        var section = service.Load().Sections[1];
        Assert.Equal(3, section.Items.Count);
        Assert.Equal("第一条", section.Items[^2]);
        Assert.Equal("第二条", section.Items[^1]);
    }

    /// <summary>更新条目只影响目标条目，其它小节完全不受影响。</summary>
    [Fact]
    public void UpdateItem_ChangesOnlyTargetItem()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.UpdateItem("重要约定", "直接说「全部否决」。", "改为：先问清楚再动手。"));

        var document = service.Load();
        Assert.Equal("改为：先问清楚再动手。", document.Sections[1].Items[0]);
        Assert.Equal("我用 Windows 11。", document.Sections[0].Items[0]);   // 其它小节未变
        Assert.Equal(4, document.Sections.Count);
    }

    /// <summary>目标条目不存在时返回 false 且不写盘。</summary>
    [Fact]
    public void UpdateItem_WhenItemMissing_ReturnsFalse()
    {
        WriteMemoryFile(SampleMarkdown);

        Assert.False(CreateService().UpdateItem("关于我", "不存在的条目", "新内容"));
        Assert.Equal(SampleMarkdown.TrimEnd(), ReadMemoryFile().TrimEnd());
    }

    /// <summary>删除条目只删目标那条，其它条目保留。</summary>
    [Fact]
    public void RemoveItem_RemovesOnlyTargetItem()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.RemoveItem("关于我", "我用 Windows 11。"));

        var document = service.Load();
        Assert.Single(document.Sections[0].Items);
        Assert.Equal("我的 DSH 数据目录在 C:\\Users\\me\\.dsh", document.Sections[0].Items[0]);
        Assert.Single(document.Sections[1].Items);   // 其它小节未受影响
    }

    // ==================== 小节增删改 ====================

    /// <summary>同名小节已存在时拒绝新建（不重复建标题）。</summary>
    [Fact]
    public void AddSection_WhenDuplicate_ReturnsFalse()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.False(service.AddSection("关于我"));
        Assert.Equal(4, service.Load().Sections.Count);
    }

    /// <summary>删除小节只删该小节及其条目。</summary>
    [Fact]
    public void RemoveSection_DeletesSectionOnly()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.RemoveSection("重要约定"));

        var document = service.Load();
        Assert.Equal(3, document.Sections.Count);
        Assert.DoesNotContain(document.Sections, s => s.Heading == "重要约定");
        Assert.Equal(2, document.Sections[0].Items.Count);   // 「关于我」的条目仍在
    }

    /// <summary>重命名小节后标题更新，条目与其它小节保持不变。</summary>
    [Fact]
    public void RenameSection_RenamesAndKeepsOtherSections()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.RenameSection("项目与背景", "项目背景"));

        var document = service.Load();
        Assert.Equal(4, document.Sections.Count);
        Assert.Contains(document.Sections, s => s.Heading == "项目背景");
        Assert.DoesNotContain(document.Sections, s => s.Heading == "项目与背景");
        Assert.Equal("我们在做「贪吃鲸鱼管理系统」。", document.Sections[2].Items[0]);
        Assert.Equal("重要约定", document.Sections[1].Heading);
    }

    /// <summary>重命名为已有小节名时拒绝，避免出现两个同名分组。</summary>
    [Fact]
    public void RenameSection_WhenNameTaken_ReturnsFalse()
    {
        WriteMemoryFile(SampleMarkdown);

        Assert.False(CreateService().RenameSection("重要约定", "关于我"));
        Assert.Equal(4, CreateService().Load().Sections.Count);   // 小节数量不变，未产生同名分组
    }

    // ==================== 备份与持久化 ====================

    /// <summary>覆盖写盘前先备份原文件，备份内容等于被覆盖前的旧内容。</summary>
    [Fact]
    public void Save_BacksUpPreviousContentBeforeOverwrite()
    {
        WriteMemoryFile(SampleMarkdown);
        var service = CreateService();

        Assert.True(service.Save(new MemoryDocument
        {
            Title = "新的标题",
            Sections = { new MemorySection { Heading = "小节", Items = { "内容" } } },
        }));

        var backups = Directory.GetFiles(_dshHome, MemoryLibraryService.MemoryFileName + ".bak.*");
        var backup = Assert.Single(backups);
        Assert.Equal(SampleMarkdown.TrimEnd(), File.ReadAllText(backup).TrimEnd());
        Assert.NotNull(service.LastBackupPath);
    }

    /// <summary>首次创建文件（原本不存在）时不产生备份。</summary>
    [Fact]
    public void Save_WhenFileNew_DoesNotCreateBackup()
    {
        var service = CreateService();

        Assert.True(service.Save(new MemoryDocument { Sections = { new MemorySection { Heading = "A" } } }));

        Assert.Null(service.LastBackupPath);
        Assert.Empty(Directory.GetFiles(_dshHome, MemoryLibraryService.MemoryFileName + ".bak.*"));
    }

    /// <summary>写入内容可被新实例读回（跨实例持久化）。</summary>
    [Fact]
    public void Save_PersistsAcrossInstances()
    {
        WriteMemoryFile(SampleMarkdown);
        CreateService().AddItem("关于我", "跨实例验证");

        var section = CreateService().Load().Sections[0];

        Assert.Contains("跨实例验证", section.Items);
    }

    // ==================== 预算信息 ====================

    /// <summary>GetBudgetInfo 返回文件真实字节数与 65536 预算的占用百分比。</summary>
    [Fact]
    public void GetBudgetInfo_ReportsFileBytesAndBudget()
    {
        WriteMemoryFile(SampleMarkdown);
        var expectedBytes = new FileInfo(MemoryPath).Length;

        var info = CreateService().GetBudgetInfo();

        Assert.Equal((int)expectedBytes, info.Bytes);
        Assert.Equal(65536, info.BudgetBytes);
        Assert.Equal(expectedBytes * 100.0 / 65536, info.Percent, 3);
        Assert.False(info.IsOverBudget);
    }

    /// <summary>文件不存在时预算为 0 且不抛异常。</summary>
    [Fact]
    public void GetBudgetInfo_WhenFileMissing_ReturnsZero()
    {
        var info = CreateService().GetBudgetInfo();

        Assert.Equal(0, info.Bytes);
        Assert.Equal(0, info.Percent);
        Assert.Equal(65536, info.BudgetBytes);
    }
}
