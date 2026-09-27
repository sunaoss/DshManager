using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>
/// dsh 安装位置搜索用的根目录集合。
/// 把"去哪找"与"怎么找"解耦：默认由 <see cref="DshDiscoveryService.GetDefaultSearchRoots"/> 按真实环境解析，
/// 单元测试可注入指向临时目录的实例，从而完全不触碰真实的 %USERPROFILE%\.dsh、npm 缓存与 PATH。
/// </summary>
public class DshSearchRoots
{
    /// <summary>手动指定的 dsh 位置（包目录 / 上级 node_modules 目录 / 启动器文件）；空 = 不启用。</summary>
    public string InstallPathOverride { get; set; } = string.Empty;

    /// <summary>npm 全局安装的 node_modules 目录清单（%APPDATA%\npm\node_modules 与 npm root -g 输出）。</summary>
    public IReadOnlyList<string> NpmGlobalNodeModules { get; set; } = Array.Empty<string>();

    /// <summary>dsh profiles 下的 node_modules 目录清单（~/.dsh/profiles/node_modules 及其子 profile）。</summary>
    public IReadOnlyList<string> DshProfilesNodeModules { get; set; } = Array.Empty<string>();

    /// <summary>npx 缓存根目录清单（%LOCALAPPDATA%\npm-cache\_npx），其下每个 hash 目录各含一份 node_modules。</summary>
    public IReadOnlyList<string> NpxCacheRoots { get; set; } = Array.Empty<string>();

    /// <summary>ai-manager 的 node_modules 目录清单（%USERPROFILE%\.ai-manager\npm-global\*\node_modules）。</summary>
    public IReadOnlyList<string> AiManagerNodeModules { get; set; } = Array.Empty<string>();

    /// <summary>ai-manager 的 bin 目录清单（%USERPROFILE%\.ai-manager\bin，内含 dsh* 启动器）。</summary>
    public IReadOnlyList<string> AiManagerBinDirs { get; set; } = Array.Empty<string>();

