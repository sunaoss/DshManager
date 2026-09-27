using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>环境检测项的严重级别。</summary>
public enum EnvCheckStatus
{
    /// <summary>通过。</summary>
    Ok,

    /// <summary>可用但有风险（如端口被占用、依赖疑似正在被插件安装重建）。</summary>
    Warning,

    /// <summary>缺失（如未安装 Node.js / dsh）。</summary>
    Missing,
}

/// <summary>
/// 单个环境检测项（只读快照，由 <see cref="EnvironmentCheckService.CheckAll"/> 产出）。
/// </summary>
public class EnvCheckItem
{
    /// <summary>检测项名称，如「Node.js」。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>检测结论级别。</summary>
    public EnvCheckStatus Status { get; init; }

    /// <summary>检测详情，如「v24.21.0  C:\Program Files\nodejs\node.exe」或「未检测到」。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>中文说明 / 建议。</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>修复方式："openUrl"（打开外部链接）| "runCommand"（执行修复命令）| "none"（只能人工处理）。</summary>
    public string FixKind { get; init; } = "none";

    /// <summary>
    /// 修复目标：FixKind=openUrl 时为 URL；FixKind=runCommand 时为完整命令行（已内含工作目录切换，可直接交给 cmd 执行）。
    /// </summary>
    public string FixTarget { get; init; } = string.Empty;

    /// <summary>修复按钮文案，如「打开下载页」「一键安装 dsh」「修复依赖」。</summary>
    public string FixLabel { get; init; } = string.Empty;

    /// <summary>官网/官方文档链接（可点击）；为空表示该项没有外部链接。</summary>
    public string LinkUrl { get; init; } = string.Empty;

    /// <summary>官网链接的按钮文案，如「Node 官网」「dsh 官方仓库」。</summary>
    public string LinkLabel { get; init; } = string.Empty;
}

/// <summary>
/// 「一键安装全部缺失项」的一个执行步骤（由 <see cref="EnvironmentCheckService.BuildInstallPlan"/> 产出）。
/// </summary>
/// <param name="ItemName">对应的检测项名称：执行时按它从**最新**检测结果回查修复动作（前一步可能已把它顺带解决）。</param>
/// <param name="Label">面向用户的中文步骤说明，用于汇总确认框与执行进度。</param>
/// <param name="FixTarget">当前检测给出的修复目标（便携版标记或命令行），用于判断是否走便携版安装流程。</param>
public sealed record EnvInstallStep(string ItemName, string Label, string FixTarget);

/// <summary>
/// 环境检测服务：逐项检测本机运行 dsh 所依赖的环境（Node.js / npm / dsh 本体 / dsh 依赖完整性 / 端口 / 配置文件），
/// 并对可自动处理的问题给出修复动作（打开下载页或执行修复命令）。
///
/// 【设计原则】
/// 1) 全部检测"异常安全"：单项失败不影响整体，降级为 Warning 并带中文原因；
/// 2) 只做只读检测，不修改任何 dsh 文件；修复动作必须由 UI 侧弹确认框后才会执行；
/// 3) 子进程调用一律带超时 + CreateNoWindow，避免卡界面、避免弹黑窗。
///
/// 【为什么把 dsh 依赖完整性单列一项】本机真实故障：插件安装/更新时 pnpm 会重建
/// &lt;DSH_HOME&gt;\profiles\node_modules，期间 @deepseek-ai 层会被清空，
/// 此时启动 dsh 就报 "Cannot find package '@deepseek-ai/dsh-llm'"。
/// 单独检测"关键依赖包是否存在"可以让用户一眼看出问题并一键修复，而不是对着启动日志发懵。
/// </summary>
public class EnvironmentCheckService
{
    /// <summary>命令超时（毫秒）：node/npm/where 一般秒回，10 秒未响应即视为检测失败。</summary>
    private const int CommandTimeoutMs = 10_000;

    /// <summary>端口探测超时（毫秒）：只要探测速度，不等待真正握手完成。</summary>
    private const int PortProbeTimeoutMs = 500;

    /// <summary>修复命令超时（分钟）：pnpm 重建依赖可能耗时数分钟，给足时间。</summary>
    private static readonly TimeSpan FixTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Node.js 官方中文下载页（npm/npx 随 Node 附带；缺失时作为"自行安装"的兜底入口）。</summary>
    public const string NodeDownloadUrl = "https://nodejs.org/zh-cn/download";

    /// <summary>Node.js 官网链接的按钮文案。</summary>
    private const string NodeDownloadLabel = "Node 官网";

    /// <summary>
    /// dsh 官方仓库（GitHub）：面板代装（npm 全局安装）之外的长期方案入口，
    /// 集中定义便于界面与文案统一引用。
    /// </summary>
    public const string DshOfficialRepoUrl = "https://github.com/deepseek-ai/deepseek-harness";

    /// <summary>dsh 官方仓库链接的按钮文案。</summary>
    private const string DshOfficialRepoLabel = "dsh 官方仓库";

