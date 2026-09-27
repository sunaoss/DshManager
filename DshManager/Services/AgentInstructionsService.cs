using System.Globalization;
using System.IO;
using System.Text;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>
/// 指令链（记忆文件）发现 + 记忆预算读写服务。
///
/// 指令链机制（来自官方包 @deepseek-ai/dsh-agent-instructions，只读调研确认）：
/// ① 用户全局：&lt;DSH_HOME&gt;\AGENTS.md；
/// ② 项目级：从项目根到会话工作目录的每一层目录中所有存在的候选文件，按「从宽泛到具体」顺序；
///    基础候选 AGENTS.md / CLAUDE.md，本地覆盖 AGENTS.local.md / CLAUDE.local.md；
/// ③ 项目根由 .git 标记向上查找；同级（同目录内）去首尾空白后内容相同的文件只加载一次；
/// ④ maxBytes（默认 65536）是「全局 + 项目级」合起来的渲染上限，maxSourceBytes（默认 1048576）是单文件上限。
///
/// 【记忆预算该写到哪：调研结论】
/// dsh 的 patch 机制（<c>&lt;DSH_HOME&gt;\profiles\&lt;name&gt;\cordis.patch.yml</c> 与 home 级
/// <c>&lt;DSH_HOME&gt;\cordis.patch.yml</c>）只作用于「profile 组合树」（bundle 层），按 id 定位条目且
/// <b>写 config 就是整块替换</b>（不深度合并，见 @deepseek-ai/cordis-plugin-include 的
/// <c>applyEntryPatches</c>：<c>target[key] = value</c>）。但在 web profile 里
/// <c>dsh-web-app/cordis.patch.yml</c> 已把宿主那条 agent-instructions 置为 <c>disabled: true</c>，
/// 真正生效的 agent-instructions 行来自「人格卡」组装文件
/// <c>&lt;DSH_HOME&gt;\.agent-presets\&lt;id&gt;\agent.cordis.yml</c>；而人格卡由 dsh-agent-presets 直接
/// 组合挂载，<b>没有 patch 层</b>（官方文档明确「本层也没有表达『standard 加一处改动』的 patch 语义」）。
/// 因此改预算的官方可行做法 = 在用户选定的人格卡里改 agent-instructions 的 config；本服务即按此实现
/// （文本级行替换 + 写前备份，保证文件其余部分逐字节不变）。随附只读人格卡拒绝修改。
/// </summary>
public class AgentInstructionsService
{
    /// <summary>agent-instructions 渲染预算的官方默认值（64 KB）。</summary>
    public const int DefaultMaxBytes = 65536;

    /// <summary>单个源指令文件的官方默认上限（1 MB）。</summary>
    public const int DefaultMaxSourceBytes = 1048576;

    /// <summary>每层目录的基础候选文件名（按此顺序加载）。</summary>
    public static readonly string[] InstructionFileCandidates = { "AGENTS.md", "CLAUDE.md" };

    /// <summary>每层目录的本地覆盖候选文件名（在基础候选之后加载）。</summary>
    public static readonly string[] LocalOverlayCandidates = { "AGENTS.local.md", "CLAUDE.local.md" };

    /// <summary>标记项目根的目录名。</summary>
    public const string ProjectRootMarker = ".git";

    /// <summary>用户可写的人格卡根目录名。</summary>
    private const string AgentPresetsDirName = ".agent-presets";
    /// <summary>人格卡组装文件名。</summary>
    private const string PresetCompositionFileName = "agent.cordis.yml";
    /// <summary>未显式指定时 dsh 的默认 preset id（dsh-web-app 的 agent-presets.default）。</summary>
    private const string DefaultPresetId = "standard";
    /// <summary>settings.yaml 中记录默认 preset 的点路径。</summary>
    private const string PresetSettingPath = "agent-presets.default";
    private const string SettingsFileName = "settings.yaml";
    /// <summary>目标条目的 id 与它 config 内的两个预算键。</summary>
    private const string InstructionsEntryId = "agent-instructions";
    private const string MaxBytesKey = "maxBytes";
    private const string MaxSourceBytesKey = "maxSourceBytes";

