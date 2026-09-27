using System.IO;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// DshConfigService / YamlTextEditor 单元测试：
/// 行级最小改写不破坏未知键与注释、点路径读写、嵌套 providers、备份策略、损坏回退。
/// 全部使用独立临时目录，绝不触碰真实 ~/.dsh 配置。
/// </summary>
public class DshConfigServiceTests
{
    /// <summary>创建独立临时目录（每个用例一份，互不干扰）。</summary>
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>清理临时目录。</summary>
    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>用 \n 拼出一个 YAML 文本（固定换行，避免源文件换行风格影响断言）。</summary>
    private static string N(params string[] lines) => string.Join("\n", lines);

    private static string SettingsPath(string dir) => Path.Combine(dir, DshConfigService.SettingsFileName);

    private static void WriteSettings(string dir, string text) => File.WriteAllText(SettingsPath(dir), text);

    /// <summary>
    /// 取「以 <paramref name="trimmedLine"/> 结尾的某一行的前导空格数」，-1 表示没找到。
    /// 用于按「缩进宽度」而不是按子串断言，避免 2/4 空格的子串包含关系造成假阳性。
    /// </summary>
    private static int IndentWidthOf(string text, string trimmedLine)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().Equals(trimmedLine, StringComparison.Ordinal))
                return line.Length - line.TrimStart().Length;
        }
        return -1;
    }

    // ==================== 基础：缺失 / 损坏 ====================

    /// <summary>文件不存在时不抛异常：读返回 null/空，LoadSettings 返回空字典。</summary>
    [Fact]
    public void Load_WhenFileMissing_DoesNotThrow_AndReadsEmpty()
    {
        var dir = NewTempDir();
        try
        {
            var service = new DshConfigService(dir);

            Assert.False(service.FileExists);
            Assert.False(service.IsDirty);
            Assert.Null(service.ReadValue("agent-default-model.model"));
            Assert.Empty(service.ReadAll());
            Assert.Empty(service.LoadSettings());
            Assert.Empty(service.ReadProviders());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>文件损坏（非法 YAML）时不抛异常：LoadSettings 回退空字典，行级读不改也不崩。</summary>
    [Fact]
    public void Load_WhenFileCorrupt_DoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, "key: [unclosed");   // 未闭合的 flow 序列，YamlDotNet 必然解析失败

            var service = new DshConfigService(dir);         // 构造函数内 Load，不应抛
            Assert.Empty(service.LoadSettings());            // YamlDotNet 解析失败 → 空字典

            // 行级改写依然可用，且不抛异常
            Assert.True(service.SetValue("a.b", "value"));
            Assert.Equal("value", service.ReadValue("a.b"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ==================== 点路径读 ====================

    /// <summary>点路径读：标量去引号，复杂值原样返回，缺失返回 null。</summary>
    [Fact]
    public void ReadValue_UnquotesScalars_KeepsComplexValues()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "foo: \"hello world\"",
                "bar: {a: 1}",
                "baz: 123",
                "qux: 'it''s'") + "\n");

            var service = new DshConfigService(dir);

            Assert.Equal("hello world", service.ReadValue("foo"));
            Assert.Equal("{a: 1}", service.ReadValue("bar"));   // 复杂值原样
            Assert.Equal("123", service.ReadValue("baz"));
            Assert.Equal("it's", service.ReadValue("qux"));
            Assert.Null(service.ReadValue("not-exist"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>读到的键路径集合包含嵌套键（点路径）。</summary>
    [Fact]
    public void ReadAll_ReturnsDottedPaths()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "agent-default-model:",
                "  provider: deepseek-official",
                "  model: deepseek-flash") + "\n");

            var keys = new DshConfigService(dir).Keys();

            Assert.Contains("agent-default-model.provider", keys);
            Assert.Contains("agent-default-model.model", keys);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ==================== 行级最小改写 ====================

    /// <summary>改已存在的键：不破坏注释、未知键与 complex 结构。</summary>
    [Fact]
    public void SetValue_UpdatesExistingKey_PreservesUnknownKeysAndComments()
    {
        var dir = NewTempDir();
        try
        {
            var original = N(
                "# 我的 DSH 设置（勿删）",
                "agent-default-model:",
                "  provider: deepseek-official",
                "  model: deepseek-flash",
                "  reasoningEffort: high",
                "llm-pi-ai:",
                "  providers: {}",
                "my-custom-key: keep-me") + "\n";
            WriteSettings(dir, original);

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("agent-default-model.model", "deepseek-v4-pro"));

            var text = service.ReadValue("agent-default-model.model");
            Assert.Equal("deepseek-v4-pro", text);

            // 注释、未知键、复杂结构、其它键都原样保留
            var raw = File.ReadAllText(service.SettingsPath);
            Assert.Contains("# 我的 DSH 设置（勿删）", raw);
            Assert.Contains("my-custom-key: keep-me", raw);
            Assert.Contains("providers: {}", raw);
            Assert.Contains("provider: deepseek-official", raw);
            Assert.Contains("reasoningEffort: high", raw);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>键不存在时插到所属父节点下（保持缩进）。</summary>
    [Fact]
    public void SetValue_InsertsMissingKey_UnderExistingParent()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "agent-default-model:",
                "  provider: deepseek-official") + "\n");

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("agent-default-model.reasoningEffort", "max"));

            Assert.Equal("max", service.ReadValue("agent-default-model.reasoningEffort"));
            // SetValue 只改内存，需显式 Save 落盘后才能校验文件内容
            Assert.NotNull(service.Save());
            // 插入行必须带 2 空格缩进，且与现有键同级
            Assert.Contains("  reasoningEffort: max", File.ReadAllText(service.SettingsPath));
            // 原有键没有被覆盖
            Assert.Equal("deepseek-official", service.ReadValue("agent-default-model.provider"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>父节点全不存在时按缩进逐层创建。</summary>
    [Fact]
    public void SetValue_CreatesMissingParentChain()
    {
        var dir = NewTempDir();
        try
        {
            var service = new DshConfigService(dir);   // 空目录 → 空内容
            Assert.True(service.SetValue("subagent-model-selection.enabled", "true"));

            Assert.Equal("true", service.ReadValue("subagent-model-selection.enabled"));
            Assert.False(File.Exists(service.SettingsPath));   // 只改内存，尚未落盘

            Assert.NotNull(service.Save());
            var saved = File.ReadAllText(service.SettingsPath);
            Assert.Contains("subagent-model-selection:", saved);
            Assert.Contains("  enabled: \"true\"", saved);   // Set 会对易歧义标量加引号
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// 文件用 4 空格缩进时，新键必须跟随该文件的步长（而不是写死 2 空格）。
    /// 写死 +2 会让新键只比父级深 2，YAML 解析出的层级与用户预期不符（挂错分节）。
    /// </summary>
    [Fact]
    public void SetValue_InsertMissingKey_FollowsFourSpaceIndentStep()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "agent-default-model:",
                "    provider: deepseek-official") + "\n");

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("agent-default-model.reasoningEffort", "max"));
            Assert.NotNull(service.Save());

            var saved = File.ReadAllText(service.SettingsPath);
            // 新键与既有同级键完全对齐：4 空格，而不是 2。
            // 注意不能用 DoesNotContain("  reasoningEffort") —— 那是子串匹配，
            // "    reasoningEffort"（4 空格）的后两个空格正好凑出该子串，会假阳性。
            // 这里按行比对缩进宽度，才真正验证步长。
            Assert.Contains("    reasoningEffort: max", saved);
            Assert.Equal(4, IndentWidthOf(saved, "reasoningEffort: max"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>父节点不存在、需要一层层补齐时，每一层都要按文件实际步长递进（4 空格）。</summary>
    [Fact]
    public void SetValue_CreateMissingParentChain_FollowsFourSpaceIndentStep()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "agent-presets:",
                "    default: greedy-whale-girl") + "\n");

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("agent-presets.greedy-whale-girl.compositionPath", "x.yaml"));
            Assert.NotNull(service.Save());

            var saved = File.ReadAllText(service.SettingsPath);
            // agent-presets 下已有 4 空格子行 → 本层步长 4；
            // 新键在 default 同级的 greedy-whale-girl 之下，即 8 空格
            Assert.Contains("    greedy-whale-girl:", saved);
            Assert.Contains("        compositionPath: x.yaml", saved);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>空值写入表示删除该键。</summary>
    [Fact]
    public void SetValue_Empty_RemovesKey()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "shell:",
                "  cwd: D:\\work") + "\n");

            var service = new DshConfigService(dir);
            Assert.True(service.Contains("shell.cwd"));

            Assert.True(service.SetValue("shell.cwd", ""));

            Assert.False(service.Contains("shell.cwd"));
            // SetValue 只改内存，需显式 Save 落盘后才能校验文件内容
            Assert.NotNull(service.Save());
            Assert.DoesNotContain("cwd:", File.ReadAllText(service.SettingsPath));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>改行尾注释所在的键时，注释要被保留。</summary>
    [Fact]
    public void SetValue_PreservesInlineComment()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "ui-theme:",
                "  preference: system # 主题偏好") + "\n");

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("ui-theme.preference", "dark"));
            // SetValue 只改内存，需显式 Save 落盘后才能校验文件内容
            Assert.NotNull(service.Save());

            var raw = File.ReadAllText(service.SettingsPath);
            Assert.Contains("preference: dark # 主题偏好", raw);
            Assert.Equal("dark", service.ReadValue("ui-theme.preference"));   // 读时已剥离注释
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>字符串的值按需加引号（易歧义标量必须引起来）；数字用 SetRawValue 保持裸值。</summary>
    [Fact]
    public void Set_QuotesAmbiguousStrings_AndSetRawKeepsBareScalars()
    {
        var dir = NewTempDir();
        try
        {
            var service = new DshConfigService(dir);

            Assert.True(service.SetValue("flags.enabled", "true"));
            Assert.Equal("\"true\"", service.ReadRaw("flags.enabled"));   // 必须加引号，否则会被当布尔
            Assert.Equal("true", service.ReadValue("flags.enabled"));      // 去引号后还原

            Assert.True(service.SetRawValue("shell.timeoutMs", "120000"));
            Assert.Equal("120000", service.ReadRaw("shell.timeoutMs"));    // 数字不加引号
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ==================== providers 读取与嵌套改写 ====================

    /// <summary>能读出 providers 下所有条目的 api / baseURL / apiKeyEnv / models。</summary>
    [Fact]
    public void ReadProviders_ParsesAllFields()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "llm-pi-ai:",
                "  providers:",
                "    my-ai:",
                "      displayName: My AI",
                "      api: openai-completions",
                "      baseURL: https://my.ai/v1",
                "      apiKeyEnv: MY_AI_KEY",
                "      models: [{id: gpt-x}, {id: gpt-y}]",
                "    other-ai:",
                "      api: anthropic-messages",
                "      baseURL: https://other.ai",
                "      apiKeyEnv: OTHER_KEY") + "\n");

            var providers = new DshConfigService(dir).ReadProviders();

            Assert.Equal(2, providers.Count);                 // 按 Id 字典序：my-ai, other-ai
            var my = providers[0];
            Assert.Equal("my-ai", my.Id);
            Assert.Equal("My AI", my.DisplayName);
            Assert.Equal("openai-completions", my.Api);
            Assert.Equal("https://my.ai/v1", my.BaseURL);
            Assert.Equal("MY_AI_KEY", my.ApiKeyEnv);
            Assert.Contains("gpt-x", my.Models);

            Assert.Equal("other-ai", providers[1].Id);
            Assert.Equal("anthropic-messages", providers[1].Api);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>最小改写嵌套字典下的 provider 字段，其它 provider 不受影响。</summary>
    [Fact]
    public void SetValue_UpdatesNestedProviderField()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N(
                "llm-pi-ai:",
                "  providers:",
                "    my-ai:",
                "      api: openai-completions",
                "      baseURL: https://old.example/v1",
                "    other-ai:",
                "      baseURL: https://other.example") + "\n");

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("llm-pi-ai.providers.my-ai.baseURL", "https://new.example/v1"));

            var providers = service.ReadProviders();
            Assert.Equal("https://new.example/v1", providers[0].BaseURL);
            Assert.Equal("https://other.example", providers[1].BaseURL);   // 未改动
            Assert.Contains("other-ai:", File.ReadAllText(service.SettingsPath));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ==================== 落盘 / 备份 ====================

    /// <summary>落盘前生成 settings.yaml.bak.yyyyMMdd-HHmmss，内容为改动前的原文。</summary>
    [Fact]
    public void Save_CreatesTimestampedBackup_WithOriginalContent()
    {
        var dir = NewTempDir();
        try
        {
            var original = N(
                "agent-default-model:",
                "  model: deepseek-flash") + "\n";
            WriteSettings(dir, original);

            var service = new DshConfigService(dir);
            Assert.True(service.SetValue("agent-default-model.model", "deepseek-v4-pro"));

            var backup = service.Save();

            Assert.NotNull(backup);
            Assert.NotEmpty(backup!);
            Assert.True(File.Exists(backup));
            Assert.StartsWith("settings.yaml.bak.", Path.GetFileName(backup));
            Assert.Equal(original, File.ReadAllText(backup));   // 备份是改动前的原文

            // 新文件已写入新值，且用新建服务能读回
            var reloaded = new DshConfigService(dir);
            Assert.Equal("deepseek-v4-pro", reloaded.ReadValue("agent-default-model.model"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>同一轮内多次保存会生成多个备份，绝不覆盖同名文件。</summary>
    [Fact]
    public void Save_MultipleTimes_KeepsSeparateBackups()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N("a: 1") + "\n");
            var service = new DshConfigService(dir);

            service.SetValue("a", "2");
            service.Save();
            service.SetValue("a", "3");
            service.Save();

            var backups = Directory.GetFiles(dir, "settings.yaml.bak.*");
            Assert.True(backups.Length >= 2, $"期望至少 2 份备份，实际 {backups.Length}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>备份最多保留 20 份（超出的按时间戳从旧到新删除）。</summary>
    [Fact]
    public void Save_PrunesBackups_ToAtMost20()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N("a: 1") + "\n");

            // 预置 25 份历史备份
            for (var i = 0; i < 25; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"settings.yaml.bak.20200101-{i:D6}"), "old");
            }

            var service = new DshConfigService(dir);
            service.SetValue("a", "2");
            service.Save();

            var backups = Directory.GetFiles(dir, "settings.yaml.bak.*");
            Assert.Equal(20, backups.Length);
            // 最新的本次备份一定还在
            Assert.Contains(backups, f => File.ReadAllText(f).Contains("a: 1"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>原子写：无临时文件残留、无 BOM、内容可读回。</summary>
    [Fact]
    public void Save_WriteIsAtomic_NoTempLeftover_NoBom()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N("a: 1") + "\n");
            var service = new DshConfigService(dir);
            service.SetValue("a", "2");
            service.Save();

            Assert.Empty(Directory.GetFiles(dir, "*dsh-manager-tmp"));

            var bytes = File.ReadAllBytes(service.SettingsPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);   // 无 BOM
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>保持原文件的换行风格与结尾换行状态（CRLF + 有结尾换行）。</summary>
    [Fact]
    public void Save_PreservesCrlfNewlineStyle()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, "a: 1\r\nb: 2\r\n");

            var service = new DshConfigService(dir);
            service.SetValue("a", "9");
            service.Save();

            var text = File.ReadAllText(service.SettingsPath);
            Assert.Contains("\r\n", text);
            Assert.EndsWith("\r\n", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // 没有落单的 \n
            Assert.Equal("9", service.ReadValue("a"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>原文件结尾没有换行时，写回后也不补换行。</summary>
    [Fact]
    public void Save_PreservesMissingTrailingNewline()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, "a: 1\nb: 2");

            var service = new DshConfigService(dir);
            service.SetValue("a", "9");
            service.Save();

            var text = File.ReadAllText(service.SettingsPath);
            Assert.False(text.EndsWith("\n"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>没有改动时 Save 不写盘、也不生成备份。</summary>
    [Fact]
    public void Save_WhenNotDirty_DoesNothing()
    {
        var dir = NewTempDir();
        try
        {
            WriteSettings(dir, N("a: 1") + "\n");
            var service = new DshConfigService(dir);

            Assert.Equal("", service.Save());
            Assert.Empty(Directory.GetFiles(dir, "settings.yaml.bak.*"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ==================== 参数目录（33 项 / 6 组） ====================

    /// <summary>参数目录：33 项、6 个分组，路径唯一。</summary>
    [Fact]
    public void ConfigFieldCatalog_Has33ItemsIn6Groups()
    {
        var all = DshManager.Models.ConfigField.All();

        Assert.Equal(33, all.Count);
        Assert.Equal(6, DshManager.Models.ConfigField.Groups().Count);

        var paths = all.Select(f => f.Path).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());   // 路径唯一
        Assert.Contains(all, f => f.Kind == DshManager.Models.ParamKind.Complex);   // Complex 只读项存在
        Assert.Contains(all, f => f.NewSessionOnly);                                 // NewSessionOnly 角标项存在
    }

    // ==================== YamlTextEditor 直测 ====================

    /// <summary>Read：拆出点路径与值，剥离行尾注释，忽略列表项。</summary>
    [Fact]
    public void YamlTextEditor_Read_ParsesPathsAndStripsComments()
    {
        var text = N(
            "# 注释",
            "agent-default-model:",
            "  model: deepseek-flash  # 行尾注释",
            "  allowed:",
            "    - a",
            "    - b") + "\n";

        var map = YamlTextEditor.Read(text);

        // 只收录叶子标量：映射头与列表项都不进 map
        Assert.Single(map);
        Assert.Equal("deepseek-flash", map["agent-default-model.model"]);
        Assert.DoesNotContain(map.Keys, k => k.Contains("- a"));
        Assert.False(map.ContainsKey("注释"));
    }

    /// <summary>QuoteIfNeeded：易歧义/含特殊字符的值必须加引号，普通值保持裸写。</summary>
    [Fact]
    public void YamlTextEditor_QuoteIfNeeded_OnlyQuotesWhenNecessary()
    {
        Assert.Equal("deepseek-flash", YamlTextEditor.QuoteIfNeeded("deepseek-flash"));
        Assert.Equal("https://api.deepseek.com", YamlTextEditor.QuoteIfNeeded("https://api.deepseek.com"));
        Assert.Equal("\"true\"", YamlTextEditor.QuoteIfNeeded("true"));
        Assert.Equal("\"off\"", YamlTextEditor.QuoteIfNeeded("off"));
        Assert.Equal("\"\"", YamlTextEditor.QuoteIfNeeded(""));
        Assert.Equal("\"a # b\"", YamlTextEditor.QuoteIfNeeded("a # b"));
    }

    /// <summary>IsComplex 只认以 { 或 [ 开头的流式值。</summary>
    [Fact]
    public void YamlTextEditor_IsComplex_DetectsFlowValues()
    {
        Assert.True(YamlTextEditor.IsComplex("{}"));
        Assert.True(YamlTextEditor.IsComplex("[{id: x}]"));
        Assert.False(YamlTextEditor.IsComplex("deepseek-flash"));
        Assert.False(YamlTextEditor.IsComplex(""));
    }
}
