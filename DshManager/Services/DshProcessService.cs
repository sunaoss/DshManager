using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>
/// 进程生命周期句柄抽象：隔离 System.Diagnostics.Process 的重型 API，
/// 使状态机与日志逻辑可在测试中注入假实现。
/// 注意：本抽象刻意不暴露标准输出流——dsh 的输出去向是磁盘日志文件，
/// 面板不再持有任何匿名管道，从而保证面板退出后 dsh 不会被管道破裂拖死。
/// 继承 <see cref="IDisposable"/>：句柄持有操作系统内核对象与 ThreadPool 等待注册，
/// 每次"启动 → 停止"都必须释放，否则长时间使用后句柄数持续上涨。
/// </summary>
public interface IProcessHandle : IDisposable
{
    /// <summary>操作系统进程 ID。</summary>
    int Id { get; }

    /// <summary>进程是否已退出。</summary>
    bool HasExited { get; }

    /// <summary>进程退出码（仅进程退出后有效）。</summary>
    int ExitCode { get; }

    /// <summary>进程退出时触发（仅触发一次）。</summary>
    event EventHandler? Exited;

    /// <summary>结束整个进程树（Windows 上使用 taskkill /T /F）。</summary>
    void KillEntireTree();

    /// <summary>等待进程退出。</summary>
    Task WaitForExitAsync();
}

/// <summary>
/// 进程启动器抽象：真实实现基于 Process.Start，测试注入假启动器。
/// </summary>
public interface IProcessLauncher
{
    /// <summary>按给定启动信息启动进程并返回句柄。</summary>
    IProcessHandle Launch(ProcessStartInfo startInfo);
}

/// <summary>基于 System.Diagnostics.Process 的真实进程启动器。</summary>
public sealed class RealProcessLauncher : IProcessLauncher
{
    public IProcessHandle Launch(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start 返回空引用");
        return new RealProcessHandle(process);
    }
}

/// <summary>包装真实 Process 的进程句柄实现。</summary>
public sealed class RealProcessHandle : IProcessHandle
{
    private readonly Process _process;

    /// <summary>持有 Exited 处理器引用：Dispose 时必须退订，否则 Process 会一直引用本对象。</summary>
    private readonly EventHandler _onExited;

    public RealProcessHandle(Process process)
    {
        _process = process;
        _onExited = (_, _) => Exited?.Invoke(this, EventArgs.Empty);

        // 开启退出事件，否则 Exited 不会触发
        _process.EnableRaisingEvents = true;
        _process.Exited += _onExited;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public event EventHandler? Exited;

    /// <summary>结束整个进程树：复用统一的按 PID 终止实现。</summary>
    public void KillEntireTree() => DshProcessService.KillProcessTree(_process.Id);

    public Task WaitForExitAsync() => _process.WaitForExitAsync();

    /// <summary>
    /// 释放 Process 句柄并退订 Exited。
    /// 【为什么必须做】Process 句柄是操作系统内核对象 + 一个注册在 ThreadPool 上的等待操作，
    /// 不释放的话每一次"启动 → 停止"循环都会泄漏一份，长时间使用后面板句柄数持续上涨。
    /// </summary>
    public void Dispose()
    {
        try
        {
            _process.Exited -= _onExited;
            _process.EnableRaisingEvents = false;
        }
        catch
        {
            // 进程已退出/句柄已失效时退订可能抛异常，不影响释放
        }

        _process.Dispose();
    }
}

/// <summary>
/// dsh 进程管理服务（事件驱动，供 UI 绑定）：
/// - 通过 CurrentState 通知 UI 状态灯（Stopped / Starting / Running / Stopping）；
/// - dsh 的 stdout / stderr 由 cmd 重定向到磁盘日志文件，本服务轮询文件增量并逐行触发 LogReceived；
/// - 核心约束：面板不持有任何管道、不在退出时对 dsh 做任何终止动作，故关闭面板不影响 dsh 存活；
/// - 支持识别 / 控制「外部启动的实例」（通过 Win32 TCP 表反查监听端口的 PID 实现接管与停止）；
/// - 暴露 dsh 相关进程列表（<see cref="Instances"/>，含内存 / CPU / 启动时间）供界面展示。
/// </summary>
public class DshProcessService : ObservableObject
{
    /// <summary>日志缓冲行数上限，防止长时间运行导致内存膨胀。</summary>
    public const int MaxLogLines = 1000;

    /// <summary>默认 Web 端口。</summary>
    public const int DefaultPort = 3080;

    /// <summary>
    /// 启动 dsh web 时追加的参数：禁止 dsh 自行打开默认浏览器。
    /// 【为什么用 --no-open】① dsh 主动拉起浏览器会与"端口就绪"的判定抢时间，拖慢面板进入 Running；
    /// ② 浏览器改由面板在端口就绪后统一打开一次（见 HomeViewModel 的自动打开逻辑），行为更可控、不会重复弹窗。
    /// </summary>
    public const string NoOpenArgument = "--no-open";

    /// <summary>启动超时秒数：dsh 装配 200+ 插件通常 9~30 秒，但首次装配 / pnpm 重建依赖会明显更久，
    /// 故留 120 秒余量；又能避免"进程既没监听端口、也没退出"时状态永久停在「启动中」。抽成常量便于单测。</summary>
    public const int DefaultStartupTimeoutSeconds = 120;

    /// <summary>默认启动超时时长（由 <see cref="DefaultStartupTimeoutSeconds"/> 派生）。</summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(DefaultStartupTimeoutSeconds);

    /// <summary>日志文件增量轮询间隔（毫秒）。</summary>
    private const int LogPollIntervalMs = 300;

    /// <summary>
    /// 「终止外部实例」后确认端口释放的最大轮询次数（配合 <see cref="LogPollIntervalMs"/> 约 1 秒）。
    /// taskkill /F 是立即生效的，若 1 秒内端口仍未释放，说明它并非由被结束的进程占用。
    /// </summary>
    private const int TerminatePortReleasePollCount = 3;

    /// <summary>匹配 dsh 日志里被报"找不到"的作用域包名（如 @deepseek-ai/dsh-llm），用于生成可读诊断。</summary>
    private static readonly Regex MissingPackageRegex =
        new(@"@deepseek-ai/[A-Za-z0-9._-]+", RegexOptions.Compiled);

    private readonly IProcessLauncher _launcher;
    private readonly Func<string, int, int, bool> _portProbe;
    private readonly Encoding _outputEncoding;
    private readonly string _logFilePath;
    private readonly Func<int, int?> _listeningPidResolver;
    private readonly Func<int, bool> _processTreeKiller;

    /// <summary>
    /// 便携版 node.exe 路径解析委托：启动命令里若用裸 node，则替换为该路径（返回 null 表示未装便携版，保持原样）。
    /// 抽成委托是为了测试可注入假实现，且每次启动都重新解析（装完便携版后无需重启面板即可生效）。
    /// </summary>
    private readonly Func<string?> _portableNodePathResolver;

    /// <summary>
    /// bin.js 真实路径解析委托：启动时把指向 profiles 符号链接的 bin.js 解析为真实绝对路径（返回 null 表示解析失败，回退原路径）。
    /// 抽成委托是为了测试可注入假实现，避免单测因默认的 File/Directory.ResolveLinkTarget 触碰真实文件系统。
    /// </summary>
    private readonly Func<string, string?> _realBinScriptResolver;

    /// <summary>启动超时时长（可注入，便于单测而不是真等 120 秒）。</summary>
    private readonly TimeSpan _startupTimeout;

    /// <summary>时钟委托（可注入假时钟，便于单测"已等待多久"的判定逻辑）。默认走 <see cref="DateTime.UtcNow"/>。</summary>
    private readonly Func<DateTime> _clock;

    private readonly object _logPollLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentQueue<string> _logBuffer = new();

    private IProcessHandle? _process;
    private volatile bool _stopRequested;
    private int _exitHandled;
    private DshProcessState _currentState = DshProcessState.Stopped;

    // 日志文件增量轮询状态（仅轮询任务线程访问）
    private CancellationTokenSource? _logPollCts;
    private Task? _logPollingTask;
    private long _logReadOffset;
    private string _pendingLineRemainder = string.Empty;

