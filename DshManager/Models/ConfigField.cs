using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DshManager.Models;

/// <summary>
/// 参数控件类型：决定参数面板用哪种编辑控件呈现。
/// </summary>
public enum ParamKind
{
    /// <summary>单行文本。</summary>
    Text,

    /// <summary>数字。</summary>
    Number,

    /// <summary>布尔开关。</summary>
    Bool,

    /// <summary>枚举下拉（候选项见 Options）。</summary>
    Enum,

    /// <summary>结构复杂（{} / []），界面只读展示，改它请直接编辑 settings.yaml。</summary>
    Complex,
}

/// <summary>
/// 一条 DSH 官方设置项的中文说明书（对应 ~/.dsh/settings.yaml 里的一个键）。
///
/// 依据：本机安装的 @deepseek-ai 包（0.1.5-rc.3）里注册 settings 的地方，
/// 一共 14 个命名空间，这里把常用的逐条翻译成中文，共 6 组 33 项。
///
/// 两条事实决定了这里的写法：
///   1. settings.yaml 是热加载的 —— 保存后值立刻进内存，不用重启 DSH；
///      只有少数参数"语义上只对之后新建的会话起作用"，这类标 NewSessionOnly（界面加 ●）。
///   2. 顶层是"命名空间 → 分节"的映射，命名空间本身带连字符、不是点分层级；
///      所以 Path 写成 agent-default-model.provider 时，第一段是命名空间、第二段才是键。
/// </summary>
public sealed class ConfigField
{
    // ==================== 六个分组名（界面按此顺序分节） ====================
    public const string GroupModel = "模型与推理";
    public const string GroupPermission = "权限与搜索";
    public const string GroupAgent = "Agent 与工具";
    public const string GroupPreset = "人格与会话";
    public const string GroupUi = "界面与语言";
    public const string GroupProvider = "自定义服务商";

    /// <summary>settings.yaml 里的点路径，例如 agent-default-model.provider。</summary>
    public string Path { get; }

    /// <summary>所属分组。</summary>
    public string Group { get; }

    /// <summary>中文名。</summary>
    public string Title { get; }

    /// <summary>控件类型。</summary>
    public ParamKind Kind { get; }

    /// <summary>官方默认值；形如「（xxx）」的是说明性占位，表示"没有可用默认值"。</summary>
    public string Default { get; }

    /// <summary>Enum 的可选值；其余类型为 null。</summary>
    public string[]? Options { get; }

    /// <summary>中文说明。</summary>
    public string Help { get; }

    /// <summary>true = 值立刻生效，但只作用于"之后新建的会话"。</summary>
    public bool NewSessionOnly { get; }

    public ConfigField(
        string path,
        string group,
        string title,
        ParamKind kind,
        string @default,
        string help,
        bool newSessionOnly,
        string[]? options = null)
    {
        Path = path;
        Group = group;
        Title = title;
        Kind = kind;
        Default = @default;
        Help = help;
        NewSessionOnly = newSessionOnly;
        Options = options;
    }

    /// <summary>默认值是不是"说明性占位"（形如 （不设置））。这类值不能真的写进文件。</summary>
    public bool HasPlaceholderDefault => Default.Length > 0 && Default[0] == '（';

    private static List<ConfigField>? _all;

