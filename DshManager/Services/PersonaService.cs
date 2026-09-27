using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshManager.Services;

/// <summary>
/// 一张人格卡，对应 &lt;DSH_HOME&gt;\.agent-presets\&lt;id&gt;\ 下的一个预设目录。
/// dsh 的官方机制是「创作即复制」：新建人格 = 整目录复制一个随附预设，之后只改副本自己的文件。
/// </summary>
public class PersonaCard
{
    /// <summary>目录名，同时是 preset id；规则 [a-z0-9][a-z0-9-]*。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>界面显示名（preset.yml 的 name）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>一句话描述（preset.yml 的 description）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>预设排序权重（preset.yml 的 order；随附预设用 1/3/5…，用户卡一般 50）。</summary>
    public int Order { get; set; } = 50;

    /// <summary>真正的 System Prompt：从 agent.cordis.yml 的 persona 块 config.prefix 提取。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>persona 块 config.suffix（可选，随附 standard 里有类似「当前工作目录」的固定尾巴）。</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>是否为随附（只读）预设——位于 node_modules 里，禁止修改 / 删除。</summary>
    public bool IsShipped { get; set; }

    /// <summary>是否为 settings.yaml 里 agent-presets.default 指向的卡。</summary>
    public bool IsDefault { get; set; }

    /// <summary>预设目录的完整路径。</summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>界面显示名：没有 name 时退化为目录名，避免空白项。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>
/// 人格卡读写服务：直接操作 dsh 官方的人格预设目录，不写 settings.yaml 的扁平键。
///
/// 关键事实（已实测）：
/// 1) 人格卡目录 &lt;DSH_HOME&gt;\.agent-presets\&lt;id&gt;\ 下有两个文件：
///    preset.yml（name / description / order）与 agent.cordis.yml（真正的 prompt 在 persona 块的 config.prefix）。
/// 2) agent.cordis.yml 里有 !!js 自定义标签、cordis:group 嵌套与多行块标量，
///    用 YamlDotNet 反序列化会失败或丢数据，因此一律采用「纯文本行级定位替换」。
/// 3) 启用某张卡 = 写 settings.yaml 的 agent-presets.default，本类自行做行级最小改写，
///    不依赖 DshConfigService（避免与其它模块的实现互相牵制）。
/// </summary>
public class PersonaService
{
    /// <summary>用户自建预设的目录名（dsh 官方常量）。</summary>
    public const string UserPresetsDirName = ".agent-presets";

    /// <summary>预设元数据文件名。</summary>
    public const string PresetFileName = "preset.yml";

    /// <summary>预设正文文件名。</summary>
    public const string CordisFileName = "agent.cordis.yml";

    /// <summary>dsh 设置文件名。</summary>
    public const string SettingsFileName = "settings.yaml";

    /// <summary>找不到任何基础模板时使用的兜底基础 id。</summary>
    private const string FallbackBaseId = "standard";

    /// <summary>新建人格卡默认写入的排序权重。</summary>
    private const int NewCardOrder = 50;

    /// <summary>无 BOM 的 UTF-8：dsh 自带文件都是无 BOM，保持一致避免解析异常。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 找不到随附模板时的最小 agent.cordis.yml：只有 persona 块，Create 会往里补 prefix。
    /// 这样即使本机没装 dsh-agent-presets 包，「新建人格卡」也不会直接失败。
    /// </summary>
    private const string FallbackCordisYaml =
        "# 由 DshManager 生成的最小人格预设（本机未找到 dsh 自带模板，未能复制完整工具链）。\n" +
        "- id: persona\n" +
        "  name: '@deepseek-ai/dsh-persona'\n" +
        "  config:\n" +
        "    prefix: \"\"\n";

    private readonly string _dshHome;
    private readonly string _dataDirectory;
    private readonly Func<IEnumerable<string>> _nodeModulesProvider;

    /// <summary>页面默认构造：按真实环境解析 DSH_HOME 与 dsh-agent-presets 的安装位置。</summary>
    public PersonaService()
        : this(DshDiscoveryService.GetDefaultDshHome(), GetDefaultDataDirectory(), null)
    {
    }

    /// <summary>
    /// 可注入构造：供单元测试完全走临时目录，不触碰真实 ~/.dsh。
    /// </summary>
    /// <param name="dshHome">dsh 数据目录（内含 .agent-presets 与 settings.yaml）。</param>
    /// <param name="dataDirectory">本程序数据目录（删除的人格卡备份到其中的 deleted-personas）。</param>
    /// <param name="nodeModulesProvider">node_modules 候选位置提供者；null 时按真实环境扫描。</param>
    public PersonaService(string dshHome, string dataDirectory, Func<IEnumerable<string>>? nodeModulesProvider)
    {
        _dshHome = dshHome;
        _dataDirectory = dataDirectory;
        _nodeModulesProvider = nodeModulesProvider ?? GetCandidateNodeModules;
    }

