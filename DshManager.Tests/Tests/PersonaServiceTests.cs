using System.IO;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// PersonaService 单元测试：人格卡目录读写、agent.cordis.yml 的 persona 段文本级替换、
/// settings.yaml 的 agent-presets.default 行级改写。
/// 全部走临时目录（含伪造的随附模板目录），绝不触碰真实 ~/.dsh。
/// </summary>
public class PersonaServiceTests : IDisposable
{
    private readonly string _root;

    public PersonaServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dsh-persona-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    // ---- 测试环境搭建 ----

    /// <summary>伪造一个 DSH_HOME（含 .agent-presets 与 settings.yaml）。</summary>
    private string DshHome => Path.Combine(_root, "dsh-home");

    /// <summary>伪造的本程序数据目录（删除备份落在其中）。</summary>
    private string DataDir => Path.Combine(_root, "app-data");

    /// <summary>伪造的 node_modules 根目录。</summary>
    private string NodeModules => Path.Combine(_root, "node_modules");

    /// <summary>伪造的随附预设目录 node_modules\@deepseek-ai\dsh-agent-presets\presets。</summary>
    private string ShippedPresetsRoot =>
        Path.Combine(NodeModules, "@deepseek-ai", "dsh-agent-presets", "presets");

    /// <summary>注入伪造 node_modules 的服务实例。</summary>
    private PersonaService CreateService() => new(DshHome, DataDir, () => [NodeModules]);

    /// <summary>完全没有随附模板的服务实例（验证优雅降级）。</summary>
    private PersonaService CreateServiceWithoutTemplates() => new(DshHome, DataDir, () => []);

    /// <summary>在指定根目录下写一张预设卡的两个文件，返回卡目录。</summary>
    private static string WritePreset(string presetsRoot, string id, string presetYaml, string cordisYaml,
                                      string? skillFileName = null)
    {
        var dir = Path.Combine(presetsRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PersonaService.PresetFileName), presetYaml);
        File.WriteAllText(Path.Combine(dir, PersonaService.CordisFileName), cordisYaml);