    /// <summary>全部 33 项官方参数定义（首次访问时构建并缓存）。</summary>
    public static IReadOnlyList<ConfigField> All()
    {
        if (_all is not null) return _all;

        _all = new List<ConfigField>
        {
            // ==================== 模型与推理 ====================
            new("agent-default-model.provider", GroupModel, "默认提供方", ParamKind.Text,
                "deepseek-official",
                "新会话默认用哪家服务。deepseek-official = DeepSeek 官方；装了第三方适配器后也可以填它的路由名。\n" +
                "这一项没有内置默认值，是本机组合给的 deepseek-official。",
                true),

            new("agent-default-model.model", GroupModel, "默认模型", ParamKind.Text,
                "deepseek-flash",
                "新会话默认用哪个模型。官方路由内置：deepseek-flash、deepseek-v4-flash、deepseek-v4-pro、deepseek-v4-flash-vision-exp。\n" +
                "填了不存在的模型 id，会在开新会话时报错。",
                true),

            new("agent-default-model.reasoningEffort", GroupModel, "思考强度", ParamKind.Enum,
                "（不设置）",
                "模型花多少力气推理。越高越聪明、也越慢越贵。\n" +
                "  off  = 不思考，最快\n  low  = 轻量思考\n  high = 认真思考\n  max  = 尽力思考（当前值）\n" +
                "官方刻意只把它放在设置层、不放进配置文件，所以这里是唯一的调节口。",
                true, new[] { "off", "low", "high", "max" }),

            new("llm-deepseek.defaultContextWindow", GroupModel, "上下文窗口", ParamKind.Number,
                "1000000",
                "和官方接口对话时按多大的上下文窗口来算。调小会更容易触发压缩（省钱），调大更吃 token。",
                false),

            new("llm-deepseek.maxTokens", GroupModel, "单次回复上限", ParamKind.Number,
                "256000",
                "一次回复最多生成多少 token。一般不用改。",
                false),

            new("llm-deepseek.thinking", GroupModel, "深度思考开关", ParamKind.Enum,
                "（不设置）",
                "是否让官方接口走思考模式。留空 = 交给模型自己决定。\n" +
                "注意：选 disabled 时，思考强度只能是 off。",
                false, new[] { "enabled", "disabled" }),

            new("llm-deepseek.baseURL", GroupModel, "接口地址", ParamKind.Text,
                "https://api.deepseek.com",
                "官方接口的地址。只有在用中转/代理时才需要改。也可以用环境变量 DEEPSEEK_BASE_URL 覆盖。",
                false),

            new("llm-deepseek.streamIdleTimeoutMs", GroupModel, "流式空闲超时", ParamKind.Number,
                "300000",
                "流式返回时，多久没数据就算卡住（毫秒）。网络差可以调大，比如 600000。",
                false),

            new("llm-deepseek.fileExpiresAfterSeconds", GroupModel, "上传文件保留时间", ParamKind.Number,
                "604800",
                "传给官方接口的文件在服务端保留多久（秒）。范围 3600 ~ 2592000。默认 604800 = 7 天。",
                false),

            // ==================== 权限与搜索 ====================
            new("permission.defaultPreset", GroupPermission, "默认权限档位", ParamKind.Enum,
                "（由组合推出，本机是 workspace-write）",
                "以后新建会话的默认放行程度。这是最影响安全的一项：\n" +
                "  danger-full-access = 完全放行，AI 可以随便改文件和跑命令（省事、风险最高）\n" +
                "  workspace-write    = 只能改工作目录里的东西\n" +
                "  read-only          = 只读，任何改动都要你点同意（最安全）\n" +
                "只影响新会话，已经开着的会话不变。",
                true, new[] { "danger-full-access", "workspace-write", "read-only" }),

            new("web-search-deepseek.apiKeyEnv", GroupPermission, "搜索密钥的环境变量名", ParamKind.Text,
                "DEEPSEEK_API_KEY",
                "联网搜索用哪个环境变量里的密钥。默认复用 DEEPSEEK_API_KEY。\n" +
                "密钥本身不放这里——放这里等于明文写进文件。",
                false),

            new("web-search-deepseek.model", GroupPermission, "搜索模型", ParamKind.Text,
                "deepseek-v4-flash",
                "联网搜索时用哪个模型。",
                false),

            new("web-search-deepseek.maxUses", GroupPermission, "单次搜索调用上限", ParamKind.Number,
                "5",
                "一轮对话里最多联网搜几次，防止它搜个没完。",
                false),

            new("web-search-deepseek.maxTokens", GroupPermission, "搜索回复上限", ParamKind.Number,
                "4096",
                "搜索模型一次最多输出多少 token。",
                false),

            new("web-search-deepseek.baseURL", GroupPermission, "搜索接口地址", ParamKind.Text,
                "（内置默认）",
                "联网搜索的接口地址，一般不用改。也可以用环境变量 DEEPSEEK_SEARCH_BASE_URL 覆盖。",
                false),

            // ==================== Agent 与工具 ====================
            new("agent-loop.maxParallelToolCalls", GroupAgent, "并行工具调用上限", ParamKind.Number,
                "10",
                "一轮里最多同时跑几个工具（比如同时读好几个文件）。调大更快，但更吃机器。",
                false),

            new("shell.timeoutMs", GroupAgent, "命令默认超时", ParamKind.Number,
                "120000",
                "AI 执行一条命令默认等多久（毫秒）。默认 120000 = 2 分钟。跑长任务可以调大。",
                false),

            new("shell.maxTimeoutMs", GroupAgent, "命令超时上限", ParamKind.Number,
                "600000",
                "AI 自己最多能把超时申请到多久（毫秒）。默认 600000 = 10 分钟。",
                false),

            new("shell.maxOutputBytes", GroupAgent, "内联输出上限", ParamKind.Number,
                "64000",
                "命令输出直接塞进对话的最大字节数，超出的部分会转到文件里。",
                false),

            new("shell.maxSpillBytes", GroupAgent, "转存输出上限", ParamKind.Number,
                "67108864",
                "输出转到文件时最多转多少字节。默认 67108864 = 64 MB。",
                false),

            new("shell.graceMs", GroupAgent, "强杀宽限时间", ParamKind.Number,
                "3000",
                "超时后先请求结束、等多久还没退再强杀（毫秒）。",
                false),

            new("shell.cwd", GroupAgent, "命令工作目录", ParamKind.Text,
                "（进程当前目录）",
                "AI 跑命令时的工作目录。留空 = 用启动 DSH 时的目录。填绝对路径。",
                false),

            new("shell.pwshPath", GroupAgent, "pwsh 路径", ParamKind.Text,
                "（自动查找）",
                "PowerShell 7（pwsh.exe）的位置。一般自动就能找到，找不到时才手填。",
                false),

            new("subagent-model-selection.enabled", GroupAgent, "允许给子任务选模型", ParamKind.Bool,
                "false",
                "开启后，委派子任务时可以指定用哪个模型（比如用便宜的跑琐事、用强的写代码）。\n" +
                "开启时必须至少配一条 allowedModels，否则校验不过。",
                true),

            new("subagent-model-selection.allowedModels", GroupAgent, "可选模型白名单", ParamKind.Complex,
                "[]",
                "形如 [{provider: deepseek-official, model: deepseek-flash}, ...] 的列表，不能重复。\n" +
                "结构复杂，请在 settings.yaml 里手工编辑。",
                true),

            // ==================== 人格与会话 ====================
            new("agent-presets.default", GroupPreset, "默认人格预设", ParamKind.Text,
                "standard",
                "新会话默认套用哪张人格卡。填你在「人格设定」里看到的卡名，例如 standard 或 greedy-whale-girl。\n" +
                "官方不校验这个名字，写错了会在开新会话时才报错。只影响新会话，已经开着的会话保持原来的人格。",
                true),

            // ==================== 界面与语言 ====================
            new("ui-theme.preference", GroupUi, "网页界面主题", ParamKind.Enum,
                "system",
                "DSH 网页界面的明暗。system = 跟随系统。这里说的是网页里的界面，不是本管理台。",
                false, new[] { "system", "light", "dark" }),

            new("ui-theme.fontSize", GroupUi, "网页正文字号", ParamKind.Number,
                "14",
                "网页界面正文的字号（像素），可填 12 ~ 17。",
                false),

            new("ui-chat.transcriptView", GroupUi, "历史记录显示方式", ParamKind.Enum,
                "compact",
                "已经完成的对话轮次怎么显示：normal 完整显示，compact 折叠成摘要（页面更清爽）。",
                false, new[] { "compact", "normal" }),

            new("ui-conversation.busyEnter", GroupUi, "忙时按回车的行为", ParamKind.Enum,
                "queue",
                "AI 正在干活时你按回车：queue = 排队等它做完；steer = 立刻插话打断它、改变它的方向。",
                false, new[] { "queue", "steer" }),

            new("locale.preference", GroupUi, "界面语言", ParamKind.Enum,
                "（跟随浏览器）",
                "网页界面的语言。随客户端内置的只有 zh 和 en。",
                false, new[] { "zh", "en" }),

            new("ui-onboarding.welcomeNoticeVersion", GroupUi, "新手提示版本", ParamKind.Text,
                "（可选）",
                "记录你已经看过哪一版欢迎提示。改小会让欢迎提示重新弹一次。一般不用动。",
                false),

            // ==================== 自定义服务商 ====================
            new("llm-pi-ai.providers", GroupProvider, "第三方服务商", ParamKind.Complex,
                "{}",
                "接入非官方服务商的地方，形如：\n" +
                "  <路由名>:\n" +
                "    displayName: 显示名\n" +
                "    api: openai-completions | openai-responses | anthropic-messages\n" +
                "    baseURL: https://...\n" +
                "    apiKeyEnv: 环境变量名\n" +
                "    models: [{id: 模型id}]\n" +
                "结构复杂，面板不拆开改——请直接手工编辑 settings.yaml。",
                false),
        };
        return _all;
    }

