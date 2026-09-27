using System.IO;
using System.Text;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// AgentInstructionsService 单元测试：全部在临时目录里伪造目录结构 / 人格卡，
/// 绝不触碰真实的 ~/.dsh，测试结束自动清理。
/// </summary>
public class AgentInstructionsServiceTests : IDisposable
{
    /// <summary>本测试独占的临时根目录（模拟一台机器的家目录 / 项目树）。</summary>
    private readonly string _base;

    public AgentInstructionsServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    // ==================== 工具方法 ====================

    /// <summary>在 _base 下建一个相对目录并返回绝对路径。</summary>
    private string MakeDir(params string[] segments)
    {
        var path = Path.Combine(new[] { _base }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>写入一个文件（自动建父目录），内容为给定文本，返回绝对路径。</summary>
    private static string WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>按 4096 字节（4 KB）写一个占位文件，便于断言 SizeText。</summary>
    private static string WriteSizedFile(string path, int sizeBytes, char fill = 'a')
        => WriteFile(path, new string(fill, sizeBytes));

    /// <summary>一份最小可用的人格卡片段：agent-instructions 条目夹在 persona 与 tool 条目之间。</summary>
    private const string CardWithInstructions = """
        # 人格卡
        - id: persona
          name: '@deepseek-ai/dsh-persona'
          config:
            prefix: "人格守则"

        - id: agent-instructions
          name: '@deepseek-ai/dsh-agent-instructions'
          config:
            maxBytes: 65536

        - id: tool-fs
          name: '@deepseek-ai/dsh-tool-fs'
        """;

    // ==================== FindProjectRoot ====================

    /// <summary>命中 .git 标记：返回该目录。</summary>
    [Fact]
    public void FindProjectRoot_WhenMarkerPresent_ReturnsThatDirectory()
    {
        var root = MakeDir("repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var work = MakeDir("repo", "src", "app");

        Assert.Equal(Path.GetFullPath(root), AgentInstructionsService.FindProjectRoot(work));
    }

    /// <summary>无标记时返回工作目录本身（注入探测谓词，避免受真实磁盘上的 .git 影响）。</summary>
    [Fact]
    public void FindProjectRoot_WhenNoMarker_ReturnsWorkDirectoryItself()
    {
        var work = MakeDir("plain", "a", "b");

        var result = AgentInstructionsService.FindProjectRoot(work, _ => false);

        Assert.Equal(Path.GetFullPath(work), result);
    }

    /// <summary>标记在多级之上时能正确上溯到最接近的那个。</summary>
    [Fact]
    public void FindProjectRoot_WhenMarkerSeveralLevelsUp_WalksUp()
    {
        var root = MakeDir("deep");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var work = MakeDir("deep", "l1", "l2", "l3");

        Assert.Equal(Path.GetFullPath(root), AgentInstructionsService.FindProjectRoot(work));
    }

    // ==================== DiscoverChain ====================

    /// <summary>主场景：项目根 → 中间层 → 工作目录，断言顺序 / Depth / ScopeText / 本地覆盖 / TotalBytes。</summary>
    [Fact]
    public void DiscoverChain_OrdersBroadToSpecificWithDepthsAndScopes()
    {
        var root = MakeDir("proj");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var mid = MakeDir("proj", "mid");
        var work = MakeDir("proj", "mid", "work");

        WriteSizedFile(Path.Combine(root, "AGENTS.md"), 4096);              // 4.0 KB
        WriteFile(Path.Combine(mid, "CLAUDE.md"), "mid content");            // 11 字节
        WriteFile(Path.Combine(work, "AGENTS.md"), "work content");
        WriteFile(Path.Combine(work, "AGENTS.local.md"), "local overlay");

        var chain = new AgentInstructionsService().DiscoverChain(work);

        Assert.True(chain.ProjectRootFound);
        Assert.Equal(Path.GetFullPath(root), chain.ProjectRoot);
        Assert.Equal(Path.GetFullPath(work), chain.WorkDirectory);

        Assert.Equal(4, chain.Files.Count);
        Assert.Equal(
            new[] { "AGENTS.md", "CLAUDE.md", "AGENTS.md", "AGENTS.local.md" },
            chain.Files.Select(f => f.FileName).ToArray());

        // 从宽泛到具体：项目根(0) → 中间层(1) → 工作目录(2)
        Assert.Equal(new[] { 0, 1, 2, 2 }, chain.Files.Select(f => f.Depth).ToArray());

        Assert.Equal(new[] { "项目根", "上级目录（第 1 层）", "工作目录", "工作目录（本地覆盖）" },
            chain.Files.Select(f => f.ScopeText).ToArray());

        Assert.Equal(new[] { false, false, false, true }, chain.Files.Select(f => f.IsLocalOverlay).ToArray());
        Assert.All(chain.Files, f => Assert.True(f.Exists));

        // 优先级：项目根最低，工作目录最高
        Assert.Equal("低（较宽泛）", chain.Files[0].PriorityText);
        Assert.Equal("高（最具体）", chain.Files[chain.Files.Count - 1].PriorityText);

        // 大小展示 + 合计
        Assert.Equal("4.0 KB", chain.Files[0].SizeText);
        Assert.Equal("4.0 KB", AgentInstructionsService.FormatSize(4096));
        Assert.Equal(chain.Files.Sum(f => f.SizeBytes), chain.TotalBytes);
    }

    /// <summary>只返回真实存在的文件（同层缺失的候选不出现）。</summary>
    [Fact]
    public void DiscoverChain_OnlyReturnsExistingFiles()
    {
        var root = MakeDir("only");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var work = MakeDir("only", "work");

        WriteFile(Path.Combine(root, "AGENTS.md"), "root");
        WriteFile(Path.Combine(work, "AGENTS.md"), "work");

        var chain = new AgentInstructionsService().DiscoverChain(work);

        Assert.Equal(2, chain.Files.Count);
        Assert.Equal(new[] { 0, 1 }, chain.Files.Select(f => f.Depth).ToArray());
        Assert.DoesNotContain(chain.Files, f => f.FileName == "CLAUDE.md");
    }

    /// <summary>同目录内去首尾空白后内容相同的候选只保留最早一个（AGENTS.md 优先于 CLAUDE.md）。</summary>
    [Fact]
    public void DiscoverChain_DeduplicatesIdenticalSiblingContent()
    {
        var root = MakeDir("dup");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var work = MakeDir("dup", "work");

        WriteFile(Path.Combine(work, "AGENTS.md"), "same body");
        WriteFile(Path.Combine(work, "CLAUDE.md"), "\n  same body  \n");   // 去首尾空白后一致

        var chain = new AgentInstructionsService().DiscoverChain(work);

        Assert.Single(chain.Files);
        Assert.Equal("AGENTS.md", chain.Files[0].FileName);
    }

    /// <summary>空目录：不抛异常、返回空 Files。</summary>
    [Fact]
    public void DiscoverChain_EmptyDirectory_ReturnsEmptyFiles()
    {
        var empty = MakeDir("empty");

        var chain = new AgentInstructionsService().DiscoverChain(empty);

        Assert.Empty(chain.Files);
        Assert.Equal(0L, chain.TotalBytes);
    }

    /// <summary>目录不存在：不抛异常、返回空 Files。</summary>
    [Fact]
    public void DiscoverChain_NonexistentDirectory_ReturnsEmptyFiles()
    {
        var missing = Path.Combine(_base, "not", "here");

        var chain = new AgentInstructionsService().DiscoverChain(missing);

        Assert.Empty(chain.Files);
        Assert.Equal(0L, chain.TotalBytes);
    }

    // ==================== ReadBudget ====================

    /// <summary>配置缺失时返回官方默认值且 IsDefault=true。</summary>
    [Fact]
    public void ReadBudget_WhenConfigMissing_ReturnsDefaults()
    {
        var budget = new AgentInstructionsService(_base).ReadBudget();

        Assert.Equal(AgentInstructionsService.DefaultMaxBytes, budget.MaxBytes);
        Assert.Equal(AgentInstructionsService.DefaultMaxSourceBytes, budget.MaxSourceBytes);
        Assert.True(budget.IsDefault);
        Assert.Equal("官方默认值", budget.SourceDescription);
    }

    /// <summary>从 settings.yaml 选定的人格卡里读出显式值。</summary>
    [Fact]
    public void ReadBudget_ReadsSelectedCardExplicitValues()
    {
        WriteFile(Path.Combine(_base, "settings.yaml"), "agent-presets:\n  default: my-preset\n");
        WriteFile(Path.Combine(_base, ".agent-presets", "my-preset", "agent.cordis.yml"),
            "- id: agent-instructions\n  name: '@deepseek-ai/dsh-agent-instructions'\n  config:\n    maxBytes: 131072\n    maxSourceBytes: 2097152\n");

        var budget = new AgentInstructionsService(_base).ReadBudget();

        Assert.Equal(131072, budget.MaxBytes);
        Assert.Equal(2097152, budget.MaxSourceBytes);
        Assert.False(budget.IsDefault);
        Assert.Contains("my-preset", budget.SourceDescription);
    }

    /// <summary>人格卡只写了 maxBytes 时，maxSourceBytes 用官方默认值补上。</summary>
    [Fact]
    public void ReadBudget_WhenMaxSourceBytesAbsent_UsesOfficialDefault()
    {
        WriteFile(Path.Combine(_base, "settings.yaml"), "agent-presets:\n  default: my-preset\n");
        WriteFile(Path.Combine(_base, ".agent-presets", "my-preset", "agent.cordis.yml"),
            "- id: agent-instructions\n  config:\n    maxBytes: 131072\n");

        var budget = new AgentInstructionsService(_base).ReadBudget();

        Assert.Equal(131072, budget.MaxBytes);
        Assert.Equal(AgentInstructionsService.DefaultMaxSourceBytes, budget.MaxSourceBytes);
        Assert.False(budget.IsDefault);
    }

    // ==================== WriteBudget：入参校验 ====================

    /// <summary>入参非法（0 / 负数 / maxSourceBytes &lt; maxBytes）时必须拒绝并给出中文原因。</summary>
    [Theory]
    [InlineData(0, 1048576)]
    [InlineData(-1, 1048576)]
    [InlineData(131072, 0)]
    [InlineData(131072, -5)]
    [InlineData(131072, 65536)]
    public void WriteBudget_RejectsInvalidArguments(int maxBytes, int maxSourceBytes)
    {
        var service = new AgentInstructionsService(_base);

        var ok = service.WriteBudget(maxBytes, maxSourceBytes, out var message);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ==================== WriteBudget：正常写入 ====================

    /// <summary>正常写入后可被 ReadBudget 读回；写前生成备份；文件其余部分逐字节不变。</summary>
    [Fact]
    public void WriteBudget_WritesValuesBacksUpAndKeepsRestUnchanged()
    {
        WriteFile(Path.Combine(_base, "settings.yaml"), "agent-presets:\n  default: my-preset\n");
        var card = WriteFile(Path.Combine(_base, ".agent-presets", "my-preset", "agent.cordis.yml"), CardWithInstructions);
        var original = File.ReadAllText(card);

        var ok = new AgentInstructionsService(_base).WriteBudget(131072, 2097152, out var message);

        Assert.True(ok);
        Assert.Contains(card, message);

        // 读回
        var budget = new AgentInstructionsService(_base).ReadBudget();
        Assert.Equal(131072, budget.MaxBytes);
        Assert.Equal(2097152, budget.MaxSourceBytes);
        Assert.False(budget.IsDefault);

        // 备份已生成（带时间戳，同名不覆盖）
        var backups = Directory.GetFiles(Path.GetDirectoryName(card)!, "agent.cordis.yml.bak.*");
        Assert.Single(backups);

        // 除目标两行外，其余行与原文完全一致
        var after = File.ReadAllText(card);
        Assert.Contains("    maxBytes: 131072", after);
        Assert.Contains("    maxSourceBytes: 2097152", after);
        Assert.DoesNotContain("maxBytes: 65536", after);

        static string WithoutBudgetLines(string text)
            => string.Join("\n", text.Split('\n').Where(line =>
                !line.Contains("maxBytes", StringComparison.Ordinal)
                && !line.Contains("maxSourceBytes", StringComparison.Ordinal)));
        Assert.Equal(WithoutBudgetLines(original), WithoutBudgetLines(after));
    }

    /// <summary>随附只读人格卡：拒绝修改，且文件一个字节都不变。</summary>
    [Fact]
    public void WriteBudget_RefusesShippedReadOnlyCard()
    {
        WriteFile(Path.Combine(_base, "settings.yaml"), "agent-presets:\n  default: standard\n");
        var shipped = WriteFile(
            Path.Combine(_base, "profiles", "node_modules", "@deepseek-ai", "dsh-agent-presets", "presets", "standard", "agent.cordis.yml"),
            "- id: agent-instructions\n  name: '@deepseek-ai/dsh-agent-instructions'\n  config:\n    maxBytes: 65536\n");
        var before = File.ReadAllBytes(shipped);

        var ok = new AgentInstructionsService(_base).WriteBudget(131072, 131072, out var message);

        Assert.False(ok);
        Assert.Contains("只读", message);
        Assert.Equal(before, File.ReadAllBytes(shipped));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(shipped)!, "*.bak.*"));
    }

    /// <summary>找不到任何人脸卡时拒绝写入并说明原因。</summary>
    [Fact]
    public void WriteBudget_WhenNoCardFound_Rejects()
    {
        var ok = new AgentInstructionsService(_base).WriteBudget(131072, 131072, out var message);

        Assert.False(ok);
        Assert.Contains("未找到", message);
    }

    // ==================== FormatSize 边界 ====================

    /// <summary>人类可读大小格式化边界：0 B / 512 B / 4.0 KB / 1.5 MB。</summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(4096, "4.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    public void FormatSize_FormatsBoundaries(long bytes, string expected)
    {
        Assert.Equal(expected, AgentInstructionsService.FormatSize(bytes));
    }
}