    private readonly string _dshHome;

    /// <summary>用户全局指令文件路径：&lt;DSH_HOME&gt;\AGENTS.md。</summary>
    public string GlobalFilePath { get; }

    /// <summary>构造；dshHome 为空时用 DshDiscoveryService.GetDefaultDshHome()。</summary>
    public AgentInstructionsService(string? dshHome = null)
    {
        _dshHome = string.IsNullOrWhiteSpace(dshHome) ? DshDiscoveryService.GetDefaultDshHome() : dshHome;
        GlobalFilePath = Path.Combine(_dshHome, "AGENTS.md");
    }

    /// <summary>dsh 数据目录（&lt;DSH_HOME&gt;）。</summary>
    public string DshHome => _dshHome;

    // ==================== 项目根发现 ====================

    /// <summary>
    /// 从 workDirectory 向上查找含 .git 的目录作为项目根；找不到返回 workDirectory 本身。
    /// 只读、异常安全：标记探测失败不中断上溯。
    /// </summary>
    /// <param name="workDirectory">会话工作目录。</param>
    /// <param name="directoryHasMarker">注入的标记探测（便于单测）；为 null 时探测真实文件系统的 .git。</param>
    public static string FindProjectRoot(string workDirectory, Func<string, bool>? directoryHasMarker = null)
        => FindProjectRootCore(workDirectory, directoryHasMarker).Path;

    /// <summary>项目根发现核心：同时返回是否真的命中标记（供 DiscoverChain 填 ProjectRootFound）。</summary>
    private static (string Path, bool Found) FindProjectRootCore(string workDirectory, Func<string, bool>? directoryHasMarker)
    {
        if (string.IsNullOrWhiteSpace(workDirectory)) return (string.Empty, false);

        string start;
        try { start = Path.GetFullPath(workDirectory); }
        catch { return (workDirectory, false); }   // 路径非法（含非法字符等）→ 原样返回，不抛

        var hasMarker = directoryHasMarker ?? HasProjectRootMarker;
        var current = start;
        while (true)
        {
            try
            {
                if (hasMarker(current)) return (current, true);
            }
            catch
            {
                // 标记探测异常（权限 / IO）按「本层无标记」处理，继续上溯，保证界面不崩
            }

            string? parent;
            try { parent = Path.GetDirectoryName(current); }
            catch { parent = null; }

            // 到盘根仍未命中 → 没有项目根，按约定返回工作目录本身
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return (start, false);

            current = parent;
        }
    }

    /// <summary>真实文件系统探测：目录下存在 .git（目录或文件，worktree 场景下 .git 是文件）。</summary>
    private static bool HasProjectRootMarker(string directory)
        => Directory.Exists(Path.Combine(directory, ProjectRootMarker))
           || File.Exists(Path.Combine(directory, ProjectRootMarker));

    // ==================== 指令链发现 ====================

    /// <summary>
    /// 发现完整指令链（项目根 → 工作目录，每层的基础候选 + 本地覆盖候选，只返回存在的文件，按从宽泛到具体排序）。
    /// 纯只读、异常安全：任何 IO / 权限失败都退化为空结果，绝不修改文件。
    /// </summary>
    public InstructionChain DiscoverChain(string workDirectory)
    {
        if (string.IsNullOrWhiteSpace(workDirectory)) return new InstructionChain();

        try
        {
            var cwd = Path.GetFullPath(workDirectory);
            var (root, rootFound) = FindProjectRootCore(cwd, null);
            if (root.Length == 0) return new InstructionChain { WorkDirectory = cwd };

            // 组装「项目根 → 工作目录」目录链（从宽泛到具体）；root 是 cwd 的祖先（或等于 cwd）
            var directories = BuildAncestorChain(root, cwd);
            var maxDepth = directories.Count - 1;
            var files = new List<InstructionFile>();
            long total = 0;

            for (var depth = 0; depth < directories.Count; depth++)
            {
                var directory = directories[depth];
                var isProjectRoot = depth == 0;
                var isWorkDirectory = PathEquals(directory, cwd);
                var keptTrimmedContent = new HashSet<string>(StringComparer.Ordinal);   // 同目录内容去重

                // 每层顺序固定：基础候选 → 本地覆盖候选
                total += AppendExistingFiles(files, directory, depth, maxDepth, isProjectRoot, isWorkDirectory,
                    InstructionFileCandidates, false, keptTrimmedContent);
                total += AppendExistingFiles(files, directory, depth, maxDepth, isProjectRoot, isWorkDirectory,
                    LocalOverlayCandidates, true, keptTrimmedContent);
            }

            return new InstructionChain
            {
                WorkDirectory = cwd,
                ProjectRoot = root,
                ProjectRootFound = rootFound,
                Files = files,
                TotalBytes = total,
            };
        }
        catch
        {
            // 目录不存在 / 权限不足等一律退化为空链，保证界面不崩
            return new InstructionChain { WorkDirectory = workDirectory };
        }
    }

