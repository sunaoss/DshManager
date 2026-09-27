namespace DshManager.Models;

/// <summary>
/// 插件市场中的一个条目。
/// 数据来源有两处：
/// - npm registry search 接口（包名 / 版本 / description / 链接 / 发布时间）——不含中文元数据；
/// - 包内 package.json 的 <c>dshhub</c> 段（中文简介 / 分类 / 兼容性 / 网络权限），需额外拉详情才有。
/// 因此中文简介等字段允许缺省，界面通过 <see cref="SummaryText"/> 等回退属性展示。
/// </summary>
public class PluginMarketItem
{
    /// <summary>包名，如 dsh-cost-meter。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>最新版本号。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>npm description（可能中英混排，作为中文简介缺失时的回退）。</summary>
    public string Description { get; set; } = string.Empty;

    // ==================== 以下字段来自 dshhub 元数据（可能缺省） ====================

    /// <summary>dshhub.displayName：展示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>dshhub.summary：中文简介（列表优先展示）。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>dshhub.categories：中文分类。</summary>
    public IReadOnlyList<string> Categories { get; set; } = [];

    /// <summary>dshhub.surfaces：生效面（host / web / ...）。</summary>
    public IReadOnlyList<string> Surfaces { get; set; } = [];

    /// <summary>dshhub.capabilities.provides：提供的能力（如 service:cost-meter）。</summary>
    public IReadOnlyList<string> Provides { get; set; } = [];

    /// <summary>dshhub.permissions.network：需要访问的网络地址（详情页作为安全提示逐条列出）。</summary>
    public IReadOnlyList<string> NetworkPermissions { get; set; } = [];

    /// <summary>dshhub.compatibility.dsh：要求的 dsh 版本范围，如 "&gt;=0.1.0-rc.5"。</summary>
    public string DshRequirement { get; set; } = string.Empty;

    /// <summary>dshhub.compatibility.node：要求的 node 版本范围。</summary>
    public string NodeRequirement { get; set; } = string.Empty;

    /// <summary>包内是否带 dshhub 元数据段（false 时中文简介 / 分类 / 权限等均缺失）。</summary>
    public bool HasDshHub { get; set; }

    // ==================== 通用元数据 ====================

    /// <summary>仓库地址（repository.url 或 repository 字符串）。</summary>
    public string RepositoryUrl { get; set; } = string.Empty;

    /// <summary>主页地址（package.json 的 homepage）。</summary>
    public string HomepageUrl { get; set; } = string.Empty;

    /// <summary>npm 包页面地址。</summary>
    public string NpmUrl { get; set; } = string.Empty;

    /// <summary>npm 发布时间（UTC）。</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>npm keywords（含 dsh-plugin 标记）。</summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>
    /// 最近一周下载量（来自 api.npmjs.org 的 point/last-week 接口）。
    /// 默认 0：只有「下载量」排序模式才会去拉取并写入；拉取失败的包保持 0（降级为排在末尾）。
    /// </summary>
    public int Downloads { get; set; }

    // ==================== 运行时状态（由 ViewModel 填充） ====================

    /// <summary>本机 profile 是否已安装该插件。</summary>
    public bool IsInstalled { get; set; }

    /// <summary>本机已安装的版本（未安装时为空）。</summary>
    public string InstalledVersion { get; set; } = string.Empty;

    /// <summary>
    /// 与本机 dsh 版本是否兼容；未知（无版本要求或范围无法解析）视为兼容，避免误报不兼容。
    /// 属性对外只读，由市场服务通过 <see cref="SetCompatibility"/> 写入。
    /// </summary>
    public bool IsCompatible { get; private set; } = true;

    /// <summary>供市场服务写入兼容性判定结果（属性对外只读，避免界面误改）。</summary>
    public void SetCompatibility(bool compatible) => IsCompatible = compatible;

    // ==================== 只读展示属性 ====================

    /// <summary>展示标题：优先 dshhub.displayName，回退包名。</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>列表简介：优先 dshhub.summary（中文），回退 npm description。</summary>
    public string SummaryText => string.IsNullOrWhiteSpace(Summary) ? Description : Summary;

    /// <summary>分类拼接文本，如 "费用 · Web UI · 观测"。</summary>
    public string CategoriesText => string.Join(" · ", Categories);

    /// <summary>发布日期的展示文本（UTC 日期，如 "2026-09-26"）；无日期为空。</summary>
    public string PublishedText => PublishedAt?.ToString("yyyy-MM-dd") ?? string.Empty;

    /// <summary>最近一周下载量的展示文本（如 "⬇ 18,117"）；未拉到（0）时为空。</summary>
    public string DownloadsText => Downloads > 0 ? $"⬇ {Downloads:N0}" : string.Empty;

    /// <summary>dsh 版本要求的中文提示，如 "要求 dsh &gt;=0.1.0-rc.5"；无要求时为空。</summary>
    public string CompatibilityText =>
        string.IsNullOrWhiteSpace(DshRequirement) ? string.Empty : $"要求 dsh {DshRequirement}";

    /// <summary>本机安装状态展示文本，如 "已安装 1.7.39" / "未安装"。</summary>
    public string InstalledText => IsInstalled
        ? (string.IsNullOrWhiteSpace(InstalledVersion) ? "已安装" : $"已安装 {InstalledVersion}")
        : "未安装";
}
