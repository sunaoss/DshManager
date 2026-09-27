using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using YamlDotNet.Serialization;

namespace DshManager.Services;

/// <summary>第三方服务商（llm-pi-ai.providers 下的一条条目）的只读视图。</summary>
public sealed class ProviderInfo
{
    /// <summary>路由名（providers 下的键）。</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>协议：openai-completions / openai-responses / anthropic-messages。</summary>
    public string Api { get; set; } = "";

    public string BaseURL { get; set; } = "";

    /// <summary>密钥所在的环境变量名（密钥本身不落在配置文件里）。</summary>
    public string ApiKeyEnv { get; set; } = "";

    /// <summary>模型列表原文（流式值原样返回；块状列表暂不展开）。</summary>
    public string Models { get; set; } = "";
}

/// <summary>
/// DSH 配置服务：读写 &lt;DSH_HOME&gt;/settings.yaml。
///
/// 三条规矩：
///   1. 只改目标行，别的内容一个字节都不动（靠 <see cref="YamlTextEditor"/> 实现），
///      未知键与注释永远不会被吃掉。
///   2. 每次落盘前先备份一份带时间戳的副本（settings.yaml.bak.yyyyMMdd-HHmmss），
///      改坏了能退回来；同名多次保存不覆盖，最多保留 20 份。
///   3. 先写临时文件再替换（原子写），避免写到一半断电留下半个文件。
///
/// 文件不存在 / 内容损坏时一律不抛异常：读返回 null 或空，写返回失败原因。
/// </summary>
public class DshConfigService
{
    /// <summary>主配置文件名。</summary>
    public const string SettingsFileName = "settings.yaml";

    /// <summary>临时文件后缀（同一目录内，保证 File.Move 是同盘原子替换）。</summary>
    private const string TempSuffix = ".dsh-manager-tmp";

    /// <summary>备份最多保留份数。</summary>
    private const int MaxBackups = 20;

    /// <summary>providers 下会被识别的字段（服务商条目的结构）。</summary>
    private static readonly string[] ProviderFields =
        { "displayName", "api", "baseURL", "apiKeyEnv", "models" };

    /// <summary>复杂值（列表/字典）转成单行 flow 风格 YAML，避免注入换行破坏行级改写。</summary>
    private static readonly ISerializer FlowSerializer = new SerializerBuilder().JsonCompatible().Build();

    private readonly string _dataDir;
    private string _originalText = "";

    /// <summary>上次 Load / Save 时 settings.yaml 的磁盘修改时间（UTC）；null = 当时文件不存在。</summary>
    private DateTime? _loadedWriteTimeUtc;
    private string _currentText = "";
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// 创建配置服务实例。
    /// </summary>
    /// <param name="dataDir">DSH 数据目录；为空时用 DshDiscoveryService.GetDefaultDshHome()（$DSH_HOME 或 ~/.dsh）。</param>
    public DshConfigService(string? dataDir = null)
    {
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? DshDiscoveryService.GetDefaultDshHome() : dataDir;
        Load();
    }

    /// <summary>DSH 数据目录绝对路径。</summary>
    public string DataDir => _dataDir;

    /// <summary>settings.yaml 绝对路径。</summary>
    public string SettingsPath => Path.Combine(_dataDir, SettingsFileName);

    /// <summary>最近一次失败原因（成功时为空串）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 上次 Save 是否因"外部已修改 settings.yaml"而主动放弃写入。
    /// 供 UI 区分"写失败"与"被保护性拒绝"，从而建议用户先重新加载。
    /// </summary>
    public bool HasExternalConflict { get; private set; }

    /// <summary>内存中的内容与磁盘上是否不同（是否需要保存）。</summary>
    public bool IsDirty => !string.Equals(_originalText, _currentText, StringComparison.Ordinal);

    /// <summary>settings.yaml 是否存在。</summary>
    public bool FileExists => File.Exists(SettingsPath);

    // ==================== 读 ====================

    /// <summary>从磁盘读取 settings.yaml 到内存（文件缺失按空文件处理，读取失败不抛异常）。</summary>
    public void Load()
    {
        LastError = "";
        try
        {
            _originalText = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "";
            // 记录载入时的磁盘时间戳，供 Save 做"外部改动检测"
            _loadedWriteTimeUtc = File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : (DateTime?)null;
        }
        catch (Exception ex)
        {
            // 文件被占用 / 无权限：按空配置处理，保证 UI 层安全调用
            LastError = ex.Message;
            _originalText = "";
            _loadedWriteTimeUtc = null;
        }
        _currentText = _originalText;
        RefreshValues();
    }

    /// <summary>丢弃内存中未保存的改动，重新从磁盘加载。</summary>
    public void Reload() => Load();

    /// <summary>当前内容里全部键的路径 → 原始值文本（含复杂值，行尾注释已剥离）。</summary>
    public IReadOnlyDictionary<string, string> ReadAll() => _values;