        if (skillFileName is not null)
        {
            // 随附的 cordis 预设带 skills/ 子目录，用来验证「整目录复制」
            var skillDir = Path.Combine(dir, "skills", "demo");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, skillFileName), "# demo skill");
        }
        return dir;
    }

    /// <summary>写一张用户卡（默认放到 DSH_HOME\.agent-presets 下）。</summary>
    private string WriteUserCard(string id, string cordisYaml, string presetYaml)
        => WritePreset(Path.Combine(DshHome, PersonaService.UserPresetsDirName), id, presetYaml, cordisYaml);

    private string CordisPath(string presetDir) => Path.Combine(presetDir, PersonaService.CordisFileName);

    /// <summary>按行拆分（忽略行尾风格差异，便于逐行比对）。</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // ==================== WritePersonaText：单行 prefix 原地替换 ====================

    /// <summary>替换 prefix 后，除该行外必须逐行不变（注释、空行数量、缩进全部保留）。</summary>
    [Fact]
    public void WritePersonaText_ReplacesOnlyPrefixLine_AndKeepsEverythingElse()
    {
        var dir = WriteUserCard("single", string.Join("\n", new[]
        {
            "# 顶部注释",
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    suffix: keep-me",
            "    prefix: \"old prompt\"",
            "",
            "# 后续段落（空行必须保留，否则内容会错位）",
            "- id: tool-bash",
            "  name: '@deepseek-ai/dsh-tool-bash'",
            "  disabled: !!js process.platform === 'win32'",
        }) + "\n", "name: X\ndescription: Y\norder: 50\n");

        var ok = PersonaService.WritePersonaText(dir, "new prompt", out var error);

        Assert.True(ok);
        Assert.Empty(error);

        var expected = string.Join("\n", new[]
        {
            "# 顶部注释",
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    suffix: keep-me",
            "    prefix: \"new prompt\"",
            "",
            "# 后续段落（空行必须保留，否则内容会错位）",
            "- id: tool-bash",
            "  name: '@deepseek-ai/dsh-tool-bash'",
            "  disabled: !!js process.platform === 'win32'",
        }) + "\n";

        Assert.Equal(expected, File.ReadAllText(CordisPath(dir)));
    }

    /// <summary>多行块标量 prefix 被替换为单行：块内容行被吃掉，但相邻的空行必须保留。</summary>
    [Fact]
    public void WritePersonaText_ReplacesBlockPrefix_WithoutEatingAdjacentBlankLine()
    {
        var dir = WriteUserCard("block", string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: >-",
            "      line one",
            "      line two",
            "",
            "- id: tool-bash",
            "  name: '@deepseek-ai/dsh-tool-bash'",
        }) + "\n", "name: X\ndescription: Y\norder: 50\n");

        var ok = PersonaService.WritePersonaText(dir, "hello world", out var error);

        Assert.True(ok);
        Assert.Empty(error);

        var expected = string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: \"hello world\"",
            "",
            "- id: tool-bash",
            "  name: '@deepseek-ai/dsh-tool-bash'",
        }) + "\n";

        Assert.Equal(expected, File.ReadAllText(CordisPath(dir)));
    }

    /// <summary>原文件没有结尾换行时，替换后也不得凭空补一个结尾换行。</summary>
    [Fact]
    public void WritePersonaText_KeepsMissingTrailingNewline()
    {
        var dir = WriteUserCard("no-eol", string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: \"old\"",
            "- id: tool-bash",
            "  name: '@deepseek-ai/dsh-tool-bash'",
        }), "name: X\ndescription: Y\norder: 50\n");

        Assert.True(PersonaService.WritePersonaText(dir, "new", out _));

        var expected = string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: \"new\"",
            "- id: tool-bash",
            "  name: '@deepseek-ai/dsh-tool-bash'",
        });
        Assert.Equal(expected, File.ReadAllText(CordisPath(dir)));
    }

    /// <summary>CRLF 文件的行尾风格必须逐行保留（dsh 自带 yml 里混着两种换行）。</summary>
    [Fact]
    public void WritePersonaText_PreservesCrlfLineEndings()
    {
        var dir = WriteUserCard("crlf", string.Join("\r\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: \"old\"",
            "",
            "- id: tool-bash",
        }) + "\r\n", "name: X\ndescription: Y\norder: 50\n");

        Assert.True(PersonaService.WritePersonaText(dir, "new", out _));

        var expected = string.Join("\r\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: \"new\"",
            "",
            "- id: tool-bash",
        }) + "\r\n";
        Assert.Equal(expected, File.ReadAllText(CordisPath(dir)));
    }

    // ==================== WritePersonaText：无 prefix 时的插入位置 ====================

    /// <summary>块内没有 prefix：插到 config: 之后，缩进与 config 对齐。</summary>
    [Fact]
    public void WritePersonaText_InsertsAfterConfig_WhenPrefixMissing()
    {
        var dir = WriteUserCard("insert", string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    suffix: trailing",
            "- id: tool-bash",
        }) + "\n", "name: X\ndescription: Y\norder: 50\n");

        Assert.True(PersonaService.WritePersonaText(dir, "hi", out _));

        var expected = string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  config:",
            "    prefix: \"hi\"",
            "    suffix: trailing",
            "- id: tool-bash",
        }) + "\n";
        Assert.Equal(expected, File.ReadAllText(CordisPath(dir)));
    }

    /// <summary>块内既没有 prefix 也没有 config：插到块尾，缩进比块头深一级。</summary>
    [Fact]
    public void WritePersonaText_InsertsAtBlockEnd_WhenNoConfig()
    {
        var dir = WriteUserCard("bare", string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "- id: tool-bash",
        }) + "\n", "name: X\ndescription: Y\norder: 50\n");

        Assert.True(PersonaService.WritePersonaText(dir, "hi", out _));

        var expected = string.Join("\n", new[]
        {
            "- id: persona",
            "  name: '@deepseek-ai/dsh-persona'",
            "  prefix: \"hi\"",
            "- id: tool-bash",
        }) + "\n";
        Assert.Equal(expected, File.ReadAllText(CordisPath(dir)));
    }

    // ==================== WritePersonaText：失败路径 ====================

    /// <summary>找不到 persona 块必须返回失败并给出明确错误，且不修改文件（不能静默）。</summary>
    [Fact]
    public void WritePersonaText_WhenPersonaBlockMissing_FailsAndLeavesFileUntouched()
    {
        var dir = Path.Combine(DshHome, PersonaService.UserPresetsDirName, "broken");
        Directory.CreateDirectory(dir);
        var original = "# 没有 persona 块\n- id: tool-bash\n  name: x\n";
        File.WriteAllText(CordisPath(dir), original);

        var ok = PersonaService.WritePersonaText(dir, "nope", out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
        Assert.Equal(original, File.ReadAllText(CordisPath(dir)));
    }

    /// <summary>连 agent.cordis.yml 都不存在时同样返回失败而非抛异常。</summary>
    [Fact]
    public void WritePersonaText_WhenFileMissing_FailsGracefully()
    {
        var dir = Path.Combine(DshHome, PersonaService.UserPresetsDirName, "empty-dir");
        Directory.CreateDirectory(dir);

        Assert.False(PersonaService.WritePersonaText(dir, "nope", out var error));
        Assert.NotEmpty(error);
    }

    // ==================== Create：复制模板 + preset.yml / 正文写入 ====================

    /// <summary>新建人格卡：整目录复制随附模板（含 skills 子目录），并写 preset.yml 与 persona 正文。</summary>
    [Fact]
    public void Create_CopiesTemplateDir_AndWritesPresetAndPersona()
    {
        WritePreset(ShippedPresetsRoot, "standard",
            "name: 标准模式\ndescription: 功能完整的编码 Agent。\norder: 1\n",
            string.Join("\n", new[]
            {
                "- id: persona",
                "  name: '@deepseek-ai/dsh-persona'",
                "  config:",
                "    suffix: Your working directory is {{cwd}}.",
                "    prefix: \"standard prompt\"",
                "",
                "- id: tool-bash",
                "  name: '@deepseek-ai/dsh-tool-bash'",
            }) + "\n",
            skillFileName: "SKILL.md");

        var service = CreateService();
        var card = service.Create("standard", "my-card", "我的卡", "测试描述", "你好\n世界", out var error);

        Assert.NotNull(card);
        Assert.Empty(error);
        var created = card!;
        Assert.Equal("my-card", created.Id);
        Assert.Equal("我的卡", created.Name);
        Assert.Equal("测试描述", created.Description);
        Assert.Equal(50, created.Order);
        Assert.Equal("你好 世界", created.SystemPrompt);                       // 换行被压成单行
        Assert.Equal("Your working directory is {{cwd}}.", created.Suffix);   // suffix 原样保留

        // 子目录（技能）随目录一起被复制
        Assert.True(File.Exists(Path.Combine(created.DirectoryPath, "skills", "demo", "SKILL.md")));

        var presetText = File.ReadAllText(Path.Combine(created.DirectoryPath, PersonaService.PresetFileName));
        Assert.Contains("name: 我的卡", presetText);
        Assert.Contains("description: 测试描述", presetText);
        Assert.Contains("order: 50", presetText);
    }

    /// <summary>id 重名时自动加数字后缀去重（persona → persona-2 …）。</summary>
    [Fact]
    public void Create_DuplicateId_AppendsNumericSuffix()
    {
        WritePreset(ShippedPresetsRoot, "standard", "name: 标准\norder: 1\n",
            "- id: persona\n  name: '@deepseek-ai/dsh-persona'\n  config:\n    prefix: \"p\"\n");

        var service = CreateService();
        var first = service.Create("standard", "persona", "一号", "", "a", out _);
        var second = service.Create("standard", "persona", "二号", "", "b", out _);

        Assert.Equal("persona", first!.Id);
        Assert.Equal("persona-2", second!.Id);
    }

    /// <summary>找不到任何随附模板时优雅降级：仍能生成最小骨架卡并写入正文。</summary>
    [Fact]
    public void Create_WhenNoShippedTemplates_FallsBackToMinimalSkeleton()
    {
        var service = CreateServiceWithoutTemplates();
        var card = service.Create("standard", "mini", "迷你卡", "", "hi", out var error);

        Assert.NotNull(card);
        Assert.Empty(error);
        Assert.False(service.HasShippedTemplates);
        Assert.Equal("hi", card!.SystemPrompt);
    }

    // ==================== LoadAll：用户卡在前、随附卡标记只读与默认 ====================

    /// <summary>用户卡排在前、随附卡标记 IsShipped，并按 settings.yaml 标出当前默认卡。</summary>
    [Fact]
    public void LoadAll_UserCardsFirst_AndMarksShippedAndDefault()
    {
        WriteUserCard("mine", "- id: persona\n  name: '@deepseek-ai/dsh-persona'\n  config:\n    prefix: \"mine\"\n",
            "name: 我的卡\ndescription: 自建\norder: 50\n");
        WritePreset(ShippedPresetsRoot, "standard", "name: 标准模式\norder: 1\n",
            "- id: persona\n  name: '@deepseek-ai/dsh-persona'\n  config:\n    prefix: \"std\"\n");

        Directory.CreateDirectory(DshHome);
        File.WriteAllText(Path.Combine(DshHome, PersonaService.SettingsFileName),
            "agent-default-model:\n  model: deepseek-flash\nagent-presets:\n  default: standard\n");

        var cards = CreateService().LoadAll();

        Assert.Equal(2, cards.Count);
        Assert.Equal("mine", cards[0].Id);
        Assert.False(cards[0].IsShipped);
        Assert.False(cards[0].IsDefault);
        Assert.Equal("standard", cards[1].Id);
        Assert.True(cards[1].IsShipped);
        Assert.True(cards[1].IsDefault);
    }

    // ==================== Save / Delete ====================

    /// <summary>保存只改 preset.yml 与 persona.prefix；随附卡一律拒绝。</summary>
    [Fact]
    public void Save_UpdatesPresetAndPrefix_AndRejectsShippedCard()
    {
        WritePreset(ShippedPresetsRoot, "standard", "name: 标准\norder: 1\n",
            "- id: persona\n  name: '@deepseek-ai/dsh-persona'\n  config:\n    prefix: \"old\"\n");

        var service = CreateService();
        var card = service.Create("standard", "my-card", "旧名", "旧描述", "旧正文", out _)!;

        card.Name = "新名";
        card.Description = "新描述";
        card.SystemPrompt = "新正文";
        Assert.True(service.Save(card, out var error));
        Assert.Empty(error);

        var reloaded = PersonaService.LoadOne(card.DirectoryPath)!;
        Assert.Equal("新名", reloaded.Name);
        Assert.Equal("新描述", reloaded.Description);
        Assert.Equal("新正文", reloaded.SystemPrompt);

        // 随附卡只读：保存必须失败
        var shipped = PersonaService.LoadOne(Path.Combine(ShippedPresetsRoot, "standard"))!;
        shipped.IsShipped = true;
        Assert.False(service.Save(shipped, out var shippedError));
        Assert.NotEmpty(shippedError);
    }

    /// <summary>删除是移动到 deleted-personas 备份目录，不真删；随附卡拒绝删除。</summary>
    [Fact]
    public void Delete_MovesCardToDeletedPersonas_AndRejectsShippedCard()
    {
        WritePreset(ShippedPresetsRoot, "standard", "name: 标准\norder: 1\n",
            "- id: persona\n  name: '@deepseek-ai/dsh-persona'\n  config:\n    prefix: \"p\"\n");

        var service = CreateService();
        var card = service.Create("standard", "my-card", "我的卡", "", "正文", out _)!;

        Assert.True(service.Delete(card, out var error));
        Assert.Empty(error);
        Assert.False(Directory.Exists(card.DirectoryPath));
        Assert.Single(Directory.GetDirectories(service.DeletedPersonasRoot));

        var shipped = PersonaService.LoadOne(Path.Combine(ShippedPresetsRoot, "standard"))!;
        shipped.IsShipped = true;
        Assert.False(service.Delete(shipped, out var shippedError));
        Assert.NotEmpty(shippedError);
    }

    // ==================== SetDefault：settings.yaml 行级最小改写 ====================

    /// <summary>只改 agent-presets.default 一行，同文件其它键逐行保持不变，并生成时间戳备份。</summary>
    [Fact]
    public void SetDefault_ReplacesOnlyDefaultLine_AndKeepsOtherKeys()
    {
        Directory.CreateDirectory(DshHome);
        var settingsPath = Path.Combine(DshHome, PersonaService.SettingsFileName);
        var original = string.Join("\n", new[]
        {
            "llm-pi-ai:",
            "  providers: {}",
            "agent-default-model:",
            "  provider: deepseek-official",
            "  model: deepseek-flash",
            "agent-presets:",
            "  default: standard",
            "ui-theme:",
            "  preference: system",
        }) + "\n";
        File.WriteAllText(settingsPath, original);

        var service = CreateService();
        Assert.True(service.SetDefault("greedy-whale-girl", out var error));
        Assert.Empty(error);

        // 读回新默认值
        Assert.Equal("greedy-whale-girl", service.ReadDefaultPresetId());

        // 逐行比对：只有 default 行不同
        var before = SplitLines(original);
        var after = SplitLines(File.ReadAllText(settingsPath));
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].TrimStart().StartsWith("default:", StringComparison.Ordinal)) continue;
            Assert.Equal(before[i], after[i]);
        }
        Assert.Contains("  default: greedy-whale-girl", after);

        // 写前备份
        Assert.Single(Directory.GetFiles(DshHome, "settings.yaml.bak.*"));
    }

    /// <summary>settings.yaml 里完全没有 agent-presets 段时，在文件末尾追加该段而不动原有键。</summary>
    [Fact]
    public void SetDefault_AppendsSection_WhenSectionMissing()
    {
        Directory.CreateDirectory(DshHome);
        var settingsPath = Path.Combine(DshHome, PersonaService.SettingsFileName);
        File.WriteAllText(settingsPath, "llm-pi-ai:\n  providers: {}\n");

        var service = CreateService();
        Assert.True(service.SetDefault("my-card", out var error));
        Assert.Empty(error);

        var text = File.ReadAllText(settingsPath);
        Assert.Contains("providers: {}", text);        // 原有键保留
        Assert.Contains("agent-presets:", text);       // 新段已追加
        Assert.Contains("  default: my-card", text);
        Assert.Equal("my-card", service.ReadDefaultPresetId());
    }

    /// <summary>settings.yaml 不存在时也能创建，并写入默认人格。</summary>
    [Fact]
    public void SetDefault_CreatesSettingsFile_WhenMissing()
    {
        var service = CreateService();

        Assert.True(service.SetDefault("my-card", out var error));
        Assert.Empty(error);
        Assert.True(File.Exists(service.SettingsFilePath));
        Assert.Equal("my-card", service.ReadDefaultPresetId());
    }
}
