using System.IO;
using System.Text;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// LongMemoryPluginService 单元测试：全部在临时目录里伪造 dshHome / profile，
/// 绝不触碰真实的 ~/.dsh；插件源目录也只在测试输出目录下临时创建、结束即清理。
/// </summary>
public class LongMemoryPluginServiceTests : IDisposable
{
    /// <summary>本测试独占的临时 DSH_HOME。</summary>
    private readonly string _base;

    private const string ProfileName = "web";

    public LongMemoryPluginServiceTests()
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

    private LongMemoryPluginService NewService() => new(_base, ProfileName);

    private string PatchPath => Path.Combine(_base, "profiles", ProfileName, "cordis.patch.yml");
    private string InstallDir => Path.Combine(_base, "profiles", ProfileName, "plugins", "dsh-long-memory");
    private string LogPath => Path.Combine(_base, "dsh-long-memory.log");
    private string SettingsPath => Path.Combine(_base, "settings.yaml");
    private string MemoryPath => Path.Combine(_base, "MEMORY.md");

    /// <summary>写入 patch（自动建父目录，行尾统一为 \n）。</summary>
    private void WritePatch(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PatchPath)!);
        File.WriteAllText(PatchPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    /// <summary>写入任意文件（自动建父目录）。</summary>
    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>在 plugin 安装目录里放一个占位文件（模拟已安装）。</summary>
    private void CreateInstallDir()
    {
        Directory.CreateDirectory(InstallDir);
        WriteFile(Path.Combine(InstallDir, "index.js"), "// installed\n");
    }

    /// <summary>统计子串出现次数。</summary>
    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    /// <summary>一份含注释头与另一个插件 insert 的 patch（用于验证「不破坏其它内容」）。</summary>
    private static string[] PatchWithOtherPlugin() =>
    [
        "# profile 的 patch 层注释头（不得被破坏）",
        "- insert:",
        "    - id: other-plugin",
        "      name: './plugins/other/index.js'",
        "      config:",
        "        enabled: true",
    ];

    // ---- 插件源目录（&lt;程序目录&gt;\dsh-plugin\dsh-long-memory）----

    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "dsh-plugin", "dsh-long-memory");

    /// <summary>临时造出插件源目录（含子目录，用于验证递归复制）。</summary>
    private static void CreateSourceDir()
    {
        Directory.CreateDirectory(SourceDir);
        WriteFile(Path.Combine(SourceDir, "index.js"), "// test plugin\n");
        WriteFile(Path.Combine(SourceDir, "sub", "extra.txt"), "extra\n");
    }

    private static void DeleteSourceDir()
    {
        try
        {
            if (Directory.Exists(SourceDir)) Directory.Delete(SourceDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    // ==================== IsInstalled 三态 ====================

    /// <summary>目录缺失：未安装。</summary>
    [Fact]
    public void IsInstalled_WhenNothing_ReturnsFalse()
    {
        Assert.False(NewService().IsInstalled());
    }

    /// <summary>目录在但 patch 无 insert：未安装。</summary>
    [Fact]
    public void IsInstalled_WhenOnlyInstallDirectory_ReturnsFalse()
    {
        CreateInstallDir();

        Assert.False(NewService().IsInstalled());
    }

    /// <summary>目录 + patch insert 齐备：已安装。</summary>
    [Fact]
    public void IsInstalled_WhenDirectoryAndPatchInsert_ReturnsTrue()
    {
        CreateInstallDir();
        WritePatch(
            "# 注释头",
            "- insert:",
            "    - id: long-memory",
            "      name: './plugins/dsh-long-memory/index.js'",
            "      config:",
            "        enabled: true");

        Assert.True(NewService().IsInstalled());
    }

    // ==================== Install ====================

    /// <summary>安装：递归复制插件目录、patch 追加 insert、保留注释头与其它插件行、生成备份。</summary>
    [Fact]
    public void Install_CopiesDirectoryAppendsInsertKeepsOthersAndBacksUpPatch()
    {
        CreateSourceDir();
        WritePatch(PatchWithOtherPlugin());
        try
        {
            var service = NewService();

            var ok = service.Install(out var message);

            Assert.True(ok);
            Assert.Contains("已安装", message);
            Assert.Contains(PatchPath, message);

            // 插件目录已复制（含子目录文件）
            Assert.True(File.Exists(Path.Combine(InstallDir, "index.js")));
            Assert.True(File.Exists(Path.Combine(InstallDir, "sub", "extra.txt")));

            // patch：注释头、其它插件行保留，long-memory insert 已追加
            var patch = File.ReadAllText(PatchPath);
            Assert.Contains("# profile 的 patch 层注释头（不得被破坏）", patch);
            Assert.Contains("id: other-plugin", patch);
            Assert.Contains("id: long-memory", patch);
            Assert.EndsWith("\n", patch);

            // 备份已生成
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(PatchPath)!, "cordis.patch.yml.bak.*"));

            // 状态变为已安装
            Assert.True(service.IsInstalled());
        }
        finally
        {
            DeleteSourceDir();
        }
    }

    /// <summary>已挂载时再次安装：不重复追加 insert（幂等）。</summary>
    [Fact]
    public void Install_WhenAlreadyMounted_DoesNotDuplicateInsert()
    {
        CreateSourceDir();
        WritePatch(PatchWithOtherPlugin());
        try
        {
            var service = NewService();
            Assert.True(service.Install(out _));

            Assert.True(service.Install(out var message));

            var patch = File.ReadAllText(PatchPath);
            Assert.Equal(1, CountOccurrences(patch, "id: long-memory"));
            Assert.Contains("已存在", message);
        }
        finally
        {
            DeleteSourceDir();
        }
    }

    /// <summary>插件源目录缺失：安装失败，message 含中文提示。</summary>
    [Fact]
    public void Install_WhenSourceMissing_ReturnsFalseWithChineseMessage()
    {
        DeleteSourceDir();   // 确保源目录不存在

        var ok = NewService().Install(out var message);

        Assert.False(ok);
        Assert.Contains("缺失", message);
        Assert.False(Directory.Exists(InstallDir));
    }

    // ==================== Uninstall ====================

    /// <summary>卸载：移除 long-memory 项、删除插件目录与日志、其它插件行与注释头不受影响、生成备份。</summary>
    [Fact]
    public void Uninstall_RemovesInsertKeepsOthersAndDeletesArtifacts()
    {
        CreateInstallDir();
        WriteFile(LogPath, "[2026-01-01T00:00:00.000Z] [plugin.load] {}\n");
        // 同一个 insert 块里同时有另一个插件项与 long-memory 项
        WritePatch(
            "# profile 的 patch 层注释头（不得被破坏）",
            "- insert:",
            "    - id: other-plugin",
            "      name: './plugins/other/index.js'",
            "      config:",
            "        enabled: true",
            "    - id: long-memory",
            "      name: './plugins/dsh-long-memory/index.js'",
            "      config:",
            "        maxBytes: 262144");

        var ok = NewService().Uninstall(out var message);

        Assert.True(ok);
        Assert.Contains("已卸载", message);

        // patch：long-memory 已移除，其它内容保留，仍以换行结尾
        var patch = File.ReadAllText(PatchPath);
        Assert.DoesNotContain("id: long-memory", patch);
        Assert.Contains("# profile 的 patch 层注释头（不得被破坏）", patch);
        Assert.Contains("id: other-plugin", patch);
        Assert.Contains("- insert:", patch);
        Assert.EndsWith("\n", patch);

        // 插件目录与日志已删除，备份已生成
        Assert.False(Directory.Exists(InstallDir));
        Assert.False(File.Exists(LogPath));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(PatchPath)!, "cordis.patch.yml.bak.*"));
    }

    /// <summary>insert 块里只有 long-memory 一项：整块删除，注释头保留。</summary>
    [Fact]
    public void Uninstall_WhenOnlyItem_RemovesWholeInsertBlock()
    {
        CreateInstallDir();
        WritePatch(
            "# 注释头",
            "- insert:",
            "    - id: long-memory",
            "      name: './plugins/dsh-long-memory/index.js'",
            "      config:",
            "        enabled: true");

        var ok = NewService().Uninstall(out _);

        Assert.True(ok);
        var patch = File.ReadAllText(PatchPath);
        Assert.DoesNotContain("insert", patch);
        Assert.DoesNotContain("long-memory", patch);
        Assert.Contains("# 注释头", patch);
        Assert.EndsWith("\n", patch);
    }

    // ==================== 记忆文件读写 ====================

    /// <summary>记忆文件不存在时读取为空串。</summary>
    [Fact]
    public void ReadMemory_WhenMissing_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NewService().ReadMemory());
        Assert.Equal(0L, NewService().GetMemoryBytes());
    }

    /// <summary>写入：首次创建无备份，二次写入生成备份；字节数正确。</summary>
    [Fact]
    public void WriteMemory_CreatesThenBacksUp()
    {
        var service = NewService();

        Assert.True(service.WriteMemory("第一版", out var firstMessage));
        Assert.Equal("第一版", File.ReadAllText(MemoryPath, Encoding.UTF8));
        Assert.Contains("创建", firstMessage);
        Assert.Empty(Directory.GetFiles(_base, "MEMORY.md.bak.*"));   // 首次创建不产生备份

        Assert.True(service.WriteMemory("第二版", out var secondMessage));
        Assert.Equal("第二版", File.ReadAllText(MemoryPath, Encoding.UTF8));
        Assert.Contains("备份", secondMessage);
        Assert.NotEmpty(Directory.GetFiles(_base, "MEMORY.md.bak.*"));

        Assert.Equal((long)Encoding.UTF8.GetByteCount("第二版"), service.GetMemoryBytes());
    }

    // ==================== ReadConfig ====================

    /// <summary>settings.yaml 无该段：返回插件默认值且 IsDefault=true。</summary>
    [Fact]
    public void ReadConfig_WhenNoSection_ReturnsDefaults()
    {
        WriteFile(SettingsPath, "agent-presets:\n  default: my-preset\n");

        var config = NewService().ReadConfig();

        Assert.Equal(262144, config.MaxBytes);
        Assert.Equal(2097152, config.MaxSourceBytes);
        Assert.True(config.Enabled);
        Assert.Equal(string.Empty, config.File);
        Assert.True(config.IsDefault);
        Assert.Contains("没有 long-memory 段", config.SourceDescription);

        // 未配置 file → 记忆文件用默认路径
        Assert.Equal(MemoryPath, NewService().MemoryFilePath);
    }

    /// <summary>settings.yaml 有该段：正确解析各字段，File 优先作为记忆文件路径。</summary>
    [Fact]
    public void ReadConfig_ParsesSection()
    {
        WriteFile(SettingsPath,
            "llm-pi-ai:\n  providers: {}\nlong-memory:\n  maxBytes: 131072\n  maxSourceBytes: 4194304\n  enabled: false\n  file: D:\\mem\\custom.md\n");

        var config = NewService().ReadConfig();

        Assert.Equal(131072, config.MaxBytes);
        Assert.Equal(4194304, config.MaxSourceBytes);
        Assert.False(config.Enabled);
        Assert.Equal("D:\\mem\\custom.md", config.File);
        Assert.False(config.IsDefault);
        Assert.Contains("long-memory 段", config.SourceDescription);

        Assert.Equal("D:\\mem\\custom.md", NewService().MemoryFilePath);
    }

    // ==================== WriteConfig ====================

    /// <summary>写入后可读回，且不破坏 settings.yaml 的其它键与注释。</summary>
    [Fact]
    public void WriteConfig_UpdatesAndKeepsOtherKeysAndComments()
    {
        WriteFile(SettingsPath,
            "# 顶部注释（不得被破坏）\nllm-pi-ai:\n  providers: {}\nagent-presets:\n  default: my-preset\n");

        var ok = NewService().WriteConfig(131072, 4194304, false, out var message);

        Assert.True(ok);
        Assert.Contains("热生效", message);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("# 顶部注释（不得被破坏）", text);
        Assert.Contains("providers: {}", text);
        Assert.Contains("default: my-preset", text);
        Assert.Contains("long-memory:", text);

        var config = NewService().ReadConfig();
        Assert.Equal(131072, config.MaxBytes);
        Assert.Equal(4194304, config.MaxSourceBytes);
        Assert.False(config.Enabled);
        Assert.False(config.IsDefault);

        // 备份已生成
        Assert.NotEmpty(Directory.GetFiles(_base, "settings.yaml.bak.*"));
    }

    /// <summary>settings.yaml 不存在：新建并写入该段。</summary>
    [Fact]
    public void WriteConfig_CreatesFileWhenMissing()
    {
        Assert.False(File.Exists(SettingsPath));

        var ok = NewService().WriteConfig(524288, 2097152, true, out _);

        Assert.True(ok);
        Assert.True(File.Exists(SettingsPath));
        Assert.EndsWith("\n", File.ReadAllText(SettingsPath));

        var config = NewService().ReadConfig();
        Assert.Equal(524288, config.MaxBytes);
        Assert.True(config.Enabled);
        Assert.False(config.IsDefault);
    }

    /// <summary>非法入参（非正数）必须拒绝并给出中文原因。</summary>
    [Theory]
    [InlineData(0, 2097152)]
    [InlineData(-1, 2097152)]
    [InlineData(262144, 0)]
    public void WriteConfig_RejectsInvalidArguments(int maxBytes, int maxSourceBytes)
    {
        var ok = NewService().WriteConfig(maxBytes, maxSourceBytes, true, out var message);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ==================== ReadRecentLogLines ====================

    /// <summary>日志不存在返回空；存在时只取尾部若干行。</summary>
    [Fact]
    public void ReadRecentLogLines_MissingReturnsEmptyAndLimitsCount()
    {
        Assert.Empty(NewService().ReadRecentLogLines(10));

        var lines = Enumerable.Range(0, 10).Select(i => $"line{i}").ToArray();
        File.WriteAllText(LogPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

        var recent = NewService().ReadRecentLogLines(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal("line7", recent[0]);
        Assert.Equal("line9", recent[2]);

        Assert.Empty(NewService().ReadRecentLogLines(0));
    }

    // ==================== Install：dsh 环境前置检查 ====================

    /// <summary>&lt;DSH_HOME&gt;\profiles 整体不存在（本机没装过 dsh）：拒绝安装且不产生任何改动。</summary>
    [Fact]
    public void Install_WhenProfilesDirectoryMissing_ReturnsFalseAndChangesNothing()
    {
        CreateSourceDir();
        try
        {
            var service = NewService();

            var ok = service.Install(out var message);

            Assert.False(ok);
            Assert.Contains("未检测到 dsh 环境", message);
            // 前置检查必须不改动磁盘：既不建 profiles 目录树，也不写插件目录与 patch
            Assert.False(Directory.Exists(Path.Combine(_base, "profiles")));
            Assert.False(Directory.Exists(InstallDir));
            Assert.False(File.Exists(PatchPath));
        }
        finally
        {
            DeleteSourceDir();
        }
    }

    /// <summary>profiles 存在但 profiles\&lt;profile&gt; 不存在：属于正常情况，安装应成功并创建该 profile 目录。</summary>
    [Fact]
    public void Install_WhenOnlyProfileSubdirectoryMissing_Succeeds()
    {
        CreateSourceDir();
        Directory.CreateDirectory(Path.Combine(_base, "profiles"));   // 模拟装过 dsh，但还没有这个 profile
        try
        {
            var service = NewService();

            var ok = service.Install(out var message);

            Assert.True(ok);
            Assert.Contains("已安装", message);
            Assert.True(Directory.Exists(InstallDir));
            Assert.True(File.Exists(PatchPath));
            Assert.True(service.IsInstalled());
        }
        finally
        {
            DeleteSourceDir();
        }
    }
}