    /// <summary>取某路径的原始值文本（未去引号）；不存在返回 null。</summary>
    public string? ReadRaw(string path) => _values.TryGetValue(path, out var raw) ? raw : null;

    /// <summary>取某路径的值：标量已去引号，复杂值原样；不存在返回 null。</summary>
    public string? ReadValue(string path)
    {
        var raw = ReadRaw(path);
        if (raw is null) return null;
        return YamlTextEditor.IsComplex(raw) ? raw : YamlTextEditor.Unquote(raw);
    }

    /// <summary>当前内容里是否存在该键。</summary>
    public bool Contains(string path) => _values.ContainsKey(path);

    /// <summary>当前内容里全部键路径（字典序）。</summary>
    public IReadOnlyList<string> Keys()
    {
        var list = _values.Keys.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// 读出 llm-pi-ai.providers 下的全部服务商条目及其 api / baseURL / apiKeyEnv / models。
    /// 支持嵌套字典写法（providers.&lt;id&gt;.&lt;field&gt;）与流式复杂值（providers: {...} 暂不展开）。
    /// </summary>
    public IReadOnlyList<ProviderInfo> ReadProviders()
    {
        const string prefix = "llm-pi-ai.providers.";
        var map = new Dictionary<string, ProviderInfo>(StringComparer.Ordinal);

        foreach (var (path, raw) in _values)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = path.Substring(prefix.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0 || dot == rest.Length - 1) continue;

            var id = rest.Substring(0, dot);
            var field = rest.Substring(dot + 1);
            if (Array.IndexOf(ProviderFields, field) < 0) continue;

            if (!map.TryGetValue(id, out var info))
            {
                info = new ProviderInfo { Id = id };
                map[id] = info;
            }

            var text = YamlTextEditor.IsComplex(raw) ? raw : YamlTextEditor.Unquote(raw);
            switch (field)
            {
                case "displayName": info.DisplayName = text; break;
                case "api": info.Api = text; break;
                case "baseURL": info.BaseURL = text; break;
                case "apiKeyEnv": info.ApiKeyEnv = text; break;
                case "models": info.Models = text; break;
            }
        }

        return map.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    // ==================== 写（内存逐项最小改写） ====================

    /// <summary>
    /// 逐项最小改写某个键的字符串值（按需加引号）。传空值表示"删掉这个键"。
    /// 只动内存，落盘要另外调 <see cref="Save"/>。
    /// </summary>
    public bool SetValue(string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(path)) { LastError = "路径为空"; return false; }
        var v = value ?? "";
        return Apply(v.Length == 0 ? YamlTextEditor.Remove(_currentText, path) : YamlTextEditor.Set(_currentText, path, v));
    }