    /// <summary>用户自建人格卡根目录 &lt;DSH_HOME&gt;\.agent-presets。</summary>
    public string UserPresetsRoot => Path.Combine(_dshHome, UserPresetsDirName);

    /// <summary>settings.yaml 完整路径。</summary>
    public string SettingsFilePath => Path.Combine(_dshHome, SettingsFileName);

    /// <summary>删除的人格卡备份根目录（不真删，只移动，便于手工找回）。</summary>
    public string DeletedPersonasRoot => Path.Combine(_dataDirectory, "deleted-personas");

    /// <summary>随附预设根目录（第一个命中的 node_modules 下的 presets）；找不到返回空字符串。</summary>
    public string ShippedPresetsRoot => EnumerateShippedPresetRoots().FirstOrDefault() ?? string.Empty;

    /// <summary>是否成功发现了随附模板（用于界面给出降级提示）。</summary>
    public bool HasShippedTemplates => ShippedPresetsRoot.Length > 0;

    /// <summary>本程序数据目录：与 JsonStoreService 默认目录保持一致。</summary>
    private static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(localAppData, "DshManager", "Data");
    }

    // ==================== 随附模板发现 ====================

    /// <summary>
    /// 列出本机可能装着 @deepseek-ai 包的 node_modules 目录。
    /// dsh 本体与 dsh-agent-presets 不一定在同一个安装位置（npx 每个包一个缓存目录），
    /// 所以必须遍历所有候选位置去找，不能只认一个。
    /// </summary>
    public static IEnumerable<string> GetCandidateNodeModules()
    {
        var result = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // 1) npx 缓存：%LOCALAPPDATA%\npm-cache\_npx\<hash>\node_modules（本机实测位置）
        AddNpxCacheNodeModules(localAppData, result);

        // 2) npm 全局：%APPDATA%\npm\node_modules 与 %LOCALAPPDATA%\npm\node_modules
        if (!string.IsNullOrWhiteSpace(roaming))
            result.Add(Path.Combine(roaming, "npm", "node_modules"));
        if (!string.IsNullOrWhiteSpace(localAppData))
            result.Add(Path.Combine(localAppData, "npm", "node_modules"));

        // 3) 兜底：Node 官方安装目录
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            result.Add(Path.Combine(programFiles, "nodejs", "node_modules"));

        // 只保留真实存在的目录，并去重
        return result.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>把 %LOCALAPPDATA%\npm-cache\_npx\*\node_modules 这类 npx 缓存目录加入候选清单。</summary>
    private static void AddNpxCacheNodeModules(string localAppData, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(localAppData)) return;
        var npxRoot = Path.Combine(localAppData, "npm-cache", "_npx");
        foreach (var cacheDir in SafeGetDirectories(npxRoot))
        {
            var nodeModules = Path.Combine(cacheDir, "node_modules");
            if (Directory.Exists(nodeModules)) result.Add(nodeModules);
        }
    }

    /// <summary>枚举所有存在随附预设的 presets 目录（多个 npx 缓存可能各有一份）。</summary>
    private IEnumerable<string> EnumerateShippedPresetRoots()
    {
        IEnumerable<string> candidates;
        try
        {
            candidates = _nodeModulesProvider() ?? Enumerable.Empty<string>();
        }
        catch
        {
            // 候选提供者异常时优雅降级：视为找不到随附模板，不影响用户卡管理
            candidates = Enumerable.Empty<string>();
        }

        foreach (var nodeModules in candidates)
        {
            if (string.IsNullOrWhiteSpace(nodeModules)) continue;
            var presets = Path.Combine(nodeModules, "@deepseek-ai", "dsh-agent-presets", "presets");
            if (Directory.Exists(presets)) yield return presets;
        }
    }

    // ==================== 读取 ====================

    /// <summary>读取全部人格卡：用户自建在前，随附只读卡在后。</summary>
    public IReadOnlyList<PersonaCard> LoadAll()
    {
        var cards = new List<PersonaCard>();
        var defaultId = ReadDefaultPresetId();

        // 1) 用户自建卡（可编辑 / 可删除）
        foreach (var dir in SafeGetDirectories(UserPresetsRoot))
        {
            var card = LoadOne(dir);
            if (card is not null)
            {
                card.IsShipped = false;
                cards.Add(card);
            }
        }

        // 2) 随附只读卡：多个 npx 缓存里可能有同名副本，按 id 去重只保留第一份
        var seenShippedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateShippedPresetRoots())
        {
            foreach (var dir in SafeGetDirectories(root))
            {
                if (!seenShippedIds.Add(Path.GetFileName(dir))) continue;
                var card = LoadOne(dir);
                if (card is null) continue;
                card.IsShipped = true;
                cards.Add(card);
            }
        }