    /// <summary>
    /// 「临时方案」提醒（dsh 本体项）：面板默认以 npm 全局安装 dsh，属快速方案；
    /// 长期使用或需要自定义部署时应按官方仓库说明自行安装。
    /// </summary>
    private const string DshFastTrackNotice =
        "；属面板代装的快速方式，也可以按官方仓库说明自行安装或改用其他部署方式";

    /// <summary>
    /// FixTarget 特殊标记：表示该修复动作由"面板内安装便携版 Node"流程处理，**不是** shell 命令。
    /// UI 侧必须特判此标记去调用 <see cref="NodeRuntimeService.InstallPortableNodeAsync"/>，
    /// 绝不能把它交给 cmd 执行。
    /// </summary>
    public const string InstallPortableNodeMarker = "内置命令:install-portable-node";

    /// <summary>
    /// npm/npx 缺失（以及"系统没有 npm 导致装不了 dsh"）时的一键修复按钮文案。
    /// 面板便携版 Node 自带 npm.cmd / npx.cmd，所以这种情况与"装 Node"是同一个动作。
    /// </summary>
    public const string PortableNodeInstallLabel = "一键安装 Node（便携版，含 npm/npx）";

    /// <summary>dsh 的 npm 包名（与 DshInstallService 默认安装的包一致）。</summary>
    private const string DshPackageName = "@deepseek-ai/dsh";

    /// <summary>检测项名称常量：界面与「一键安装全部缺失项」都按名称定位检测项，集中定义避免魔法字符串漂移。</summary>
    public const string NodeItemName = "Node.js";

    /// <summary>检测项名称：npm / npx。</summary>
    public const string NpmItemName = "npm / npx";

    /// <summary>检测项名称：dsh 本体。</summary>
    public const string DshItemName = "dsh 本体";

    /// <summary>检测项名称：dsh 依赖完整性。</summary>
    public const string DshDepsItemName = "dsh 依赖完整性";

    /// <summary>检测项名称：端口可用性。</summary>
    public const string PortItemName = "端口可用性";

    /// <summary>检测项名称：配置文件。</summary>
    public const string ConfigItemName = "配置文件";

    /// <summary>默认 profile 名（与插件页、市场页保持一致）。</summary>
    private const string DefaultProfileName = "web";

    /// <summary>
    /// dsh 依赖中的关键包清单（固定相对路径判存在性）。
    /// 这几个包分别是 LLM 接入层、会话层、基础库与 Web UI，任一缺失都会让 dsh 启动即崩。
    /// 刻意不做递归扫描：profiles\node_modules 是 pnpm 的巨型目录，递归会拖死界面。
    /// </summary>
    private static readonly string[] RequiredSharedPackages = { "dsh-llm", "dsh-session", "dsh-base", "dsh-web-app" };