    /// <summary>把某层目录下存在的候选文件追加进结果，返回新增字节数（只读，不改文件）。</summary>
    private static long AppendExistingFiles(
        List<InstructionFile> target, string directory, int depth, int maxDepth,
        bool isProjectRoot, bool isWorkDirectory, string[] candidates, bool isLocalOverlay,
        HashSet<string> keptTrimmedContent)
    {
        long added = 0;
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            var size = TryGetFileSize(path);
            if (size is null) continue;   // 只返回真实存在的文件

            // 官方语义：同目录内去首尾空白后内容相同的文件只加载一次（复制来的 CLAUDE.md 不重复注入）
            if (!ShouldKeepByContent(path, keptTrimmedContent)) continue;

            target.Add(new InstructionFile
            {
                Path = path,
                FileName = candidate,
                Directory = directory,
                Depth = depth,
                Exists = true,
                SizeBytes = size.Value,
                IsLocalOverlay = isLocalOverlay,
                ScopeText = BuildScopeText(depth, isProjectRoot, isWorkDirectory, isLocalOverlay),
                SizeText = FormatSize(size.Value),
                PriorityText = BuildPriorityText(depth, maxDepth),
            });
            added += size.Value;
        }
        return added;
    }

    /// <summary>由 root（含）到 cwd（含）的目录链，顺序为从宽泛到具体。</summary>
    private static List<string> BuildAncestorChain(string root, string cwd)
    {
        var chain = new List<string>();
        var current = cwd;
        while (!PathEquals(current, root))
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathEquals(parent, current)) break;   // 兜底：理论上 root 是 cwd 祖先
            current = parent;
        }
        chain.Add(root);
        chain.Reverse();   // 反转成 项目根 → 工作目录
        return chain;
    }

    /// <summary>按「去首尾空白后的内容」做同目录去重；读取失败时保守放行（宁可重复展示也不漏文件）。</summary>
    private static bool ShouldKeepByContent(string path, HashSet<string> keptTrimmedContent)
    {
        try
        {
            return keptTrimmedContent.Add(File.ReadAllText(path, Encoding.UTF8).Trim());
        }
        catch
        {
            return true;
        }
    }

    /// <summary>文件大小（字节）；不存在或读取失败返回 null。</summary>
    private static long? TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>层级描述（中文）：项目根 / 工作目录 / 上级目录（第 N 层），本地覆盖额外标注。</summary>
    private static string BuildScopeText(int depth, bool isProjectRoot, bool isWorkDirectory, bool isLocalOverlay)
    {
        var scope = isWorkDirectory && !isProjectRoot
            ? "工作目录"
            : isProjectRoot ? "项目根" : $"上级目录（第 {depth} 层）";
        return isLocalOverlay ? scope + "（本地覆盖）" : scope;
    }

    /// <summary>优先级说明：越靠项目根越宽泛，越靠工作目录越具体。</summary>
    private static string BuildPriorityText(int depth, int maxDepth)
    {
        if (depth == 0) return "低（较宽泛）";
        if (depth >= maxDepth) return "高（最具体）";
        return $"中（第 {depth} 层）";
    }

    /// <summary>人类可读大小：&lt;1 KB 用 B，&lt;1 MB 用 KB，否则用 MB（保留 1 位小数）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Windows 下路径比较统一用 OrdinalIgnoreCase。</summary>
    private static bool PathEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // ==================== 记忆预算读取 ====================

    /// <summary>
    /// 读取当前生效的记忆预算：优先取「用户选定人格卡」里 agent-instructions 的 config；
    /// 读不到就用官方默认值并把 IsDefault 置 true。
    /// </summary>
    public DshBudgetConfig ReadBudget()
    {
        try
        {
            var card = ResolveActivePresetCard();
            if (card is null) return DefaultBudget();

            var lines = SplitLines(File.ReadAllText(card.Path, Encoding.UTF8));
            var (start, end) = FindEntryBlock(lines, InstructionsEntryId);
            if (start < 0) return DefaultBudget();

            var maxBytes = ReadIntValue(lines, start, end, MaxBytesKey);
            if (maxBytes is null || maxBytes.Value <= 0) return DefaultBudget();

            return new DshBudgetConfig
            {
                MaxBytes = maxBytes.Value,
                MaxSourceBytes = ReadIntValue(lines, start, end, MaxSourceBytesKey) ?? DefaultMaxSourceBytes,
                IsDefault = false,
                SourceDescription = $"人格卡 {card.PresetId} 的 {PresetCompositionFileName}",
            };
        }
        catch
        {
            return DefaultBudget();
        }
    }

    /// <summary>官方默认预算（配置读不到时使用）。</summary>
    private static DshBudgetConfig DefaultBudget() => new()
    {
        MaxBytes = DefaultMaxBytes,
        MaxSourceBytes = DefaultMaxSourceBytes,
        IsDefault = true,
        SourceDescription = "官方默认值",
    };

    // ==================== 记忆预算写入 ====================

    /// <summary>
    /// 写入记忆预算；message 说明写到哪个文件、结果如何。
    /// 采用「文本级行替换」改写用户选定人格卡里 agent-instructions 的 maxBytes / maxSourceBytes，
    /// 保证文件其余部分逐字节不变；写前先做带时间戳的备份（同名不覆盖）。随附只读人格卡拒绝修改。
    /// </summary>
    public bool WriteBudget(int maxBytes, int maxSourceBytes, out string message)
    {
        // ① 入参校验：两个都必须是正数，且源文件上限不小于渲染预算
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
        if (maxSourceBytes < maxBytes)
        {
            message = $"maxSourceBytes（{maxSourceBytes}）不能小于 maxBytes（{maxBytes}）：单个源文件上限至少要不小于渲染预算";
            return false;
        }

        try
        {
            var card = ResolveActivePresetCard();
            if (card is null)
            {
                message = $"未找到生效的人格卡（已按 settings.yaml 的 {PresetSettingPath} 查找），无法写入预算";
                return false;
            }
            if (!card.Writable)
            {
                message = $"生效的是随附只读人格卡 {card.PresetId}（{card.Path}），拒绝修改；请先在面板里复制一份自有预设";
                return false;
            }

            // 用字节读取，记录是否存在 UTF-8 BOM，写回时原样保留（保证「其余部分逐字节不变」）
            var raw = File.ReadAllBytes(card.Path);
            var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var original = new UTF8Encoding(false).GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));
            var newline = DetectNewline(original);
            var lines = SplitLines(original);
            var (start, end) = FindEntryBlock(lines, InstructionsEntryId);
            if (start < 0)
            {
                message = $"人格卡 {PresetCompositionFileName} 里找不到 {InstructionsEntryId} 条目，未做修改";
                return false;
            }

            // ② 逐个键做行级替换（有则改该行、无则在该条目的 config 块内追加）
            var updated = SetEntryIntValue(lines, start, end, MaxBytesKey, maxBytes);
            if (updated is not null)
            {
                (start, end) = FindEntryBlock(updated, InstructionsEntryId);
                updated = SetEntryIntValue(updated, start, end, MaxSourceBytesKey, maxSourceBytes);
            }
            if (updated is null)
            {
                message = $"{InstructionsEntryId} 的 config 是内联写法，无法安全做行级替换，未做修改";
                return false;
            }

            // ③ 写前备份，再整体写回（保留原换行风格与 BOM 状态）
            var backupPath = BackupFile(card.Path);
            var encoding = new UTF8Encoding(false);
            var newText = string.Join(newline, updated);
            var body = encoding.GetBytes(newText);
            var output = hasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
            File.WriteAllBytes(card.Path, output);

            message = $"已写入 {card.Path}（人格卡 {card.PresetId}）：maxBytes={maxBytes}、" +
                      $"maxSourceBytes={maxSourceBytes}；备份：{backupPath}。" +
                      $"说明：profile 的 cordis.patch.yml 无法覆盖人格卡内的该行，故直接改人格卡";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入记忆预算失败：{ex.Message}";
            return false;
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

    // ==================== 人格卡解析 ====================

    /// <summary>一张可定位的人格卡：id、组装文件绝对路径、是否可写（用户根可写，随附根只读）。</summary>
    private sealed record PresetCard(string PresetId, string Path, bool Writable);

    /// <summary>
    /// 定位生效人格卡：先按 settings.yaml 的 agent-presets.default 取 id（缺省 standard），
    /// 先找用户根（&lt;DSH_HOME&gt;\.agent-presets）、再找随附根（dsh-agent-presets 包内）。
    /// </summary>
    private PresetCard? ResolveActivePresetCard()
    {
        var presetId = ReadDefaultPresetId();
        foreach (var card in EnumeratePresetCards(presetId))
        {
            try
            {
                if (File.Exists(card.Path)) return card;
            }
            catch
            {
                // 单个候选探测失败时继续尝试下一个
            }
        }
        return null;
    }

    /// <summary>按「用户根（可写）→ 随附根（只读）」顺序给出候选人格卡。</summary>
    private IEnumerable<PresetCard> EnumeratePresetCards(string presetId)
    {
        // 【为什么必须校验】presetId 直接来自 settings.yaml 的 agent-presets.default，
        // 而 settings.yaml 是磁盘上的可写文件（dsh 自身 / 用户都会改）。
        // 不校验就 Path.Combine，p… 这类值会把路径拼到 _dshHome 之外，
        // 随后 WriteBudget 会对该路径 File.WriteAllBytes ⇒ 任意文件覆盖。
        // 只接受官方 id 规则（[a-z0-9][a-z0-9-]*），同时挡住分隔符与 ".."。
        if (!PersonaService.IsValidId(presetId))
            yield break;

        yield return new PresetCard(
            presetId,
            Path.Combine(_dshHome, AgentPresetsDirName, presetId, PresetCompositionFileName),
            Writable: true);

        yield return new PresetCard(
            presetId,
            Path.Combine(_dshHome, "profiles", "node_modules", "@deepseek-ai", "dsh-agent-presets",
                         "presets", presetId, PresetCompositionFileName),
            Writable: false);
    }

    /// <summary>读 settings.yaml 的 agent-presets.default；缺失 / 异常时回落到 standard。</summary>
    private string ReadDefaultPresetId()
    {
        try
        {
            var settingsPath = Path.Combine(_dshHome, SettingsFileName);
            if (!File.Exists(settingsPath)) return DefaultPresetId;

            var map = YamlTextEditor.Read(File.ReadAllText(settingsPath, Encoding.UTF8));
            var value = map.TryGetValue(PresetSettingPath, out var raw) ? YamlTextEditor.Unquote(raw) : "";
            return string.IsNullOrWhiteSpace(value) ? DefaultPresetId : value.Trim();
        }
        catch
        {
            return DefaultPresetId;
        }
    }

    // ==================== 行级 YAML 小工具 ====================
    // 为什么不解析成对象再序列化：人格卡是官方 / 用户手写的组合文件，整块重写会吃掉注释、
    // 改变缩进与顺序。这里只动目标那一行，其余行原样搬过去。

    /// <summary>按原换行风格拆行（末尾的尾部空串代表「文件以换行结尾」）。</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>探测原文件换行风格，写回时保持一致。</summary>
    private static string DetectNewline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>定位 <c>- id: &lt;entryId&gt;</c> 条目的行区间：Start 含、End 为下一个同级条目（或文件末尾）。</summary>
    private static (int Start, int End) FindEntryBlock(string[] lines, string entryId)
    {
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;
            if (!string.Equals(trimmed.Substring(2).Trim(), "id: " + entryId, StringComparison.Ordinal)) continue;
            start = i;
            break;
        }
        if (start < 0) return (-1, -1);

        var indent = LeadingSpaces(lines[start]);
        var end = start + 1;
        while (end < lines.Length)
        {
            var trimmed = lines[end].TrimStart();
            // 只有「同级或更浅缩进的 - 项」才是条目边界；config 里的嵌套列表缩进更深，不算
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && LeadingSpaces(lines[end]) <= indent) break;
            end++;
        }
        return (start, end);
    }

    /// <summary>读条目块内某个 <c>key: 数字</c> 的整数值；不存在或非数字返回 null。</summary>
    private static int? ReadIntValue(string[] lines, int start, int end, string key)
    {
        for (var i = start; i < end; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var value = trimmed.Substring(key.Length + 1).Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// 在条目块内设置 <c>key: value</c>：有该键就只改那一行（保留缩进）；没有就在 config 块内追加，
    /// 连 config 都没有则新建 config 块。发现 config 是内联写法（<c>config: {..}</c>）时返回 null 拒绝写入。
    /// </summary>
    private static string[]? SetEntryIntValue(string[] lines, int start, int end, string key, int value)
    {
        if (start < 0) return null;
        var list = new List<string>(lines);
        var text = value.ToString(CultureInfo.InvariantCulture);

        // 定位 config: 行；同时排除内联 config（行级替换会破坏结构）
        var configLine = -1;
        for (var i = start + 1; i < end; i++)
        {
            var trimmed = list[i].Trim();
            if (!trimmed.StartsWith("config:", StringComparison.Ordinal)) continue;
            if (trimmed.Length > "config:".Length) return null;
            configLine = i;
            break;
        }

        // ① 已有目标键 → 只改那一行
        for (var i = start + 1; i < end; i++)
        {
            var trimmed = list[i].TrimStart();
            if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            list[i] = new string(' ', LeadingSpaces(list[i])) + key + ": " + text;
            return list.ToArray();
        }

        // ② 无 config 行 → 在条目末尾新建 config 块
        if (configLine < 0)
        {
            var entryIndent = LeadingSpaces(list[start]);
            var insertAt = ClampInsertIndex(list, end);
            list.Insert(insertAt, new string(' ', entryIndent + 4) + key + ": " + text);
            list.Insert(insertAt, new string(' ', entryIndent + 2) + "config:");
            return list.ToArray();
        }

        // ③ 有 config 行但缺该键 → 追加到 config 块内，沿用同级缩进
        var configIndent = LeadingSpaces(list[configLine]);
        var childIndent = configIndent + 2;
        var target = configLine + 1;
        for (var i = configLine + 1; i < end; i++)
        {
            if (list[i].Trim().Length == 0) continue;            // 空行不推进插入点，避免把新键插到分隔空行之后
            if (LeadingSpaces(list[i]) <= configIndent) break;   // config 块结束
            childIndent = LeadingSpaces(list[i]);
            target = i + 1;
        }
        list.Insert(ClampInsertIndex(list, target), new string(' ', childIndent) + key + ": " + text);
        return list.ToArray();
    }

    /// <summary>修正插入点：落在文件末尾时往前跳过尾部空行，避免把结尾换行挤成中间空行。</summary>
    private static int ClampInsertIndex(List<string> lines, int index)
    {
        var at = Math.Min(Math.Max(index, 0), lines.Count);
        if (at == lines.Count)
        {
            while (at > 0 && lines[at - 1].Trim().Length == 0) at--;
        }
        return at;
    }

    /// <summary>行首空格数（YAML 用空格缩进）。</summary>
    private static int LeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }
}