    // 外部实例（非本面板启动）相关状态
    private int? _externalProcessId;

    /// <summary>当前进程状态（UI 状态灯绑定），变化时触发 PropertyChanged 与 StateChanged。</summary>
    public DshProcessState CurrentState => _currentState;

    /// <summary>本次启动的超时时长（供界面按同一口径估算进度百分比，避免界面与服务两套阈值不一致）。</summary>
    public TimeSpan StartupTimeout => _startupTimeout;

    /// <summary>当前运行的实例是否由外部方式启动（开机自启脚本等）；面板自启实例为 false。</summary>
    public bool IsExternalInstance { get; private set; }

    /// <summary>外部实例的主进程 PID（未接管 / 反查不到时为 null），供界面展示与「终止该实例」确认框使用。</summary>
    public int? ExternalProcessId => _externalProcessId;

    /// <summary>
    /// 在最近一次进程探测结果（<see cref="Instances"/>）里查找外部实例对应的进程详情（命令行等）。
    /// 匹配不到（如权限不足读不到命令行、探测尚未刷新）返回 null，界面据此退化为"只显示 PID"。
    /// </summary>
    public DshInstanceInfo? FindExternalInstance()
        => _externalProcessId is int pid ? Instances.FirstOrDefault(i => i.Pid == pid) : null;

    /// <summary>dsh Web 端口（启动时更新）。</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>dsh Web UI 地址（启动时更新）。</summary>
    public string WebUrl { get; private set; } = $"http://127.0.0.1:{DefaultPort}";

    /// <summary>收到一行进程输出（日志文件增量）时触发；UI 端需自行调度到 UI 线程。</summary>
    public event EventHandler<string>? LogReceived;

    /// <summary>进程状态变化时触发，参数为新状态。</summary>
    public event EventHandler<DshProcessState>? StateChanged;

    /// <summary>进程意外退出时触发（主动 StopAsync 不触发此事件）。</summary>
    public event EventHandler? ProcessExited;

    /// <summary>日志缓冲的线程安全快照（供 UI 初始加载）。</summary>
    public IReadOnlyList<string> LogLines => _logBuffer.ToArray();

    /// <summary>最近一次探测到的 dsh 相关进程列表（主服务在前），由 <see cref="RefreshInstances"/> 刷新。</summary>
    public IReadOnlyList<DshInstanceInfo> Instances { get; private set; } = Array.Empty<DshInstanceInfo>();

    /// <summary>
    /// 默认日志文件路径：%LOCALAPPDATA%\DshManager\logs\dsh-web.log。
    /// 选 LocalApplicationData 而非程序目录，保证安装到 Program Files 后仍可写。
    /// </summary>
    public static string DefaultLogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DshManager", "logs", "dsh-web.log");

    /// <summary>
    /// 创建服务实例。
    /// </summary>
    /// <param name="launcher">进程启动器；默认使用真实实现，测试可注入假启动器。</param>
    /// <param name="portProbe">端口探测委托 (host, port, timeoutMs) =&gt; bool；默认使用 IsPortOpen。</param>
    /// <param name="outputEncoding">日志文件解码编码；默认 UTF-8，避免中文乱码。</param>
    /// <param name="logFilePath">dsh 输出重定向目标文件；默认 <see cref="DefaultLogFilePath"/>，测试可注入临时文件。</param>
    /// <param name="listeningPidResolver">监听端口 → PID 解析委托；默认走 Win32 TCP 表反查，测试可注入假实现避免真实探测。</param>
    /// <param name="processTreeKiller">按 PID 结束进程树委托；默认走 taskkill，测试可注入假实现避免真实 kill。</param>
    /// <param name="portableNodePathResolver">
    /// 面板便携版 node.exe 路径解析委托；默认每次启动时向 <see cref="NodeRuntimeService"/> 查询。
    /// 测试可注入假实现（返回 null 即等价于"未安装便携版"）。
    /// </param>
    /// <param name="startupTimeout">
    /// 启动超时时长；默认 <see cref="DefaultStartupTimeout"/>（120 秒）。测试可注入 <see cref="TimeSpan.Zero"/> 立即触发超时。
    /// </param>
    /// <param name="clock">时钟委托；默认 <see cref="DateTime.UtcNow"/>。测试可注入假时钟，避免真的等待超时时长。</param>
    /// <param name="realBinScriptResolver">
    /// bin.js 真实路径解析委托；默认走 File/Directory.ResolveLinkTarget。
    /// 测试可注入假实现（返回 null 即等价于"解析失败、回退原路径"），从而完全不触碰真实文件系统。
    /// </param>
    public DshProcessService(
        IProcessLauncher? launcher = null,
        Func<string, int, int, bool>? portProbe = null,
        Encoding? outputEncoding = null,
        string? logFilePath = null,
        Func<int, int?>? listeningPidResolver = null,
        Func<int, bool>? processTreeKiller = null,
        Func<string?>? portableNodePathResolver = null,
        TimeSpan? startupTimeout = null,
        Func<DateTime>? clock = null,
        Func<string, string?>? realBinScriptResolver = null)
    {
        _launcher = launcher ?? new RealProcessLauncher();
        _portProbe = portProbe ?? IsPortOpen;
        _outputEncoding = outputEncoding ?? Encoding.UTF8;
        _logFilePath = string.IsNullOrWhiteSpace(logFilePath) ? DefaultLogFilePath : logFilePath;
        // 端口占用者反查统一走 DshProcessProbe（Win32 TCP 表），不再解析 netstat 文本
        _listeningPidResolver = listeningPidResolver ?? (port => DshProcessProbe.FindPortOwner(port));
        _processTreeKiller = processTreeKiller ?? KillProcessTree;
        // 默认解析器每次调用都新建轻量服务实例：能感知"刚刚装好的便携版"，无需重启面板
        _portableNodePathResolver = portableNodePathResolver
            ?? (() => new NodeRuntimeService().GetPreferredNodePath());
        // bin.js 真实路径解析器：生产走 File/Directory.ResolveLinkTarget，单测可注入假实现（完全不触碰文件系统）
        _realBinScriptResolver = realBinScriptResolver ?? ResolveRealBinScript;
        // 超时与时钟均可注入：生产用 120 秒 + 系统时钟，单测用零超时 / 假时钟
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// 拆分启动命令为「可执行文件 + 参数前缀」，供 <see cref="StartAsync"/> 与插件服务复用。
    /// 支持的格式：
    /// - 双引号包裹的可执行路径（路径内可含空格）："C:\Program Files\nodejs\npx.cmd" --yes @deepseek-ai/dsh；
    /// - 无引号、无空格的普通路径：C:\x\dsh.cmd；
    /// - 空字符串 / 仅可执行文件：参数前缀为空字符串。
    /// 引号未闭合等异常写法按"整体作为可执行文件、无参数"处理，绝不抛异常。
    /// </summary>
    public static void SplitCommand(string command, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrWhiteSpace(command)) return;

        var trimmed = command.Trim();
        if (trimmed[0] == '"')
        {
            // 引号包裹路径：以第二个引号作为路径与参数的分界
            var closing = trimmed.IndexOf('"', 1);
            if (closing < 0)
            {
                // 引号未闭合：退化为整体当可执行文件
                fileName = trimmed[1..];
                return;
            }
            fileName = trimmed[1..closing];
            arguments = trimmed[(closing + 1)..].Trim();
            return;
        }

        // 无引号：以第一个空格分隔可执行文件与参数前缀
        var space = trimmed.IndexOf(' ');
        if (space < 0)
        {
            fileName = trimmed;
            return;
        }
        fileName = trimmed[..space];
        arguments = trimmed[(space + 1)..].Trim();
    }

    /// <summary>
    /// 从"node 可执行文件 + 参数前缀"里提取 node 将要执行的 bin.js 路径。
    /// 仅识别 `node &lt;bin.js&gt;` 形态（可执行文件是 node / node.exe，且首个参数以 .js 结尾）；
    /// npx.cmd / dsh.cmd 等启动器形态返回 null（它们没有可直接兜底的 bin.js）。
    /// 抽成静态纯函数便于单测，不触碰文件系统。
    /// </summary>
    public static string? ExtractBinScriptPath(string fileName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(arguments))
            return null;
        if (!IsNodeExecutable(fileName))
            return null;