    /// <summary>版本号提取正则：匹配 v24.21.0 / 10.9.0 / 0.1.5 这类输出。</summary>
    private static readonly Regex VersionRegex = new(@"v?\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);

    private readonly string _dshHome;
    private readonly string _profileName;
    private readonly DshDiscoveryService _discovery;

    /// <summary>便携版 Node 运行时服务：检测时优先使用面板自己安装的便携版。</summary>
    private readonly NodeRuntimeService _nodeRuntime;
    private readonly Func<string, string, string?> _runCommand;
    private readonly Func<string, string?> _locateExecutable;
    private readonly Func<int, bool> _portProbe;

    /// <summary>
    /// 创建环境检测服务。
    /// </summary>
    /// <param name="dshHome">dsh 数据目录；为空时取 DSH_HOME 环境变量或 ~/.dsh。</param>
    /// <param name="profileName">dsh profile 名；为空时用 "web"。</param>
    /// <param name="port">要检测的 Web 端口；为空时读取 settings.json 的 Port（失败回退 3080）。</param>
    /// <param name="discovery">dsh 发现服务；为空时用全局共享实例（AppServices.Discovery）。</param>
    /// <param name="commandRunner">命令执行器 (文件名, 参数) =&gt; 输出；默认带超时的真实子进程实现，测试可注入假实现。</param>
    /// <param name="executableLocator">可执行文件定位器（如 node）=&gt; 完整路径；默认走 where.exe，测试可注入假实现。</param>
    /// <param name="portProbe">端口占用探测委托；默认走 DshProcessService.IsPortOpen。</param>
    /// <param name="nodeRuntime">便携版 Node 运行时服务；为空时使用默认实例（测试注入指向空目录的实例以隔离真实环境）。</param>
    public EnvironmentCheckService(
        string? dshHome = null,
        string? profileName = null,
        int? port = null,
        DshDiscoveryService? discovery = null,
        Func<string, string, string?>? commandRunner = null,
        Func<string, string?>? executableLocator = null,
        Func<int, bool>? portProbe = null,
        NodeRuntimeService? nodeRuntime = null)
    {
        _dshHome = string.IsNullOrWhiteSpace(dshHome) ? DshDiscoveryService.GetDefaultDshHome() : dshHome;
        _profileName = string.IsNullOrWhiteSpace(profileName) ? DefaultProfileName : profileName;
        _discovery = discovery ?? AppServices.Discovery;
        _nodeRuntime = nodeRuntime ?? new NodeRuntimeService();
        _runCommand = commandRunner ?? RunCommandWithTimeout;
        _locateExecutable = executableLocator ?? LocateExecutableDefault;
        _portProbe = portProbe ?? (p => DshProcessService.IsPortOpen("127.0.0.1", p, PortProbeTimeoutMs));
        Port = port is > 0 and <= 65535 ? port.Value : ReadConfiguredPortSafe();
    }

    /// <summary>当前检测的 Web 端口（界面可在设置页改端口后同步更新）。</summary>
    public int Port { get; set; }

    /// <summary>dsh 数据目录（DSH_HOME）。</summary>
    public string DshHome => _dshHome;

    /// <summary>当前 profile 的目录：&lt;DSH_HOME&gt;\profiles\&lt;name&gt;。</summary>
    public string ProfileDirectory => Path.Combine(_dshHome, "profiles", _profileName);

    /// <summary>
    /// 逐项检测本机运行 dsh 所需的环境。
    /// 任何单项异常都不会让整体抛异常：失败的项降级为 Warning 并带中文原因。
    /// </summary>
    public IReadOnlyList<EnvCheckItem> CheckAll() => new[]
    {
        SafeCheck(NodeItemName, CheckNode),
        SafeCheck(NpmItemName, CheckNpm),
        SafeCheck(DshItemName, CheckDsh),
        SafeCheck(DshDepsItemName, CheckDshDependencies),
        SafeCheck(PortItemName, CheckPort),
        SafeCheck(ConfigItemName, CheckConfigFiles),
    };

    // ==================== 各项检测 ====================

    /// <summary>
    /// 1) Node.js：优先使用面板自己安装的便携版（装了即视为 Ok，并在详情里注明来源与路径）；
    /// 便携版不存在或不可执行时才回退到 PATH 里的 node。
    /// 两者都缺失时提供「一键安装便携版 Node」修复动作（FixTarget 为特殊标记，由 UI 走安装流程）。
    /// </summary>
    private EnvCheckItem CheckNode()
    {
        string? version = null;
        string? path = null;
        var fromPortable = false;

        // 系统已有 Node 优先：dsh 运行时会调用系统 shell / 原生模块 / git 等外部命令，
        // 若改用面板便携版启动，这些子进程的环境会与系统不一致（用户明确要求系统优先）。
        version = ExtractVersion(SafeRun("node", "--version"));
        if (version is not null)
        {
            // 定位 node 可执行文件仅用于展示；取不到不影响「已安装」结论
            path = SafeLocate("node");
        }
        else
        {
            // 系统完全没有 Node → 回退到面板便携版（免管理员，仅面板启动 dsh 时使用）
            var portableNode = SafePortablePath(GetPortableNodePathSafe);
            if (!string.IsNullOrWhiteSpace(portableNode))
            {
                version = ExtractVersion(SafeRun(QuoteIfNeeded(portableNode), "--version"));
                if (version is not null)
                {
                    path = portableNode;
                    fromPortable = true;
                }
            }
        }

        if (version is null)
        {
            return new EnvCheckItem
            {
                Name = NodeItemName,
                Status = EnvCheckStatus.Missing,
                Detail = "未检测到",
                Hint = "dsh 需要 Node.js 运行。可点右侧一键安装免管理员的便携版（属面板代装的快速方案，仅面板启动 dsh 用，不改系统环境）；" +
                       "长期使用建议到 Node 官网安装正式版，也可自行到 nodejs.org 安装后重启面板重新检测",
                // 走面板内便携版安装流程：FixTarget 是特殊标记而非 shell 命令，UI 会特判它
                FixKind = "runCommand",
                FixTarget = InstallPortableNodeMarker,
                FixLabel = "一键安装 Node（便携版）",
                LinkLabel = NodeDownloadLabel,
                LinkUrl = NodeDownloadUrl,
            };
        }

        var detail = path switch
        {
            null => version,
            _ when fromPortable => $"{version}  {path}（系统未找到 Node，回退使用面板便携版）",
            _ => $"{version}  {path}（系统 Node）",
        };
        return new EnvCheckItem
        {
            Name = "Node.js",
            Status = EnvCheckStatus.Ok,
            Detail = detail,
            // 系统 Node 就绪时保持原有"已就绪"说明不加提醒；只有回退使用面板便携版时才提示这是临时快速方案
            Hint = fromPortable
                ? "面板安装的是便携版（免管理员、仅面板启动 dsh 用，属快速方案）；长期使用建议到 Node 官网安装正式版"
                : "已就绪（使用系统 Node，dsh 子进程环境与系统一致）",
            FixKind = "none",
            LinkLabel = NodeDownloadLabel,
            LinkUrl = NodeDownloadUrl,
        };
    }

    /// <summary>
    /// 2) npm / npx：**系统 PATH 优先**（与 Node 检测保持一致，保证 dsh 子进程环境统一）；
    /// 系统缺失时才回退到面板便携版自带的 npm.cmd / npx.cmd；
    /// 都缺失时提供「一键安装 Node（便携版，含 npm/npx）」修复动作 ——
    /// 面板便携版 Node 自带 npm.cmd / npx.cmd，装一份自带 npm 的 Node 就能一并解决
    /// （即使本机已有 node 但缺 npm，便携版同样能补上）。
    /// </summary>
    private EnvCheckItem CheckNpm()
    {
        // 先用系统 PATH 里的 npm / npx
        var npm = ExtractVersion(SafeRun("npm", "--version"));
        var npx = ExtractVersion(SafeRun("npx", "--version"));
        var fromPortable = false;

        // 系统缺失才尝试面板便携版自带的（npx.cmd 与 npm.cmd 在便携版目录里并存）
        if (npm is null || npx is null)
        {
            var portableNpm = SafePortablePath(GetPortableNpmPathSafe);
            if (!string.IsNullOrWhiteSpace(portableNpm))
            {
                var portableNpx = Path.Combine(Path.GetDirectoryName(portableNpm)!, "npx.cmd");
                if (!SafeFileExists(portableNpx)) portableNpx = null;

                npm ??= ExtractVersion(SafeRun(QuoteIfNeeded(portableNpm), "--version"));
                npx ??= ExtractVersion(SafeRun(portableNpx is null ? "npx" : QuoteIfNeeded(portableNpx), "--version"));
                fromPortable = true;
            }
        }

        if (npm is null || npx is null)
        {
            return new EnvCheckItem
            {
                Name = NpmItemName,
                Status = EnvCheckStatus.Warning,
                Detail = $"npm：{npm ?? "未检测到"}；npx：{npx ?? "未检测到"}",
                // 一键安装便携版 Node 即可补上 npm/npx（便携版自带）；也保留"自行到 nodejs.org 装官方版"的兜底说明
                Hint = $"npm / npx 随 Node.js 一起安装（安装时请勾选 npm）。可点右侧一键安装一份自带 npm/npx 的便携版 Node（属面板代装的快速方案），" +
                       $"也可自行到 {NodeDownloadUrl} 安装官方版后重启面板重新检测",
                FixKind = "runCommand",
                FixTarget = InstallPortableNodeMarker,
                FixLabel = PortableNodeInstallLabel,
            };
        }

        return new EnvCheckItem
        {
            Name = NpmItemName,
            Status = EnvCheckStatus.Ok,
            Detail = $"npm {npm}  npx {npx}",
            Hint = fromPortable
                ? "系统 PATH 里没有 npm/npx，回退使用面板便携版自带的（属面板代装的快速方案）"
                : "已就绪（使用系统 npm/npx）",
            FixKind = "none",
        };
    }

    /// <summary>3) dsh 本体：复用 DshDiscoveryService 扫描结果，展示版本与推荐安装位置来源。</summary>
    private EnvCheckItem CheckDsh()
    {
        var info = SafeScan();
        if (info is null || !info.IsInstalled)
        {
            return BuildDshInstallFixItem(
                EnvCheckStatus.Missing,
                "未检测到",
                "本机未安装 dsh，可点右侧按钮一键安装");
        }

        var recommended = info.RecommendedInstallation;
        var location = recommended is not null
            ? $"{recommended.SourceText}：{recommended.PackageDir}"
            : (string.IsNullOrWhiteSpace(info.LaunchCommand) ? info.DataDir : $"启动方式：{info.LaunchCommand}");
        // 版本识别失败时用"版本未知"，避免显示成 "vunknown"
        var versionText = string.IsNullOrWhiteSpace(info.Version) || info.Version == "unknown"
            ? "版本未知"
            : $"v{info.Version}";

        // 找到了文件但没有任何可用启动方式：多半是坏 shim / 损坏的安装，降级为 Warning 并建议重装
        if (recommended is null && string.IsNullOrWhiteSpace(info.LaunchCommand))
        {
            return BuildDshInstallFixItem(
                EnvCheckStatus.Warning,
                $"{versionText}  {location}",
                "检测到 dsh 相关文件，但未找到可用的启动方式，建议重新安装");
        }

        return new EnvCheckItem
        {
            Name = DshItemName,
            Status = EnvCheckStatus.Ok,
            Detail = $"{versionText}  {location}",
            // 已安装也提示"面板代装属快速方式"，并给出官方仓库链接供长期使用 / 自定义部署参考
            Hint = $"已安装（面板代装方式为 npm install -g {DshPackageName}，属快速方式；长期使用或自定义部署建议按官方仓库说明自行安装）",
            FixKind = "none",
            LinkLabel = DshOfficialRepoLabel,
            LinkUrl = DshOfficialRepoUrl,
        };
    }

    /// <summary>
    /// 构造"dsh 缺失 / 需重装"的检测项，并**动态决定修复动作**：
    /// - 系统有 npm（或面板便携版已装）→ 给出真正的 npm 安装命令（见 <see cref="BuildDshInstallCommand"/>）；
    /// - 全新电脑连 npm 都没有 → 修复动作改为"先安装便携版 Node"（便携版自带 npm）。
    ///   为什么不能继续用裸 `npm install -g`：全新电脑 PATH 里根本没有 npm，
    ///   点下去只会报"不是内部或外部命令"，用户还得自己再找 Node 安装包，就不是"一键"了。
    /// </summary>
    private EnvCheckItem BuildDshInstallFixItem(EnvCheckStatus status, string detail, string hint)
    {
        var installCommand = BuildDshInstallCommand();
        if (installCommand is null)
        {
            return new EnvCheckItem
            {
                Name = DshItemName,
                Status = status,
                Detail = detail,
                Hint = $"{hint}；但系统里没有可用的 npm，需要先安装 Node（含 npm），点右侧按钮即可" +
                       $"（面板会下载免管理员的便携版 Node，它自带 npm/npx），装好后可再点这里用 npm install -g {DshPackageName} 安装 dsh" +
                       DshFastTrackNotice,
                FixKind = "runCommand",
                FixTarget = InstallPortableNodeMarker,
                FixLabel = PortableNodeInstallLabel,
                LinkLabel = DshOfficialRepoLabel,
                LinkUrl = DshOfficialRepoUrl,
            };
        }

        return new EnvCheckItem
        {
            Name = DshItemName,
            Status = status,
            Detail = detail,
            Hint = $"{hint}（{installCommand}）{DshFastTrackNotice}",
            FixKind = "runCommand",
            FixTarget = installCommand,
            FixLabel = "一键安装 dsh",
            LinkLabel = DshOfficialRepoLabel,
            LinkUrl = DshOfficialRepoUrl,
        };
    }

    /// <summary>
    /// 4) dsh 依赖完整性：检查 &lt;DSH_HOME&gt;\profiles\node_modules\@deepseek-ai 下的关键包是否存在。
    /// 这是本次故障的直接教训项（见类注释）。
    /// </summary>
    private EnvCheckItem CheckDshDependencies()
    {
        var profilesDir = Path.Combine(_dshHome, "profiles");
        if (!SafeDirectoryExists(profilesDir))
        {
            // profiles 目录都不存在 → dsh 尚未安装 / 从未启动过，此时给修复命令没有意义
            return new EnvCheckItem
            {
                Name = DshDepsItemName,
                Status = EnvCheckStatus.Warning,
                Detail = $"未检测到 dsh profiles 目录：{profilesDir}",
                Hint = "请先安装并至少启动一次 dsh，再回到这里点「重新检测」",
                FixKind = "none",
            };
        }

        var sharedScopeDir = Path.Combine(profilesDir, "node_modules", "@deepseek-ai");
        var missing = RequiredSharedPackages
            .Where(pkg => !SafeDirectoryExists(Path.Combine(sharedScopeDir, pkg)))
            .ToList();

        if (missing.Count == 0)
        {
            return new EnvCheckItem
            {
                Name = DshDepsItemName,
                Status = EnvCheckStatus.Ok,
                Detail = $"关键依赖齐全（{RequiredSharedPackages.Length} 个）：{string.Join("、", RequiredSharedPackages)}",
                Hint = "依赖来源：profiles\\node_modules\\@deepseek-ai",
                FixKind = "none",
            };
        }

        var fixTarget = BuildDependencyFixCommand();
        return new EnvCheckItem
        {
            Name = "dsh 依赖完整性",
            Status = EnvCheckStatus.Warning,
            Detail = $"缺失：{string.Join("、", missing)}（依赖来源：{sharedScopeDir}）",
            Hint = "dsh 的依赖可能正在被插件安装重建或被损坏；等 1 分钟后重试，若仍失败请点「修复依赖」",
            FixKind = "runCommand",
            FixTarget = fixTarget,
            FixLabel = "修复依赖",
        };
    }

    /// <summary>
    /// 构造"修复依赖"命令，并**在命令里带上 cd /d 切到 profile 目录**（pnpm 必须在该目录下才能识别 workspace）。
    /// 首选与面板插件管理完全一致的官方路径：node &lt;bin.js&gt; plugin --profile &lt;name&gt; install（本质是 pnpm install，
    /// 但用 dsh 自己的入口可保证 pnpm 版本 / 配置一致）；
    /// bin.js 不存在（dsh 入口缺失）时回退为直接 `pnpm install`。
    /// 命令里的 node 前缀由 <see cref="ResolveNodeCommand"/> 动态解析（系统优先、便携版兜底）。
    /// </summary>
    private string BuildDependencyFixCommand()
    {
        var binJs = Path.Combine(_dshHome, "profiles", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var install = SafeFileExists(binJs)
            ? $"{ResolveNodeCommand()} \"{binJs}\" plugin --profile {_profileName} install"
            : "pnpm install";
        return $"cd /d \"{ProfileDirectory}\" && {install}";
    }

    // ==================== 命令前缀动态解析（系统优先、便携版兜底） ====================

    /// <summary>
    /// 解析执行 npm 时应使用的前缀，遵循"系统优先、便携版兜底"的统一策略：
    /// 1) 系统 PATH 有 npm（能打印出版本）→ 裸 "npm"，沿用系统环境，与启动 dsh 的策略一致；
    /// 2) 否则若面板便携版存在 → **便携版 npm.cmd 的绝对路径**（含空格自动加引号）。
    ///    为什么必须给绝对路径：Windows 上 npm 实为 .cmd 批处理，全新电脑 PATH 里没有它，
    ///    只有绝对路径才能让命令行直接命中面板便携版里的 npm.cmd；
    /// 3) 都没有 → null，调用方需先引导用户安装便携版 Node（便携版自带 npm）。
    /// </summary>
    private string? ResolveNpmCommand()
    {
        if (ExtractVersion(SafeRun("npm", "--version")) is not null) return "npm";

        var portableNpm = SafePortablePath(GetPortableNpmPathSafe);
        return string.IsNullOrWhiteSpace(portableNpm) ? null : QuoteIfNeeded(portableNpm);
    }

    /// <summary>
    /// 解析命令里 node 的前缀：
    /// 系统有 node → 裸 "node"（沿用系统环境，与 <see cref="NodeRuntimeService.GetPreferredNodePath"/> 的系统优先策略一致）；
    /// 系统没有 node 但便携版存在 → 便携版 node.exe 绝对路径（含空格自动加引号）。
    /// 都缺失时回退裸 "node"：命令可能失败，但与用户手敲行为一致，且对应检测项的 Hint 会提示先装 Node。
    /// </summary>
    private string ResolveNodeCommand()
    {
        if (ExtractVersion(SafeRun("node", "--version")) is not null) return "node";

        var portableNode = SafePortablePath(GetPortableNodePathSafe);
        return string.IsNullOrWhiteSpace(portableNode) ? "node" : QuoteIfNeeded(portableNode);
    }

    /// <summary>
    /// 构造"一键安装 dsh"命令；npm 前缀动态选择（见 <see cref="ResolveNpmCommand"/>）。
    /// 返回 null 表示本机连 npm 都没有、便携版也未安装 —— 此时必须先装便携版 Node（便携版自带 npm），
    /// 直接跑裸 `npm install -g` 在全新电脑上必然报"不是内部或外部命令"。
    /// </summary>
    private string? BuildDshInstallCommand()
    {
        var npm = ResolveNpmCommand();
        return npm is null ? null : $"{npm} install -g {DshPackageName}";
    }

    /// <summary>5) 端口可用性：端口已被占用多半是已有 dsh 在跑。</summary>
    private EnvCheckItem CheckPort()
    {
        var inUse = SafePortProbe(Port);
        return inUse
            ? new EnvCheckItem
            {
                Name = PortItemName,
                Status = EnvCheckStatus.Warning,
                Detail = $"端口 {Port} 已被占用",
                Hint = "端口已被占用，可能是已有一个 dsh 在运行；可先停止它或到设置页改端口",
                FixKind = "none",
            }
            : new EnvCheckItem
            {
                Name = PortItemName,
                Status = EnvCheckStatus.Ok,
                Detail = $"端口 {Port} 可用",
                Hint = "可正常启动 dsh",
                FixKind = "none",
            };
    }

    /// <summary>6) 配置文件完整性：settings.yaml 与 .credentials.yaml（后者缺失只给 Warning，不阻断使用）。</summary>
    private EnvCheckItem CheckConfigFiles()
    {
        var settingsPath = Path.Combine(_dshHome, "settings.yaml");
        var credentialsPath = Path.Combine(_dshHome, ".credentials.yaml");
        var hasSettings = SafeFileExists(settingsPath);
        var hasCredentials = SafeFileExists(credentialsPath);

        if (hasSettings && hasCredentials)
        {
            return new EnvCheckItem
            {
                Name = ConfigItemName,
                Status = EnvCheckStatus.Ok,
                Detail = "settings.yaml / .credentials.yaml 均已找到",
                Hint = "配置文件完整",
                FixKind = "none",
            };
        }

        // 缺凭据 → 可能无法调用模型；缺 settings.yaml → dsh 可能未初始化。都只提示不阻断
        var detail = (hasSettings, hasCredentials) switch
        {
            (false, false) => "未找到 settings.yaml 与 .credentials.yaml",
            (false, true) => "未找到 settings.yaml",
            _ => "未找到 .credentials.yaml",
        };
        var hint = hasSettings
            ? "未找到 API Key 凭据文件，可能无法调用模型（可在 dsh 中完成一次登录 / 配置）"
            : "未找到 settings.yaml，dsh 可能尚未初始化（至少启动过一次 dsh 会生成）";

        return new EnvCheckItem
        {
            Name = "配置文件",
            Status = EnvCheckStatus.Warning,
            Detail = detail,
            Hint = hint,
            FixKind = "none",
        };
    }

    // ==================== 「一键安装全部缺失项」编排（纯计算，便于单测） ====================

    /// <summary>该项是否存在可执行的命令型修复动作（一键安装只编排这类项）。</summary>
    private static bool IsExecutableFix(EnvCheckItem item)
        => item.FixKind == "runCommand"
           && !string.IsNullOrWhiteSpace(item.FixTarget)
           && !string.IsNullOrWhiteSpace(item.FixLabel);

    /// <summary>
    /// 是否存在"1 个以上"可执行的命令型修复项 —— 决定「一键安装全部缺失项」按钮是否可用。
    /// 只有 0 或 1 项时按钮不可用：单项有它自己的「一键安装 / 修复」按钮，批量入口没有必要。
    /// </summary>
    public static bool HasMultipleExecutableFixes(IEnumerable<EnvCheckItem> items)
        => items.Count(IsExecutableFix) > 1;

    /// <summary>
    /// 某一步失败后应跳过的后续步骤：依赖被破坏时后续步骤必然失败，跳过可省下用户等待时间。
    /// Node / npm 失败 → dsh 与依赖都装不了；dsh 本体失败 → 走 dsh 入口的依赖修复也无从谈起。
    /// </summary>
    public static IReadOnlyCollection<string> GetStagesToSkipAfterFailure(string itemName)
        => itemName switch
        {
            NodeItemName or NpmItemName => new[] { DshItemName, DshDepsItemName },
            DshItemName => new[] { DshDepsItemName },
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// 依据检测结果编排「一键安装全部缺失项」的执行计划，严格按依赖顺序：
    /// Node（含 npm/npx）→ dsh 本体 → dsh 依赖。
    ///
    /// 【为什么把 Node 与 npm/npx 合并成一步】npm/npx 的修复动作本身就是安装便携版 Node
    /// （便携版自带 npm/npx），两者分开会让同一份约 35 MB 的 zip 被下载两次。
    /// 【为什么 dsh 本体"要先装 Node"时也并入同一步】系统没有 npm 时 dsh 的修复动作同样是装便携版 Node，
    /// 并入后可复用同一份下载，不重复。
    ///
    /// 本方法只做纯计算、不执行任何命令；实际执行由界面按顺序逐项触发，且每步完成后重新检测再决定下一步。
    /// </summary>
    public static IReadOnlyList<EnvInstallStep> BuildInstallPlan(IEnumerable<EnvCheckItem> items)
    {
        var fixable = items.Where(IsExecutableFix).ToList();
        var plan = new List<EnvInstallStep>();

        // Node 与 npm/npx 合并为一步；ItemName 取"实际需要修复的那一项"，执行时按名称回查最新结果
        var nodeOrNpm = fixable.FirstOrDefault(i => i.Name is NodeItemName or NpmItemName);
        if (nodeOrNpm is not null)
            plan.Add(new EnvInstallStep(nodeOrNpm.Name, "安装 Node（便携版，含 npm/npx）", InstallPortableNodeMarker));

        var dsh = fixable.FirstOrDefault(i => i.Name == DshItemName);
        if (dsh is not null)
        {
            // 系统无 npm 时 dsh 的修复动作是"先装便携版 Node"：上面没登记 Node 步则补一步，避免重复下载
            if (dsh.FixTarget == InstallPortableNodeMarker && nodeOrNpm is null)
                plan.Add(new EnvInstallStep(NodeItemName, "安装 Node（便携版，含 npm/npx）", InstallPortableNodeMarker));
            plan.Add(new EnvInstallStep(DshItemName, "安装 dsh 本体", dsh.FixTarget));
        }

        var deps = fixable.FirstOrDefault(i => i.Name == DshDepsItemName);
        if (deps is not null)
            plan.Add(new EnvInstallStep(DshDepsItemName, "修复 dsh 依赖", deps.FixTarget));

        return plan;
    }

    // ==================== 修复命令执行 ====================

    /// <summary>
    /// 运行某个修复命令（用于 FixKind=runCommand 的项）；逐行回传输出，成功（退出码 0）返回 true。
    /// 统一经 cmd.exe 执行，因此命令里可以带 `cd /d "..." &amp;&amp; ...` 之类的目录切换；
    /// 启动失败 / 超时 / 非零退出一律返回 false，不抛异常。
    /// </summary>
    public async Task<bool> RunFixAsync(string commandLine, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        // 便携版安装标记不是 shell 命令：必须由 UI 调用 NodeRuntimeService 走安装流程，
        // 这里显式拦截，避免误把它交给 cmd 执行而报"不是内部或外部命令"。
        if (string.Equals(commandLine, InstallPortableNodeMarker, StringComparison.Ordinal))
        {
            onOutput?.Invoke("⚠️ 该修复动作需由面板界面按钮触发（便携版 Node 安装流程），不能作为命令行执行");
            return false;
        }

        if (!OperatingSystem.IsWindows()) return false;

        onOutput?.Invoke($"> {commandLine}");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // chcp 65001 让 cmd 会话使用 UTF-8 代码页，配合下面的 UTF-8 解码避免中文乱码
            Arguments = $"/c chcp 65001 >nul & {commandLine}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };

        try
        {
            if (!process.Start())
            {
                onOutput?.Invoke("❌ 无法启动 cmd.exe");
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 叠加固定超时与调用方取消信号：任一先到即强制结束进程树
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FixTimeout);

            var exited = true;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                onOutput?.Invoke(ct.IsCancellationRequested ? "⚠️ 修复命令已取消" : "⚠️ 修复命令执行超时（超过 10 分钟）");
                return false;
            }

            onOutput?.Invoke(process.ExitCode == 0 ? "✅ 命令执行成功" : $"❌ 命令执行失败（退出码 {process.ExitCode}）");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            onOutput?.Invoke($"❌ 启动修复命令失败：{ex.Message}");
            return false;
        }
    }

    // ==================== 外部链接校验 ====================

    /// <summary>
    /// 校验外部链接是否可安全交给系统浏览器打开：只接受 http/https 绝对地址。
    /// 拒绝空串、相对路径，以及 file:// / javascript: 等可能带来风险的协议。
    ///
    /// 【为何单独抽成 public static】界面点击链接按钮前要做一次校验，
    /// 抽出来既可被 UI 复用，也能直接在单测里覆盖"空串 / 非法协议被拒绝"等分支（无需真的打开浏览器）。
    /// </summary>
    public static bool IsSafeExternalUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // ==================== 内部工具 ====================

    /// <summary>单项检测的异常兜底：失败降级为 Warning 而非抛异常。</summary>
    private static EnvCheckItem SafeCheck(string name, Func<EnvCheckItem> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            return new EnvCheckItem
            {
                Name = name,
                Status = EnvCheckStatus.Warning,
                Detail = $"检测失败：{ex.Message}",
                Hint = "可点「重新检测」再试一次",
                FixKind = "none",
            };
        }
    }

    /// <summary>安全运行命令；执行器异常时返回 null。</summary>
    private string? SafeRun(string fileName, string arguments)
    {
        try { return _runCommand(fileName, arguments); }
        catch { return null; }
    }

    /// <summary>安全定位可执行文件；定位器异常时返回 null。</summary>
    private string? SafeLocate(string name)
    {
        try { return _locateExecutable(name); }
        catch { return null; }
    }

    /// <summary>安全探测端口占用；探测异常一律视为"可用"，避免误报。</summary>
    private bool SafePortProbe(int port)
    {
        try { return _portProbe(port); }
        catch { return false; }
    }

    /// <summary>安全扫描 dsh 安装状态；异常时返回 null（按未安装处理）。</summary>
    private DshInfo? SafeScan()
    {
        try { return _discovery.Scan(); }
        catch { return null; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    /// <summary>安全读取便携版 node.exe 路径；服务异常时返回 null（视为未安装）。</summary>
    private string? GetPortableNodePathSafe()
    {
        try { return _nodeRuntime.GetPortableNodePath(); }
        catch { return null; }
    }

    /// <summary>安全读取便携版 npm.cmd 路径；服务异常时返回 null（视为未安装）。</summary>
    private string? GetPortableNpmPathSafe()
    {
        try { return _nodeRuntime.GetPortableNpmPath(); }
        catch { return null; }
    }

    /// <summary>安全调用便携版路径提供者；异常时返回 null。</summary>
    private static string? SafePortablePath(Func<string?> provider)
    {
        try { return provider(); }
        catch { return null; }
    }

    /// <summary>路径含空格时加双引号，避免 cmd 把可执行文件路径截断成多个 token。</summary>
    private static string QuoteIfNeeded(string path)
        => path.Contains(' ') ? $"\"{path}\"" : path;

    private static bool SafeDirectoryExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>默认端口读取：settings.json 的 Port，非法 / 读取失败回退 3080。</summary>
    private static int ReadConfiguredPortSafe()
    {
        try
        {
            var port = AppServices.Store.Load<AppSettings>("settings.json").Port;
            return port is > 0 and <= 65535 ? port : DshProcessService.DefaultPort;
        }
        catch
        {
            return DshProcessService.DefaultPort;
        }
    }

    /// <summary>
    /// 从命令输出里提取版本号：取第一行非空内容并匹配 v24.21.0 / 10.9.0 这类形态；
    /// 匹配不到（如命令不存在输出错误文本）返回 null。
    /// </summary>
    private static string? ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var firstLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (firstLine is null) return null;
        var match = VersionRegex.Match(firstLine);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// 默认命令执行器：启动子进程、重定向输出，带超时（超时强制结束进程树）；任何失败返回 null。
    ///
    /// 【为什么经 cmd.exe 执行】Windows 上 npm / npx 实际是 .cmd 批处理脚本（并不存在 npm.exe），
    /// 直接 Process.Start(FileName="npm", UseShellExecute=false) 会抛"系统找不到指定的文件"（本机实测确认），
    /// 交给 cmd 解析才能像手敲命令行一样正确命中 .cmd/.bat（node.exe 则两种方式都能跑）。
    /// chcp 65001 配合 UTF-8 解码，保证中文输出不乱码。
    /// </summary>
    private static string? RunCommandWithTimeout(string fileName, string arguments)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : fileName,
                Arguments = isWindows ? $"/c chcp 65001 >nul & {fileName} {arguments}" : arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!process.Start()) return null;

            // 异步读走管道，避免输出量大时缓冲区满导致死锁
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                return null;
            }
            process.WaitForExit(); // 排空异步输出

            var text = $"{stdout.Result}\n{stderr.Result}".Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            // 可执行文件不存在 / 无权限等：视为检测失败
            return null;
        }
    }

    /// <summary>
    /// 默认可执行文件定位器：`where.exe &lt;name&gt;` 取第一个"看起来像路径"的行。
    /// 为什么要过滤：找不到时 where.exe 会把中文错误信息打到输出里，直接取首行会得到错误文本。
    /// </summary>
    private string? LocateExecutableDefault(string name)
    {
        var output = SafeRun("where.exe", name);
        if (string.IsNullOrWhiteSpace(output)) return null;

        return output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && (Path.IsPathRooted(l) || l.Contains('\\') || l.Contains('/')));
    }
}