    /// <summary>按出现顺序取出所有分组名。</summary>
    public static IReadOnlyList<string> Groups()
    {
        var groups = new List<string>();
        foreach (var field in All())
        {
            if (!groups.Contains(field.Group)) groups.Add(field.Group);
        }
        return groups;
    }

    /// <summary>取某一组下的全部参数（保持定义顺序）。</summary>
    public static IReadOnlyList<ConfigField> InGroup(string group)
    {
        var list = new List<ConfigField>();
        foreach (var field in All())
        {
            if (field.Group == group) list.Add(field);
        }
        return list;
    }

    /// <summary>按点路径查找参数定义；找不到返回 null。</summary>
    public static ConfigField? Find(string path)
    {
        foreach (var field in All())
        {
            if (field.Path == path) return field;
        }
        return null;
    }

    /// <summary>参数总条数。</summary>
    public static int Count => All().Count;
}

/// <summary>
/// 参数值的可绑定包装：作为表单的绑定载体（字符串 / 布尔），并记录"载入时的原值"，
/// 以便保存时只写真正改动过的项（未改的键不动，也不把官方默认值无谓地写进文件）。
/// </summary>
public partial class ConfigFieldValue : ObservableObject
{
    public ConfigField Meta { get; }

    /// <summary>字符串编辑载体：Text / Number / Enum / Complex（Complex 为只读展示）。</summary>
    [ObservableProperty]
    private string stringValue = "";