        var first = ReadFirstArgument(arguments);
        return first.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? first : null;
    }

    /// <summary>
    /// 由"用于启动的 bin.js 路径"推导 NODE_PATH 取值，即 bin.js 所在安装根的 node_modules 绝对路径。
    ///
    /// 【为什么这么取】bin.js 恒位于 &lt;installRoot&gt;\node_modules\@deepseek-ai\dsh\lib\bin.js
    /// （&lt;installRoot&gt; 可能是某个 _npx\&lt;hash&gt;，也可能是 npm 全局目录）。
    /// 而 Node 的 NODE_PATH 语义是：NODE_PATH=&lt;X&gt; 时解析 @scope/pkg 会查 &lt;X&gt;\@scope\pkg，
    /// 所以 X 必须是 **node_modules 这一层**，写成 ...\node_modules\@deepseek-ai 会多一层、完全无效。
    ///
    /// 【容错】路径分隔符不敏感（/ 与 \ 等价）、大小写不敏感；找不到 \node_modules\ 段时返回 null（不抛异常）。
    ///
    /// 【实测结论（本机 node v24 验证）】NODE_PATH 指向 <c>…\node_modules</c> 时
    /// <c>require.resolve('@scope/pkg')</c> 成功；指向 <c>…\node_modules\@scope</c> 时一律 MODULE_NOT_FOUND。
    /// 即：Task 描述里提到的旧脚本写法 <c>NODE_PATH=…\_npx\&lt;hash&gt;\node_modules\@deepseek-ai</c> 对
    /// dsh 内部 <c>import "@deepseek-ai/dsh-xxx"</c> 这类**作用域包名**其实无效
    /// （旧脚本能跑通靠的是 bin.js 位于 node_modules 树内、可沿上级 node_modules 自然解析到兄弟包）。
    /// 因此这里坚持取 node_modules 这一层，才是真正能兜底的取值。
    /// </summary>
    public static string? ResolveNodePathFromBinScript(string? binScriptPath)
    {
        if (string.IsNullOrWhiteSpace(binScriptPath)) return null;

        // 统一为正斜杠后再按段匹配，兼容 C:/x/... 与 C:\x\... 两种写法
        var normalized = binScriptPath.Replace('/', '\\');
        const string marker = "\\node_modules\\";
        // 取最后一个 \node_modules\：bin.js 的安装根 node_modules 一定紧邻 \@deepseek-ai\dsh\lib，
        // 用 LastIndexOf 可自动忽略安装根自身路径里可能出现的同名目录段
        var index = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        // index 指向 node_modules 前的分隔符；截到 node_modules 结尾（不含其后的分隔符）
        return normalized[..(index + marker.Length - 1)];
    }

    /// <summary>
    /// 尝试把 bin.js 路径解析为"真实路径"，用于绕开 ~\.dsh\profiles\ 下那批易失效的符号链接 / Junction。
    ///
    /// 【为什么需要】profiles 里的 @deepseek-ai\dsh 通常是指向 npm npx 缓存的链接，缓存被重建时链接会短暂失效，
    /// 直接用它启动会立刻报 100 个 "Cannot find package"；改用真实绝对路径可显著降低这种抖动。
    /// 【回退策略】解析不到（不是链接 / 目标不存在 / 权限不足 / 解析器抛异常）一律返回原路径，绝不报错、绝不阻断启动。
    /// 【可测性】真实解析器通过参数注入，单测可传入"成功"/"失败"两种假实现，完全不触碰文件系统。
    /// </summary>
    /// <param name="binScriptPath">原始 bin.js 路径。</param>
    /// <param name="realPathResolver">真实路径解析器；为 null 时使用基于 File/Directory.ResolveLinkTarget 的默认实现。</param>
    public static string TryResolveRealBinScript(string? binScriptPath, Func<string, string?>? realPathResolver = null)
    {
        if (string.IsNullOrWhiteSpace(binScriptPath)) return binScriptPath ?? string.Empty;

        try
        {
            var resolver = realPathResolver ?? ResolveRealBinScript;
            var resolved = resolver(binScriptPath);
            return string.IsNullOrWhiteSpace(resolved) ? binScriptPath : resolved;
        }
        catch
        {
            // 解析过程任何异常都等价于"解析失败"：回退原路径
            return binScriptPath;
        }
    }

    /// <summary>
    /// 默认真实路径解析器：先看 bin.js 自身是否为链接；否则**从包目录往上逐级**找第一个重解析点（Junction / 符号链接），
    /// 解析出真实目录后把剩余相对尾巴拼回。任何失败返回 null（由 <see cref="TryResolveRealBinScript"/> 回退原路径）。
    ///
    /// 【为什么逐级向上找而不是只看包目录】profiles 下的链接层级并不固定：实测既可能落在
    /// <c>@deepseek-ai\dsh</c>（包目录），也可能落在 <c>@deepseek-ai</c>（作用域目录）甚至 <c>node_modules</c> 这一层。
    /// 旧实现只解析"包目录"这一个层级，后两种布局下会静默解析失败、退回链接路径——这正是"能力已实现却未生效"的原因。
    /// 向上最多走到 <c>node_modules</c>（再往上与安装无关），避免无谓地一路走到盘根。
    ///
    /// 【为什么要复核解析结果存在】链接可能指向残缺目录（缺 lib\bin.js）。此时解析出来的物理 bin.js 根本不存在，
    /// 换过去会把"链接还能用"变成"必然启动失败"，因此这种情况返回 null、回退链接路径。
    /// </summary>
    private static string? ResolveRealBinScript(string binScriptPath)
    {
        var full = TryGetFullPathSafe(binScriptPath);
        if (full is null) return null;

        // 1) bin.js 自身就是链接：直接返回最终目标
        var fileTarget = TryResolveLink(() => File.ResolveLinkTarget(full, returnFinalTarget: true));
        if (fileTarget is not null) return fileTarget;

        // 2) 从包目录往上逐级找第一个链接：解析它，再把相对尾巴（如 lib\bin.js）拼回
        for (var current = Path.GetDirectoryName(full);
             !string.IsNullOrWhiteSpace(current);
             current = Path.GetDirectoryName(current))
        {
            var dirTarget = TryResolveLink(() => Directory.ResolveLinkTarget(current, returnFinalTarget: true));
            if (dirTarget is not null)
            {
                var candidate = Path.Combine(dirTarget, Path.GetRelativePath(current, full));
                // 物理 bin.js 必须真实存在，否则回退链接路径（宁可用链接，也不给一个不存在的路径）
                return File.Exists(candidate) ? candidate : null;
            }

            // 走到 node_modules 这一层仍未发现链接 → 说明安装树里没有重解析点，无需继续向上
            if (string.Equals(Path.GetFileName(current), "node_modules", StringComparison.OrdinalIgnoreCase))
                break;
        }

        return null;
    }