        // 3) 标记当前默认卡
        var hasDefault = !string.IsNullOrWhiteSpace(defaultId);
        foreach (var card in cards)
        {
            card.IsDefault = hasDefault && string.Equals(card.Id, defaultId, StringComparison.OrdinalIgnoreCase);
        }
        return cards;
    }

    /// <summary>读一张人格卡；目录里没有 agent.cordis.yml 时返回 null（不是有效人格卡）。</summary>
    public static PersonaCard? LoadOne(string directoryPath)
    {
        try
        {
            var cordis = Path.Combine(directoryPath, CordisFileName);
            if (!File.Exists(cordis)) return null;

            var card = new PersonaCard
            {
                Id = Path.GetFileName(directoryPath),
                DirectoryPath = directoryPath,
            };

            var meta = Path.Combine(directoryPath, PresetFileName);
            if (File.Exists(meta))
            {
                var yaml = File.ReadAllText(meta);
                card.Name = ReadYamlScalar(yaml, "name");
                card.Description = ReadYamlScalar(yaml, "description");
                if (int.TryParse(ReadYamlScalar(yaml, "order"), out var order) && order > 0) card.Order = order;
            }
            if (string.IsNullOrWhiteSpace(card.Name)) card.Name = card.Id;

            var lines = ReadLines(cordis);
            var blockStart = FindPersonaBlock(lines);
            if (blockStart >= 0)
            {
                card.SystemPrompt = ReadPersonaScalar(lines, blockStart, "prefix");
                card.Suffix = ReadPersonaScalar(lines, blockStart, "suffix");
            }
            return card;
        }
        catch
        {
            // 单个卡目录读取失败不应影响整页列表
            return null;
        }
    }

    /// <summary>读取 settings.yaml 里 agent-presets.default 的值；没有则返回空字符串。</summary>
    public string ReadDefaultPresetId()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return string.Empty;
            var lines = ReadLines(SettingsFilePath);

            var section = IndexOfSection(lines, "agent-presets");
            if (section < 0) return string.Empty;

            for (var i = section + 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0) continue;
                if (IndentOf(lines[i]) == 0) break;   // 回到顶层 → 该段结束
                if (trimmed.StartsWith("default:", StringComparison.Ordinal))
                    return Unquote(trimmed["default:".Length..].Trim());
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ==================== 写入 ====================

    /// <summary>
    /// 新建人格卡：整目录复制一份基础模板（含子目录），清只读属性，再写 preset.yml 与 persona 正文。
    /// 复制而非从零生成：这样能继承模板自带的工具、技能与资产，只改人格那一段。
    /// </summary>
    /// <param name="baseId">基础模板 id（如 standard）；找不到时会退化为最小骨架。</param>
    /// <param name="id">期望的目录名；不合法时返回失败，被占用时自动加 -2/-3 后缀去重。</param>
    public PersonaCard? Create(string baseId, string id, string name, string description, string systemPrompt, out string error)
    {
        error = string.Empty;

        var desired = (id ?? string.Empty).Trim();
        if (!IsValidId(desired))
        {
            error = "人格卡 id 只能用 1~40 位小写字母、数字和减号，且必须以字母或数字开头。";
            return null;
        }

        try
        {
            var baseDir = FindPresetDirectory(baseId);
            var newId = ResolveAvailableId(desired);
            var target = Path.Combine(UserPresetsRoot, newId);

            Directory.CreateDirectory(UserPresetsRoot);
            if (baseDir is not null)
            {
                CopyDirectory(baseDir, target);
                ClearReadOnlyAttributes(target);   // 随附模板常带只读属性，复制过来后必须清掉才能改
            }
            else
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, CordisFileName), FallbackCordisYaml, Utf8NoBom);
            }

            WritePresetMeta(target, name, description, NewCardOrder);

            // 正文写入失败时人格卡目录已经建好，仍返回卡对象并把失败原因带走，由界面提示
            if (!WritePersonaText(target, systemPrompt, out var writeError))
                error = $"人格卡已创建，但写入人格正文失败：{writeError}";

            return LoadOne(target);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>保存人格卡：只改 preset.yml 与 persona 块的 prefix（随附卡一律拒绝）。</summary>
    public bool Save(PersonaCard card, out string error)
    {
        error = string.Empty;
        if (card is null)
        {
            error = "没有选中的人格卡。";
            return false;
        }
        if (card.IsShipped)
        {
            error = "这是 dsh 自带预设，不能修改；请用「另存为我的卡」复制一份再改。";
            return false;
        }

        try
        {
            WritePresetMeta(card.DirectoryPath, card.Name, card.Description, card.Order);
            return WritePersonaText(card.DirectoryPath, card.SystemPrompt, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>删除人格卡：移动到 &lt;数据目录&gt;\deleted-personas\&lt;id&gt;.&lt;时间戳&gt;，不真删。</summary>
    public bool Delete(PersonaCard card, out string error)
    {
        error = string.Empty;
        if (card is null)
        {
            error = "没有选中的人格卡。";
            return false;
        }
        if (card.IsShipped)
        {
            error = "dsh 自带预设不能删除。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(DeletedPersonasRoot);
            var target = Path.Combine(DeletedPersonasRoot, $"{card.Id}.{DateTime.Now:yyyyMMdd-HHmmss}");
            ClearReadOnlyAttributes(card.DirectoryPath);
            Directory.Move(card.DirectoryPath, target);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把某张人格卡设为新会话的默认人格：写 settings.yaml 的 agent-presets.default。
    /// 这里用「行级最小改写」而不是反序列化整份 YAML，原因有两个：
    /// 1) settings.yaml 里可能有面板不认识的结构，整份重写容易丢键、改格式；
    /// 2) DshConfigService 有独立职责，不该被本模块的写入细节牵制。
    /// 写前先备份 settings.yaml.bak.yyyyMMdd-HHmmss，出错可手工回滚。
    /// </summary>
    public bool SetDefault(string cardId, out string error)
    {
        error = string.Empty;
        var value = (cardId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "人格卡 id 不能为空。";
            return false;
        }

        try
        {
            var file = SettingsFilePath;
            Directory.CreateDirectory(_dshHome);

            var lines = new List<string>();
            var ends = new List<string>();
            if (File.Exists(file))
            {
                BackupFile(file);
                ReadLinesKeepEnds(file, out var rawLines, out var rawEnds);
                lines.AddRange(rawLines);
                ends.AddRange(rawEnds);
            }

            var fallbackEnd = ends.Count > 0 ? PickLineEnding(ends) : "\n";
            var scalar = "default: " + QuoteYamlScalar(value);

            var section = IndexOfSection(lines, "agent-presets");
            if (section < 0)
            {
                // 完全没有 agent-presets 段：在文件末尾追加一段
                TrimTrailingMarker(lines, ends);
                // 追加新段前先保证上一行以换行结尾，避免新段落粘在上一行末尾
                if (lines.Count > 0 && ends[^1].Length == 0) ends[^1] = fallbackEnd;
                lines.Add("agent-presets:");
                ends.Add(fallbackEnd);
                lines.Add("  " + scalar);
                ends.Add(fallbackEnd);
            }
            else
            {
                var defaultLine = FindDefaultLine(lines, section);
                if (defaultLine >= 0)
                {
                    // 原地替换：保留原缩进，其余内容一字不动
                    var indent = lines[defaultLine][..(lines[defaultLine].Length - lines[defaultLine].TrimStart().Length)];
                    lines[defaultLine] = indent + scalar;
                }
                else
                {
                    // 段内没有 default：插到段头之后，用 2 空格缩进与同段其它键对齐
                    lines.Insert(section + 1, "  " + scalar);
                    ends.Insert(section + 1, fallbackEnd);
                }
            }

            WriteLinesPreservingEnds(file, lines, ends, fallbackEnd);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>在资源管理器里定位人格卡目录（找不到目录则打开用户卡根目录）。</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 资源管理器无法启动（如被策略禁用）时静默忽略，不影响其它功能
        }
    }

    // ==================== id 规则与去重 ====================

    /// <summary>id 规则：官方要求 [a-z0-9][a-z0-9-]*。</summary>
    public static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!IsLowerAsciiOrDigit(id[0])) return false;
        return id.All(static c => IsLowerAsciiOrDigit(c) || c == '-');
    }

    /// <summary>
    /// 由显示名生成一个合法的 id 建议值：ASCII 直接保留，空格与分隔符转减号，
    /// 中文等无法映射的字符丢弃；结果不合法则回退为 persona（重名去重交给 Create）。
    /// </summary>
    public static string SuggestId(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim())
        {
            if (IsLowerAsciiOrDigit(c)) sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '_' or '-' or '.') sb.Append('-');
            // 其它字符（含中文）无法稳定转写，直接跳过
        }

        var slug = CollapseDashes(sb.ToString());
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return IsValidId(slug) ? slug : "persona";
    }

    /// <summary>把 id 落到一个没被占用的值上：persona → persona-2 → persona-3 …</summary>
    private string ResolveAvailableId(string desired)
    {
        if (IsIdFree(desired)) return desired;
        for (var n = 2; n <= 999; n++)
        {
            var candidate = $"{desired}-{n}";
            if (IsIdFree(candidate)) return candidate;
        }
        return $"{desired}-{DateTime.Now:HHmmss}";
    }

    /// <summary>用户目录与所有随附预设里都没有占用该 id。</summary>
    private bool IsIdFree(string id)
    {
        try
        {
            if (Directory.Exists(Path.Combine(UserPresetsRoot, id))) return false;
            foreach (var root in EnumerateShippedPresetRoots())
            {
                if (Directory.Exists(Path.Combine(root, id))) return false;
            }
        }
        catch
        {
            // 探测失败时保守认为可用，最终由文件系统操作给出真实结果
        }
        return true;
    }

    /// <summary>按 id 找预设目录：优先随附模板，其次用户目录；找不到返回 null。</summary>
    private string? FindPresetDirectory(string baseId)
    {
        var id = string.IsNullOrWhiteSpace(baseId) ? FallbackBaseId : baseId.Trim();
        foreach (var root in EnumerateShippedPresetRoots())
        {
            var dir = Path.Combine(root, id);
            if (Directory.Exists(dir)) return dir;
        }

        var userDir = Path.Combine(UserPresetsRoot, id);
        return Directory.Exists(userDir) ? userDir : null;
    }

    private static bool IsLowerAsciiOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static string CollapseDashes(string s)
    {
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var c in s)
        {
            if (c == '-')
            {
                if (!prevDash) sb.Append('-');
                prevDash = true;
            }
            else
            {
                sb.Append(c);
                prevDash = false;
            }
        }
        return sb.ToString().Trim('-');
    }

    // ==================== agent.cordis.yml 的 persona 段读写（核心） ====================

    /// <summary>
    /// 把人格正文写进 persona 块的 config.prefix。
    ///
    /// 为什么用「纯文本行级替换」而不是 YAML 解析：
    /// agent.cordis.yml 含 !!js 自定义标签、cordis:group 嵌套与多行块标量，
    /// YamlDotNet 反序列化会失败或丢数据；而且我们只想动一行，解析再回写风险太大。
    ///
    /// 为什么值要压成单行双引号标量：
    /// 单行标量不需要处理块缩进，也绝不会破坏同一文件里其它段落的缩进结构。
    /// </summary>
    /// <param name="presetDirectory">人格卡目录。</param>
    /// <param name="systemPrompt">人格正文（任意换行，会折叠成单行）。</param>
    public static bool WritePersonaText(string presetDirectory, string systemPrompt, out string error)
    {
        error = string.Empty;
        var file = Path.Combine(presetDirectory, CordisFileName);
        if (!File.Exists(file))
        {
            error = $"预设目录里没有 {CordisFileName}：{presetDirectory}";
            return false;
        }

        try
        {
            ReadLinesKeepEnds(file, out var rawLines, out var rawEnds);

            var blockStart = FindPersonaBlock(rawLines);
            if (blockStart < 0)
            {
                // 明确报错而不是静默失败：否则用户会以为保存成功、实际 prompt 没变
                error = $"{CordisFileName} 里找不到「- id: persona」段落，无法写入人格正文。";
                return false;
            }

            // 1) 换行与连续空白压成单行（YAML 单行标量不允许裸换行）
            var oneLine = CollapseSpaces(systemPrompt ?? string.Empty).Trim();
            var scalar = "prefix: \"" + EscapeDoubleQuoted(oneLine) + "\"";
            var baseIndent = IndentOf(rawLines[blockStart]);

            // 2) 在 persona 块内定位 prefix: 行（缩进回落到块外即视为块结束）
            var prefixLine = -1;
            var prefixIndent = 0;
            for (var i = blockStart + 1; i < rawLines.Length; i++)
            {
                if (rawLines[i].Trim().Length == 0) continue;
                var indent = IndentOf(rawLines[i]);
                if (indent <= baseIndent) break;
                if (rawLines[i].TrimStart().StartsWith("prefix:", StringComparison.Ordinal))
                {
                    prefixLine = i;
                    prefixIndent = indent;
                    break;
                }
            }

            var lines = new List<string>();
            var ends = new List<string>();
            var fallbackEnd = PickLineEnding(rawEnds);
            if (prefixLine >= 0)
            {
                // 3a) 替换已有 prefix 行：只吃属于 prefix 的块内容行，空行决不吃
                //     （吃掉空行会让整个文件少一行、后续内容全部错位）
                var blockEnd = FindScalarBlockEnd(rawLines, prefixLine, prefixIndent);
                var prefixLineEnd = rawEnds[prefixLine];
                for (var i = 0; i < rawLines.Length; i++)
                {
                    if (i == prefixLine)
                    {
                        // 沿用原行的结束符，保证文件除该行文本外逐字节结构不变
                        lines.Add(new string(' ', prefixIndent) + scalar);
                        ends.Add(prefixLineEnd);
                    }
                    else if (i > prefixLine && i < blockEnd)
                    {
                        continue;
                    }
                    else
                    {
                        lines.Add(rawLines[i]);
                        ends.Add(rawEnds[i]);
                    }
                }
            }
            else
            {
                // 3b) 块内没有 prefix：插到 config: 之后（没有 config 就插到块尾）
                var (insertAt, insertIndent) = FindPrefixInsertPosition(rawLines, blockStart, baseIndent);
                var newLine = new string(' ', insertIndent) + scalar;
                for (var i = 0; i <= rawLines.Length; i++)
                {
                    if (i == insertAt)
                    {
                        lines.Add(newLine);
                        ends.Add(fallbackEnd);
                    }
                    if (i == rawLines.Length) break;
                    lines.Add(rawLines[i]);
                    ends.Add(rawEnds[i]);
                }
            }

            // 严格保持原文「末尾有没有换行」的状态：替换 / 插入都不该改变它
            // （原文以换行结尾时读入会多出一个空标记行，这里据此判断）
            var originalEndsWithNewline = rawLines.Length > 0 && rawLines[^1].Length == 0 && rawEnds[^1].Length == 0;
            if (ends.Count > 0)
            {
                if (originalEndsWithNewline)
                {
                    // 末行是真实内容行却没有结束符（块被吃到了文件末尾）→ 补回原文的结尾换行
                    if (lines[^1].Length > 0 && ends[^1].Length == 0) ends[^1] = fallbackEnd;
                }
                else
                {
                    // 原文没有结尾换行 → 输出最后一行也必须没有结束符
                    ends[^1] = string.Empty;
                }
            }

            WriteLinesPreservingEnds(file, lines, ends, fallbackEnd);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>找到「- id: persona」那一行的下标；找不到返回 -1。</summary>
    private static int FindPersonaBlock(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("- id:", StringComparison.Ordinal)) continue;
            if (trimmed["- id:".Length..].Trim() == "persona") return i;
        }
        return -1;
    }

    /// <summary>
    /// 计算块标量（如 prefix: | 或 prefix: "x"）占用的行区间末尾（不含）。
    /// 规则：只吸收缩进比键更深的行；空行仅当它后面还有更深缩进内容时才算块的一部分，
    /// 否则视为段落分隔——这是「空行不能被吞掉」的关键。
    /// </summary>
    private static int FindScalarBlockEnd(string[] lines, int keyLine, int keyIndent)
    {
        var end = keyLine + 1;
        for (var j = keyLine + 1; j < lines.Length; j++)
        {
            if (lines[j].Trim().Length == 0)
            {
                var moreContentFollows = false;
                for (var k = j + 1; k < lines.Length; k++)
                {
                    if (lines[k].Trim().Length == 0) continue;
                    moreContentFollows = IndentOf(lines[k]) > keyIndent;
                    break;
                }
                if (!moreContentFollows) break;
                end = j + 1;
                continue;
            }
            if (IndentOf(lines[j]) <= keyIndent) break;
            end = j + 1;
        }
        return end;
    }

    /// <summary>
    /// 计算「块内新增 prefix 行」的插入位置与缩进：紧跟 config: 之后。
    /// 注意缩进是 config: 的缩进再加一级（+2 空格），因为 prefix 是 config 的**子键**，
    /// 必须与 config 下已有的兄弟键（如 suffix）对齐，而不是与 config 本身同级。
    /// </summary>
    private static (int InsertAt, int Indent) FindPrefixInsertPosition(string[] lines, int blockStart, int baseIndent)
    {
        var lastBlockLine = blockStart;
        for (var i = blockStart + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (IndentOf(lines[i]) <= baseIndent) break;
            lastBlockLine = i;
            if (lines[i].TrimStart().StartsWith("config:", StringComparison.Ordinal))
                return (i + 1, IndentOf(lines[i]) + 2);
        }
        return (lastBlockLine + 1, baseIndent + 2);
    }

    /// <summary>从 persona 块里读一个标量：支持单行（含引号）与块标量（&gt;- / | 等），读到的多行会折叠成一行。</summary>
    private static string ReadPersonaScalar(string[] lines, int blockStart, string key)
    {
        var baseIndent = IndentOf(lines[blockStart]);
        var prefix = key + ":";

        for (var i = blockStart + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var indent = IndentOf(lines[i]);
            if (indent <= baseIndent) break;

            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = trimmed[prefix.Length..].Trim();
            if (rest.Length > 0 && rest is not (">-" or ">" or "|" or "|-" or "|+" or ">+"))
                return Unquote(rest);

            // 块写法：收集缩进更深的后续行，折叠成单行
            var sb = new StringBuilder();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length == 0)
                {
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    continue;
                }
                if (IndentOf(lines[j]) <= indent) break;
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                sb.Append(lines[j].Trim());
            }
            return CollapseSpaces(sb.ToString()).Trim();
        }
        return string.Empty;
    }

    /// <summary>写 preset.yml：name / description / order。</summary>
    private static void WritePresetMeta(string presetDirectory, string name, string description, int order)
    {
        var sb = new StringBuilder();
        sb.Append("name: ").Append(QuoteYamlScalar(name)).Append('\n');
        sb.Append("description: ").Append(QuoteYamlScalar(description)).Append('\n');
        sb.Append("order: ").Append(order > 0 ? order : NewCardOrder).Append('\n');
        File.WriteAllText(Path.Combine(presetDirectory, PresetFileName), sb.ToString(), Utf8NoBom);
    }

    // ==================== YAML 文本小工具 ====================

    /// <summary>YAML 标量安全包裹：含特殊字符时加双引号并转义。</summary>
    private static string QuoteYamlScalar(string value)
    {
        var s = value ?? string.Empty;
        var needQuote = s.Length == 0;
        foreach (var c in s)
        {
            if (c is ':' or '#' or '"' or '\'' or '\n' or '\r' or '&' or '*' or '!' or '|' or '>' or '%' or '@')
            {
                needQuote = true;
                break;
            }
        }
        if (s.Length > 0 && (s[0] == ' ' || s[^1] == ' ')) needQuote = true;
        return needQuote ? "\"" + EscapeDoubleQuoted(s) + "\"" : s;
    }

    /// <summary>去掉 YAML 标量外层的引号（双引号额外反转义）。</summary>
    private static string Unquote(string value)
    {
        var s = value.Trim();
        if (s.Length < 2) return s;
        if (s[0] == '"' && s[^1] == '"') return UnescapeDoubleQuoted(s[1..^1]);
        if (s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        return s;
    }

    /// <summary>双引号标量里的转义：\ → \\，" → \"（与写入时保持互逆）。</summary>
    private static string EscapeDoubleQuoted(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string UnescapeDoubleQuoted(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            i++;
            sb.Append(s[i] switch
            {
                'n' => '\n',
                't' => '\t',
                _ => s[i],   // \\ 与 \" 都退化为字面字符
            });
        }
        return sb.ToString();
    }

    /// <summary>读 preset.yml 里的简单标量字段（行级解析，避免为两个字段引入 YAML 依赖）。</summary>
    private static string ReadYamlScalar(string yaml, string key)
    {
        if (string.IsNullOrEmpty(yaml)) return string.Empty;
        var prefix = key + ":";
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
            return Unquote(trimmed[prefix.Length..].Trim());
        }
        return string.Empty;
    }

    /// <summary>找到顶层段落（缩进 0 的「key:」）的行下标；找不到返回 -1。</summary>
    private static int IndexOfSection(IReadOnlyList<string> lines, string key)
    {
        var prefix = key + ":";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart() != lines[i]) continue;   // 非顶层
            var trimmed = lines[i].TrimEnd();
            if (trimmed == prefix || trimmed.StartsWith(prefix + " ", StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>在某个顶层段内找 default: 行；找不到返回 -1。</summary>
    private static int FindDefaultLine(IReadOnlyList<string> lines, int sectionIndex)
    {
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            if (IndentOf(lines[i]) == 0) break;   // 回到顶层 → 段结束
            if (trimmed.StartsWith("default:", StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    // ==================== 文件与文本小工具 ====================

    /// <summary>
    /// 按行读取，并保留每行原来的结束符（\n / \r\n）。
    /// dsh 自带 yml 里混着两种换行，统一规范化会改变文件字节，
    /// 因此必须原样保留：lines[i] 不含结束符，ends[i] 是该行的结束符。
    /// </summary>
    private static void ReadLinesKeepEnds(string file, out string[] lines, out string[] ends)
    {
        var all = File.ReadAllText(file);
        var ls = new List<string>();
        var es = new List<string>();

        var i = 0;
        while (i < all.Length)
        {
            var nl = all.IndexOf('\n', i);
            if (nl < 0)
            {
                ls.Add(all[i..]);
                es.Add(string.Empty);
                break;
            }
            var contentEnd = nl;
            var end = "\n";
            if (nl > i && all[nl - 1] == '\r')
            {
                contentEnd = nl - 1;
                end = "\r\n";
            }
            ls.Add(all[i..contentEnd]);
            es.Add(end);
            i = nl + 1;
        }

        // 文件以换行结尾时补一个「空标记行」：写回时据此判断是否需要结尾换行
        if (all.Length > 0 && all[^1] == '\n')
        {
            ls.Add(string.Empty);
            es.Add(string.Empty);
        }

        lines = ls.ToArray();
        ends = es.ToArray();
    }

    private static string[] ReadLines(string file)
    {
        ReadLinesKeepEnds(file, out var lines, out _);
        return lines;
    }

    /// <summary>
    /// 按行写回并逐行沿用原结束符。
    /// 先丢掉「文件以换行结尾」产生的空标记行：结尾换行由上一行自己的结束符表达，
    /// 这样既不会多写空行，也能保持原文件「有没有结尾换行」的状态。
    /// </summary>
    private static void WriteLinesPreservingEnds(string file, List<string> lines, List<string> ends, string fallbackEnd)
    {
        TrimTrailingMarker(lines, ends);

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            var end = i < ends.Count ? ends[i] : fallbackEnd;
            if (end.Length == 0 && i < lines.Count - 1) end = fallbackEnd;
            sb.Append(end);
        }
        File.WriteAllText(file, sb.ToString(), Utf8NoBom);
    }

    /// <summary>
    /// 丢掉尾部的「空标记行」。
    /// 该标记只在「文件以换行结尾」时出现，作用仅是告诉我们原文有结尾换行；
    /// 真正写回时由上一行自己的结束符表达结尾换行，所以这里必须把它移除，
    /// 否则会多写一个空行。注意：**不能**顺手给最后一行补换行符，
    /// 那会破坏「原文没有结尾换行」的文件。
    /// </summary>
    private static void TrimTrailingMarker(List<string> lines, List<string> ends)
    {
        if (lines.Count == 0) return;
        if (lines[^1].Length == 0 && ends[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
            ends.RemoveAt(ends.Count - 1);
        }
    }

    /// <summary>统计文件里最常用的行结束符，新写入的行沿用它。</summary>
    private static string PickLineEnding(IReadOnlyList<string> ends)
    {
        int crlf = 0, lf = 0;
        foreach (var e in ends)
        {
            if (e == "\r\n") crlf++;
            else if (e == "\n") lf++;
        }
        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>行首空格数（dsh 的 yml 只用空格缩进）。</summary>
    private static int IndentOf(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    /// <summary>把所有空白（空格 / 制表符 / 换行）折叠成单个空格。</summary>
    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder();
        var prevSpace = false;
        foreach (var c in s ?? string.Empty)
        {
            var isSpace = c is ' ' or '\t' or '\n' or '\r';
            if (isSpace)
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>列目录：目录不存在或不可读时返回空列表，绝不抛异常。</summary>
    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Enumerable.Empty<string>();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>递归复制目录（含全部子目录，如 cordis 预设的 skills/）。</summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>清除只读属性（随附模板在 node_modules 里可能带只读位，复制过来的副本必须可写）。</summary>
    private static void ClearReadOnlyAttributes(string directory)
    {
        try
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // 个别文件清不掉不影响主体写入，交给后续写入抛出的真实错误
        }
    }

    /// <summary>
    /// 写前备份为 settings.yaml.bak.yyyyMMdd-HHmmss（同一秒内多次备份加序号，绝不覆盖已有备份）。
    /// </summary>
    private void BackupFile(string file)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = $"{file}.bak.{stamp}";

        // 【为什么要加序号】时间戳只精确到秒：一秒内连做两次备份（如用户快速点两下保存）
        // 会用 overwrite: true 把上一份覆盖掉，等于少了一层可回退的现场。
        // 本文件其余三处备份（人格卡 / 指令 / 长记忆）都已有同样的 while+序号逻辑。
        var index = 1;
        while (File.Exists(backup))
            backup = $"{file}.bak.{stamp}-{index++}";

        File.Copy(file, backup);
    }
}
