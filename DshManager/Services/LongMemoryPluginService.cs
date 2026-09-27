using System.Globalization;
using System.IO;
using System.Text;

namespace DshManager.Services;

/// <summary>扩展记忆插件的配置（从 settings.yaml 的 long-memory 段解析）。</summary>
public class LongMemoryConfig
{
    public int MaxBytes { get; init; } = 262144;
    public int MaxSourceBytes { get; init; } = 2097152;
    public bool Enabled { get; init; } = true;
    /// <summary>记忆文件路径；空 = 默认 &lt;DSH_HOME&gt;\MEMORY.md。</summary>
    public string File { get; init; } = string.Empty;
    /// <summary>是否未在 settings.yaml 中配置（即用的插件默认值）。</summary>
    public bool IsDefault { get; init; }
    /// <summary>配置来源描述，如「&lt;DSH_HOME&gt;\settings.yaml 的 long-memory 段」。</summary>
    public string SourceDescription { get; init; } = string.Empty;
}

/// <summary>
/// 扩展记忆（大容量插件 dsh-long-memory）管理服务：安装 / 卸载、记忆文件读写、settings.yaml 配置读写、运行日志。
///
/// 【为什么安装要自己实现而不是调 dsh 命令】已实测：该插件是「patch 挂载」，
/// <c>dsh plugin add</c> 会因 pnpm link + ESM realpath 解析不到 @deepseek-ai/schemastery 而静默失败，
/// 且它不出现在 <c>dsh plugin list</c>、<c>dsh plugin remove</c> 也卸不掉。因此本服务用
/// 「复制插件目录到 profile + 在 cordis.patch.yml 追加 insert」的方式安装，卸载则反向操作。
///
/// 【cordis.patch.yml 为什么用文本级处理】该文件是「顶层 YAML 数组 + 注释头」，其它插件行也在里面。
/// 解析成对象再序列化会吃掉注释、改变缩进与顺序，所以这里只在文本层定位 <c>- insert:</c> 块与其中的
/// <c>id: long-memory</c> 项，只增删目标行，其余行原样保留。任何修改前都先做带时间戳的备份。
///
/// 【settings.yaml 为什么复用 YamlTextEditor】同一原因：settings.yaml 由官方维护、可能含新键与注释，
/// 只按点路径改目标行，避免整文件重写吃掉未知内容。
///
/// 全部为纯本地文件操作，不启动任何进程；一切异常都转为 false + 中文 message，绝不抛到界面。
/// </summary>
public class LongMemoryPluginService
{
    private const string PluginDirName = "dsh-long-memory";
    private const string PluginSourceFolderName = "dsh-plugin";
    private const string DefaultProfileName = "web";
    private const string PatchFileName = "cordis.patch.yml";
    private const string SettingsFileName = "settings.yaml";
    private const string MemoryFileName = "MEMORY.md";
    private const string LogFileName = "dsh-long-memory.log";

    /// <summary>插件在 patch 里的条目 id（= 插件 name）。</summary>
    private const string PluginId = "long-memory";
    /// <summary>settings.yaml 里的配置命名空间（与插件 SETTINGS_NS 一致）。</summary>
    private const string SettingsNamespace = "long-memory";
    /// <summary>patch 挂载时使用的 profile 相对路径（这样插件内 @deepseek-ai/* 裸包能从 profile 的 node_modules 解析）。</summary>
    private const string PluginMountPath = "./plugins/dsh-long-memory/index.js";

    /// <summary>插件 schema 的默认注入字节上限（256 KB）。</summary>
    private const int DefaultMaxBytes = 262144;
    /// <summary>插件 schema 的默认单文件读取上限（2 MB）。</summary>
    private const int DefaultMaxSourceBytes = 2097152;

    private readonly string _dshHome;

    /// <summary>构造；dshHome 为空时用 DshDiscoveryService.GetDefaultDshHome()。</summary>
    public LongMemoryPluginService(string? dshHome = null, string? profileName = null)
    {
        _dshHome = string.IsNullOrWhiteSpace(dshHome) ? DshDiscoveryService.GetDefaultDshHome() : dshHome;
        ProfileName = string.IsNullOrWhiteSpace(profileName) ? DefaultProfileName : profileName;
    }

    /// <summary>dsh 数据目录（&lt;DSH_HOME&gt;）。</summary>
    public string DshHome => _dshHome;