    /// <summary>尽力把路径归一为绝对路径；失败返回 null（等价于"解析失败"，绝不抛异常）。</summary>
    private static string? TryGetFullPathSafe(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>执行一次链接解析并把异常 / 空结果统一收敛为 null（不是链接、目标缺失、权限不足都算解析失败）。</summary>
    private static string? TryResolveLink(Func<FileSystemInfo?> resolve)
    {
        try
        {
            var target = resolve();
            return string.IsNullOrWhiteSpace(target?.FullName) ? null : target!.FullName;
        }
        catch { return null; }
    }

    /// <summary>
    /// 为启动进程设置兜底环境变量（NODE_PATH / DSH_HOME）。
    ///
    /// 【为什么设 NODE_PATH】dsh 的依赖解析默认只看 profiles\node_modules\@deepseek-ai\* 那批符号链接；
    /// 链接抖动时会出现 100 个包一起 "Cannot find package"。补一条固定的 node_modules 兜底路径即可显著降低该故障。
    /// 【为什么不覆盖】若用户/系统已显式设置同名变量，尊重其配置不动（ProcessStartInfo 的环境变量默认继承当前进程环境）。
    /// 【可测性】纯静态函数，单测可直接传入 ProcessStartInfo 断言，不启动任何进程。
    /// </summary>
    public static void ApplyStartupEnvironment(ProcessStartInfo startInfo, string? nodePath, string? dshHome)
    {
        if (startInfo is null) return;

        if (!string.IsNullOrWhiteSpace(nodePath) && !HasNonEmptyEnvironmentValue(startInfo, "NODE_PATH"))
            startInfo.EnvironmentVariables["NODE_PATH"] = nodePath;

        if (!string.IsNullOrWhiteSpace(dshHome) && !HasNonEmptyEnvironmentValue(startInfo, "DSH_HOME"))
            startInfo.EnvironmentVariables["DSH_HOME"] = dshHome;
    }

    /// <summary>启动信息里是否已存在非空的同名环境变量（继承自当前进程或显式设置）。</summary>
    private static bool HasNonEmptyEnvironmentValue(ProcessStartInfo startInfo, string name)
    {
        // ProcessStartInfo.EnvironmentVariables 返回的是 StringDictionary（非泛型），没有 TryGetValue。
        var variables = startInfo.EnvironmentVariables;
        return variables is not null
            && variables.ContainsKey(name)
            && !string.IsNullOrWhiteSpace(variables[name]);
    }

    /// <summary>可执行文件是否为 node（裸 node，或任意路径下的 node.exe）。</summary>
    private static bool IsNodeExecutable(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        return string.Equals(name, "node", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "node.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取参数串的第一个参数：支持双引号包裹（路径含空格），畸形写法返回空字符串。</summary>
    private static string ReadFirstArgument(string arguments)
    {
        var trimmed = arguments.TrimStart();
        if (trimmed.Length == 0) return string.Empty;

        if (trimmed[0] == '"')
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing < 0 ? string.Empty : trimmed[1..closing];
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>把参数串的首个参数替换为新路径（保留原有缩进与其余参数；含空格路径自动加双引号）。</summary>
    private static string ReplaceLeadingArgument(string arguments, string newPath)
    {
        var trimmed = arguments.TrimStart();
        var leadingSpaces = arguments[..(arguments.Length - trimmed.Length)];

        int tokenEnd;
        if (trimmed.Length > 0 && trimmed[0] == '"')
        {
            var closing = trimmed.IndexOf('"', 1);
            tokenEnd = closing < 0 ? trimmed.Length : closing + 1;
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            tokenEnd = space < 0 ? trimmed.Length : space;
        }

        return leadingSpaces + QuoteIfNeeded(newPath) + trimmed[tokenEnd..];
    }

    /// <summary>
    /// 把一个值安全地放进 cmd.exe 命令行：用双引号整体包住，并把值里原有的双引号翻倍。
    /// </summary>
    /// <remarks>
    /// 【为什么要转义】cmd 解析 "/c ..." 时引号是配对解析的：值里多出一个孤立引号会让后面的内容
    /// "跑出引号"，其中的 &amp; | ^ 立刻变成命令分隔符。把内部引号写成 ""（cmd 在引号内把 "" 视作一个
    /// 字面引号）就能保持配对平衡，元字符始终待在引号里，只被当普通文本传给子进程。
    /// 空值渲染成一对空引号，等价于空参数。
    /// </remarks>
    private static string QuoteForCmd(string? value)
    {
        var safe = (value ?? string.Empty).Replace("\"", "\"\"");
        return $"\"{safe}\"";
    }

    /// <summary>含空格的路径加双引号，保证命令串里不会被空格截断。</summary>
    private static string QuoteIfNeeded(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// 取面板实际使用的 dsh home（默认 ~\.dsh；用户显式设了 DSH_HOME 则用其值），
    /// 仅用于给子进程补一个一致的 DSH_HOME；取不到（异常 / 空）时返回 null 表示不设置。
    /// </summary>
    private static string? TryGetDshHome()
    {
        try
        {
            var home = DshDiscoveryService.GetDefaultDshHome();
            return string.IsNullOrWhiteSpace(home) ? null : home;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 若启动命令用的是"裸 node"（不带路径的 node / node.exe），且面板已装便携版 Node，
    /// 则返回便携版 node.exe 的绝对路径，否则原样返回。
    ///
    /// 【为什么只替换裸 node】dsh 的 LaunchCommand 有两种形态：
    /// `node "&lt;bin.js&gt;"`（依赖 PATH 里的 node）与 `"C:\...\npx.cmd" --yes ...`（自带路径）；
    /// 只有前者会因为系统没装 Node 而失败，因此只替换它，带路径的命令一律不动，避免破坏现有行为。
    /// 【为什么每次启动都重新解析】用户装完便携版后无需重启面板，下一次启动即生效。
    /// </summary>
    private string ResolvePortableNodePath(string fileName)
    {
        if (!IsBareNodeCommand(fileName)) return fileName;

        try
        {
            var portableNode = _portableNodePathResolver();
            return string.IsNullOrWhiteSpace(portableNode) ? fileName : portableNode;
        }
        catch
        {
            // 解析失败等价于"未安装便携版"：保持原命令，绝不影响启动流程
            return fileName;
        }
    }

    /// <summary>是否为不带路径的裸 node 命令（node / node.exe，大小写不敏感）；带路径的不算。</summary>
    private static bool IsBareNodeCommand(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        // 含路径分隔符说明已指定具体位置（如 C:\Program Files\nodejs\node.exe），不替换
        if (fileName.Contains('\\') || fileName.Contains('/')) return false;

        return string.Equals(fileName, "node", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "node.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 启动 dsh：状态 → Starting，通过 cmd 把输出重定向到日志文件后拉起进程，轮询 Web 端口，就绪 → Running。
    /// 若进程在端口就绪前退出，视为启动失败，回 Stopped 并触发 ProcessExited。
    /// </summary>
    public async Task StartAsync(string dshCommand, string workDir, int port)
    {
        await _lifecycleLock.WaitAsync();
        IProcessHandle? process = null;
        try
        {
            // 幂等保护：已处于启动中 / 运行中则忽略本次调用
            if (_currentState is DshProcessState.Starting or DshProcessState.Running)
                return;

            // 复位本次启动的停止标志与退出处理标志（重启场景需要）
            _stopRequested = false;
            Interlocked.Exchange(ref _exitHandled, 0);
            // 面板自己启动的实例不是外部实例
            IsExternalInstance = false;
            _externalProcessId = null;

            Port = port;
            WebUrl = $"http://127.0.0.1:{port}";

            // 把"命令 + 参数前缀"正确拆分：如 "\"C:\Program Files\nodejs\npx.cmd\" --yes @deepseek-ai/dsh@0.1.5-rc.3"
            SplitCommand(dshCommand, out var fileName, out var prefixArgs);
            // 关键：面板已安装便携版 Node 时，把启动命令里的裸 node 换成便携版绝对路径
            // （LaunchCommand 形如 `node "<bin.js>"`，裸 node 依赖系统 PATH；换成便携版后
            //  用户没装系统级 Node 也能启动 dsh；未装便携版时原样返回，行为与以前完全一致）
            // 说明：本改动不改动"系统优先"原则——node 仍由 NodeRuntimeService 决定，这里只影响 bin.js 路径与环境变量。
            fileName = ResolvePortableNodePath(fileName);

            // 关键：bin.js 优先用"真实路径"启动，绕开 ~\.dsh\profiles\ 下那批易失效的符号链接 / Junction。
            // 【为什么】profiles 里的 @deepseek-ai\dsh 通常是指向 npm npx 缓存的链接，缓存重建时链接会短暂失效，
            // 直接用链接路径启动会立刻报 100 个 "Cannot find package"。解析失败则回退原路径（不报错、不阻断启动）。
            var binScriptPath = ExtractBinScriptPath(fileName, prefixArgs);
            if (binScriptPath is not null)
            {
                var realBinScript = TryResolveRealBinScript(binScriptPath, _realBinScriptResolver);
                if (!string.Equals(realBinScript, binScriptPath, StringComparison.OrdinalIgnoreCase))
                    prefixArgs = ReplaceLeadingArgument(prefixArgs, realBinScript);
                // NODE_PATH 用"真实路径"推导：解析成功时指向 npx 缓存 / 全局目录的 node_modules，比链接路径更稳
                binScriptPath = realBinScript;
            }

            // NODE_PATH 兜底：Node 语义下解析 @scope/pkg 会查 <NODE_PATH>\@scope\pkg，
            // 故取值必须是 dsh 安装根的 node_modules 这一层（不是 @deepseek-ai，多一层会完全无效）。
            // 用途：profiles 下那 240 个符号链接抖动时，依赖仍能经这条固定路径解析，避免 100 个包一起报缺失。
            var nodePathValue = ResolveNodePathFromBinScript(binScriptPath);

            // DSH_HOME 取面板实际使用的 dsh home（~\.dsh）；取不到则留空、不设置
            var dshHomeValue = TryGetDshHome();

            // 参数 = 前缀参数 + web --port <port> + --no-open
            // 【为什么加 --no-open】让 dsh 不要自己拉起默认浏览器：① 避免抢浏览器拖慢 / 干扰"端口就绪"判定；
            // ② 由面板在端口就绪（Running）后统一打开一次网页，行为更可控（旧版工具用了同一参数）。
            var argsPart = string.IsNullOrWhiteSpace(prefixArgs)
                ? $"web --port {port} {NoOpenArgument}"
                : $"{prefixArgs} web --port {port} {NoOpenArgument}";

            // 启动日志把"实际将执行的命令 + 兜底环境变量 + 工作目录"完整记录一行，便于下次排查（不含敏感信息）
            AddLogLine(
                $"正在启动 dsh（命令：{fileName} {argsPart}；" +
                $"工作目录：{(string.IsNullOrWhiteSpace(workDir) ? "(默认)" : workDir)}；" +
                $"NODE_PATH：{nodePathValue ?? "(未设置)"}；DSH_HOME：{dshHomeValue ?? "(未设置)"}）...");
            SetState(DshProcessState.Starting);

            // 每次启动都是全新会话日志：先清空旧内容，避免历史输出混淆
            PrepareLogFile();

            // 关键：用 cmd 把 dsh 的 stdout/stderr 追加写入日志文件，面板不再重定向管道。
            // chcp 65001 让 cmd 会话使用 UTF-8 代码页，配合按 UTF-8 读文件即可保证中文不乱码。
            // 三个插值位都过 QuoteForCmd：路径或参数里带引号会把这条命令截断（旧实现只加引号不转义），
            // 参数里带 & | ^ 更会被 cmd 当成命令分隔符——那就是一条现成的任意命令执行路径。
            var arguments =
                $"/c chcp 65001 >nul & {QuoteForCmd(fileName)} {argsPart} >> {QuoteForCmd(_logFilePath)} 2>&1";

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                // 工作目录为空时保持默认，避免无效路径导致启动失败
                WorkingDirectory = string.IsNullOrWhiteSpace(workDir) ? null : workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 不重定向任何管道：面板退出后 dsh 不会因管道破裂而崩溃
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            // 设置兜底环境变量：NODE_PATH（依赖解析兜底）+ DSH_HOME（与面板使用一致的 dsh home）；
            // 若用户/系统已显式设置同名变量则尊重其值、不覆盖。
            ApplyStartupEnvironment(startInfo, nodePathValue, dshHomeValue);

            try
            {
                process = _launcher.Launch(startInfo);
            }
            catch (Exception ex)
            {
                // 启动失败（如可执行文件不存在）→ 回 Stopped，通过日志事件通知 UI
                AddLogLine($"启动 dsh 失败: {ex.Message}");
                SetState(DshProcessState.Stopped);
                return;
            }

            _process = process;
            process.Exited += OnProcessExited;

            // 启动日志文件增量轮询（替代原来的 stdout / stderr 管道逐行读取）
            StartLogPolling();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        // 锁外等待端口就绪（长轮询不占用生命周期锁，便于并发调用停止）
        if (process is not null)
            await WaitForPortAsync(process, port);
    }

    /// <summary>停止 dsh：结束进程树（或按 PID 结束外部实例），状态 Stopping → Stopped。</summary>
    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            // 已停止时无需处理
            if (_currentState == DshProcessState.Stopped)
                return;

            // 置停止标志，让端口轮询尽快退出
            _stopRequested = true;
            var process = _process;
            _process = null;
            SetState(DshProcessState.Stopping);
            AddLogLine("正在停止 dsh ...");

            try
            {
                if (process is not null)
                {
                    // 结束整个进程树（真实实现：taskkill /PID <pid> /T /F）
                    process.KillEntireTree();
                    // 等待进程完全退出，带超时兜底，避免 kill 失败导致永久挂起
                    await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(5)));

                    // 释放句柄：退订 Exited 并 Dispose Process，避免每次启停循环泄漏内核句柄
                    process.Dispose();
                }
                else if (IsExternalInstance)
                {
                    // 外部实例：本服务没有进程句柄，按端口反查出的 PID 结束整棵进程树
                    var pid = _externalProcessId ?? _listeningPidResolver(Port);
                    if (pid is int externalPid && externalPid > 0)
                    {
                        _processTreeKiller(externalPid);
                        AddLogLine($"已结束外部启动的 dsh 进程（PID {externalPid}）");
                    }
                    IsExternalInstance = false;
                    _externalProcessId = null;
                }
            }
            catch (Exception ex)
            {
                AddLogLine($"停止 dsh 失败: {ex.Message}");
            }

            // 日志轮询收尾：取消轮询并做最后一次增量读取
            await StopLogPollingAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        SetState(DshProcessState.Stopped);
        AddLogLine("dsh 已停止");
    }

    /// <summary>重启 dsh：先停止再启动。</summary>
    public async Task RestartAsync(string dshCommand, string workDir, int port)
    {
        await StopAsync();
        await StartAsync(dshCommand, workDir, port);
    }

    /// <summary>
    /// 终止「外部启动」的 dsh 实例（用户明确点击 + 确认后才由调用方触发），结束后状态回 Stopped，让「启动」恢复可用。
    ///
    /// 【为什么与 StopAsync 分开】StopAsync 语义是"停止本面板拉起的实例"，它会在失败时静默容忍（如句柄已失效）；
    /// 而外部实例不是面板拉起的、可能属于用户的其他脚本，误杀代价更高，因此：
    /// - 必须显式确认（确认框由调用方 UI 负责）；
    /// - 只有确实处于「外部实例」状态才动手，面板自启实例一律拒绝（提示改用「停止」）；
    /// - 以"端口是否真的释放"作为成功判据，避免 taskkill 静默失败却报告成功。
    /// 【为什么不复用 StopAsync】避免把"停止"与"终止外部"两种语义混在一起（误操作风险）。
    /// </summary>
    /// <param name="onOutput">可选的逐条进度回调（便于调用方展示实时反馈）；调用方若已订阅 LogReceived 请传 null，避免重复。</param>
    /// <param name="ct">取消令牌（页面销毁 / 用户取消）。</param>
    /// <returns>是否成功终止（端口已释放且状态回 Stopped）。</returns>
    public async Task<bool> TerminateExternalAsync(Action<string>? onOutput = null, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            // 前置校验：只处理"正在运行的外部实例"，其余情况给出明确中文原因，绝不误杀
            if (!IsExternalInstance)
            {
                return FailTerminate("当前没有检测到外部启动的 dsh 实例，无需终止", onOutput);
            }

            var pid = _externalProcessId ?? _listeningPidResolver(Port);
            if (pid is not int externalPid || externalPid <= 0)
            {
                return FailTerminate($"无法确定外部 dsh 实例的进程 PID（端口 {Port}），终止已取消", onOutput);
            }

            Report($"正在终止外部 dsh 实例（PID {externalPid}）...", onOutput);
            // 只结束这一棵进程树，不触碰其它进程（真实实现：taskkill /PID <pid> /T /F）
            var killed = _processTreeKiller(externalPid);

            // 成功判据取"端口真的释放"：taskkill 对已退出的进程会返回非 0，但仍算目标达成
            var released = false;
            for (var i = 0; i < TerminatePortReleasePollCount && !released; i++)
            {
                if (!_portProbe("127.0.0.1", Port, 300))
                {
                    released = true;
                    break;
                }
                await Task.Delay(LogPollIntervalMs, ct);
            }

            if (!released)
            {
                // 失败不改状态、不清外部标记：用户还能再次尝试或改用其它方式处理
                return FailTerminate(
                    killed
                        ? $"已向 PID {externalPid} 发送终止信号，但端口 {Port} 仍被占用（可能被其它实例占用），终止未完成"
                        : $"终止外部 dsh 实例失败（未能结束 PID {externalPid}，可能权限不足或进程已受保护）",
                    onOutput);
            }

            // 成功后清空外部标记并回 Stopped，界面「启动」按钮随之恢复可用
            IsExternalInstance = false;
            _externalProcessId = null;
            Report($"已终止外部启动的 dsh 实例（PID {externalPid}），现在可以点「启动」由面板重新拉起", onOutput);
            SetState(DshProcessState.Stopped);
            return true;
        }
        catch (OperationCanceledException)
        {
            return FailTerminate("终止外部 dsh 实例已取消", onOutput);
        }
        catch (Exception ex)
        {
            return FailTerminate($"终止外部 dsh 实例异常：{ex.Message}", onOutput);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>终止失败的统一收尾：写一条中文原因并返回 false（保证调用方总能拿到可展示的反馈）。</summary>
    private bool FailTerminate(string reason, Action<string>? onOutput)
    {
        Report($"❌ {reason}", onOutput);
        return false;
    }

    /// <summary>同时写入日志缓冲（UI 日志区）与可选的进度回调。</summary>
    private void Report(string line, Action<string>? onOutput)
    {
        AddLogLine(line);
        onOutput?.Invoke(line);
    }

    /// <summary>清空日志缓冲。</summary>
    public void ClearLogs()
    {
        while (_logBuffer.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 刷新外部实例接管状态：
    /// - 当前 Stopped 且端口已在监听 → 判定为外部启动的实例，切 Running 并标记 IsExternalInstance；
    /// - 当前是外部实例但端口不再监听 → 回 Stopped；
    /// - 面板自己启动的实例（IsExternalInstance 为 false）不受影响。
    /// </summary>
    public async Task RefreshExternalStateAsync(int port)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            Port = port;
            WebUrl = $"http://127.0.0.1:{port}";

            if (_currentState == DshProcessState.Stopped)
            {
                if (!_portProbe("127.0.0.1", port, 300))
                    return; // 端口未监听：保持 Stopped

                // 接管已在运行的外部实例
                _externalProcessId = _listeningPidResolver(port);
                IsExternalInstance = true;
                AddLogLine("检测到已有 dsh 实例在运行（外部启动），面板已接管监视");
                SetState(DshProcessState.Running);
                return;
            }

            // 外部实例消失（如被系统脚本停止）→ 回 Stopped
            if (IsExternalInstance && _currentState == DshProcessState.Running
                && !_portProbe("127.0.0.1", port, 300))
            {
                IsExternalInstance = false;
                _externalProcessId = null;
                AddLogLine("外部启动的 dsh 实例已退出");
                SetState(DshProcessState.Stopped);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 刷新 dsh 相关进程列表（只读探测，异常安全：任何失败都退化为空列表）。
    /// 主服务裁定：占用当前 <see cref="Port"/> 的进程优先；端口占用者不在 dsh 进程列表中时，
    /// 退回「命令行显式带 --port」的进程。结果写入 <see cref="Instances"/> 并通知 UI。
    /// </summary>
    public void RefreshInstances()
    {
        List<DshInstanceInfo> list;
        try
        {
            list = DshProcessProbe.Detect().ToList();
        }
        catch
        {
            list = new List<DshInstanceInfo>();
        }

        // 反查监听当前端口的 PID：dsh 若以默认端口启动，命令行里可能没有 --port，只能靠端口占用者识别
        var ownerPid = DshProcessProbe.FindPortOwner(Port);
        var ownerInList = ownerPid is int pid && list.Any(item => item.Pid == pid);

        foreach (var instance in list)
            instance.IsMain = ownerInList ? instance.Pid == ownerPid : instance.Port > 0;

        // 主服务排前面，其次按 PID 升序
        list.Sort((a, b) => a.IsMain != b.IsMain
            ? (a.IsMain ? -1 : 1)
            : a.Pid.CompareTo(b.Pid));

        Instances = list;
        OnPropertyChanged(nameof(Instances));
    }

    /// <summary>
    /// 从 netstat -ano 输出中解析监听指定端口的进程 PID：
    /// 仅匹配本地地址为 127.0.0.1:&lt;port&gt; / 0.0.0.0:&lt;port&gt; / [::]:&lt;port&gt; 且状态为 LISTENING 的行，
    /// 取最后一列 PID；无匹配或解析失败返回 null。
    /// 注意：生产路径已改为 <see cref="DshProcessProbe.FindPortOwner"/>（Win32 API）反查端口占用者，
    /// 本方法仅作为纯文本解析工具保留（便于独立测试与外部输出核查）。
    /// </summary>
    public static int? ParseListeningProcessId(string? netstatOutput, int port)
    {
        if (string.IsNullOrWhiteSpace(netstatOutput))
            return null;

        var expectedPort = port.ToString();
        foreach (var rawLine in netstatOutput.Split('\n'))
        {
            // 列：协议 | 本地地址 | 外部地址 | 状态 | PID
            var columns = rawLine.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5)
                continue; // 表头 / 空行 / 畸形行：列数不足，忽略

            if (!string.Equals(columns[3], "LISTENING", StringComparison.OrdinalIgnoreCase))
                continue; // 非监听状态（ESTABLISHED / TIME_WAIT 等）一律忽略

            if (!IsWatchedLocalEndpoint(columns[1], expectedPort))
                continue;

            return int.TryParse(columns[^1], out var pid) ? pid : null;
        }
        return null;
    }

    /// <summary>
    /// 从最近日志行里识别 dsh 启动失败的常见模式，返回一条中文可读解释（由调用方作为普通日志行追加）；
    /// 未命中任何已知模式时返回 null（不做任何事，保持原有日志体验）。
    ///
    /// 【为什么要做】dsh 启动失败时日志区只有英文堆栈，普通用户无法判断到底是
    /// "依赖正在被插件安装重建"、"端口被占用"还是"没装 Node.js"。这里把最常见的三类原因翻译成中文并给出下一步建议。
    /// 【为什么是纯静态函数】便于单元测试直接喂日志文本断言，不需要真实进程 / 文件 / 网络。
    /// </summary>
    /// <param name="recentLogLines">最近若干行 dsh 输出（顺序无关，内部整体匹配）。</param>
    /// <param name="port">本次启动使用的端口（端口占用提示需要）。</param>
    public static string? DiagnoseStartupFailure(IReadOnlyList<string> recentLogLines, int port)
    {
        if (recentLogLines is null || recentLogLines.Count == 0)
            return null;

        var joined = string.Join("\n", recentLogLines.Where(l => !string.IsNullOrWhiteSpace(l)));
        if (joined.Length == 0)
            return null;

        // 1) 依赖不完整：报找不到 @deepseek-ai/* 的包（本次真实故障就是这个），或 ESM 的 ERR_MODULE_NOT_FOUND
        if ((joined.Contains("Cannot find package", StringComparison.OrdinalIgnoreCase)
                && joined.Contains("@deepseek-ai/", StringComparison.OrdinalIgnoreCase))
            || joined.Contains("ERR_MODULE_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return $"💡 诊断：dsh 依赖不完整（找不到 {ExtractMissingPackage(joined)}）。" +
                   "常见于插件安装/更新时 pnpm 正在重建 node_modules，或依赖被损坏。" +
                   "面板已尝试 NODE_PATH 兜底；若仍失败，可能是 npm 缓存正在重建（等 1 分钟重试），" +
                   "或到首页「环境检测」点「修复依赖」。";
        }

        // 2) 端口被占用（Node 抛 EADDRINUSE / 系统提示 address already in use）
        if (joined.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
            || joined.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return $"💡 诊断：端口 {port} 已被占用，可能已有一个 dsh 在运行。可先点「停止」，或到设置页更换端口。";
        }

        // 3) 找不到 Node.js：cmd 的中文提示 / sh 的英文提示。
        //    英文 "is not recognized" 太宽泛，必须同时出现 "node" 才算命中，避免把其它命令缺失误判成 Node 缺失。
        var nodeMissing = joined.Contains("'node' 不是内部或外部命令")
            || joined.Contains("node: not found", StringComparison.OrdinalIgnoreCase)
            || (joined.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
                && joined.Contains("node", StringComparison.OrdinalIgnoreCase));
        if (nodeMissing)
        {
            return "💡 诊断：未找到 Node.js。dsh 需要 Node.js 运行，请到首页「环境检测」里按引导安装。";
        }

        return null;
    }

    /// <summary>从日志里提取第一个被报缺失的 @deepseek-ai 包名；提取不到时用通配写法。</summary>
    private static string ExtractMissingPackage(string log)
    {
        var match = MissingPackageRegex.Match(log);
        return match.Success ? match.Value : "@deepseek-ai/*";
    }

    /// <summary>本地地址是否为受关注的监听端点：127.0.0.1 / 0.0.0.0 / [::] 且端口匹配。</summary>
    private static bool IsWatchedLocalEndpoint(string localAddress, string expectedPort)
    {
        var separator = localAddress.LastIndexOf(':');
        if (separator <= 0)
            return false;

        if (!string.Equals(localAddress[(separator + 1)..], expectedPort, StringComparison.Ordinal))
            return false;

        var host = localAddress[..separator];
        return host is "127.0.0.1" or "0.0.0.0" or "[::]";
    }

    /// <summary>
    /// 结束指定 PID 的整棵进程树：
    /// Windows 使用 taskkill /PID &lt;pid&gt; /T /F；其他平台使用 .NET 原生整树终止。
    /// 返回 true 仅代表"确认杀死"，false 代表失败或未能确认。异常一律吞掉返回 false。
    /// </summary>
    public static bool KillProcessTree(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                // 【为什么不能无条件 return true】taskkill 会失败（权限不足、进程受保护、
                // 找不到该进程等）并以非 0 退出；以前把它当成功，调用方就会把"终止失败"
                // 粉饰成"已发送终止信号"，用户等半天端口还占着也不知道为什么。
                if (killer is null)
                    return false;

                // 必须带超时：taskkill 卡住（如目标进程处于不可中断状态）时不能拖死调用线程
                if (!killer.WaitForExit(TerminateTimeoutMs))
                {
                    try { killer.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                    return false;
                }

                return killer.ExitCode == 0;
            }

            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>taskkill 等待上限（毫秒）：超时即视为未能确认终止。</summary>
    private const int TerminateTimeoutMs = 10_000;

    /// <summary>
    /// 探测指定主机端口是否已开放（TCP 连接成功即视为就绪）。
    /// 抽成静态方法，便于独立测试与复用。
    /// </summary>
    public static bool IsPortOpen(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var client = new TcpClient();
            // 异步连接 + 同步超时等待：成功连接返回 true，超时 / 拒绝返回 false
            return client.ConnectAsync(host, port).Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            // 目标拒绝连接（端口未监听）等异常一律视为未就绪
            return false;
        }
    }

    /// <summary>
    /// 轮询等待 Web 端口就绪：就绪 → Running；进程提前退出 → 意外退出处理；超过启动超时 → 回 Stopped 并给出中文诊断。
    /// </summary>
    private async Task WaitForPortAsync(IProcessHandle process, int port)
    {
        var startedAt = _clock();
        while (!_stopRequested)
        {
            if (_portProbe("127.0.0.1", port, 300))
            {
                AddLogLine($"dsh Web 端口 {port} 已就绪：{WebUrl}");
                SetState(DshProcessState.Running);
                return;
            }

            if (process.HasExited)
            {
                // 端口就绪前进程已退出 → 启动失败 / 意外退出
                HandleProcessExit(process.ExitCode);
                return;
            }

            // 超时保护：dsh 进程既没监听端口、也没退出（如依赖正在被 pnpm 重建 / 启动卡住）时，
            // 状态会永久停在「启动中」。到达上限后回 Stopped 并给出诊断，绝不静默卡死。
            if (_clock() - startedAt >= _startupTimeout)
            {
                HandleStartupTimeout(process, port);
                return;
            }

            await Task.Delay(200);
        }
        // 主动停止：状态由 StopAsync 负责，此处静默返回
    }

    /// <summary>
    /// 启动超时收尾：记录日志（若进程仍存活则明确说明）、追加中文诊断、恢复到 Stopped。
    ///
    /// 【为什么不在这里杀进程】超时只说明"迟迟未监听端口"，此时 dsh 很可能正在被 pnpm 重建依赖，
    /// 强行结束会中断重建、并可能损坏 node_modules；因此面板只如实告知 + 回退状态，
    /// 把"是否清理"的决定权留给用户（可到「进程列表」按 PID 手动结束）。这也符合"不做静默副作用"的约束。
    /// </summary>
    private void HandleStartupTimeout(IProcessHandle process, int port)
    {
        var seconds = _startupTimeout.TotalSeconds;

        // 统一给出「启动超时（N 秒）：端口 X 未就绪」这一条可检索的结论，界面日志与用户反馈都能对上
        AddLogLine($"⚠️ 启动超时（{seconds:F0} 秒）：端口 {port} 未就绪。");

        if (!process.HasExited)
        {
            AddLogLine($"dsh 进程（PID {process.Id}）仍在运行但未监听端口。" +
                       "面板不会自动结束它（避免误杀正在装配插件的进程）；如需清理，可在「进程列表」中按 PID 手动结束。");
        }

        // 优先用最近日志里的具体错误（依赖缺失 / 端口占用 / 缺 Node）给出精确诊断；
        // 识别不到时退回"三类可能原因"的通用诊断。绝不抛异常、绝不静默。
        var hint = DiagnoseStartupFailure(RecentLogLines(), port)
            ?? BuildStartupTimeoutHint(port, _startupTimeout);
        AddLogLine(hint);

        // 恢复到 Stopped，让用户可再次操作；若进程仍在跑，后续刷新会把它当作外部实例重新接管
        SetState(DshProcessState.Stopped);
    }

    /// <summary>
    /// 启动超时的通用中文诊断文案（纯静态，便于单测）。
    /// 为什么给"三类可能原因"：超时意味着 dsh 既没退出也没监听端口，最常见的三种解释是
    /// 依赖重建 / 端口被占 / 启动失败；普通用户需要明确的下一步动作，而不是干等。
    /// </summary>
    /// <param name="port">本次启动使用的端口。</param>
    /// <param name="timeout">已等待的超时时长。</param>
    public static string BuildStartupTimeoutHint(int port, TimeSpan timeout)
        => $"💡 启动超时（已等待 {timeout.TotalSeconds:F0} 秒，端口 {port} 仍不可用）。" +
           "可能原因：① 插件依赖正在被 pnpm 重建（可能是依赖正在重建，请等 1 分钟重试）；" +
           "② 端口被其他程序占用；" +
           "③ dsh 启动失败（请到「日志」页查看详细错误）。" +
           "若重试仍失败，到首页「环境检测」点「修复依赖」。";

    /// <summary>
    /// 启动进度百分比估算（纯静态，便于单测）：按"已用秒数 / 超时秒数"线性推进。
    ///
    /// 【为什么上限是 95%】进度受超时时间驱动，无法真正预知 dsh 何时监听端口：
    /// 若让它跑到 100%，用户会以为"已经好了但网页还是打不开"；留最后 5% 表示"即将完成"，
    /// 只有端口真正就绪（进入 Running）时才由界面直接置 100。
    /// 【边界】0 秒 → 0；达到或超过超时 → 95（不会超过上限）；超时非正数时同样按上限处理。
    /// </summary>
    /// <param name="elapsedSeconds">已等待秒数。</param>
    /// <param name="timeoutSeconds">启动超时秒数。</param>
    public static int EstimateStartProgress(double elapsedSeconds, double timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
            return 95; // 无有效超时（如测试注入零超时）：直接视为已到上限
        if (elapsedSeconds <= 0)
            return 0;

        var ratio = elapsedSeconds / timeoutSeconds;
        if (ratio >= 1)
            return 95;

        return (int)Math.Clamp(ratio * 95, 0, 95);
    }

    /// <summary>
    /// 是否允许「接管并重启」：仅当处于运行中、且该实例是外部启动（不由本面板管理）。
    /// 抽成纯静态便于单测。界面据此在外部实例状态下把主按钮切换为「接管并重启」，
    /// 解决"端口被外部实例占用 → 启动按钮被禁用 → 用户误以为点启动无反应"的问题。
    /// </summary>
    public static bool CanTakeOverAndRestart(DshProcessState state, bool isExternalInstance)
        => state == DshProcessState.Running && isExternalInstance;

    // ==================== 日志文件增量采集 ====================

    /// <summary>清空日志文件并确保目录存在；失败时仅记录日志，不阻断启动。</summary>
    private void PrepareLogFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_logFilePath, string.Empty);
        }
        catch (Exception ex)
        {
            AddLogLine($"准备日志文件失败（{_logFilePath}）: {ex.Message}");
        }
    }

    /// <summary>启动日志文件轮询：从 0 偏移开始，按固定间隔读取新增内容。</summary>
    private void StartLogPolling()
    {
        lock (_logPollLock)
        {
            _logReadOffset = 0;
            _pendingLineRemainder = string.Empty;
            _logPollCts = new CancellationTokenSource();
            var token = _logPollCts.Token;

            _logPollingTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        ReadLogFileIncrement(flushRemainder: false);
                        await Task.Delay(LogPollIntervalMs, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消：退出循环后做收尾读取
                }
                // 收尾：把进程退出前写入的最后增量与残留的未完结行刷出
                ReadLogFileIncrement(flushRemainder: true);
            }, token);
        }
    }

    /// <summary>取消并等待日志轮询任务结束（含最后一次增量读取）。</summary>
    private async Task StopLogPollingAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_logPollLock)
        {
            cts = _logPollCts;
            task = _logPollingTask;
            _logPollCts = null;
            _logPollingTask = null;
        }

        if (cts is null)
            return;

        cts.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* 轮询任务异常不应影响停止流程 */ }
        }
        cts.Dispose();
    }

    /// <summary>读取日志文件自上次偏移以来的新增内容并按行转发；flushRemainder 为 true 时刷出未完结的尾行。</summary>
    private void ReadLogFileIncrement(bool flushRemainder)
    {
        if (!File.Exists(_logFilePath))
            return;

        try
        {
            // FileShare.ReadWrite：允许与 cmd 的写入句柄并存，避免读取被独占锁阻塞
            using var stream = new FileStream(
                _logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // 文件被截断 / 重写时把偏移复位，避免读取位置越界
            if (stream.Length < _logReadOffset)
                _logReadOffset = 0;

            var available = stream.Length - _logReadOffset;
            if (available <= 0)
            {
                if (flushRemainder) FlushRemainderLine();
                return;
            }

            stream.Seek(_logReadOffset, SeekOrigin.Begin);
            // 单轮读取上限受 int 约束；超长增量会在下一轮继续读取
            var buffer = new byte[(int)Math.Min(available, int.MaxValue)];
            var read = stream.Read(buffer, 0, buffer.Length);
            _logReadOffset += read;
            AppendLogText(_outputEncoding.GetString(buffer, 0, read), flushRemainder);
        }
        catch
        {
            // 文件暂时被占用 / 读取竞争：略过本轮，下次轮询重试
        }
    }

    /// <summary>把一段新增文本按行拆分转发：最后一段若无换行符则视为不完整行，留待下次拼接。</summary>
    private void AppendLogText(string text, bool flushRemainder)
    {
        var combined = _pendingLineRemainder + text;
        var lines = combined.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            AddLogLine(lines[i].TrimEnd('\r'));
        }

        _pendingLineRemainder = lines[^1];
        if (flushRemainder)
            FlushRemainderLine();
    }

    /// <summary>刷出残留的未完结尾行（进程退出后调用，确保最后一行不丢失）。</summary>
    private void FlushRemainderLine()
    {
        if (_pendingLineRemainder.Length == 0)
            return;
        AddLogLine(_pendingLineRemainder.TrimEnd('\r'));
        _pendingLineRemainder = string.Empty;
    }

    // ==================== 进程退出处理 ====================

    /// <summary>进程退出统一处理入口（Exited 事件与端口轮询两路汇合）。</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is IProcessHandle handle)
            HandleProcessExit(handle.ExitCode);
    }

    private void HandleProcessExit(int exitCode)
    {
        // 幂等保护：一个进程实例只处理一次退出
        if (Interlocked.Exchange(ref _exitHandled, 1) == 1)
            return;

        // 主动停止时不在此处理（状态由 StopAsync 驱动，避免重复通知）
        if (_stopRequested)
            return;

        // 【为什么要探一次端口】面板启动 dsh 走的是 cmd.exe 包装进程，Exited 事件来自这个包装器。
        // 包装器退出后由它拉起的 node 子进程有可能仍在运行（例如 shim 提前返回、或 node 被重新挂载），
        // 此时端口仍在监听。旧实现无论哪种情况都记"dsh 进程已退出（退出码 N）"，并把 node 的退出码
        // 当成 dsh 的退出码展示 —— 用户看到"已退出"，可端口还占着，日志里给的退出码也不是 node 的。
        var stillListening = false;
        try
        {
            stillListening = _portProbe("127.0.0.1", Port, 300);
        }
        catch
        {
            // 探测本身失败时按"已退出"处理，不影响主流程
        }

        if (stillListening)
        {
            AddLogLine($"启动器进程已退出（退出码 {exitCode}），但端口 {Port} 仍在监听："
                + "dsh 服务仍由另一个进程（node）提供服务，面板无法再管理该进程；"
                + "如需彻底停止，请在首页使用「终止外部实例」或手工结束该 node 进程。");
        }
        else
        {
            AddLogLine($"dsh 进程已退出（退出码 {exitCode}）");
        }

        SetState(DshProcessState.Stopped);
        ProcessExited?.Invoke(this, EventArgs.Empty);

        // 收尾读取日志 + 启动失败诊断。
        // 顺序很关键：必须先等日志轮询把"进程退出前写入的最后几行"读出来，再做错误模式匹配，
        // 否则会漏掉真正的错误行（dsh 先写错误再退出，而轮询间隔是 300ms）。
        // 该任务不影响上面的状态机（状态已同步切换完毕），只是末尾补一条中文解释。
        _ = StopLogPollingAndDiagnoseAsync();
    }

    /// <summary>先等日志轮询收尾（含最后一次增量读取），再匹配常见启动失败模式并追加中文解释。</summary>
    private async Task StopLogPollingAndDiagnoseAsync()
    {
        try
        {
            await StopLogPollingAsync().ConfigureAwait(false);

            var hint = DiagnoseStartupFailure(RecentLogLines(), Port);
            if (hint is not null)
                AddLogLine(hint);
        }
        catch
        {
            // 诊断属于锦上添花：任何异常都不得影响进程状态机与界面
        }
    }

    /// <summary>取日志缓冲末尾的若干行（诊断只需看最近输出）。</summary>
    private IReadOnlyList<string> RecentLogLines(int max = 100)
    {
        var all = _logBuffer.ToArray();
        return all.Length <= max ? all : all[^max..];
    }

    /// <summary>写入一行日志：入队缓冲（带上限裁剪）并触发 LogReceived。</summary>
    private void AddLogLine(string line)
    {
        _logBuffer.Enqueue(line);
        while (_logBuffer.Count > MaxLogLines)
            _logBuffer.TryDequeue(out _);

        LogReceived?.Invoke(this, line);
    }

    /// <summary>切换状态：更新 CurrentState（通知属性变化）并触发 StateChanged。</summary>
    private void SetState(DshProcessState newState)
    {
        if (_currentState == newState)
            return;

        _currentState = newState;

        // 【为什么在这里调度 UI 线程】SetState 的调用点既有 UI 线程（按钮命令），
        // 也有后台线程（日志轮询任务、端口探测循环、Exited 事件）。
        // WPF 绑定自身对 PropertyChanged 宽容，但 StateChanged 的订阅方会直接改 UI 对象，
        // 在后台线程触发就是跨线程访问 → 随机抛 InvalidOperationException。
        RunOnUiThread(() =>
        {
            OnPropertyChanged(nameof(CurrentState));
            StateChanged?.Invoke(this, newState);
        });
    }

    /// <summary>
    /// 把状态通知调度到 UI 线程；无 Application（单元测试 / 控制台宿主）时直接执行。
    /// 与 ViewModels.HomeViewModel.RunOnUiThread 同一策略：判空 + 判关闭 + 已在本线程则不排队。
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