    /// <summary>PATH 目录清单（在其中查找 dsh 启动器）。</summary>
    public IReadOnlyList<string> PathDirs { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 「锁定安装位置」的解析结果：供启动流程（Scan）与设置页（重新搜索 / 搬运）复用。
/// </summary>
public sealed class DshInstallLocationResult
{
    /// <summary>最终生效的安装项（含 bin.js / 版本等）；未找到可用位置时为 null。</summary>
    public DshInstallation? EffectiveInstallation { get; init; }

    /// <summary>本次是否已经执行过搜索（为 true 时调用方不应再重复搜索）。</summary>
    public bool WasSearched { get; init; }

    /// <summary>需要写回面板配置的锁定路径；值未变化 / 无需锁定时为 null。</summary>
    public string? LockedPathToPersist { get; init; }

    /// <summary>本次生效的锁定路径（用于界面展示）。</summary>
    public string? LockedPath { get; init; }

    /// <summary>原锁定路径是否已失效并被自动替换。</summary>
    public bool WasStale { get; init; }

    /// <summary>面向日志 / 界面的中性提示文案（空字符串表示无需提示）。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// dsh 安装搬运计划：把"整搬运哪一段目录、搬到哪里"抽成纯字符串运算的结果，便于单测与事前校验。
/// </summary>
public sealed class DshMovePlan
{
    /// <summary>计划是否可用（false 时 <see cref="Reason"/> 为中文原因）。</summary>
    public bool IsValid { get; init; }

    /// <summary>不可用原因（中文）；可用时为空字符串。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>源安装根目录（直接包含 node_modules 的那一层，如 …\_npx\&lt;hash&gt;）。</summary>
    public string SourceRoot { get; init; } = string.Empty;

    /// <summary>目标安装根目录（= 用户选定的目标文件夹 + 源目录名）。</summary>
    public string DestRoot { get; init; } = string.Empty;

    /// <summary>源包目录（…\node_modules\@deepseek-ai\dsh）。</summary>
    public string SourcePackageDir { get; init; } = string.Empty;

    /// <summary>搬运后的新包目录（…\node_modules\@deepseek-ai\dsh）。</summary>
    public string DestPackageDir { get; init; } = string.Empty;
}

/// <summary>dsh 安装搬运结果。</summary>
public sealed class DshInstallMoveResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>面向界面 / 日志的中文说明（成功含新旧位置，失败含原因）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>搬运后的新包目录（失败时为 null）。</summary>
    public string? NewPackageDir { get; init; }

    /// <summary>本次修复的符号链接 / Junction 数量。</summary>
    public int RepairedLinks { get; init; }
}

/// <summary>
/// dsh 发现服务：在本地自动搜索 dsh 启动器、数据目录、工作目录，并检测版本。
/// 默认按真实环境（PATH、%USERPROFILE% 等）搜索；
/// 构造函数可注入"候选位置提供者"等依赖，便于单元测试模拟环境而不依赖真实文件系统。
/// </summary>
public class DshDiscoveryService
{
    /// <summary>版本检测命令的超时时间（毫秒）：dsh --version 5 秒未响应即视为检测失败。</summary>
    private const int VersionTimeoutMs = 5000;

    /// <summary>解析 start-dsh-web.ps1 中 $workdir 变量的正则（兼容单双引号与无引号写法）。</summary>
    private static readonly Regex WorkDirRegex =
        new(@"\$workdir\s*=\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析 start-dsh-web.ps1 中 $pkg 变量（npx 包规格，如 '@deepseek-ai/dsh@0.1.5-rc.3'）的正则。</summary>
    private static readonly Regex PackageSpecRegex =
        new(@"\$pkg\s*=\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 版本号正则：主体为 x.y.z，允许带 -rc.3 之类的预发布后缀；用于校验 --version 输出是否真的是版本号。
    /// </summary>
    private static readonly Regex VersionRegex =
        new(@"\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?", RegexOptions.Compiled);

    /// <summary>npx 回退时的默认包规格（无法从 ps1 解析 $pkg 时使用）。</summary>
    private const string DefaultPackageSpec = "@deepseek-ai/dsh";

    /// <summary>面板设置文件名（锁定安装位置与手动指定位置都存在这里）。</summary>
    private const string SettingsFileName = "settings.json";

    // ============ 各安装来源的优先级分值（数值越大越优先）============
    // 设计理由：手动指定代表用户明确意图，永远第一；其余按"可信 / 稳定"程度递减：
    // npm 全局是安装 / 更新脚本的目标位置，最可信；
    // dsh profiles 是 dsh 自身的 profile 级安装；
    // npx 缓存是临时下载物，可能残留旧版本；
    // ai-manager 是第三方面板自己维护的副本；
    // PATH 与工作目录里的启动器最不可控，放最后。
    private const int RankManual = 1000;
    private const int RankNpmGlobal = 500;
    private const int RankDshProfiles = 400;
    private const int RankNpxCache = 300;
    private const int RankAiManager = 200;
    private const int RankPathLauncher = 150;
    private const int RankWorkDir = 100;

    /// <summary>
    /// 视作"可启动器文件"的名字清单（不含 dsh.ps1：Process.Start 无法直接执行脚本，
    /// 且 PATH 启动器场景下 .ps1 需要 powershell 包装才可用）。
    /// </summary>
    private static readonly string[] LauncherFileNames = { "dsh.cmd", "dsh.exe", "dsh.bat", "dsh" };

    /// <summary>npm root -g 结果缓存：同一进程内只需询问一次 npm，避免每次扫描都拉子进程。</summary>
    private static readonly Lazy<string> NpmRootGlobalLazy = new(QueryNpmRootGlobal);

    private readonly Func<IEnumerable<string>> _candidateProvider;
    private readonly Func<string, bool> _pathExists;
    private readonly Func<string, string?> _fileContentReader;
    private readonly Func<string, string?> _versionDetector;
    private readonly Func<string?> _npxResolver;
    private readonly Func<DshSearchRoots> _searchRootProvider;
    private readonly string _dataDir;

    /// <summary>文件存在性检查（区分"是文件"而非"是目录"）：校验安装目录必须含 package.json / lib\bin.js；可注入便于单测隔离真实文件系统。</summary>
    private readonly Func<string, bool> _fileExists;

    /// <summary>
    /// 已锁定安装位置提供者；为 null 表示"本实例不启用锁定功能"（单测默认如此，
    /// 从而保持 Scan 的历史行为完全不变）。生产实例由 AppServices 注入真实的设置读取。
    /// </summary>
    private readonly Func<string?>? _lockedPathProvider;

    /// <summary>锁定安装位置写回者（持久化到 settings.json）；为 null 表示不落盘。</summary>
    private readonly Action<string>? _lockedPathSaver;

    /// <summary>
    /// 最近一次 Scan 产生的"安装位置"中性提示（如「使用已锁定的安装位置：…」「原锁定位置已失效…已自动改为…」）。
    /// 供首页把它写进日志区、设置页展示；空字符串表示无提示。
    /// </summary>
    public string LastInstallLocationMessage { get; private set; } = string.Empty;

    /// <summary>
    /// 默认实例：使用真实环境（PATH、%USERPROFILE% 等）进行扫描。
    /// 同时接线"锁定安装位置"：读取并写回 settings.json 的 <see cref="AppSettings.DshInstallPathOverride"/>。
    /// </summary>
    public DshDiscoveryService()
        : this(GetDefaultCandidatePaths, GetDefaultDshHome(), PathExists, ReadFileSafe, DetectVersionSafe, FindNpxExecutable,
               fileExists: SafeFileExists, lockedPathProvider: ReadLockedInstallPath,
               lockedPathSaver: WriteLockedInstallPathToSettings)
    {
    }

    /// <summary>
    /// 可注入构造函数：供单元测试模拟环境（不依赖真实文件系统 / PATH / npm）。
    /// </summary>
    /// <param name="candidateProvider">候选位置提供者；为 null 时使用真实搜索逻辑。</param>
    /// <param name="dataDir">dsh 数据目录（~/.dsh 或 $DSH_HOME）。</param>
    /// <param name="pathExists">路径存在性检查（兼容文件与目录），默认 File.Exists || Directory.Exists。</param>
    /// <param name="fileContentReader">读取文本文件内容（解析 start-dsh-web.ps1 用）；读取失败返回 null。</param>
    /// <param name="versionDetector">版本检测器：入参为可执行文件路径，返回 --version 的原始输出或 null（不做校验，由调用方用版本正则甄别）。</param>
    /// <param name="npxResolver">npx 可执行文件查找器（返回完整路径或 null）；为 null 时使用真实 PATH 搜索。</param>
    /// <param name="searchRootProvider">安装位置搜索根目录提供者；为 null 时按真实环境解析（见 <see cref="GetDefaultSearchRoots"/>）。</param>
    /// <param name="fileExists">文件存在性检查（默认走 File.Exists）；校验安装目录时使用，可注入以隔离真实文件系统。</param>
    /// <param name="lockedPathProvider">已锁定安装位置提供者；为 null 表示不启用锁定功能（保持 Scan 原有行为）。</param>
    /// <param name="lockedPathSaver">锁定安装位置写回者；为 null 表示不落盘。</param>
    public DshDiscoveryService(
        Func<IEnumerable<string>>? candidateProvider,
        string? dataDir,
        Func<string, bool>? pathExists = null,
        Func<string, string?>? fileContentReader = null,
        Func<string, string?>? versionDetector = null,
        Func<string?>? npxResolver = null,
        Func<DshSearchRoots>? searchRootProvider = null,
        Func<string, bool>? fileExists = null,
        Func<string?>? lockedPathProvider = null,
        Action<string>? lockedPathSaver = null)
    {
        _candidateProvider = candidateProvider ?? GetDefaultCandidatePaths;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? GetDefaultDshHome() : dataDir;
        _pathExists = pathExists ?? PathExists;
        _fileContentReader = fileContentReader ?? ReadFileSafe;
        _versionDetector = versionDetector ?? DetectVersionSafe;
        _npxResolver = npxResolver ?? FindNpxExecutable;
        _searchRootProvider = searchRootProvider ?? GetDefaultSearchRoots;
        _fileExists = fileExists ?? SafeFileExists;
        _lockedPathProvider = lockedPathProvider;
        _lockedPathSaver = lockedPathSaver;
    }

    /// <summary>
    /// 获取 dsh 数据目录：优先取 $DSH_HOME 环境变量，否则 %USERPROFILE%\.dsh。
    /// </summary>
    public static string GetDefaultDshHome()
    {
        var env = Environment.GetEnvironmentVariable("DSH_HOME");
        return string.IsNullOrWhiteSpace(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : env;
    }

    /// <summary>
    /// 按发现优先级返回默认候选位置清单（真实环境搜索）。
    /// 顺序：PATH 中的 dsh → %USERPROFILE%\.ai-manager\bin → ~/.dsh → npm 全局包 → 脚本记录的工作目录。
    /// </summary>
    public static IEnumerable<string> GetDefaultCandidatePaths()
    {
        var paths = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dshHome = GetDefaultDshHome();

        // 1) PATH 环境变量中的 dsh 启动器（dsh.exe / dsh.cmd / dsh 等）
        paths.AddRange(FindDshInPath());

        // 2) %USERPROFILE%\.ai-manager\bin 下的 dsh 启动器文件
        var aiManagerBin = Path.Combine(userProfile, ".ai-manager", "bin");
        paths.AddRange(
            new[] { "dsh", "dsh.exe", "dsh.cmd", "dsh.bat", "dsh.ps1" }
                .Select(name => Path.Combine(aiManagerBin, name)));

        // 3) 数据目录 ~/.dsh
        paths.Add(dshHome);

        // 4) npm 全局目录下的 @deepseek-ai/dsh 包
        paths.Add(FindNpmGlobalDshPackage());

        // 5) start-dsh-web.ps1 中记录的 $workdir 工作目录
        paths.Add(ResolveWorkDirFromScript(dshHome));

        // 去空去重，保持搜索优先级顺序
        return paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
    }

    /// <summary>
    /// 扫描本地环境，返回 dsh 安装状态信息。
    /// 任何一步（文件不存在、命令不可用、版本检测失败）都不会抛异常，保证 UI 层安全调用。
    /// </summary>
    public DshInfo Scan()
    {
        var info = new DshInfo { DataDir = _dataDir };

        // 1. 收集候选位置，过滤出真实存在的路径记入 SourcesFound（供"文件查看"页复用）
        var candidates = SafeGetCandidates().ToList();
        foreach (var path in candidates)
        {
            if (_pathExists(path))
            {
                info.SourcesFound.Add(path);
            }
        }

        // 2. 锁定安装位置解析（核心新增）：
        //    - 已锁定且校验通过 → 直接采用，**完全不触发搜索**（省时且结果稳定）；
        //    - 已锁定但失效 → 自动重新搜索并更新锁定值（中性提示，不抛异常、不弹错误框）；
        //    - 未锁定 → 自动搜索取第一个可用结果并自动锁定它。
        //    未启用锁定（提供者为 null，单测场景）时结果为空，Scan 行为与历史版本完全一致。
        var location = SafeResolveLockedLocation();
        LastInstallLocationMessage = location.Message;

        // 锁定的新值需要落盘（首次自动锁定 / 失效后改指新位置）
        if (location.LockedPathToPersist is not null)
            SafePersistLockedPath(location.LockedPathToPersist);

        if (location.EffectiveInstallation is not null)
        {
            // 锁定命中：只放这一项，不再做多来源探测
            info.Installations = new[] { location.EffectiveInstallation };
            info.RecommendedInstallation = location.EffectiveInstallation;
        }
        else if (location.WasSearched)
        {
            // 已经搜过但确实没有可用位置：不要重复搜索
            info.Installations = Array.Empty<DshInstallation>();
            info.RecommendedInstallation = null;
        }
        else
        {
            // 未启用锁定：沿用原有的"多来源探测 + 选最佳"逻辑
            info.Installations = SafeDetectInstallations();
            info.RecommendedInstallation = SelectBest(info.Installations);
        }

        // 3. 判定安装状态：找到候选位置或任一安装位置即视为已安装
        //    （即便 PATH / 数据目录都没有，只要 npx 缓存等位置存在包，也算装过）
        info.IsInstalled = info.SourcesFound.Count > 0 || info.Installations.Count > 0;

        // 4. 解析启动器路径：按搜索优先级取第一个"存在的 dsh 启动器文件"（仅供界面展示位置）
        info.LauncherPath = ResolveLauncherPath(candidates);

        // 5. 一次性读取启动脚本内容：用于解析工作目录与 npx 包规格（避免重复 IO）
        var scriptContent = ReadStartScript();

        // 6. 解析工作目录：start-dsh-web.ps1 中的 $workdir 变量
        info.WorkDir = ParseWorkDir(scriptContent);

        // 7. 优先使用"推荐安装位置"：可用时用更可靠的启动方式（node <bin.js>）并直接取其版本
        var recommended = info.RecommendedInstallation;
        if (recommended is not null && recommended.IsUsable)
        {
            info.LaunchCommand = BuildLaunchCommand(recommended) ?? string.Empty;

            if (IsKnownVersion(recommended.Version))
            {
                info.Version = recommended.Version;
                return info;
            }

            // 推荐位置可用但版本未知（如仅有 shim）：启动方式仍用推荐位置，版本再走原有校验逻辑补齐
            var (_, fallbackVersion) = ResolveUsableLauncher(candidates);
            info.Version = fallbackVersion;
            return info;
        }

        // 8. 回退：本地启动器可用性校验（逐个实际执行 --version），取第一个可用的
        var (launcher, version) = ResolveUsableLauncher(candidates);
        if (launcher.Length > 0)
        {
            info.LaunchCommand = launcher;
            info.Version = version;
            return info;
        }

        // 9. 所有本地启动器都不可用 → npx 回退（本机真实可用的启动方式）
        ApplyNpxFallback(info, scriptContent);
        return info;
    }

    /// <summary>
    /// 由推荐/可用安装位置构造启动命令（保证可被 <see cref="DshProcessService.SplitCommand"/> 正确拆分）。
    /// 优先用 node 直接执行包的 bin.js：不依赖 npm 生成的 shim 是否指向正确路径
    /// （本机实测就存在指向不存在路径的坏 shim）；没有 bin.js 时才退回启动器文件。
    /// </summary>
    private static string? BuildLaunchCommand(DshInstallation installation)
    {
        if (!string.IsNullOrWhiteSpace(installation.BinJsPath))
            return $"node {QuoteIfNeeded(installation.BinJsPath)}";
        if (!string.IsNullOrWhiteSpace(installation.LauncherPath))
            return QuoteIfNeeded(installation.LauncherPath);
        return null;
    }

    /// <summary>版本号是否已成功识别（"unknown" / 空 视为未识别）。</summary>
    private static bool IsKnownVersion(string? version)
        => !string.IsNullOrWhiteSpace(version) && version != "unknown";

    // ==================== 多来源安装位置探测 ====================

    /// <summary>
    /// 按来源优先级探测本机所有 dsh 安装位置（纯只读，任何一步失败都不抛异常）。
    /// 来源与优先级：手动指定(1000) → npm 全局(500) → dsh profiles(400) → npx 缓存(300)
    /// → ai-manager(200) → PATH 启动器(150) → 脚本工作目录(100)。
    /// 返回结果已去重并按排序规则（版本高者优先，同版本按来源优先级）排列。
    /// </summary>
    public IReadOnlyList<DshInstallation> DetectInstallations()
    {
        var roots = SafeGetSearchRoots();
        var found = new List<DshInstallation>();

        // 1) 手动指定：用户明确指定的位置，优先级最高
        AddManualInstallation(found, roots.InstallPathOverride);

        // 2) npm 全局：%APPDATA%\npm\node_modules 与 npm root -g 输出
        foreach (var nodeModules in roots.NpmGlobalNodeModules)
            AddPackageInstallation(found, nodeModules, DshInstallSource.NpmGlobal);

        // 3) dsh profiles：~/.dsh/profiles/node_modules 及 ~/.dsh/profiles/*/node_modules
        foreach (var nodeModules in roots.DshProfilesNodeModules)
            AddPackageInstallation(found, nodeModules, DshInstallSource.DshProfiles);

        // 4) npx 缓存：每个 hash 子目录只做"一层枚举 + 固定相对路径存在性判断"，绝不递归全盘
        foreach (var cacheRoot in roots.NpxCacheRoots)
            foreach (var hashDir in SafeGetDirectories(cacheRoot))
                AddPackageInstallation(found, Path.Combine(hashDir, "node_modules"), DshInstallSource.NpxCache);

        // 5) ai-manager：npm-global 下的包目录 + bin 下的 dsh* 启动器
        foreach (var nodeModules in roots.AiManagerNodeModules)
            AddPackageInstallation(found, nodeModules, DshInstallSource.AiManager);
        foreach (var binDir in roots.AiManagerBinDirs)
            AddLauncherInstallations(found, binDir, DshInstallSource.AiManager);

        // 6) PATH 启动器
        foreach (var dir in roots.PathDirs)
            AddLauncherInstallations(found, dir, DshInstallSource.PathLauncher);

        // 7) 脚本解析：$workdir 指向的工作目录下的 node_modules；$pkg 提供版本兜底
        AddWorkDirInstallation(found);

        return SortAndDeduplicate(found);
    }

    /// <summary>
    /// 从候选安装中选出最佳项：先按排序规则排列，再返回第一个"可用"的；没有可用项时返回 null。
    /// </summary>
    public DshInstallation? SelectBest(IEnumerable<DshInstallation>? installations)
    {
        if (installations is null) return null;
        return installations
            .Where(i => i.IsUsable)
            .OrderBy(i => i, Comparer<DshInstallation>.Create(CompareInstallations))
            .FirstOrDefault();
    }

    /// <summary>
    /// 安装位置优先级比较（负数表示 a 更靠前，符合 List.Sort / OrderBy 语义）。
    /// 规则（参考上一版 CompareScore 的用意）：
    /// 1) 手动指定永远第一（用户明确指定就不参与版本比较）；
    /// 2) 先比主次版本，版本更高者优先 —— 这样"更新过的 npm 全局"会压过旧缓存，
    ///    而"全局还停在旧版本"时会让位给更新的缓存，避免出现"更新了却还在跑旧版本"；
    /// 3) 主次版本相同再比来源优先级 Rank（npm 全局 &gt; dsh profiles &gt; npx 缓存 &gt; ai-manager &gt; PATH &gt; 工作目录）；
    /// 4) 仍相同则比完整版本，最后按包目录路径做确定性排序。
    /// </summary>
    public static int CompareInstallations(DshInstallation? a, DshInstallation? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        // 规则 1：手动指定永远第一
        if (a.Rank >= RankManual || b.Rank >= RankManual)
            return b.Rank.CompareTo(a.Rank);

        // 规则 2：主次版本更高者优先（无法解析的版本视为 0.0，排最后）
        var (majorA, minorA) = ParseMajorMinor(a.Version);
        var (majorB, minorB) = ParseMajorMinor(b.Version);
        if (majorA != majorB) return majorB.CompareTo(majorA);
        if (minorA != minorB) return minorB.CompareTo(minorA);

        // 规则 3：同主次版本比来源优先级
        if (a.Rank != b.Rank) return b.Rank.CompareTo(a.Rank);

        // 规则 4：完整版本 → 路径，保证排序结果稳定
        var versionCompare = CompareCoreVersions(b.Version, a.Version);
        return versionCompare != 0
            ? versionCompare
            : string.Compare(a.PackageDir, b.PackageDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>去重（同路径只保留先加入者，即更高优先来源）并按比较器排序。</summary>
    private static IReadOnlyList<DshInstallation> SortAndDeduplicate(List<DshInstallation> found)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<DshInstallation>();
        foreach (var item in found)
        {
            var key = string.IsNullOrWhiteSpace(item.PackageDir) ? item.LauncherPath ?? string.Empty : item.PackageDir;
            if (key.Length == 0 || !seen.Add(key)) continue;
            unique.Add(item);
        }
        unique.Sort(CompareInstallations);
        return unique;
    }

    // ==================== 锁定安装位置（记住 + 失效自动重搜） ====================

    /// <summary>
    /// 校验某目录是否为**可用的 dsh 安装目录**：必须同时存在 package.json 与 lib\bin.js。
    /// 抽成 static 纯函数（可注入 fileExists）便于单测与设置页预校验；任何异常都返回 false。
    /// </summary>
    public static bool ValidateInstallDir(string? dir, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var probe = fileExists ?? SafeFileExists;
        try
        {
            var full = Path.GetFullPath(dir);
            return probe(Path.Combine(full, "package.json"))
                && probe(Path.Combine(full, "lib", "bin.js"));
        }
        catch { return false; }
    }

    /// <summary>实例版校验：尊重注入的文件存在性检查，便于单测隔离真实文件系统。</summary>
    public bool IsInstallDir(string? dir) => ValidateInstallDir(dir, _fileExists);

    /// <summary>
    /// 把候选包目录解析为**物理路径**：若该目录是符号链接 / Junction，则返回它最终指向的真实目录，否则原样返回。
    ///
    /// 【为什么锁定物理路径更稳】profiles 下的 <c>@deepseek-ai\dsh</c> 通常是指向 npm npx 缓存的 Junction，
    /// 一旦 npx 缓存被清理或换 hash，这个链接就会**悬空**，届时任何"用链接路径启动"的命令都会立刻失败；
    /// 物理路径指向真实包目录，只要包还在就一定能启动（用户"把 dsh 挪到别处"也不会因链接失效而报错）。
    ///
    /// 【为什么解析失败要静默回退】解析会触碰真实文件系统，可能因权限不足、目标缺失、重解析点损坏等任意原因失败；
    /// 这类失败只是"拿不到更优路径"，不应阻断启动，故一律回退入参原路径，绝不抛异常。
    ///
    /// 【为什么还要复核 bin.js】若物理路径下缺 <c>lib\bin.js</c>（链接指向了残缺目录），换过去反而会把
    /// "可用"变成"不可用"，因此只在物理路径确有 package.json + lib\bin.js 时才替换。
    /// </summary>
    /// <param name="packageDir">候选包目录（可能本身就是符号链接 / Junction）。</param>
    /// <param name="fileExistsProbe">文件存在性检查（默认真实文件系统；异常安全）。</param>
    public static string ResolvePhysicalPackageDir(string? packageDir, Func<string, bool>? fileExistsProbe = null)
    {
        // 空 / 纯空白输入没有"候选包目录"可言，归一为空字符串返回（调用方据此判空）
        if (string.IsNullOrWhiteSpace(packageDir)) return string.Empty;

        try
        {
            var full = Path.GetFullPath(packageDir);
            var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            var physical = target?.FullName;

            // 不是链接（或解析不出目标）→ 原样返回
            if (string.IsNullOrWhiteSpace(physical)) return packageDir;
            if (string.Equals(physical, full, StringComparison.OrdinalIgnoreCase)) return packageDir;

            // 物理路径必须是完整安装（package.json + lib\bin.js 都在）才值得替换
            var probe = fileExistsProbe ?? SafeFileExists;
            var isComplete = probe(Path.Combine(physical, "package.json"))
                && probe(Path.Combine(physical, "lib", "bin.js"));
            return isComplete ? physical : packageDir;
        }
        catch
        {
            // 权限 / 目标不存在 / 重解析点损坏等一律视为"解析失败"：静默回退原路径
            return packageDir;
        }
    }

    /// <summary>
    /// 按"锁定优先"语义解析应使用的 dsh 安装位置（启动流程与设置页「重新搜索」共用）：
    /// ① 锁定路径有效 → 直接采用，**不执行任何搜索**；
    /// ② 锁定路径失效 → 自动重新搜索并给出「原锁定位置已失效：…；已自动改为：…」的中性提示；
    /// ③ 未锁定 → 自动搜索并采用第一个可用结果（沿用现有优先级），并自动锁定它。
    /// 【为什么不抛异常 / 不弹错误框】安装位置失效属于"环境自己变了"的常见情况，
    /// 用户要的是"面板自己修好继续用"，而不是一个阻断性的错误弹窗；因此失败只落在一句提示文本里。
    /// </summary>
    public DshInstallLocationResult ResolveInstallLocation(string? lockedPath)
    {
        try
        {
            // ① 锁定有效：直接采用（关键：不触碰 DetectInstallations，避免无谓搜索）
            if (!string.IsNullOrWhiteSpace(lockedPath) && IsInstallDir(lockedPath))
            {
                // 归一为绝对路径（启动命令里的 bin.js 必须是绝对路径，避免依赖 cmd 的当前目录），
                // 并把链接（profiles 下的 Junction）解析为**物理路径**：锁定值与启动命令都用物理路径，
                // 这样即便 npx 缓存被清理导致链接悬空，面板记下的真实安装位置依然可用。
                var normalized = TryGetFullPath(lockedPath) ?? lockedPath;
                var resolved = ResolvePhysicalPackageDir(normalized);
                var locked = CreatePackageInstallation(resolved, DshInstallSource.Manual)
                    ?? CreatePackageInstallation(normalized, DshInstallSource.Manual);
                if (locked is not null)
                {
                    return new DshInstallLocationResult
                    {
                        EffectiveInstallation = locked,
                        LockedPath = locked.PackageDir,
                        // 锁定值由链接改写成物理路径时必须回写配置，否则下次仍带着易悬空的链接
                        LockedPathToPersist = string.Equals(locked.PackageDir, normalized, StringComparison.OrdinalIgnoreCase)
                            ? null
                            : locked.PackageDir,
                        Message = $"使用已锁定的安装位置：{locked.PackageDir}",
                    };
                }
            }

            // ②/③ 需要搜索：锁定失效时先记住旧值，用于生成"已自动改为"的提示
            var wasStale = !string.IsNullOrWhiteSpace(lockedPath);
            var best = SelectBest(DetectInstallations());

            // 只有"真正含 package.json + lib\bin.js 的包目录"才值得锁定；
            // 否则锁定值下次必被判失效，形成"每次启动都重搜"的循环，因此宁可不锁。
            if (best is null || !IsInstallDir(best.PackageDir))
            {
                return new DshInstallLocationResult
                {
                    WasStale = wasStale,
                    WasSearched = true,
                    Message = wasStale
                        ? $"原锁定位置已失效：{lockedPath}；自动搜索未找到可用的 dsh 安装位置，将回退到其它启动方式"
                        : "未找到可用的 dsh 安装位置",
                };
            }

            // 搜索到的位置同样可能是链接（dsh profiles 的来源优先级高于 npx 缓存），
            // 这里统一换成物理路径再锁定：既避免把易悬空的链接写进配置，也让启动命令直接用物理 bin.js。
            var effective = ResolveToPhysical(best);
            return new DshInstallLocationResult
            {
                EffectiveInstallation = effective,
                WasStale = wasStale,
                WasSearched = true,
                LockedPath = effective.PackageDir,
                LockedPathToPersist = effective.PackageDir,
                Message = wasStale
                    ? $"原锁定位置已失效：{lockedPath}；已自动改为：{effective.PackageDir}"
                    : $"已自动锁定安装位置：{effective.PackageDir}",
            };
        }
        catch
        {
            // 任何异常都等价于"没有结论"：交给调用方走原有回退逻辑
            return new DshInstallLocationResult();
        }
    }

    /// <summary>
    /// 把搜索得到的安装项换成"物理路径版本"：包目录解析为真实路径后重新构造安装项
    /// （版本 / bin.js / 来源保持不变）；解析没变化或重构失败时原样返回，保证不会因这一步把可用项变不可用。
    /// </summary>
    private DshInstallation ResolveToPhysical(DshInstallation installation)
    {
        var physicalDir = ResolvePhysicalPackageDir(installation.PackageDir);
        if (string.Equals(physicalDir, installation.PackageDir, StringComparison.OrdinalIgnoreCase))
            return installation;

        return CreatePackageInstallation(physicalDir, installation.Source) ?? installation;
    }

    /// <summary>把锁定位置写回面板配置（设置页「重新搜索」/「清除锁定」/ 搬运完成后使用）；传空字符串表示清除锁定。</summary>
    public void SaveLockedInstallPath(string? path) => SafePersistLockedPath(path ?? string.Empty);

    /// <summary>读取已锁定位置并解析（提供者为 null 表示未启用锁定 → 返回空结果，走原有扫描逻辑）。</summary>
    private DshInstallLocationResult SafeResolveLockedLocation()
    {
        var provider = _lockedPathProvider;
        if (provider is null) return new DshInstallLocationResult();

        string? lockedPath;
        try { lockedPath = provider(); }
        catch { lockedPath = null; }

        return ResolveInstallLocation(lockedPath);
    }

    /// <summary>安全落盘：写盘失败不影响本次使用（下次启动会重新搜索并再写一次）。</summary>
    private void SafePersistLockedPath(string path)
    {
        try { _lockedPathSaver?.Invoke(path); }
        catch { /* 忽略：锁定只是优化项，不是启动的必要条件 */ }
    }

    /// <summary>读取 settings.json 里的锁定安装位置；读取失败按"未锁定"处理。</summary>
    private static string ReadLockedInstallPath()
    {
        try { return AppServices.Store.Load<AppSettings>(SettingsFileName).DshInstallPathOverride ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>把锁定安装位置写回 settings.json；值未变化时不写（避免无谓 IO）。</summary>
    private static void WriteLockedInstallPathToSettings(string path)
    {
        try
        {
            var store = AppServices.Store;

            // 用 Update 在写锁内完成"读-改-写"：写成 Load + Save 两步的话，
            // 中间可能被设置页/记忆页插入一次保存，对方刚写的字段会被这里整段覆盖。
            store.Update<AppSettings>(SettingsFileName, settings =>
            {
                if (string.Equals(settings.DshInstallPathOverride, path, StringComparison.OrdinalIgnoreCase))
                    return;

                settings.DshInstallPathOverride = path;
            });
        }
        catch { /* 写盘失败忽略 */ }
    }

    // ==================== 搬运 dsh 安装位置 ====================

    /// <summary>
    /// 生成搬运计划（纯字符串运算，无任何文件系统副作用，便于单测边界）。
    /// 约定：安装位置形如 <c>…\node_modules\@deepseek-ai\dsh</c>，
    /// 搬运单位是"直接包含 node_modules 的那一层目录"（npx 场景即 <c>_npx\&lt;hash&gt;</c>），
    /// 因此目标位置 = 用户选定的目标文件夹 + 源目录名。
    /// </summary>
    public static DshMovePlan BuildMovePlan(string? packageDir, string? targetParentDir)
    {
        if (string.IsNullOrWhiteSpace(packageDir))
            return InvalidMovePlan("未指定 dsh 安装位置（源目录）");
        if (string.IsNullOrWhiteSpace(targetParentDir))
            return InvalidMovePlan("未选择目标文件夹");

        string sourcePackageDir, sourceRoot, destRoot;
        try
        {
            sourcePackageDir = Path.GetFullPath(packageDir);

            // 由 …\node_modules\@deepseek-ai\dsh 上退两级得到 node_modules 与其父目录（安装根）
            var scopeDir = Path.GetDirectoryName(TrimTrailingSeparators(sourcePackageDir));
            var nodeModules = string.IsNullOrWhiteSpace(scopeDir) ? null : Path.GetDirectoryName(scopeDir);
            if (string.IsNullOrWhiteSpace(nodeModules)
                || !string.Equals(Path.GetFileName(nodeModules), "node_modules", StringComparison.OrdinalIgnoreCase))
            {
                return InvalidMovePlan(@"安装位置结构不符合预期：应形如 …\node_modules\@deepseek-ai\dsh");
            }

            sourceRoot = Path.GetDirectoryName(nodeModules) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(Path.GetFileName(sourceRoot)))
                return InvalidMovePlan("无法推导安装根目录，请改用「浏览…」指定完整的安装位置");

            destRoot = Path.Combine(Path.GetFullPath(targetParentDir), Path.GetFileName(sourceRoot));
        }
        catch (Exception ex)
        {
            return InvalidMovePlan($"路径解析失败：{ex.Message}");
        }

        if (string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase))
            return InvalidMovePlan("目标位置与源位置相同，无需搬运");
        if (IsUnder(destRoot, sourceRoot))
            return InvalidMovePlan("目标位置不能位于源安装目录内部");
        if (IsUnder(sourceRoot, destRoot))
            return InvalidMovePlan("源安装目录不能位于目标位置内部");

        return new DshMovePlan
        {
            IsValid = true,
            SourceRoot = sourceRoot,
            DestRoot = destRoot,
            SourcePackageDir = sourcePackageDir,
            DestPackageDir = Path.Combine(destRoot, "node_modules", "@deepseek-ai", "dsh"),
        };
    }

    private static DshMovePlan InvalidMovePlan(string reason) => new() { IsValid = false, Reason = reason };

    /// <summary>尽力把路径归一为绝对路径；失败返回 null（调用方回退原值，绝不抛异常）。</summary>
    private static string? TryGetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>child 是否位于 parent 之下（不区分大小写；相等不算"之下"）。纯字符串判断，便于单测。</summary>
    public static bool IsUnder(string? child, string? parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return false;

        var c = TrimTrailingSeparators(child);
        var p = TrimTrailingSeparators(parent);
        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return false;

        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把位于 oldRoot 之下的路径重映射到 newRoot 之下（用于更新面板自身记录的旧路径引用）；
    /// 不在 oldRoot 之下时原样返回。纯字符串运算，便于单测。
    /// </summary>
    public static string RemapPathUnder(string? path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot))
            return path ?? string.Empty;
        if (!IsUnder(path, oldRoot)) return path;

        try { return Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path)); }
        catch { return path; }
    }

    /// <summary>递归统计目录体积（搬运确认框展示"约 N"用）；失败返回 0，绝不抛异常。</summary>
    /// <remarks>
    /// 【为什么跳过 reparse point】dsh 安装树里布满 Junction / 符号链接（profiles 下数百个插件链接）。
    /// 若跟随链接统计，同一个文件会被重复计入，且目录链接成环时枚举不收敛 —— 体积虚高好几倍，
    /// 用户在搬运确认框看到的"约 N MB"就没了参考价值。
    /// </remarks>
    public static long GetDirectorySizeSafe(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                try { total += new FileInfo(file).Length; } catch { /* 单文件读不到时跳过 */ }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 把 dsh 安装根目录整体搬运到目标文件夹，并修复受影响的符号链接。
    ///
    /// 【为什么是"复制 → 校验 → 删源"而不是 Directory.Move】
    /// 搬运 200+ MB、上千文件的过程中，任何一步失败（占用 / 权限 / 断电）都会让"移动"两边都不完整；
    /// 先完整复制、校验新位置的 lib\bin.js 存在，再删除源目录，可以保证**任何失败都不会丢数据**。
    ///
    /// 【失败保证不留半成品】复制阶段失败 / 被取消 / 校验不通过，都会删除本次新建的目标目录，源目录保持不动。
    /// 【进度与取消】progress 按"已复制文件数 / 总文件数"上报 0~1；ct 可在复制过程中取消。
    /// </summary>
    /// <param name="sourcePackageDir">源包目录（锁定位置）。</param>
    /// <param name="targetParentDir">用户选定的目标文件夹。</param>
    /// <param name="progress">进度回调（0~1）。</param>
    /// <param name="log">过程日志回调（逐条中文说明）。</param>
    /// <param name="ct">取消令牌。</param>
    public DshInstallMoveResult MoveInstall(
        string sourcePackageDir,
        string targetParentDir,
        IProgress<double>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var plan = BuildMovePlan(sourcePackageDir, targetParentDir);
        if (!plan.IsValid) return MoveFailed(plan.Reason);

        // 源必须完整有效：缺 bin.js 一律拒绝，避免把不完整目录搬走
        if (!ValidateInstallDir(plan.SourcePackageDir, _fileExists))
            return MoveFailed($@"源安装位置无效或缺少 lib\bin.js：{plan.SourcePackageDir}");
        if (!Directory.Exists(plan.SourceRoot))
            return MoveFailed($"源安装根目录不存在：{plan.SourceRoot}");
        if (IsReparsePoint(plan.SourceRoot))
            return MoveFailed("源安装根目录是一个链接（快捷方式），请先锁定它的真实位置再搬运");
        if (Directory.Exists(plan.DestRoot))
            return MoveFailed($"目标位置已存在同名目录：{plan.DestRoot}（请先改名或删除后再试）");

        var destCreated = false;
        try
        {
            log?.Invoke($"正在统计待搬运文件：{plan.SourceRoot}");

            // 【为什么必须跳过 reparse point】dsh 的安装树里本来就有大量 Junction / 符号链接
            // （profiles 下的插件链接就有几百个，node_modules 里也常见）。
            // 默认的 SearchOption.AllDirectories 会把链接当普通目录追进去，后果有三个：
            // ① 同一个文件被复制两次（体积虚高、复制时间翻倍）；
            // ② 链接的"链接语义"丢失 —— 复制过来的是一份实体副本，原本的指向关系没了；
            // ③ 目录链接成环时不收敛，枚举直接卡死。
            // 这里跳过 reparse point：链接本身在后面单独重建（见 RecreateDirectoryLink）。
            var enumerateOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            var dirs = Directory.EnumerateDirectories(plan.SourceRoot, "*", enumerateOptions).ToList();
            var files = Directory.EnumerateFiles(plan.SourceRoot, "*", enumerateOptions).ToList();
            log?.Invoke($"共 {files.Count} 个文件，开始复制到 {plan.DestRoot}");

            Directory.CreateDirectory(plan.DestRoot);
            destCreated = true;

            // 先建目录树再复制文件，避免深层路径因父目录不存在而失败
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(plan.DestRoot, Path.GetRelativePath(plan.SourceRoot, dir)));
            }

            var copied = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(plan.DestRoot, Path.GetRelativePath(plan.SourceRoot, file));
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                File.Copy(file, target, overwrite: true);
                copied++;
                if (files.Count > 0) progress?.Report((double)copied / files.Count);
            }
        }
        catch (OperationCanceledException)
        {
            CleanupDestDir(plan.DestRoot, destCreated, log);
            return MoveFailed("搬运已取消：目标目录已清理，源目录未改动");
        }
        catch (Exception ex)
        {
            CleanupDestDir(plan.DestRoot, destCreated, log);
            return MoveFailed($"复制失败：{ex.Message}（目标目录已清理，源目录未改动）");
        }

        // 复制完成后必须校验新位置的 lib\bin.js 确实存在，否则视为失败并清理目标
        if (!ValidateInstallDir(plan.DestPackageDir, _fileExists))
        {
            CleanupDestDir(plan.DestRoot, destCreated, log);
            return MoveFailed(@"搬运结果校验失败：目标目录缺少 lib\bin.js，已清理目标目录，源目录未改动");
        }

        progress?.Report(1.0);

        // 【顺序很关键】先修复指向旧位置的链接（此时旧目标还在，解析一定成功），再删除源目录。
        // 这样即使"删源"失败，也只是留下一个可清理的旧目录，链接与锁定位置都已是新位置，状态自洽。
        var repaired = RepairLinks(plan.SourceRoot, plan.DestRoot, log);

        // 校验通过后才删源；删源失败不算搬运失败（目标已完整），但要在提示里如实说明
        var sourceDeleted = TryDeleteDirectory(plan.SourceRoot, log);

        var message = $"已从 {plan.SourceRoot} 搬运到 {plan.DestRoot}；锁定位置已更新为 {plan.DestPackageDir}"
            + (sourceDeleted ? string.Empty : "；注意：源目录删除失败（可能被占用），请手动清理")
            + (repaired > 0 ? $"；已修复 {repaired} 个指向旧位置的链接" : string.Empty);

        return new DshInstallMoveResult
        {
            Success = true,
            Message = message,
            NewPackageDir = plan.DestPackageDir,
            RepairedLinks = repaired,
        };
    }

    /// <summary>搬运失败的统一结果（含中文原因），保证调用方总能拿到可展示的反馈。</summary>
    private static DshInstallMoveResult MoveFailed(string reason)
        => new() { Success = false, Message = $"❌ {reason}" };

    /// <summary>清理本次新建的目标目录（复制失败 / 取消时用）；只删自己创建的，绝不触碰用户已有目录。</summary>
    private static void CleanupDestDir(string destRoot, bool created, Action<string>? log)
    {
        if (!created) return;
        try { if (Directory.Exists(destRoot)) Directory.Delete(destRoot, recursive: true); }
        catch (Exception ex) { log?.Invoke($"清理目标目录失败，请手动删除：{destRoot}（{ex.Message}）"); }
    }

    /// <summary>尽力删除源目录；返回是否删除成功（失败只记日志，不影响"搬运成功"的结论）。</summary>
    private static bool TryDeleteDirectory(string dir, Action<string>? log)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"删除源目录失败：{dir}（{ex.Message}）");
            return false;
        }
    }

    /// <summary>路径是否为 Windows 重解析点（Junction / 符号链接）；异常按"否"处理。</summary>
    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// 修复所有"指向旧安装根目录"的目录链接（Junction / 符号链接），使其重新指向新位置。
    /// 扫描范围限定在 <c>&lt;DSH_HOME&gt;\profiles\node_modules\@deepseek-ai\*</c>
    /// 与 <c>&lt;DSH_HOME&gt;\profiles\&lt;profile&gt;\node_modules\@deepseek-ai\*</c>（真实故障点即此处）。
    /// 单个链接失败只记日志并继续（不因个别链接让整个搬运失败）；返回成功修复的数量。
    /// </summary>
    public int RepairLinks(string oldRoot, string newRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot)) return 0;

        var repaired = 0;
        foreach (var linkPath in EnumerateScopedLinks())
        {
            try
            {
                var info = new DirectoryInfo(linkPath);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0) continue;

                var target = info.LinkTarget;
                if (string.IsNullOrWhiteSpace(target) || !IsUnder(target, oldRoot)) continue;

                var newTarget = Path.Combine(newRoot, Path.GetRelativePath(oldRoot, target));
                // .NET 的 FileSystemInfo 没有公开的 LinkType（那是 PowerShell 的属性）。
                // 本方法处理的都是 dsh 用 mklink /J 建的 Windows 目录 Junction，故按 Junction 重建。
                const string linkType = "Junction";

                // recursive:false —— 只删链接本身，绝不会删除链接指向的目标内容
                Directory.Delete(linkPath, recursive: false);

                if (RecreateDirectoryLink(linkPath, newTarget, linkType, out var error))
                {
                    repaired++;
                    log?.Invoke($"已修复链接：{linkPath} → {newTarget}");
                }
                else
                {
                    log?.Invoke($"修复链接失败：{linkPath}（{error}）");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"修复链接异常：{linkPath}（{ex.Message}）");
            }
        }
        return repaired;
    }

    /// <summary>枚举需要检查的 @deepseek-ai 包链接位置（profiles 及其一级子 profile），不深度递归。</summary>
    private IEnumerable<string> EnumerateScopedLinks()
    {
        var profilesDir = Path.Combine(_dataDir, "profiles");
        var scopes = new List<string> { profilesDir };
        scopes.AddRange(SafeGetDirectories(profilesDir));

        foreach (var scope in scopes)
        {
            var aiDir = Path.Combine(scope, "node_modules", "@deepseek-ai");
            foreach (var entry in SafeGetDirectories(aiDir))
                yield return entry;
        }
    }

    /// <summary>
    /// 按原链接类型重建目录链接：Junction 用 <c>mklink /J</c>（无需管理员 / 开发者模式，与 dsh 自身创建的链接一致），
    /// 符号链接走 .NET 原生 API。失败通过 error 返回原因，不抛异常。
    /// </summary>
    private static bool RecreateDirectoryLink(string linkPath, string target, string? linkType, out string error)
    {
        error = string.Empty;

        // 【为什么要先校验入参】这两个路径最终会交给 cmd.exe 解析（旧实现是拼 mklink 命令行），
        // 而它们来自磁盘上的安装目录扫描结果。路径里只要出现引号或 & | ^ 这类 cmd 元字符，
        // 就能把一条 mklink 命令截断成两条，等于给出一个"任意命令执行"入口。
        // 合法路径不可能含引号或控制字符，直接拒绝比事后转义更可靠。
        if (!IsSafeLinkPath(linkPath, out var linkReason))
        {
            error = $"链接路径非法：{linkReason}";
            return false;
        }
        if (!IsSafeLinkPath(target, out var targetReason))
        {
            error = $"链接目标非法：{targetReason}";
            return false;
        }

        try
        {
            if (string.Equals(linkType, "Junction", StringComparison.OrdinalIgnoreCase))
            {
                // 【为什么不用 cmd.exe mklink /J】三个理由，逐条都比拼命令行强：
                // 1) 没有 shell 层，路径不再经过 cmd 的元字符解析 ⇒ 注入面直接消失；
                // 2) 不必再手工维护 cmd 的引号转义规则（那种转义极易写错且难以测试）；
                // 3) 出错时拿到的是带 errno 的 IOException，比解析 stderr 文本稳。
                // 注：.NET 在 Windows 上创建 Junction 不需要管理员权限或开发者模式，
                // 与 mklink /J 的行为一致，所以这里没有能力退化。
                Directory.CreateSymbolicLink(linkPath, target);
                return true;
            }

            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 校验将参与链接创建的路径：非空、不含引号/换行/制表符等会被命令行或 YAML 误解析的字符。
    /// </summary>
    private static bool IsSafeLinkPath(string path, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "路径为空";
            return false;
        }

        foreach (var ch in path)
        {
            // 引号会破坏命令行的引号配对；CR/LF/TAB 会让单行命令变成多行或被截断。
            if (ch is '"' or '\r' or '\n' or '\t')
            {
                reason = $"路径含非法字符 U+{(int)ch:X4}：{path}";
                return false;
            }
        }

        try
        {
            // 顺带确认路径形态合法（拒绝 "C:\a\..\..\b" 之外的畸形输入与非法字符）
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>安全获取搜索根目录；提供者异常时返回空集合，保证探测不崩溃。</summary>
    private DshSearchRoots SafeGetSearchRoots()
    {
        try { return _searchRootProvider() ?? new DshSearchRoots(); }
        catch { return new DshSearchRoots(); }
    }

    /// <summary>安全调用多来源探测；异常时返回空清单，保证 Scan 不崩溃。</summary>
    private IReadOnlyList<DshInstallation> SafeDetectInstallations()
    {
        try { return DetectInstallations(); }
        catch { return Array.Empty<DshInstallation>(); }
    }

    /// <summary>手动指定安装：支持包目录、其上级 node_modules 目录，或启动器文件三种写法。</summary>
    private void AddManualInstallation(List<DshInstallation> found, string overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return;
        try
        {
            // 写法 A：直接给出启动器文件
            if (SafeFileExists(overridePath))
            {
                found.Add(CreateLauncherInstallation(overridePath, DshInstallSource.Manual));
                return;
            }
            if (!Directory.Exists(overridePath)) return;

            // 写法 B：给出 @deepseek-ai\dsh 包目录本身
            if (IsPackageDir(overridePath))
            {
                AddPackageInstallationAt(found, overridePath, DshInstallSource.Manual);
                return;
            }

            // 写法 C：给出上级 node_modules 目录
            AddPackageInstallation(found, overridePath, DshInstallSource.Manual);

            // 目录存在但不是包结构：仍记录下来（标记不可用，供用户核对）
            if (!found.Exists(i => i.Source == DshInstallSource.Manual))
            {
                found.Add(new DshInstallation
                {
                    PackageDir = overridePath,
                    Version = "unknown",
                    Source = DshInstallSource.Manual,
                    Rank = RankManual,
                    IsUsable = false,
                });
            }
        }
        catch { /* 手动路径异常（权限等）时忽略，不影响自动搜索结果 */ }
    }

    /// <summary>在指定 node_modules 目录下按固定相对路径查找 @deepseek-ai\dsh 包。</summary>
    private void AddPackageInstallation(List<DshInstallation> found, string nodeModulesDir, DshInstallSource source)
    {
        if (string.IsNullOrWhiteSpace(nodeModulesDir)) return;
        AddPackageInstallationAt(found, Path.Combine(nodeModulesDir, "@deepseek-ai", "dsh"), source);
    }

    /// <summary>由包目录构造安装信息并加入清单（非包目录 / 异常时忽略）。</summary>
    private void AddPackageInstallationAt(List<DshInstallation> found, string packageDir, DshInstallSource source)
    {
        var installation = CreatePackageInstallation(packageDir, source);
        if (installation is not null) found.Add(installation);
    }

    /// <summary>由包目录构造安装信息：读版本、定位 bin.js、统计体积、判断 Web 配套包与可用性。</summary>
    private DshInstallation? CreatePackageInstallation(string packageDir, DshInstallSource source)
    {
        try
        {
            if (!IsPackageDir(packageDir)) return null;

            var binJs = Path.Combine(packageDir, "lib", "bin.js");
            var binExists = SafeFileExists(binJs);
            // 包目录是 <node_modules>\@deepseek-ai\dsh，故 node_modules 要往上退两级
            var nodeModules = Path.GetDirectoryName(
                Path.GetDirectoryName(TrimTrailingSeparators(packageDir)) ?? string.Empty) ?? string.Empty;

            var installation = new DshInstallation
            {
                PackageDir = packageDir,
                BinJsPath = binExists ? binJs : null,
                Version = ReadPackageVersion(Path.Combine(packageDir, "package.json")),
                SizeBytes = DirectorySizeSafe(packageDir),
                HasWebApp = HasWebAppPackage(nodeModules),
                Source = source,
                Rank = RankFor(source),
                IsUsable = binExists,
            };

            // 回填符号链接信息：profiles 下的包常是指向 npx 缓存的 Junction，
            // 标注后用户才不会把同一物理位置的两个入口误认为重复安装
            ApplyLinkInfo(installation, packageDir);
            return installation;
        }
        catch { return null; }
    }

    /// <summary>
    /// 检测包目录是否为符号链接 / Junction（Windows 重解析点），并回填 <see cref="DshInstallation.IsLink"/> /
    /// <see cref="DshInstallation.LinkTarget"/>。
    /// 判定依据：<see cref="DirectoryInfo.Attributes"/> 含 <see cref="FileAttributes.ReparsePoint"/>，
    /// 目标路径取 <see cref="DirectoryInfo.LinkTarget"/>。
    /// 为什么要标注：dsh profiles 下的包目录常是指向 npx 缓存的 Junction，
    /// 安装位置清单会因此出现"同一物理位置的两个入口"，标注「快捷方式 → 目标」可消除重复的困惑。
    /// 任何异常（目标不存在 / 权限不足）都按普通目录处理，绝不影响探测。
    /// </summary>
    private static void ApplyLinkInfo(DshInstallation installation, string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            // LinkTarget 只在重解析点上有意义：普通目录（无 ReparsePoint）直接视为非链接
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0) return;

            installation.IsLink = true;
            installation.LinkTarget = string.IsNullOrWhiteSpace(info.LinkTarget) ? null : info.LinkTarget;
        }
        catch { /* 读不到链接信息（权限 / 目标缺失）时按普通目录处理 */ }
    }

    /// <summary>目录是否像 @deepseek-ai\dsh 包目录（含 package.json 或 lib 目录）。</summary>
    private bool IsPackageDir(string dir)
    {
        if (!GatedExists(dir)) return false;          // 尊重注入的存在性检查，便于单测隔离真实环境
        if (!Directory.Exists(dir)) return false;
        return SafeFileExists(Path.Combine(dir, "package.json"))
            || SafeFileExists(Path.Combine(dir, "lib", "bin.js"))
            || Directory.Exists(Path.Combine(dir, "lib"));
    }

    /// <summary>同级 node_modules 下是否存在 dsh-web-app 配套包。</summary>
    private bool HasWebAppPackage(string nodeModules)
    {
        if (string.IsNullOrWhiteSpace(nodeModules)) return false;
        try
        {
            return GatedExists(Path.Combine(nodeModules, "dsh-web-app"))
                || GatedExists(Path.Combine(nodeModules, "@deepseek-ai", "dsh-web-app"));
        }
        catch { return false; }
    }

    /// <summary>在目录下查找 dsh 启动器文件并加入清单。</summary>
    private void AddLauncherInstallations(List<DshInstallation> found, string dir, DshInstallSource source)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        foreach (var name in LauncherFileNames)
        {
            var full = Path.Combine(dir, name);
            if (!GatedExists(full) || !SafeFileExists(full)) continue;
            found.Add(CreateLauncherInstallation(full, source));
        }
    }

    /// <summary>由启动器文件构造安装信息（无包目录时按约定把 shim 路径填入 PackageDir）。</summary>
    private static DshInstallation CreateLauncherInstallation(string launcherPath, DshInstallSource source)
        => new()
        {
            PackageDir = launcherPath,
            LauncherPath = launcherPath,
            Version = "unknown",
            Source = source,
            Rank = RankFor(source),
            IsUsable = true,
        };

    /// <summary>
    /// 从 start-dsh-web.ps1 解析出 $workdir，若该工作目录下存在 node_modules\@deepseek-ai\dsh 则加入清单；
    /// $pkg 里钉住的版本作为 package.json 缺失 / 损坏时的兜底版本。
    /// </summary>
    private void AddWorkDirInstallation(List<DshInstallation> found)
    {
        try
        {
            var scriptContent = ReadStartScript();
            if (string.IsNullOrWhiteSpace(scriptContent)) return;

            var workDir = ParseWorkDir(scriptContent);
            if (string.IsNullOrWhiteSpace(workDir)) return;

            var packageDir = Path.Combine(workDir, "node_modules", "@deepseek-ai", "dsh");
            var installation = CreatePackageInstallation(packageDir, DshInstallSource.WorkDir);
            if (installation is null) return;

            // $pkg 形如 '@deepseek-ai/dsh@0.1.5-rc.3'：包目录里读不到版本时用它兜底
            if (!IsKnownVersion(installation.Version))
            {
                var specVersion = ValidateVersion(ParsePackageSpec(scriptContent));
                if (specVersion is not null) installation.Version = specVersion;
            }
            found.Add(installation);
        }
        catch { /* 脚本不可读时跳过该来源 */ }
    }

    /// <summary>来源 → 优先级分值。</summary>
    private static int RankFor(DshInstallSource source) => source switch
    {
        DshInstallSource.Manual => RankManual,
        DshInstallSource.NpmGlobal => RankNpmGlobal,
        DshInstallSource.DshProfiles => RankDshProfiles,
        DshInstallSource.NpxCache => RankNpxCache,
        DshInstallSource.AiManager => RankAiManager,
        DshInstallSource.PathLauncher => RankPathLauncher,
        DshInstallSource.WorkDir => RankWorkDir,
        _ => 0,
    };

    /// <summary>读取 package.json 的 version；内容缺失 / 损坏 / 无该字段时返回 "unknown"。</summary>
    private string ReadPackageVersion(string packageJsonPath)
    {
        try
        {
            var content = _fileContentReader(packageJsonPath);
            if (string.IsNullOrWhiteSpace(content)) return "unknown";
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                var text = version.GetString();
                return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
            }
            return "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>解析版本号的主次版本；无法解析（含 "unknown"）时返回 (0, 0)。</summary>
    private static (int Major, int Minor) ParseMajorMinor(string? version)
    {
        var parsed = ParseCoreVersion(version);
        return parsed is null ? (0, 0) : (parsed.Major, parsed.Minor);
    }

    /// <summary>比较两个版本的核心部分（忽略 -rc.x 预发布后缀）；无法解析者视为最低。</summary>
    private static int CompareCoreVersions(string? a, string? b)
    {
        var versionA = ParseCoreVersion(a);
        var versionB = ParseCoreVersion(b);
        if (versionA is null && versionB is null) return 0;
        if (versionA is null) return -1;
        if (versionB is null) return 1;
        return versionA.CompareTo(versionB);
    }

    /// <summary>把 "0.1.5-rc.3" 这类版本解析为 System.Version（去掉预发布后缀）；失败返回 null。</summary>
    private static Version? ParseCoreVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "unknown") return null;
        var dash = version.IndexOf('-');
        var core = dash > 0 ? version[..dash] : version;
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    /// <summary>去掉路径末尾的分隔符（避免 Path.GetDirectoryName 把尾分隔符后的空段当成文件名）。</summary>
    private static string TrimTrailingSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>尊重注入的存在性检查（异常安全）；用于让单测能隔离真实文件系统。</summary>
    private bool GatedExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && _pathExists(path); }
        catch { return false; }
    }

    /// <summary>真实文件存在性检查（异常安全）。</summary>
    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    /// <summary>非递归统计目录体积（只统计直接子文件）；失败返回 0。</summary>
    private static long DirectorySizeSafe(string dir)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.GetFiles(dir))
            {
                try { total += new FileInfo(file).Length; } catch { /* 单个文件读不到时跳过 */ }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>安全枚举子目录（异常时返回空数组）。</summary>
    private static string[] SafeGetDirectories(string dir)
    {
        try { return string.IsNullOrWhiteSpace(dir) ? Array.Empty<string>() : Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 按真实环境解析搜索根目录：PATH、%USERPROFILE%\.dsh、%LOCALAPPDATA%\npm-cache\_npx、
    /// %APPDATA%\npm\node_modules、npm root -g、%USERPROFILE%\.ai-manager，以及设置页的手动指定项。
    /// 只做"一层目录枚举"，不做递归全盘扫描，保证探测足够快。
    /// </summary>
    public static DshSearchRoots GetDefaultSearchRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dshHome = GetDefaultDshHome();

        // npm 全局：标准位置 + npm root -g（应对自定义 prefix 的机器）；两者都作为 node_modules 目录
        var npmNodeModules = new List<string> { Path.Combine(appData, "npm", "node_modules") };
        var npmRootGlobal = RunNpmRootGlobal();
        if (!string.IsNullOrWhiteSpace(npmRootGlobal))
            npmNodeModules.Add(npmRootGlobal.Trim());

        // dsh profiles：~/.dsh/profiles/node_modules 以及 ~/.dsh/profiles/*/node_modules
        var profilesDir = Path.Combine(dshHome, "profiles");
        var profileNodeModules = new List<string> { Path.Combine(profilesDir, "node_modules") };
        foreach (var profile in SafeGetDirectories(profilesDir))
            profileNodeModules.Add(Path.Combine(profile, "node_modules"));

        // ai-manager：%USERPROFILE%\.ai-manager\npm-global\<node 版本>\node_modules
        var aiNpmGlobal = Path.Combine(userProfile, ".ai-manager", "npm-global");
        var aiNodeModules = new List<string>();
        foreach (var nodeDir in SafeGetDirectories(aiNpmGlobal))
            aiNodeModules.Add(Path.Combine(nodeDir, "node_modules"));
        aiNodeModules.Add(Path.Combine(aiNpmGlobal, "node_modules")); // 兼容无 node 版本层的写法

        return new DshSearchRoots
        {
            InstallPathOverride = ReadInstallPathOverride(),
            PathDirs = GetPathDirectories(),
            NpxCacheRoots = new[] { Path.Combine(localAppData, "npm-cache", "_npx") },
            NpmGlobalNodeModules = Distinct(npmNodeModules),
            DshProfilesNodeModules = Distinct(profileNodeModules),
            AiManagerNodeModules = Distinct(aiNodeModules),
            AiManagerBinDirs = new[] { Path.Combine(userProfile, ".ai-manager", "bin") },
        };
    }

    /// <summary>读取设置页里的"手动指定安装位置"（空 = 自动搜索）。读取失败按空处理。</summary>
    private static string ReadInstallPathOverride()
    {
        try { return AppServices.Store.Load<AppSettings>(SettingsFileName).InstallPathOverride ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>PATH 环境变量的目录清单。</summary>
    private static IReadOnlyList<string> GetPathDirectories()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrWhiteSpace(pathValue)
            ? Array.Empty<string>()
            : pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                       .Select(dir => dir.Trim())
                       .ToArray();
    }

    /// <summary>去空去重（不区分大小写），保持原顺序。</summary>
    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths)
        => paths.Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>
    /// npx 回退：把 LaunchCommand / Version 按 npx 方式填好。
    /// 找不到 npx 时 LaunchCommand 留空、Version 记 unknown（绝不写入报错文本）。
    /// </summary>
    private void ApplyNpxFallback(DshInfo info, string? scriptContent)
    {
        var npxPath = SafeResolveNpx();
        if (string.IsNullOrWhiteSpace(npxPath))
        {
            info.Version = "unknown";
            return;
        }

        // 包规格优先取 start-dsh-web.ps1 的 $pkg（用户钉住的版本），取不到用默认包名
        var spec = ParsePackageSpec(scriptContent);
        if (string.IsNullOrWhiteSpace(spec)) spec = DefaultPackageSpec;

        // 含空格路径必须带双引号，否则 StartAsync 拆分命令时会被空格截断
        info.LaunchCommand = $"{QuoteIfNeeded(npxPath)} --yes {spec}";
        // 版本尽力获取：优先 $pkg 里的版本号，再退到 npx --version，取不到则 unknown
        info.Version = ResolveNpxVersion(npxPath, spec);
    }

    /// <summary>解析 npx 回退时的展示版本号：优先 ps1 包规格内的版本，其次 npx --version，最后 unknown。</summary>
    private string ResolveNpxVersion(string npxPath, string spec)
    {
        var fromSpec = ValidateVersion(spec);
        if (fromSpec is not null) return fromSpec;

        var fromNpx = ValidateVersion(_versionDetector(npxPath));
        return fromNpx ?? "unknown";
    }

    /// <summary>
    /// 校验 --version 的原始输出：能匹配到 x.y.z（允许 -rc.3 后缀）才返回该版本号，否则返回 null。
    /// 抽成 public static 便于独立测试（错误文本如"不是内部或外部命令"会被判定为 null）。
    /// </summary>
    public static string? ValidateVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = VersionRegex.Match(output);
        return match.Success ? match.Value : null;
    }

    /// <summary>从 ps1 内容中解析 $pkg 包规格（如 '@deepseek-ai/dsh@0.1.5-rc.3'）；解析不到返回空字符串。</summary>
    public static string ParsePackageSpec(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var match = PackageSpecRegex.Match(content);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>含空格的路径加双引号，保证后续命令拆分不被空格截断。</summary>
    private static string QuoteIfNeeded(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>读取 start-dsh-web.ps1 内容；不存在或不可读返回 null。</summary>
    private string? ReadStartScript() => _fileContentReader(Path.Combine(_dataDir, "start-dsh-web.ps1"));

    /// <summary>安全调用 npx 查找器；异常时返回 null，不打断扫描。</summary>
    private string? SafeResolveNpx()
    {
        try
        {
            return _npxResolver();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按候选顺序找第一个"可用"的本地启动器：存在、是文件、且 --version 输出能解析出版本号。
    /// 返回 (启动器路径, 版本号)；都不可用时返回 (空字符串, "unknown")。
    /// </summary>
    private (string Path, string Version) ResolveUsableLauncher(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var name = Path.GetFileName(path);
            if (!name.StartsWith("dsh", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_pathExists(path) || Directory.Exists(path)) continue;

            // 仅当 --version 输出里能匹配到版本号时，才认为该启动器真正可用
            var version = ValidateVersion(_versionDetector(path));
            if (version is not null)
                return (path, version);
        }
        return (string.Empty, "unknown");
    }

    /// <summary>
    /// 获取候选位置清单；候选提供者异常时安全降级为空列表，不打断扫描。
    /// </summary>
    private IEnumerable<string> SafeGetCandidates()
    {
        try
        {
            return _candidateProvider() ?? Enumerable.Empty<string>();
        }
        catch
        {
            // 候选提供者异常（如读取脚本失败）时返回空清单，保证 Scan 不崩溃
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// 解析启动器路径：按候选顺序取第一个"文件名以 dsh 开头且为文件（非目录）"的路径。
    /// </summary>
    private string ResolveLauncherPath(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var name = Path.GetFileName(path);
            if (name.StartsWith("dsh", StringComparison.OrdinalIgnoreCase)
                && _pathExists(path)
                && !Directory.Exists(path))
            {
                return path;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 从脚本内容中提取 $workdir 变量值（兼容 $workdir = "D:\xxx" / 'D:\xxx' / D:\xxx 写法）。
    /// </summary>
    private static string ParseWorkDir(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var match = WorkDirRegex.Match(content);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>从磁盘读取 start-dsh-web.ps1 并解析工作目录（静态版本，供默认候选清单使用）。</summary>
    private static string ResolveWorkDirFromScript(string dshHome)
    {
        try
        {
            var scriptPath = Path.Combine(dshHome, "start-dsh-web.ps1");
            return File.Exists(scriptPath) ? ParseWorkDir(File.ReadAllText(scriptPath)) : string.Empty;
        }
        catch
        {
            // 脚本不可读（权限/占用）时跳过该搜索项
            return string.Empty;
        }
    }

    /// <summary>在 PATH 环境变量中查找 dsh 启动器文件。</summary>
    private static IEnumerable<string> FindDshInPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return Enumerable.Empty<string>();

        var results = new List<string>();
        var names = new[] { "dsh.exe", "dsh.cmd", "dsh.bat", "dsh", "dsh.ps1" };
        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) results.Add(full);
            }
        }
        return results;
    }

    /// <summary>
    /// 在 PATH 环境变量中查找 npx 可执行文件，返回第一个命中的完整路径，找不到返回 null。
    /// 优先级：npx.cmd → npx.exe → npx.bat → npx（不选 npx.ps1，
    /// 因为 Process.Start(UseShellExecute=false) 无法直接启动 .ps1）。
    /// </summary>
    public static string? FindNpxExecutable()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;

        var dirs = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        // 按扩展名优先级逐个名字，在全部 PATH 目录里查找（先命中 npx.cmd 就优先于其它目录的 npx.exe）
        foreach (var name in new[] { "npx.cmd", "npx.exe", "npx.bat", "npx" })
        {
            foreach (var dir in dirs)
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>查找 npm 全局目录（npm root -g）下的 @deepseek-ai/dsh 包目录。</summary>
    private static string FindNpmGlobalDshPackage()
    {
        var npmRoot = RunNpmRootGlobal();
        if (string.IsNullOrWhiteSpace(npmRoot)) return string.Empty;

        // 包目录固定在 node_modules/@deepseek-ai/dsh 下
        var pkgDir = Path.Combine(npmRoot, "@deepseek-ai", "dsh");
        return Directory.Exists(pkgDir) ? pkgDir : string.Empty;
    }

    /// <summary>获取 npm root -g 输出（进程内缓存：一次扫描只询问一次 npm）；npm 不可用时返回空字符串。</summary>
    private static string RunNpmRootGlobal() => NpmRootGlobalLazy.Value;

    /// <summary>实际执行 npm root -g 获取 npm 全局包目录；npm 不可用 / 超时返回空字符串。</summary>
    private static string QueryNpmRootGlobal()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "npm",
                    Arguments = "root -g",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start()) return string.Empty;

            // 【为什么必须先异步读、再等退出】stderr 已重定向成管道：
            // 只同步读 stdout 的话，一旦 npm 往 stderr 写满缓冲区（警告/弃用提示很容易做到），
            // 子进程会阻塞在写 stderr 上再也不退出；而调用方正阻塞在 ReadToEnd() 上等它退出 ⇒ 互相死锁。
            // 同时排在读之后的 WaitForExit(3000) 永远轮不到执行，超时判定沦为死代码。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            // npm root -g 一般秒回；3 秒未退出则强制结束，避免卡住扫描
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 进程已退出则忽略 */ }
            }

            process.WaitForExit(); // 排空异步输出
            var output = stdout.Result;
            _ = stderr.Result; // stderr 只用于排空，不参与结果
            return output.Trim();
        }
        catch
        {
            // npm 不存在或执行异常时，跳过 npm 全局包这一搜索项
            return string.Empty;
        }
    }

    /// <summary>
    /// 默认版本检测：运行 &lt;exe&gt; --version（超时 5 秒），返回第一行非空原始输出，失败返回 null。
    /// 注意：返回值可能是报错文本，调用方需用 <see cref="ValidateVersion"/> 正则甄别后才可使用。
    /// </summary>
    private static string? DetectVersionSafe(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath)) return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            var output = new StringBuilder();
            // 异步读取 stdout/stderr，避免输出缓冲区满导致死锁
            process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
            process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);

            if (!process.Start()) return null;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 等待退出；超时 5 秒未响应则强制结束并视为检测失败
            if (!process.WaitForExit(VersionTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                process.WaitForExit();
                return null;
            }
            process.WaitForExit(); // 排空异步输出事件，确保输出完整

            var text = output.ToString();
            // 取第一行非空内容作为版本号（dsh --version 输出形如 "0.1.5-rc.3"）
            return text.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
        }
        catch
        {
            // 启动器不可执行（如无扩展名 shim 或为目录）时视为无法检测
            return null;
        }
    }

    private static void AppendLine(StringBuilder sb, string? line)
    {
        if (!string.IsNullOrEmpty(line)) sb.AppendLine(line);
    }

    /// <summary>路径存在性检查：兼容文件与目录（~/.dsh 等目录也应被识别为候选位置）。</summary>
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>安全读取文本文件；不存在或读取失败返回 null。</summary>
    private static string? ReadFileSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