    /// <summary>布尔编辑载体：Bool 类型。</summary>
    [ObservableProperty]
    private bool boolValue;

    /// <summary>Enum 候选项（含文件中出现但不在官方列表里的现值，避免下拉框丢值）。</summary>
    public ObservableCollection<string> Options { get; }

    /// <summary>载入时文件里的值；保存时用它与当前值比较，判断是否真的改过。</summary>
    private string _loadedValue = "";

    /// <summary>默认值展示文本（占位默认按原样显示，便于用户理解）。</summary>
    public string DefaultText => string.IsNullOrEmpty(Meta.Default) ? "（空）" : Meta.Default;

    /// <summary>当前编辑值是否与载入时不同（用于"已改"角标与最小写入）。</summary>
    public bool IsChanged => Meta.Kind == ParamKind.Bool
        ? (BoolValue ? "true" : "false") != _loadedValue
        : (StringValue ?? "").Trim() != _loadedValue;

    public ConfigFieldValue(ConfigField meta)
    {
        Meta = meta;
        Options = new ObservableCollection<string>(meta.Options ?? Array.Empty<string>());
    }

    /// <summary>按 settings.yaml 中读到的原值填充控件；raw 为 null 表示文件里没有这个键。</summary>
    public void LoadValue(string? raw)
    {
        if (Meta.Kind == ParamKind.Bool)
        {
            // 键不存在时展示参数默认值，让用户看到"实际生效值"
            var effective = raw ?? (Meta.Default.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false");
            BoolValue = string.Equals(effective, "true", StringComparison.OrdinalIgnoreCase);
            _loadedValue = BoolValue ? "true" : "false";
            return;
        }

        var value = raw;
        if (value is null)
        {
            // 键不存在：占位默认值不是真实值，留空；真实默认值直接展示
            value = Meta.HasPlaceholderDefault ? "" : Meta.Default;
        }

        // Enum 的现值若不在官方候选中，补进下拉框，避免选择被清空
        if (Meta.Kind == ParamKind.Enum && value.Length > 0 && !Options.Contains(value))
        {
            Options.Add(value);
        }
        StringValue = value;
        _loadedValue = value;
    }

    /// <summary>把表单重置为参数默认值（只改内存，不写盘）。</summary>
    public void ResetToDefault()
    {
        if (Meta.Kind == ParamKind.Bool)
        {
            BoolValue = Meta.Default.Equals("true", StringComparison.OrdinalIgnoreCase);
            return;
        }

        var value = Meta.HasPlaceholderDefault ? "" : Meta.Default;
        if (Meta.Kind == ParamKind.Enum && value.Length > 0 && !Options.Contains(value))
        {
            Options.Add(value);
        }
        StringValue = value;
    }

    /// <summary>当前编辑值转换为写入 YAML 的标量文本。</summary>
    public string ToStorageValue() => Meta.Kind == ParamKind.Bool
        ? (BoolValue ? "true" : "false")
        : (StringValue ?? "").Trim();

    // 编辑值变化时，同步通知 IsChanged（"已改"角标依赖它刷新）
    partial void OnStringValueChanged(string value) => OnPropertyChanged(nameof(IsChanged));

    partial void OnBoolValueChanged(bool value) => OnPropertyChanged(nameof(IsChanged));
}