    /// <summary>
    /// 逐项最小改写某个键的标量值（原样写入，不加引号），用于布尔/数字等类型。传空值表示删键。
    /// </summary>
    public bool SetRawValue(string path, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(path)) { LastError = "路径为空"; return false; }
        var v = rawValue ?? "";
        return Apply(v.Length == 0 ? YamlTextEditor.Remove(_currentText, path) : YamlTextEditor.SetRaw(_currentText, path, v));
    }

    private bool Apply(string? nextText)
    {
        if (nextText is null) { LastError = "写入失败：父级结构不支持"; return false; }
        _currentText = nextText;
        RefreshValues();
        return true;
    }

    /// <summary>
    /// 落盘：先把原文件备份为 settings.yaml.bak.yyyyMMdd-HHmmss，再原子替换写入。
    /// </summary>
    /// <returns>
    /// 备份文件路径（原文件存在时）；原文件不存在时为 ""；没有改动或写入失败时为 null，原因见 LastError。
    /// </returns>
    public string? Save()
    {
        LastError = "";
        HasExternalConflict = false;
        try
        {
            if (!IsDirty) return "";

            // 【为什么保存前要比时间戳】本服务的实例是长生命周期的（配置页构造时 Load 一次），
            // 停留期间 settings.yaml 可能被外部改动（dsh 自身写回、用户用编辑器改）。
            // 不做检测就是拿内存里的旧快照整段覆盖磁盘 → 外部改动被无声回滚，
            // 用户事后只看到"我的配置自己变回去了"。备份虽在，但没有任何提示。
            if (DetectExternalChange())
            {
                HasExternalConflict = true;
                LastError = "settings.yaml 已被外部修改（可能是 dsh 自身或编辑器）。为避免覆盖对方改动，本次未写入；请先点「重新加载」再修改。";
                return null;
            }

            Directory.CreateDirectory(_dataDir);
            var backup = File.Exists(SettingsPath) ? CreateBackup() : "";

            // 先写临时文件、再替换：写入中途失败也不会破坏原文件
            var tmp = SettingsPath + TempSuffix;
            File.WriteAllText(tmp, _currentText, new UTF8Encoding(false));   // 无 BOM
            File.Move(tmp, SettingsPath, overwrite: true);

            _originalText = _currentText;   // 保存后以当前内容为新基准
            // 更新基准时间戳：否则紧接着的第二次保存会被自己的写入误判成"外部改动"
            _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(SettingsPath);
            return backup;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 检测 settings.yaml 是否在本实例上次 Load/Save 之后被外部改动过。
    /// 文件被删除（外部清掉）或时间戳前移/后移都算改动。
    /// </summary>
    private bool DetectExternalChange()
    {
        try
        {
            if (_loadedWriteTimeUtc is null)
                return false;   // 载入时文件不存在：之后新建出来不算冲突（属正常首次落盘）

            if (!File.Exists(SettingsPath))
                return true;    // 载入时有、现在没了 → 外部删除

            // 时间戳比较带 1 秒容差：FAT/网络盘的时间戳精度可能只到秒，避免自己写完立刻误报
            var current = File.GetLastWriteTimeUtc(SettingsPath);
            return Math.Abs((current - _loadedWriteTimeUtc.Value).TotalSeconds) >= 1.0;
        }
        catch
        {
            // 时间戳读不到（权限/占用）：宁可放过也不阻断用户保存
            return false;
        }
    }

    /// <summary>备份原文件；同一秒内多次保存时加序号，绝不覆盖已有备份。</summary>
    private string CreateBackup()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = SettingsFileName + ".bak." + stamp;
        var path = Path.Combine(_dataDir, baseName);

        var index = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(_dataDir, baseName + "-" + index.ToString(CultureInfo.InvariantCulture));
            index++;
        }

        File.Copy(SettingsPath, path);
        PruneBackups();
        return path;
    }

    /// <summary>备份只保留最近 20 份，免得越攒越多。</summary>
    private void PruneBackups()
    {
        try
        {
            var files = Directory.GetFiles(_dataDir, SettingsFileName + ".bak.*");
            if (files.Length <= MaxBackups) return;
            // 备份名里的时间戳可字典序排序，删掉最旧的若干份
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Length - MaxBackups; i++) File.Delete(files[i]);
        }
        catch
        {
            // 备份清理失败不影响本次保存
        }
    }

    private void RefreshValues() => _values = YamlTextEditor.Read(_currentText);

    // ==================== 兼容旧调用（人格页写 settings.yaml 用） ====================

    /// <summary>
    /// 把 settings.yaml 解析成嵌套字典（供"写回某个顶层键"这类旧用法）。
    /// 文件缺失或损坏时返回空字典，不抛异常。
    /// </summary>
    public Dictionary<string, object?> LoadSettings()
    {
        try
        {
            var text = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "";
            if (text.Trim().Length == 0) return new Dictionary<string, object?>();

            var deserializer = new DeserializerBuilder()
                .WithAttemptingUnquotedStringTypeDeserialization()   // 让 123 / true 还原为 int / bool
                .Build();
            using var reader = new StringReader(text);
            var node = deserializer.Deserialize<Dictionary<string, object?>>(reader);
            return node ?? new Dictionary<string, object?>();
        }
        catch
        {
            // 非法 YAML：返回空字典，保证调用方安全
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// 把嵌套字典逐叶子最小改写回 settings.yaml（未知键与注释保留），然后落盘。
    /// 仅为兼容旧调用（人格页）保留；配置页走 SetValue/SetRawValue + Save。
    /// </summary>
    public bool SaveSettings(Dictionary<string, object?> data)
    {
        try
        {
            Load();   // 以磁盘现状为基线，避免叠加此前未保存的改动
            foreach (var (path, token) in Flatten(data))
            {
                if (!SetRawValue(path, token)) return false;
            }
            return Save() is not null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>把嵌套字典摊平成「点路径 → 可写入的标量文本」。</summary>
    private static List<(string Path, string Token)> Flatten(IDictionary? node, string prefix = "")
    {
        var result = new List<(string, string)>();
        if (node is null) return result;

        foreach (DictionaryEntry entry in node)
        {
            var key = entry.Key?.ToString() ?? "";
            if (key.Length == 0) continue;
            var path = prefix.Length == 0 ? key : prefix + "." + key;
            AddLeaf(result, path, entry.Value);
        }
        return result;
    }

    /// <summary>把一个叶子值转成写入 YAML 的文本（复杂值转单行 flow 风格）。</summary>
    private static void AddLeaf(List<(string, string)> output, string path, object? value)
    {
        switch (value)
        {
            case null:
                output.Add((path, ""));   // 空 = 删键
                break;
            case IDictionary dict:
                foreach (var item in Flatten(dict, path)) output.Add(item);
                break;
            case string s:
                output.Add((path, YamlTextEditor.QuoteIfNeeded(s)));
                break;
            case bool b:
                output.Add((path, b ? "true" : "false"));
                break;
            case IFormattable f:
                output.Add((path, f.ToString(null, CultureInfo.InvariantCulture)));
                break;
            default:
                output.Add((path, FlowSerializer.Serialize(value).TrimEnd('\r', '\n')));
                break;
        }
    }
}