    /// <summary>profile 名（默认为本机在用的 web）。</summary>
    public string ProfileName { get; }

    /// <summary>
    /// 记忆文件路径：配置里的 file（相对路径按"相对 DSH_HOME"解析），未配置则为 &lt;DSH_HOME&gt;\MEMORY.md。
    /// </summary>
    public string MemoryFilePath
    {
        get
        {
            var configured = ReadConfig().File;
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(_dshHome, MemoryFileName);

            var resolved = Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(_dshHome, configured));

            // 【为什么这里不再过滤】long-memory.file 是插件的正式配置项，允许指向 DSH_HOME 之外
            // （用户可能把记忆文件放到别处、或放在同步盘里）。读路径必须原样尊重它。
            // 任意文件覆盖的风险改由写入侧把关：见 TryResolveWritePath / WriteMemory。
            return resolved;
        }
    }

    /// <summary>
    /// 解析可用于"写入"的记忆文件路径。仅当目标位于 DSH_HOME 之内时才放行。
    /// </summary>
    /// <remarks>
    /// 【为什么要单独收紧写入】long-memory.file 来自磁盘上的 settings.yaml（插件/用户可改），
    /// WriteMemory 会对它 CreateDirectory + 整份覆盖写。若不加限制，构造一个任意绝对路径
    /// 就等于给插件开了"任意文件覆盖"开关——连 dsh 自己的配置都能被写掉。
    /// 读侧宽松（尊重用户配置）、写侧收紧（只在自己家里动土），是这个配置项的合理边界。
    /// </remarks>
    private bool TryResolveWritePath(out string path)
    {
        path = MemoryFilePath;
        return IsUnderDshHome(path);
    }

    /// <summary>判断路径是否位于 DSH_HOME 之内（含自身）。</summary>
    private bool IsUnderDshHome(string fullPath)
    {
        try
        {
            var root = Path.GetFullPath(_dshHome)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>插件源目录：&lt;程序目录&gt;\dsh-plugin\dsh-long-memory（随程序发布）。</summary>
    public string PluginSourceDirectory => Path.Combine(AppContext.BaseDirectory, PluginSourceFolderName, PluginDirName);

    /// <summary>插件安装目录：&lt;DSH_HOME&gt;\profiles\&lt;profile&gt;\plugins\dsh-long-memory。</summary>
    public string PluginInstallDirectory => Path.Combine(_dshHome, "profiles", ProfileName, "plugins", PluginDirName);

    /// <summary>profile 的 patch 文件：&lt;DSH_HOME&gt;\profiles\&lt;profile&gt;\cordis.patch.yml。</summary>
    public string PatchFilePath => Path.Combine(_dshHome, "profiles", ProfileName, PatchFileName);

    /// <summary>插件运行日志：&lt;DSH_HOME&gt;\dsh-long-memory.log。</summary>
    public string LogFilePath => Path.Combine(_dshHome, LogFileName);

    // ==================== 安装 / 卸载 ====================

    /// <summary>是否已安装：插件目录存在 且 patch 文件里有 id 为 long-memory 的 insert。</summary>
    public bool IsInstalled()
    {
        try
        {
            if (!Directory.Exists(PluginInstallDirectory)) return false;
            if (!File.Exists(PatchFilePath)) return false;
            return ContainsLongMemoryInsert(SplitLines(File.ReadAllText(PatchFilePath, Encoding.UTF8)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>安装：复制插件目录到 profile + 在 patch 数组追加 insert 行；写前备份 patch。</summary>
    public bool Install(out string message)
    {
        // ① 源目录缺失（安装包不完整）→ 明确拒绝，不做任何改动
        if (!Directory.Exists(PluginSourceDirectory))
        {
            message = $"插件源文件缺失（{PluginSourceDirectory}），可能安装包不完整，请确认程序目录下有 dsh-plugin\\dsh-long-memory";
            return false;
        }

        // ② dsh 环境前置检查：只有当整个 <DSH_HOME>\profiles 目录不存在时才拒绝——
        //    说明本机还没装过 / 没启动过 DeepSeek Harness，此时若继续会凭空造出空 profile 目录树。
        //    注意：只判 profiles 整体不存在；profiles\<profile> 缺失属于正常情况，创建它是合理的。
        var profilesDirectory = Path.Combine(_dshHome, "profiles");
        if (!Directory.Exists(profilesDirectory))
        {
            message = $"未检测到 dsh 环境（{profilesDirectory} 不存在），请先安装 DeepSeek Harness 并至少启动过一次，再点「一键安装」";
            return false;
        }

        try
        {
            // ③ 先删目标目录再整体递归复制：保证「更新安装」不残留旧文件
            CopyDirectory(PluginSourceDirectory, PluginInstallDirectory);

            // ④ patch：已有 long-memory 的 insert 就幂等跳过，避免重复挂载同一个插件
            var patchExisted = File.Exists(PatchFilePath);
            var raw = patchExisted ? File.ReadAllText(PatchFilePath, Encoding.UTF8) : string.Empty;
            var newline = patchExisted ? DetectNewline(raw) : "\n";
            var lines = patchExisted ? SplitLines(raw) : new[] { string.Empty };   // 新文件用一个尾空串占位，保证结果以换行结尾

            if (ContainsLongMemoryInsert(lines))
            {
                message = $"插件已复制到 {PluginInstallDirectory}；{PatchFilePath} 里已存在 long-memory 的 insert，无需重复追加";
                return true;
            }

            var backup = patchExisted ? BackupFile(PatchFilePath) : null;
            var updated = new List<string>(lines);
            var insertAt = updated.Count;
            while (insertAt > 0 && updated[insertAt - 1].Trim().Length == 0) insertAt--;   // 跳过尾部换行标记
            updated.InsertRange(insertAt, BuildInsertBlockLines());

            Directory.CreateDirectory(Path.GetDirectoryName(PatchFilePath)!);
            File.WriteAllText(PatchFilePath, string.Join(newline, updated), new UTF8Encoding(false));
            message = backup is null
                ? $"已安装：插件复制到 {PluginInstallDirectory}，并新建 {PatchFilePath} 写入 insert；请重启 dsh 生效"
                : $"已安装：插件复制到 {PluginInstallDirectory}，并在 {PatchFilePath} 追加 insert（备份：{backup}）；请重启 dsh 生效";
            return true;
        }
        catch (Exception ex)
        {
            message = $"安装失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>卸载：备份 patch → 移除 long-memory 的 insert 项 → 删除插件目录与日志。</summary>
    public bool Uninstall(out string message)
    {
        try
        {
            var steps = new List<string>();

            // ① 改 patch：定位 insert 块里的 long-memory 项并移除；其余插件行与注释头不动
            if (File.Exists(PatchFilePath))
            {
                var raw = File.ReadAllText(PatchFilePath, Encoding.UTF8);
                var (lines, removed) = RemoveLongMemoryInsert(SplitLines(raw));
                if (removed)
                {
                    var backup = BackupFile(PatchFilePath);
                    File.WriteAllText(PatchFilePath, string.Join(DetectNewline(raw), lines), new UTF8Encoding(false));
                    steps.Add($"已从 {PatchFilePath} 移除 long-memory 的 insert（备份：{backup}）");
                }
                else
                {
                    steps.Add($"{PatchFilePath} 里没有 long-memory 的 insert，未改动");
                }
            }
            else
            {
                steps.Add($"{PatchFilePath} 不存在，无需改 patch");
            }

            // ② 删插件目录与日志（存在才删）
            if (Directory.Exists(PluginInstallDirectory))
            {
                Directory.Delete(PluginInstallDirectory, recursive: true);
                steps.Add($"已删除插件目录 {PluginInstallDirectory}");
            }
            if (File.Exists(LogFilePath))
            {
                File.Delete(LogFilePath);
                steps.Add($"已删除日志 {LogFilePath}");
            }

            message = "已卸载：" + string.Join("；", steps) + "。请重启 dsh 生效";
            return true;
        }
        catch (Exception ex)
        {
            message = $"卸载失败：{ex.Message}";
            return false;
        }
    }

    // ==================== 记忆文件 ====================

    /// <summary>读取记忆文件全文；文件不存在返回空串。</summary>
    public string ReadMemory()
    {
        try
        {
            var path = MemoryFilePath;
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>写入记忆文件（写前备份 MEMORY.md）；插件按 mtime+size 自动重读，无需重启 dsh。</summary>
    public bool WriteMemory(string text, out string message)
    {
        try
        {
            // 写入侧收紧：只允许写 DSH_HOME 之内的路径（见 TryResolveWritePath 的说明）
            if (!TryResolveWritePath(out var path))
            {
                message = $"拒绝写入：配置的记忆文件不在 DSH_HOME 之内（{MemoryFilePath}）。"
                    + "为避免覆盖任意文件，请把 long-memory.file 改到 DSH_HOME 之下，或留空使用默认的 MEMORY.md。";
                return false;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var backup = File.Exists(path) ? BackupFile(path) : null;
            File.WriteAllText(path, text ?? string.Empty, new UTF8Encoding(false));

            var bytes = GetMemoryBytes();
            message = backup is null
                ? $"{path} 已创建并写入 {bytes} 字节（首次创建，无备份）"
                : $"{path} 已写入 {bytes} 字节（备份：{backup}）；插件按 mtime+size 自动重读，无需重启 dsh";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入记忆文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>记忆文件字节数；文件不存在或读取失败返回 0。</summary>
    public long GetMemoryBytes()
    {
        try
        {
            var path = MemoryFilePath;
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ==================== settings.yaml 配置 ====================

    /// <summary>解析 settings.yaml 的 long-memory 段；缺失 / 读取失败一律返回插件默认值（IsDefault=true）。</summary>
    public LongMemoryConfig ReadConfig()
    {
        try
        {
            var path = Path.Combine(_dshHome, SettingsFileName);
            if (!File.Exists(path)) return DefaultConfig("插件默认值（未找到 settings.yaml）");

            var map = YamlTextEditor.Read(File.ReadAllText(path, Encoding.UTF8));
            var prefix = SettingsNamespace + ".";

            // 只要该段里出现任意一个键，就认为用户已显式配置过
            var hasSection = false;
            foreach (var key in map.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) { hasSection = true; break; }
            }
            if (!hasSection) return DefaultConfig($"插件默认值（{path} 里没有 long-memory 段）");

            return new LongMemoryConfig
            {
                MaxBytes = ReadIntValue(map, prefix + "maxBytes") ?? DefaultMaxBytes,
                MaxSourceBytes = ReadIntValue(map, prefix + "maxSourceBytes") ?? DefaultMaxSourceBytes,
                Enabled = ReadBoolValue(map, prefix + "enabled") ?? true,
                File = map.TryGetValue(prefix + "file", out var raw) ? YamlTextEditor.Unquote(raw) : string.Empty,
                IsDefault = false,
                SourceDescription = $"{path} 的 long-memory 段",
            };
        }
        catch
        {
            return DefaultConfig("插件默认值（读取 settings.yaml 失败）");
        }
    }

    /// <summary>
    /// 写入 settings.yaml 的 long-memory 段（复用 YamlTextEditor 按行最小改写，保留注释与未知键）。
    /// 该段热生效：插件在每步读取 settings 解析值，无需重启 dsh。
    /// </summary>
    public bool WriteConfig(int maxBytes, int maxSourceBytes, bool enabled, out string message)
    {
        if (maxBytes <= 0)
        {
            message = $"maxBytes 必须为正数（当前 {maxBytes}）";
            return false;
        }
        if (maxSourceBytes <= 0)
        {
            message = $"maxSourceBytes 必须为正数（当前 {maxSourceBytes}）";
            return false;
        }

        try
        {
            var path = Path.Combine(_dshHome, SettingsFileName);
            var existed = File.Exists(path);
            var text = existed ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            var prefix = SettingsNamespace + ".";

            // 逐个键做行级替换（有则改该行、无则新建；缺父级会自动补齐 long-memory:）
            var updated = YamlTextEditor.SetRaw(text, prefix + "maxBytes", maxBytes.ToString(CultureInfo.InvariantCulture));
            if (updated is null) return Failure("maxBytes 写入失败：无法解析 settings.yaml", out message);

            updated = YamlTextEditor.SetRaw(updated, prefix + "maxSourceBytes", maxSourceBytes.ToString(CultureInfo.InvariantCulture));
            if (updated is null) return Failure("maxSourceBytes 写入失败：无法解析 settings.yaml", out message);

            updated = YamlTextEditor.SetRaw(updated, prefix + "enabled", enabled ? "true" : "false");
            if (updated is null) return Failure("enabled 写入失败：无法解析 settings.yaml", out message);

            // 保持文件以换行结尾（原文件已结尾则不改动）
            if (!updated.EndsWith("\n", StringComparison.Ordinal))
                updated += updated.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            var backup = existed ? BackupFile(path) : null;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, updated, new UTF8Encoding(false));

            message = $"已写入 {path} 的 long-memory 段：maxBytes={maxBytes}、maxSourceBytes={maxSourceBytes}、enabled={(enabled ? "true" : "false")}"
                    + (backup is null ? "（新建文件）" : $"（备份：{backup}）")
                    + "；热生效，无需重启 dsh";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入 settings.yaml 失败：{ex.Message}";
            return false;
        }
    }

    // ==================== 运行日志 ====================

    /// <summary>读取日志尾部最多 maxLines 行；文件不存在 / 参数非法返回空列表。</summary>
    public IReadOnlyList<string> ReadRecentLogLines(int maxLines)
    {
        if (maxLines <= 0) return Array.Empty<string>();
        try
        {
            var path = LogFilePath;
            if (!File.Exists(path)) return Array.Empty<string>();

            var lines = SplitLines(File.ReadAllText(path, Encoding.UTF8));
            var end = lines.Length;
            while (end > 0 && lines[end - 1].Trim().Length == 0) end--;   // 去掉尾部换行标记
            var start = Math.Max(0, end - maxLines);

            var result = new List<string>(end - start);
            for (var i = start; i < end; i++) result.Add(lines[i]);
            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ==================== patch 文本级处理 ====================

    /// <summary>生成要追加的 insert 块（缩进与官方 patch 一致：顶层 0、项 4、name/config 6、config 子项 8）。</summary>
    private static List<string> BuildInsertBlockLines() =>
    [
        "- insert:",
        $"    - id: {PluginId}",
        $"      name: '{PluginMountPath}'",
        "      config:",
        $"        maxBytes: {DefaultMaxBytes}",
        $"        maxSourceBytes: {DefaultMaxSourceBytes}",
        "        enabled: true",
    ];

    /// <summary>
    /// 判断 patch 行里是否已有 long-memory 的 insert 项。
    /// 只认「顶层 - insert: 块内的 - id: long-memory」（同时兼容 `- insert: [{ id: long-memory, ... }]` 内联写法），
    /// 避免把别的顶层条目里的同名字符串误判成已安装。
    /// </summary>
    private static bool ContainsLongMemoryInsert(string[] lines)
    {
        var inInsert = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;

            var indent = line.Length - trimmed.Length;
            var item = trimmed.Substring(2).Trim();

            if (indent == 0)
            {
                // 顶层条目：只有 insert 才进入「块内」状态，其它顶层条目自动离开
                inInsert = item.StartsWith("insert:", StringComparison.Ordinal);
                if (inInsert && item.Contains("id: " + PluginId, StringComparison.Ordinal)) return true;   // 内联写法
                continue;
            }
            if (inInsert && item.Contains("id: " + PluginId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// 从 patch 行里移除 long-memory 的 insert 项：
    /// 若该 insert 块只剩这一项则整块删掉，否则只删该项及其子行（name / config 及缩进更深的内容）。
    /// 返回是否真的发生了删除，调用方据此决定要不要写回与备份。
    /// </summary>
    private static (List<string> Lines, bool Removed) RemoveLongMemoryInsert(string[] lines)
    {
        var list = new List<string>(lines);
        var insertStart = -1;
        var itemStart = -1;

        // ① 定位顶层 - insert: 行与其内部的 - id: long-memory 行
        for (var i = 0; i < list.Count; i++)
        {
            var trimmed = list[i].TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;

            var indent = list[i].Length - trimmed.Length;
            var item = trimmed.Substring(2).Trim();

            if (indent == 0)
            {
                // 顶层条目：不是 insert 说明已离开上一个 insert 块 → 放弃；
                // 又是 insert（可能存在多个 insert 块）则切换到新块继续找
                if (!item.StartsWith("insert:", StringComparison.Ordinal))
                {
                    if (insertStart >= 0) break;
                    continue;
                }

                // 内联写法：`- insert: [{ id: long-memory, ... }]` 整行即该项，直接删这一行
                if (item.Contains("id: " + PluginId, StringComparison.Ordinal))
                {
                    list.RemoveAt(i);
                    return (list, true);
                }
                insertStart = i;
                continue;
            }

            if (insertStart >= 0 && item == "id: " + PluginId)
            {
                itemStart = i;
                break;
            }
        }

        if (insertStart < 0 || itemStart < 0) return (list, false);

        // ② 项区间：到下一个「同级或更浅缩进的 - 项」为止（config 子项缩进更深，算在本项内）
        var itemIndent = LeadingSpaces(list[itemStart]);
        var itemEnd = itemStart + 1;
        while (itemEnd < list.Count)
        {
            var t = list[itemEnd].TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) && LeadingSpaces(list[itemEnd]) <= itemIndent) break;
            itemEnd++;
        }

        // ③ insert 块区间：到下一个「顶层 - 项」为止
        var blockEnd = insertStart + 1;
        while (blockEnd < list.Count)
        {
            var t = list[blockEnd].TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) && LeadingSpaces(list[blockEnd]) == 0) break;
            blockEnd++;
        }

        // ④ 删掉本项后块内是否还剩别的 - 项
        var hasOtherItem = false;
        for (var i = insertStart + 1; i < blockEnd; i++)
        {
            if (i >= itemStart && i < itemEnd) continue;
            var t = list[i].TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) && LeadingSpaces(list[i]) > 0) { hasOtherItem = true; break; }
        }

        if (hasOtherItem)
        {
            var end = TrimTrailingBlank(lines, itemEnd);
            list.RemoveRange(itemStart, end - itemStart);
        }
        else
        {
            var end = TrimTrailingBlank(lines, blockEnd);
            list.RemoveRange(insertStart, end - insertStart);
        }
        return (list, true);
    }

    /// <summary>
    /// 若删除范围一直延伸到文件末尾，就把末尾的空白行排除在删除范围外——
    /// 那个空串只是「文件以换行结尾」的标记，删了会把结尾换行弄丢。
    /// </summary>
    private static int TrimTrailingBlank(string[] lines, int end)
    {
        if (end < lines.Length) return end;
        while (end > 0 && lines[end - 1].Trim().Length == 0) end--;
        return end;
    }

    // ==================== 小工具 ====================

    /// <summary>构造默认配置对象（用于配置缺失 / 读取失败）。</summary>
    private static LongMemoryConfig DefaultConfig(string sourceDescription) => new()
    {
        MaxBytes = DefaultMaxBytes,
        MaxSourceBytes = DefaultMaxSourceBytes,
        Enabled = true,
        File = string.Empty,
        IsDefault = true,
        SourceDescription = sourceDescription,
    };

    /// <summary>写配置失败时的统一返回（同时给 out message 赋值）。</summary>
    private static bool Failure(string text, out string message)
    {
        message = text;
        return false;
    }

    /// <summary>从解析出的 YAML 键值表里取整数；缺失 / 非数字返回 null。</summary>
    private static int? ReadIntValue(Dictionary<string, string> map, string path)
    {
        if (!map.TryGetValue(path, out var raw)) return null;
        var text = YamlTextEditor.Unquote(raw).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>从解析出的 YAML 键值表里取布尔；缺失 / 非法返回 null。</summary>
    private static bool? ReadBoolValue(Dictionary<string, string> map, string path)
    {
        if (!map.TryGetValue(path, out var raw)) return null;
        var text = YamlTextEditor.Unquote(raw).Trim().ToLowerInvariant();
        if (text == "true") return true;
        if (text == "false") return false;
        return null;
    }

    /// <summary>递归复制目录：先删目标再复制，保证目标内容与源完全一致。</summary>
    private static void CopyDirectory(string source, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>写前备份为 &lt;文件&gt;.bak.yyyyMMdd-HHmmss（已存在则追加序号，同名不覆盖）。</summary>
    private static string BackupFile(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = path + ".bak." + stamp;
        var seq = 1;
        while (File.Exists(backup)) backup = path + ".bak." + stamp + "-" + seq++;
        File.Copy(path, backup);
        return backup;
    }

    /// <summary>按原换行风格拆行（末尾的空串代表「文件以换行结尾」）。</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>探测原文件换行风格，写回时保持一致。</summary>
    private static string DetectNewline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>行首空格数（YAML 用空格缩进）。</summary>
    private static int LeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }
}
