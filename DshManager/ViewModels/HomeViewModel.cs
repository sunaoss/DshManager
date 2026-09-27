using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 仪表盘首页视图模型：
/// 汇总安装状态、进程状态、dsh 进程列表与实时日志，提供启动 / 停止 / 重启 / 打开网页 / 安装 / 更新等快捷操作。
/// 进程与安装服务的事件均在后台线程触发，回调内统一经 Dispatcher 调度回 UI 线程再更新界面。
/// </summary>
public partial class HomeViewModel : ObservableObject, IDisposable
{
    /// <summary>面板设置文件名（与设置页共用同一份 settings.json，保证端口配置跨页面一致）。</summary>
    private const string SettingsFileName = "settings.json";

    /// <summary>进程列表与运行状态的定时刷新间隔。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>「已用时」刷新间隔：启动期间每秒刷新一次，让用户看到确实在推进。</summary>
    private static readonly TimeSpan StartupTickInterval = TimeSpan.FromSeconds(1);

    /// <summary>接管并重启时，等待外部实例释放端口的最长秒数（轮询探测，避免无限等待）。</summary>
    private const int PortReleaseTimeoutSeconds = 15;

    /// <summary>
    /// 面板发起的启动成功后，是否自动打开一次 dsh 网页。
    /// 【为什么需要】启动 dsh 时会带 --no-open（见 DshProcessService.NoOpenArgument），dsh 不再自己弹浏览器；
    /// 由面板在端口就绪后补这一次打开，既保证"该开的网页还是会开"，又避免 dsh 提前弹窗干扰就绪判定。
    /// 【为什么用常量】暂不新增设置项，后续若要关闭直接改这里（或后续再接到设置页）。
    /// </summary>
    // 面板启动成功后自动打开网页的开关。用 static readonly（而非 const）：
    // 作为编译期常量时 `if (!AutoOpen...) return;` 会被判定为不可达代码而产生 CS0162 警告。
    private static readonly bool AutoOpenWebAfterPanelStart = true;

    private readonly DshDiscoveryService _discovery;
    private readonly DshProcessService _process;
    private readonly DshInstallService _install;
    private readonly JsonStoreService _store;

    /// <summary>环境检测服务（Node / npm / dsh 本体 / 依赖完整性 / 端口 / 配置文件）。</summary>
    private readonly EnvironmentCheckService _envCheck;

    /// <summary>便携版 Node 运行时服务（Node.js 缺失时一键下载安装，供启动 dsh 使用）。</summary>
    private readonly NodeRuntimeService _nodeRuntime;

    /// <summary>定时刷新器：周期性刷新进程列表与外部实例状态。</summary>
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>启动计时器：仅在「启动中」期间运行，每秒刷新「已用时」文本。</summary>
    private readonly DispatcherTimer _startupTimer;

    /// <summary>本次启动的起始时刻（用于计算已用时）。</summary>
    private DateTime _startupStartedAt;

    /// <summary>当前生效的 dsh Web 端口（取自 AppSettings.Port，不再硬编码）。</summary>
    private int _port = DshProcessService.DefaultPort;

    /// <summary>刷新进行中标志：WMI 探测较慢时跳过本轮，避免请求堆积。</summary>
    private bool _isRefreshing;

    private bool _disposed;

    /// <summary>应用实例引用，用于在退出时停止刷新计时器。</summary>
    private readonly Application? _application;

    /// <summary>发现结果（启动器路径 / 数据目录 / 版本等），扫描完成后填充。</summary>
    [ObservableProperty]
    private DshInfo? info;

    /// <summary>是否已安装 dsh（驱动安装 / 启动 / 更新等命令的可用性）。</summary>
    [ObservableProperty]
    private bool isInstalled;

    /// <summary>安装状态正文：已安装时 "v{版本}"，未安装时 "未安装"。</summary>
    [ObservableProperty]
    private string versionText = "检测中…";

    /// <summary>进程状态文本：已停止 / 启动中 / 运行中 / 停止中。</summary>
    [ObservableProperty]
    private string processStatusText = "已停止";

    /// <summary>进程状态灯与状态文本画刷（随状态变色）。</summary>
    [ObservableProperty]
    private Brush processStatusBrush;

    /// <summary>
    /// 当前运行的实例是否为「外部启动」（不由面板拉起，但占用了端口）。
    /// 驱动两处界面：主按钮切换为「接管并重启」、状态卡追加说明文本。
    /// </summary>
    [ObservableProperty]
    private bool isExternalInstance;

    /// <summary>是否处于「启动中」：驱动启动进度条与阶段文案的显隐。</summary>
    [ObservableProperty]
    private bool isStarting;

    /// <summary>本次启动已等待的秒数（每秒刷新一次，让用户看到确实在推进）。</summary>
    [ObservableProperty]
    private int startElapsedSeconds;

    /// <summary>
    /// 启动进度百分比（0~100）：按「已用秒数 / 启动超时秒数」估算，上限 95%，进入 Running 时才置 100。
    /// 为什么不直接跑到 100：进度受超时驱动、无法预知端口何时就绪，留 5% 表达"即将完成"，
    /// 避免"进度条满了却还打不开网页"造成的困惑。
    /// </summary>
    [ObservableProperty]
    private int startProgressPercent;

    /// <summary>启动阶段文案（含已等待秒数与超时提醒），仅在启动中非空。</summary>
    [ObservableProperty]
    private string startPhaseText = string.Empty;

    /// <summary>安装 / 更新进行中标志（期间禁用相关命令，防止重复触发）。</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>底部状态提示消息（安装 / 启动等操作的结果反馈）。</summary>
    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>dsh Web 地址（供进程状态卡展示）。</summary>
    [ObservableProperty]
    private string webUrlText = string.Empty;

    /// <summary>dsh Web 端口（供进程状态卡展示）。</summary>
    [ObservableProperty]
    private string portText = string.Empty;

    /// <summary>是否存在 dsh 相关进程（驱动「未检测到 dsh 进程」占位提示）。</summary>
    [ObservableProperty]
    private bool hasInstances;

    /// <summary>推荐使用的 dsh 安装位置（多来源探测结果中排序第一的可用项）；未找到为 null。</summary>
    [ObservableProperty]
    private DshInstallation? recommendedInstallation;

    /// <summary>安装位置探测是否已完成（用于区分"检测中"与"确实没找到"，避免提示文案闪烁）。</summary>
    [ObservableProperty]
    private bool installScanDone;

    /// <summary>是否找到了推荐安装位置（驱动界面显示位置详情 / 未找到提示）。</summary>
    [ObservableProperty]
    private bool hasRecommendedInstallation;

    /// <summary>环境检测结果摘要，如「6 项检查，2 项需要处理」。</summary>
    [ObservableProperty]
    private string envSummaryText = "环境检测中…";

    /// <summary>环境检测是否进行中（驱动「重新检测」按钮禁用与文案）。</summary>
    [ObservableProperty]
    private bool isEnvChecking;

    /// <summary>修复命令执行中的进度提示文本（如「正在执行：…」）。</summary>
    [ObservableProperty]
    private string envFixStatusText = string.Empty;

    /// <summary>修复命令的实时输出（逐行累积，最多保留末尾若干字符）。</summary>
    [ObservableProperty]
    private string envFixOutputText = string.Empty;

    /// <summary>是否有修复命令输出可展示（驱动输出区显隐）。</summary>
    [ObservableProperty]
    private bool hasEnvFixOutput;

    /// <summary>修复命令是否正在执行（期间禁用修复按钮，避免重复触发有副作用的命令）。</summary>
    [ObservableProperty]
    private bool isEnvFixRunning;

    /// <summary>
    /// 「一键安装全部缺失项」是否可用：存在 1 个以上命令型修复项、且当前没有修复在执行。
    /// 只有 0/1 项时批量入口没有必要（单项自己有按钮），故按钮禁用。
    /// </summary>
    [ObservableProperty]
    private bool canInstallAllMissing;

    /// <summary>
    /// 首页「启动方式」的展示文本：显示真正会被执行的命令（<see cref="DshInfo.LaunchCommand"/>）。
    /// 为什么不显示 <see cref="DshInfo.LauncherPath"/>：那只是"按候选顺序找到的第一个 dsh 启动文件"，
    /// 本机实测它可能是指向不存在路径的坏 shim（点启动时并不会用它），
    /// 而实际启动走的是 LaunchCommand（推荐位置的 node bin.js / 可用启动器 / npx），二者并不一致，
    /// 直接展示 LauncherPath 会误导用户。为空时给出明确提示，避免用户以为界面出错。
    /// </summary>
    public string LaunchCommandText
        => string.IsNullOrWhiteSpace(Info?.LaunchCommand) ? "未找到可用的启动方式" : Info!.LaunchCommand;

    /// <summary>Info 刷新后同步通知界面更新派生的「启动方式」文本。</summary>
    partial void OnInfoChanged(DshInfo? value) => OnPropertyChanged(nameof(LaunchCommandText));

    // ==================== 派生展示文本（按钮 ToolTip / 外部实例位置） ====================
    // 这些都是"从状态推导出来"的只读文本，统一在 RefreshDerivedUiText 里发通知刷新；
    // 关键目的：按钮被禁用时必须能从 ToolTip 看到"为什么不能点"，避免用户误以为软件"点了没反应"。

    /// <summary>
    /// 「启动」按钮的动态文案（只读派生属性，XAML 绑定必须显式 Mode=OneWay）。
    /// 随进程状态 / 是否外部实例变化，让用户"一看就知道点了会怎样"，避免"点了没反应"的错觉。
    /// </summary>
    public string StartButtonText => BuildStartButtonText(_process.CurrentState, _process.IsExternalInstance);

    /// <summary>
    /// 「启动」按钮的 ToolTip：随状态下发"点击后会发生什么"。
    /// 【为什么必须写清】外部实例在场时按钮不再置灰（本次修复），
    /// 必须说明"点击=直接打开网页"以及"想由面板重新拉起的出路（先点「终止该实例」）"，否则用户不知如何让面板接管。
    /// </summary>
    public string StartButtonToolTip
    {
        get
        {
            if (IsBusy) return "有安装 / 更新任务正在执行，请稍候";
            // 外部实例本质是"运行中"，优先按来源给出最具体的说明（含"如何由面板重新拉起"的操作路径）
            if (_process.IsExternalInstance)
                return "检测到已有 dsh 在运行（外部启动 / 非面板启动），点击将直接打开网页；如需由面板重新拉起，请先点「终止该实例」";
            return _process.CurrentState switch
            {
                DshProcessState.Starting => "正在启动中，请稍候",
                DshProcessState.Running => "dsh 已在运行（面板启动），点击将打开网页",
                DshProcessState.Stopping => "正在停止，请稍候",
                _ => IsInstalled ? "启动 dsh（将由面板拉起一个新实例）" : "尚未安装 dsh，点击将提示安装",
            };
        }
    }

    /// <summary>
    /// 「停止」按钮的 ToolTip：外部实例禁用「停止」，并明确指向「终止该实例」。
    /// 【为什么必须写清出路】外部实例下「停止 / 重启」都被禁用，若不说清"改用哪个按钮"，
    /// 用户会陷入"什么都不能点"的死路（同样是"点了没反应"的变体）。
    /// </summary>
    public string StopButtonToolTip => _process.IsExternalInstance
        ? "该实例由外部启动，不能用「停止」（避免误杀他人实例）；如需结束该外部实例，请用「终止该实例」"
        : _process.CurrentState is DshProcessState.Starting or DshProcessState.Running
            ? "停止面板启动的 dsh 实例"
            : "当前没有由面板启动的 dsh 实例";

    /// <summary>「重启」按钮的 ToolTip：外部实例同样需先终止（并写明出路），启动过程中不可重启。</summary>
    public string RestartButtonToolTip
    {
        get
        {
            if (IsBusy) return "有安装 / 更新任务正在执行，请稍候";
            if (_process.IsExternalInstance)
                return "外部实例不能用「重启」；如需结束该外部实例，请用「终止该实例」，再由面板「启动」";
            if (_process.CurrentState == DshProcessState.Starting) return "启动过程中不能重启，请等启动完成或先「停止」";
            return IsInstalled ? "停止并重新启动 dsh" : "尚未安装 dsh，请先点「安装」";
        }
    }

    /// <summary>「打开网页」按钮的 ToolTip。</summary>
    public string OpenWebButtonToolTip => _process.CurrentState == DshProcessState.Running
        ? "用默认浏览器打开 dsh 网页"
        : "dsh 未运行，暂无网页可打开（可先「启动」）";

    /// <summary>「终止该实例」按钮的 ToolTip。</summary>
    public string TerminateExternalButtonToolTip => "结束这个外部启动的 dsh 实例（会先弹出确认框）";

    /// <summary>外部实例的进程位置（PID + 命令行）完整文本；无外部实例或读不到时为派生占位。</summary>
    public string ExternalInstanceLocationFull
    {
        get
        {
            if (_process.ExternalProcessId is not int pid)
                return string.Empty;

            var commandLine = _process.FindExternalInstance()?.CommandLine;
            return string.IsNullOrWhiteSpace(commandLine)
                ? $"PID {pid}（未能读取命令行，可在「进程列表」核对）"
                : $"PID {pid} · {commandLine}";
        }
    }

    /// <summary>界面展示用的进程位置（过长时中间省略）；完整内容由 ToolTip 呈现。</summary>
    public string ExternalInstanceLocation => TruncateMiddle(ExternalInstanceLocationFull, 120);

    /// <summary>是否展示进程位置行：仅外部实例且确实拿到了位置信息时展示。</summary>
    public bool HasExternalInstanceLocation => IsExternalInstance && ExternalInstanceLocationFull.Length > 0;

    /// <summary>过长文本中间省略，保留首尾（PID 在首、关键路径通常在尾）。</summary>
    private static string TruncateMiddle(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;

        var half = maxLength / 2;
        return $"{text[..half]} … {text[^half..]}";
    }

    /// <summary>刷新所有派生展示文本的绑定（状态 / 外部实例 / 忙标志变化后调用）。</summary>
    private void RefreshDerivedUiText()
    {
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StartButtonToolTip));
        OnPropertyChanged(nameof(StopButtonToolTip));
        OnPropertyChanged(nameof(RestartButtonToolTip));
        OnPropertyChanged(nameof(OpenWebButtonToolTip));
        OnPropertyChanged(nameof(TerminateExternalButtonToolTip));
        OnPropertyChanged(nameof(ExternalInstanceLocationFull));
        OnPropertyChanged(nameof(ExternalInstanceLocation));
        OnPropertyChanged(nameof(HasExternalInstanceLocation));
    }

    /// <summary>
    /// 日志区是否自动滚动到底部（默认开启）。
    /// 关闭后新日志不再自动打断，方便用户向上翻看历史；由 HomeView 的滚动逻辑读取。
    /// </summary>
    [ObservableProperty]
    private bool isAutoScrollEnabled = true;

    /// <summary>实时日志集合（与 DshProcessService 缓冲保持一致，供界面绑定）。</summary>
    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>dsh 相关进程列表（PID / 角色 / 内存 / CPU / 启动时间），由定时刷新填充。</summary>
    public ObservableCollection<DshInstanceInfo> Instances { get; } = new();

    /// <summary>环境检测结果集合（检测项 + 修复动作），由「重新检测」与启动时自动检测填充。</summary>
    public ObservableCollection<EnvCheckItemViewModel> EnvironmentChecks { get; } = new();

    public HomeViewModel()
    {
        // 取全局注册表中的共享服务，跨页面保持进程状态与日志一致
        _discovery = AppServices.Discovery;
        _process = AppServices.Process;
        _install = AppServices.Install;
        _store = AppServices.Store;

        // 端口来自应用配置（设置页可改），不再是硬编码常量
        _port = ReadConfiguredPort();

        // 便携版 Node 与环境检测共用同一实例：检测项里的便携版信息与实际安装目标保持一致
        _nodeRuntime = new NodeRuntimeService();

        // 环境检测服务与首页共用同一端口；检测项全部只读，不做任何有副作用的动作
        _envCheck = new EnvironmentCheckService(port: _port, nodeRuntime: _nodeRuntime);

        // 初始化状态显示：默认"已停止"灰色，展示配置端口对应的 Web 地址
        ProcessStatusBrush = GetBrush("TextMutedBrush");
        WebUrlText = $"http://127.0.0.1:{_port}";
        PortText = _port.ToString();

        // 先把进程服务缓冲中的历史日志灌入界面集合（可能跨页面保留）
        foreach (var line in _process.LogLines)
        {
            AddLog(line);
        }

        // 订阅进程事件（后台线程触发，回调内调度到 UI 线程）
        _process.LogReceived += OnLogReceived;
        _process.StateChanged += OnStateChanged;

        // 订阅安装 / 更新事件（子进程线程触发）
        _install.OutputReceived += OnInstallOutput;
        _install.Completed += OnInstallCompleted;

        // 定时刷新：DispatcherTimer 的 Tick 在 UI 线程触发，刷新结果可直接更新界面
        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        // 启动进度计时器：仅「启动中」期间运行，每秒推进进度条与阶段文案
        _startupTimer = new DispatcherTimer { Interval = StartupTickInterval };
        _startupTimer.Tick += OnStartupTick;

        // 应用退出时停掉计时器，避免其持续持有 VM 引用并触发后台刷新导致泄漏
        _application = Application.Current;
        if (_application is not null)
            _application.Exit += OnApplicationExit;

        // 启动时异步扫描安装状态（内部会运行 npm / --version 子进程，放后台线程避免卡 UI）
        _ = ScanAsync();
        // 立即做一次进程探测，避免进程列表空等一个刷新周期
        TriggerRefresh();
        // 启动时后台跑一次环境检测（子进程探测较重，绝不阻塞 UI 构造）
        _ = RefreshEnvironmentAsync();
    }

    // ==================== 进程 / 状态定时刷新 ====================

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _startupTimer.Stop();
    }

    private void OnRefreshTick(object? sender, EventArgs e) => TriggerRefresh();

    /// <summary>启动进度心跳：按真实已用时刷新进度条与阶段文案（状态由进程服务驱动，此处只做展示）。</summary>
    private void OnStartupTick(object? sender, EventArgs e)
    {
        var timeoutSeconds = _process.StartupTimeout.TotalSeconds;
        var elapsed = (int)Math.Max(0, (DateTime.UtcNow - _startupStartedAt).TotalSeconds);

        StartElapsedSeconds = elapsed;
        StartProgressPercent = DshProcessService.EstimateStartProgress(elapsed, timeoutSeconds);
        StartPhaseText = BuildStartPhaseText(elapsed, timeoutSeconds);
    }

    /// <summary>
    /// 启动阶段文案（纯静态，便于复用）：
    /// 前 75% 时间提示"正在加载插件"，后 25% 明确告知会自动停止等待，避免用户长时间干等却不知上限。
    /// </summary>
    internal static string BuildStartPhaseText(int elapsedSeconds, double timeoutSeconds)
    {
        if (timeoutSeconds > 0 && elapsedSeconds >= timeoutSeconds * 0.75)
            return $"仍在启动中，已等待 {elapsedSeconds} 秒；若超过 {timeoutSeconds:F0} 秒会自动停止等待";

        return $"正在启动 dsh… 已等待 {elapsedSeconds} 秒（正在加载插件，通常需要 10~30 秒）";
    }

    /// <summary>触发一次刷新；上一轮未结束（WMI 探测较慢）时直接跳过，避免请求堆积。</summary>
    private void TriggerRefresh()
    {
        if (_disposed || _isRefreshing)
            return;

        _isRefreshing = true;
        _ = RefreshRuntimeStateAsync();
    }

    /// <summary>
    /// 刷新运行状态与进程列表：
    /// 1) 同步配置端口 → 2) 识别外部实例（必要时切换状态）→ 3) 后台探测进程 → 4) 回填界面集合。
    /// </summary>
    private async Task RefreshRuntimeStateAsync()
    {
        try
        {
            // 仅在 dsh 未运行时跟随设置页端口：运行中实例的端口以实际启动端口为准，
            // 否则用户一改配置就会把正在显示的 Web 地址与外部实例检测指向错误端口
            if (_process.CurrentState == DshProcessState.Stopped)
            {
                var configuredPort = ReadConfiguredPort();
                if (configuredPort != _port)
                {
                    _port = configuredPort;
                    PortText = _port.ToString();
                }
            }

            await _process.RefreshExternalStateAsync(_port);

            // 进程枚举 + WMI 读命令行是重活，放后台线程执行，避免阻塞 UI
            await Task.Run(_process.RefreshInstances);
            SyncInstances();

            // 刷新外部实例标记、按钮可用性与 ToolTip：状态未变化时不会触发 OnStateChanged，
            // 故必须在这里主动同步一次，否则"外部实例说明/终止按钮/ToolTip"会滞后
            IsExternalInstance = _process.IsExternalInstance;
            RefreshCommands();
        }
        catch
        {
            // 刷新失败保持上一次的展示结果，绝不让异常冒泡影响界面
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>把服务层探测结果同步到界面集合（数量很少，整体替换即可）。</summary>
    private void SyncInstances()
    {
        Instances.Clear();
        foreach (var instance in _process.Instances)
            Instances.Add(instance);

        HasInstances = Instances.Count > 0;
    }

    /// <summary>读取配置端口：非法或读取失败时回退默认端口，保证永远拿到可用值。</summary>
    private int ReadConfiguredPort()
    {
        try
        {
            var port = _store.Load<AppSettings>(SettingsFileName).Port;
            return port is > 0 and <= 65535 ? port : DshProcessService.DefaultPort;
        }
        catch
        {
            return DshProcessService.DefaultPort;
        }
    }

    /// <summary>
    /// 释放资源：停止定时刷新并退订所有事件。
    /// 说明：首页视图不提供页面级销毁回调，故额外挂接应用退出事件兜底停止计时器。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _startupTimer.Stop();
        _startupTimer.Tick -= OnStartupTick;

        if (_application is not null)
            _application.Exit -= OnApplicationExit;

        _process.LogReceived -= OnLogReceived;
        _process.StateChanged -= OnStateChanged;
        _install.OutputReceived -= OnInstallOutput;
        _install.Completed -= OnInstallCompleted;
    }

    // ==================== 进程事件处理 ====================

    /// <summary>
    /// 把回调安全地调度到 UI 线程。
    /// 【为什么不能直接 Application.Current.Dispatcher.Invoke】这些回调来自子进程输出线程，
    /// 应用退出过程中 Application.Current 可能已为 null、或 Dispatcher 已开始关闭，
    /// 直接 Invoke 会抛 NullReferenceException / TaskCanceledException，且异常发生在后台线程。
    /// 与 LogViewModel / PluginsViewModel 的做法保持一致：判空 + 判关闭 + 已在本线程就直接执行。
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private void OnLogReceived(object? sender, string line)
    {
        // 后台线程收到一行进程输出，调度到 UI 线程追加到日志集合
        RunOnUiThread(() => AddLog(line));
    }

    private void OnStateChanged(object? sender, DshProcessState state)
    {
        RunOnUiThread(() =>
        {
            // StartAsync 中先更新 WebUrl / Port 再切换状态，此处可拿到最新值
            WebUrlText = _process.WebUrl;
            PortText = _process.Port.ToString();
            // 外部实例标记必须跟着状态一起刷新（否则界面无法区分"外部启动/面板启动"）
            IsExternalInstance = _process.IsExternalInstance;
            UpdateProcessStatus(state);
            UpdateStartupProgress(state);
            RefreshCommands();
        });
    }

    /// <summary>状态 → 文本 / 画刷映射；外部启动的实例在文本上明确标注。</summary>
    private void UpdateProcessStatus(DshProcessState state)
    {
        // Colors.xaml 未提供黄色画刷，启动中沿用鲸鱼蓝（WhaleBlueBrush）表达"进行中"
        (ProcessStatusText, ProcessStatusBrush) = state switch
        {
            DshProcessState.Starting => ("启动中", GetBrush("WhaleBlueBrush")),
            DshProcessState.Running => (
                // 外部启动必须一眼可辨：它不是面板拉起的，用户需要"打开网页直接用"或"终止该实例"
                _process.IsExternalInstance ? "运行中（外部启动 / 非面板启动）" : "运行中",
                GetBrush("OkGreenBrush")),
            DshProcessState.Stopping => ("停止中", GetBrush("PinkAccentBrush")),
            _ => ("已停止", GetBrush("TextMutedBrush")),
        };
    }

    /// <summary>
    /// 根据状态启停「启动进度」计时器，并同步进度条相关属性：
    /// Starting → 从 0 开始计时并显示进度条；Running → 进度直接置 100（端口已就绪）；
    /// 其它状态（Stopped / Stopping）→ 停止计时并清空阶段文案。
    /// </summary>
    private void UpdateStartupProgress(DshProcessState state)
    {
        if (state == DshProcessState.Starting)
        {
            _startupStartedAt = DateTime.UtcNow;
            StartElapsedSeconds = 0;
            StartProgressPercent = 0;
            StartPhaseText = BuildStartPhaseText(0, _process.StartupTimeout.TotalSeconds);
            IsStarting = true;
            _startupTimer.Start();
            return;
        }

        _startupTimer.Stop();
        IsStarting = false;
        StartPhaseText = string.Empty;

        // 端口就绪：进度视为完成（进度条此时已因 IsStarting=false 隐藏，这里保持语义一致）
        if (state == DshProcessState.Running)
            StartProgressPercent = 100;
    }

    // ==================== 安装 / 更新事件处理 ====================

    private void OnInstallOutput(object? sender, string line)
    {
        // 安装子进程输出在后台线程触发，调度到 UI 线程追加日志
        RunOnUiThread(() => AddLog(line));
    }

    private void OnInstallCompleted(object? sender, InstallCompletedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            AddLog(e.Success
                ? $"✅ dsh 安装/更新完成（退出码 {e.ExitCode}）"
                : $"❌ dsh 安装/更新失败（退出码 {e.ExitCode}）");
            AppLogService.Instance.Info(e.Success
                ? $"dsh 安装/更新完成（退出码 {e.ExitCode}）"
                : $"dsh 安装/更新失败（退出码 {e.ExitCode}）");
            StatusMessage = e.Success ? "安装/更新完成，正在重新扫描…" : "安装/更新失败，请查看上方日志";

            // 无论成败都重新扫描，保证安装状态卡片与命令可用性即时刷新
            _ = ScanAsync();
        });
    }

    // ==================== 扫描 ====================

    /// <summary>扫描本地 dsh 安装状态并刷新界面。</summary>
    private async Task ScanAsync()
    {
        try
        {
            // 扫描可能运行 npm / dsh 子进程（最长数秒），放到后台线程避免阻塞 UI
            var info = await Task.Run(() => _discovery.Scan());
            Info = info;

            // 把"安装位置锁定"的中性提示写进日志区：
            // 已锁定时说明用的是哪个位置；锁定失效时说明"原位置已失效、已自动改为新位置"，
            // 让用户知道面板做了什么，但不打断操作（不弹错误框）。
            var lockMessage = _discovery.LastInstallLocationMessage;
            if (!string.IsNullOrWhiteSpace(lockMessage))
            {
                AddLog(lockMessage);
                AppLogService.Instance.Info($"dsh 安装位置：{lockMessage}");
            }

            IsInstalled = info.IsInstalled;
            VersionText = info.IsInstalled ? $"v{info.Version}" : "未安装";
            // 安装状态卡展示推荐安装位置（多来源探测中排序第一的可用项）
            RecommendedInstallation = info.RecommendedInstallation;
            HasRecommendedInstallation = info.RecommendedInstallation is not null;
        }
        catch (Exception ex)
        {
            // 扫描异常不崩溃，仅在日志提示
            AddLog($"扫描 dsh 状态失败：{ex.Message}");
        }
        finally
        {
            // 标记探测已结束：界面据此区分"检测中"与"确实没找到"
            InstallScanDone = true;
            // 扫描完成后识别外部启动的实例（如开机自启脚本），再统一刷新命令可用性
            await _process.RefreshExternalStateAsync(_port);
            IsExternalInstance = _process.IsExternalInstance;
            RefreshCommands();
        }
    }

    // ==================== 快捷操作命令 ====================

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // 点击行为按"当前真实状态"分派：保证任何状态下点击都有明确反馈，绝不静默。
        // 决策逻辑抽成纯函数 DecideStartAction，单元测试直接覆盖（见 HomeViewModelStartActionTests）。
        var action = DecideStartAction(_process.CurrentState, _process.IsExternalInstance);
        switch (action)
        {
            case StartAction.OpenWeb:
                // 已有实例（面板 / 外部）在运行 → 直接打开网页，并把"发生了什么"写进状态区与日志
                NotifyRunningAndOpenWeb();
                return;
            case StartAction.NotifyStarting:
                // 面板启动仍在进行中：不重复启动，只把进度同步到状态区与日志（点了也有反应）
                ReportStartHint($"dsh 正在启动中，请稍候（已等待 {StartElapsedSeconds} 秒）…");
                return;
            case StartAction.NotifyStopping:
                ReportStartHint("正在停止中，请稍候…");
                return;
        }

        // StartAction.Start：未运行 → 走原有的面板启动流程（逻辑保持不变）
        var launchCommand = Info?.LaunchCommand;
        if (string.IsNullOrWhiteSpace(launchCommand))
        {
            // 兜底：未安装 / 未找到启动器时也要"有反应"，明确写日志 + 状态提示
            AddLog("未找到可用的 dsh 启动器，也未检测到 npx");
            StatusMessage = "未找到可用的 dsh 启动器，也未检测到 npx";
            return;
        }

        // 启动前重读配置：设置页刚改的端口立即用于本次启动
        _port = ReadConfiguredPort();
        StatusMessage = "正在启动 dsh…";
        AppLogService.Instance.Info($"启动 dsh（端口 {_port}）");
        await _process.StartAsync(launchCommand, Info?.WorkDir ?? string.Empty, _port);

        // 面板发起的启动完成后，若端口已就绪则由面板补开一次网页（dsh 启动时带了 --no-open，不会自己弹）
        TryAutoOpenWebAfterPanelStart();
    }

    /// <summary>
    /// 「启动」按钮可用性：只受"安装 / 更新"这类真正互斥的忙任务限制，其余任何进程状态都保持可点。
    ///
    /// 【为什么放宽】旧规则要求 Stopped 才可点，导致外部实例在场时按钮被置灰，
    /// 用户点击毫无反馈、以为软件坏了（这正是本次修复要解决的核心问题）。
    /// 现在无论 Stopped / Running（面板）/ Running（外部）/ Starting / Stopping 都可点，
    /// 点下去一定有明确反馈（见 DecideStartAction）：执行启动 / 直接打开网页 / 提示正在启动中 / 正在停止中。
    /// 【为什么仍保留 IsBusy】安装 / 更新会改动 dsh 环境，与启动互斥；此时按钮禁用，
    /// 且 ToolTip 明确说明原因（是"有说明的暂时不可用"，不是"没反应"）。
    /// 【Starting 的补充说明】面板启动期间，异步命令自身因"执行中"而暂时禁用按钮，
    /// 但阶段文案与进度条持续刷新（已等待 N 秒），用户始终能看到进展，不会静默。
    /// </summary>
    private bool CanStart() => !IsBusy;

    /// <summary>
    /// 「启动」按钮点击后的行为决策（纯函数：无副作用、不依赖实例状态，单元测试可直接覆盖）。
    ///
    /// 【为什么外部实例也返回 OpenWeb】外部启动的实例在场时，旧的 CanStart 直接把按钮置灰，
    /// 用户点击毫无反馈 → 误以为"软件坏了 / 点了没反应"，这正是本次修复要解决的核心问题。
    /// 现在改为"永远可点、点了永远有反应"：既然 dsh 已经在跑，就顺手把网页打开，让用户立刻能用。
    /// isExternal 不改变动作（面板实例与外部实例都打开网页），保留在签名中是为了让调用方与测试
    /// 显式表达"外部实例"这一场景，并供按钮文案 / ToolTip 区分来源。
    /// </summary>
    public static StartAction DecideStartAction(DshProcessState state, bool isExternal)
    {
        _ = isExternal; // 动作与来源无关（都是"已在运行 → 打开网页"），显式忽略以免被误认为遗漏
        return state switch
        {
            // 未运行 → 走面板原有启动流程
            DshProcessState.Stopped => StartAction.Start,
            // 运行中（面板启动 / 外部启动都算）→ 直接打开网页（复用 OpenWeb，会优先解析日志里带 token 的完整地址）
            DshProcessState.Running => StartAction.OpenWeb,
            // 正在忙的瞬态 → 不静默，只给提示，绝不重复启动 / 停止
            DshProcessState.Starting => StartAction.NotifyStarting,
            DshProcessState.Stopping => StartAction.NotifyStopping,
            _ => StartAction.Start,
        };
    }

    /// <summary>
    /// 「启动」按钮的文案映射（纯函数）：让用户"一看就知道点了会怎样"。
    /// 同一个按钮只改文案与行为，绝不新增按钮，避免操作区越来越乱。
    /// </summary>
    public static string BuildStartButtonText(DshProcessState state, bool isExternal) => state switch
    {
        DshProcessState.Starting => "⏳ 启动中…",
        DshProcessState.Stopping => "⏳ 停止中…",
        // 运行中区分来源：外部实例提示"使用已运行的实例"，面板实例直接给"打开网页"
        DshProcessState.Running => isExternal ? "▶ 使用已运行的实例" : "🌐 打开网页",
        _ => "▶ 启动",
    };

    /// <summary>把「启动」按钮的一次点击反馈写到状态提示区与日志（保证"点了永远有反应"）。</summary>
    private void ReportStartHint(string message)
    {
        StatusMessage = message;
        AddLog(message);
    }

    /// <summary>
    /// 已有 dsh 在运行时点击「启动」的处理：打开网页 + 写提示。
    /// 文案区分来源：外部实例额外说明"如何让面板重新拉起"（先「终止该实例」），给用户明确出路。
    /// </summary>
    private void NotifyRunningAndOpenWeb()
    {
        ReportStartHint(_process.IsExternalInstance
            ? "检测到已有 dsh 在运行（外部启动 / 非面板启动），已直接为你打开网页；如需由面板重新拉起，请先点「终止该实例」"
            : "dsh 已在运行，已为你打开网页");

        try
        {
            OpenWeb();
        }
        catch (Exception ex)
        {
            // 打开浏览器失败只记日志，绝不影响状态与其它功能
            AppLogService.Instance.Write("进程", $"打开网页失败：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        StatusMessage = "正在停止 dsh…";
        AppLogService.Instance.Info("停止 dsh");
        await _process.StopAsync();
    }

    /// <summary>
    /// 「停止」是否可用：外部启动的实例一律禁用。
    /// 为什么这样设计：外部实例不是面板拉起的，可能是用户其它脚本 / 手工启动的，
    /// 误用「停止」等于替用户杀掉别人的进程；该场景改由用户显式确认后的「终止该实例」处理。
    /// </summary>
    private bool CanStop()
    {
        if (_process.IsExternalInstance)
            return false;

        return _process.CurrentState is DshProcessState.Starting or DshProcessState.Running;
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        var launchCommand = Info?.LaunchCommand;
        if (string.IsNullOrWhiteSpace(launchCommand))
        {
            AddLog("未找到可用的 dsh 启动器，也未检测到 npx");
            StatusMessage = "未找到可用的 dsh 启动器，也未检测到 npx";
            return;
        }

        // 重启前重读配置：让新端口随重启一起生效
        _port = ReadConfiguredPort();
        StatusMessage = "正在重启 dsh…";
        AppLogService.Instance.Info($"重启 dsh（端口 {_port}）");
        await _process.RestartAsync(launchCommand, Info?.WorkDir ?? string.Empty, _port);

        // 重启同样属于"面板发起的启动"：端口就绪后补开一次网页（与「启动」保持一致）
        TryAutoOpenWebAfterPanelStart();
    }

    /// <summary>
    /// 「重启」是否可用：外部实例同样禁用（重启必然先停止，等价于杀掉外部进程）；
    /// 启动过程中也禁用，避免与正在进行的启动流程互相踩。
    /// </summary>
    private bool CanRestart() => IsInstalled && !IsBusy
        && !_process.IsExternalInstance
        && _process.CurrentState != DshProcessState.Starting;

    [RelayCommand(CanExecute = nameof(CanOpenWeb))]
    private void OpenWeb()
    {
        // 用系统默认浏览器打开 dsh Web 页面（外部实例同样可用：这正是用户"直接连上残留实例"的诉求）。
        // 【为什么要 try/catch】本命令直接绑定按钮：没有默认浏览器或 URL 关联损坏时
        // Process.Start 会抛 Win32Exception，在 UI 线程抛出会直接让面板退出。
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ResolveWebUrl(), UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogService.Instance.Info($"打开网页失败：{ex.Message}");
            StatusMessage = "打开网页失败，请手动在浏览器访问面板地址";
        }
    }

    private bool CanOpenWeb() => _process.CurrentState == DshProcessState.Running;

    /// <summary>
    /// 解析要打开的 Web 地址：优先取日志里带 token 的完整地址（dsh 会打印 `dsh web: http://127.0.0.1:3080/?token=...`），
    /// 解析不到时退回基础地址 http://127.0.0.1:&lt;port&gt;。只扫最近 50 行，避免长日志下无谓开销。
    /// </summary>
    private string ResolveWebUrl()
    {
        var prefix = $"http://127.0.0.1:{_process.Port}";
        var start = Math.Max(0, Logs.Count - 50);

        for (var i = Logs.Count - 1; i >= start; i--)
        {
            var line = Logs[i];
            var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            // 截取到空白（空格 / 引号）为止，得到候选 URL
            var end = index;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '"')
                end++;
            var candidate = line[index..end];

            if (candidate.Contains("token", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return _process.WebUrl;
    }

    /// <summary>
    /// 面板发起的启动 / 重启成功后，自动打开一次 dsh 网页。
    ///
    /// 【触发点】仅由「启动」「重启」两个命令在 <see cref="DshProcessService"/> 的启动流程返回后调用，
    /// 且必须同时满足"当前 Running" + "非外部实例"才打开，因此：
    /// - 外部实例接管（RefreshExternalStateAsync → Running）不会走到这里，不影响他人实例；
    /// - 启动失败（超时 / 进程提前退出 → Stopped）也不会打开；
    /// - 每次启动只调用一次，不会重复弹窗。
    /// 【失败处理】打开浏览器失败只记日志，绝不影响启动流程与状态。
    /// </summary>
    private void TryAutoOpenWebAfterPanelStart()
    {
        if (!AutoOpenWebAfterPanelStart) return;
        if (_process.CurrentState != DshProcessState.Running || _process.IsExternalInstance) return;

        try
        {
            OpenWeb();
        }
        catch (Exception ex)
        {
            AppLogService.Instance.Write("进程", $"自动打开网页失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 「终止该实例」是否可用：仅当确实存在外部启动的运行中实例、且没有其它忙任务。
    /// 与「停止」严格区分：只有外部实例才允许通过这里结束，面板自启的实例走「停止」。
    /// </summary>
    private bool CanTerminateExternal()
        => !IsBusy && _process.IsExternalInstance && _process.CurrentState == DshProcessState.Running;

    /// <summary>
    /// 终止外部启动的 dsh 实例：
    /// 先弹出包含 PID 与命令行（进程位置）的确认框，用户确认后才执行，成功后状态回 Stopped、「启动」恢复可用。
    /// 【为什么不自动终止】结束别人的进程是有副作用的操作，必须由用户明确点头。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTerminateExternal))]
    private async Task TerminateExternalAsync()
    {
        var pidText = _process.ExternalProcessId is int pid ? pid.ToString() : "未知";
        var location = _process.FindExternalInstance()?.CommandLine;

        var message = new System.Text.StringBuilder();
        message.AppendLine("将强制结束这个「外部启动」的 dsh 实例：");
        message.AppendLine();
        message.AppendLine($"PID：{pidText}");
        if (!string.IsNullOrWhiteSpace(location))
            message.AppendLine($"命令行（进程位置）：{location}");
        message.AppendLine();
        message.AppendLine("这会结束该进程及其子进程（taskkill /PID <pid> /T /F），其它无关进程不受影响。");
        message.AppendLine("结束后可以点「启动」，由面板重新拉起 dsh。是否继续？");

        if (!Confirm(message.ToString(), "确认终止外部 dsh 实例")) return;

        StatusMessage = "正在终止外部 dsh 实例…";
        AppLogService.Instance.Info($"终止外部 dsh 实例（PID {pidText}）");
        var ok = await _process.TerminateExternalAsync();
        StatusMessage = ok
            ? "已终止外部 dsh 实例，现在可以点「启动」由面板重新拉起"
            : "终止外部 dsh 实例失败，请查看下方日志";

        RefreshCommands();
    }

    /// <summary>
    /// 在资源管理器中打开推荐安装位置：目录直接打开，文件（仅找到启动器 shim 的情况）用 /select 定位选中。
    /// </summary>
    [RelayCommand]
    private void OpenInstallLocation()
    {
        var target = RecommendedInstallation?.PackageDir;
        if (string.IsNullOrWhiteSpace(target)) return;

        var isDirectory = Directory.Exists(target);
        var arguments = isDirectory ? $"\"{target}\"" : $"/select,\"{target}\"";
        // explorer.exe 异常（文件关联损坏 / 目标被策略拦截）不能让面板崩掉
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogService.Instance.Info($"打开安装位置失败：{ex.Message}");
            StatusMessage = "打开目录失败，请手动前往安装目录";
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync() => await RunInstallAsync(isUpdate: false);

    private bool CanInstall() => !IsInstalled && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync() => await RunInstallAsync(isUpdate: true);

    private bool CanUpdate() => IsInstalled && !IsBusy;

    /// <summary>执行安装 / 更新：置忙禁用命令，完成事件统一在 OnInstallCompleted 中收尾。</summary>
    private async Task RunInstallAsync(bool isUpdate)
    {
        IsBusy = true;
        StatusMessage = isUpdate ? "正在更新 dsh…" : "正在安装 dsh…";
        AppLogService.Instance.Info(isUpdate ? "开始更新 dsh…" : "开始安装 dsh…");
        RefreshCommands();
        try
        {
            if (isUpdate)
            {
                await _install.UpdateAsync();
            }
            else
            {
                await _install.InstallAsync();
            }
        }
        finally
        {
            // 无论成功失败都复位忙状态并刷新命令可用性
            IsBusy = false;
            RefreshCommands();
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        // 清空服务缓冲与界面集合，两边保持一致
        _process.ClearLogs();
        Logs.Clear();
    }

    // ==================== 环境检测 ====================

    /// <summary>
    /// 重新跑一次环境检测并回填界面。
    /// 检测内部会执行 node / npm / where 等子进程（较重），放后台线程执行，避免卡住 UI；
    /// 结果集合在 UI 线程整体替换。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshEnvironment))]
    private async Task RefreshEnvironmentAsync()
    {
        if (IsEnvChecking) return;

        IsEnvChecking = true;
        EnvSummaryText = "环境检测中…";
        try
        {
            // 端口可能已在设置页改过 → 检测前同步，保证"端口可用性"结论针对当前端口
            _envCheck.Port = ReadConfiguredPort();

            var items = await Task.Run(() => _envCheck.CheckAll());

            EnvironmentChecks.Clear();
            foreach (var item in items)
                EnvironmentChecks.Add(new EnvCheckItemViewModel(item));

            var needAttention = EnvironmentChecks.Count(i => i.Status != EnvCheckStatus.Ok);
            EnvSummaryText = needAttention == 0
                ? $"{EnvironmentChecks.Count} 项检查全部通过 ✅"
                : $"{EnvironmentChecks.Count} 项检查，{needAttention} 项需要处理";
            AppLogService.Instance.Write("环境", $"环境检测：{EnvSummaryText}");
        }
        catch (Exception ex)
        {
            // 检测整体异常（极少见，服务内已逐项兜底）：只提示，不影响其他功能
            EnvSummaryText = $"环境检测失败：{ex.Message}";
        }
        finally
        {
            IsEnvChecking = false;
            RefreshEnvCommands();
        }
    }

    private bool CanRefreshEnvironment() => !IsEnvChecking;

    /// <summary>
    /// 执行某一项检测的修复动作：
    /// - openUrl → 用系统默认浏览器打开链接（无副作用，直接执行）；
    /// - runCommand → 先弹确认对话框告知将执行的动作，用户确认后才执行，完成后自动重新检测。
    /// 为什么不自动执行命令：安装/修复依赖会改动 dsh 环境，属于有副作用的操作，必须由用户明确同意。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunEnvFix))]
    private async Task RunEnvFixAsync(EnvCheckItemViewModel? item)
    {
        if (item is null || IsEnvFixRunning || !item.HasFix) return;

        if (item.FixKind == "openUrl")
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = item.FixTarget, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                EnvFixStatusText = $"打开链接失败：{ex.Message}";
                HasEnvFixOutput = true;
                EnvFixOutputText = EnvFixStatusText;
            }
            return;
        }

        if (item.FixKind != "runCommand") return;

        // 便携版 Node 安装标记：不是 shell 命令，改走面板内的下载/安装流程（同样要先经用户确认）
        if (item.FixTarget == EnvironmentCheckService.InstallPortableNodeMarker)
        {
            await InstallPortableNodeFromUiAsync();
            return;
        }

        // 命令型修复有副作用：执行前必须让用户看清到底要跑什么命令
        var confirmed = Confirm(
            $"即将执行以下命令（会修改本机 dsh 环境）：\n\n{item.FixTarget}\n\n" +
            "可能需要几分钟，请先确认没有正在进行的插件安装 / 更新。是否继续？",
            "确认执行修复命令");
        if (!confirmed) return;

        EnvFixOutputText = string.Empty; // 单项修复：清空旧输出，只要本次结果
        await ExecuteFixCommandAsync(item);

        // 命令跑完后自动重新检测，让用户直接看到结果变化（无需手动点「重新检测」）
        await RefreshEnvironmentAsync();
    }

    private bool CanRunEnvFix(EnvCheckItemViewModel? item) => !IsEnvFixRunning && item?.HasFix == true;

    /// <summary>
    /// 用系统默认浏览器打开某个检测项的官网链接（如 Node 官网 / dsh 官方仓库）。
    ///
    /// 【校验】链接虽来自服务层常量，仍统一走 <see cref="EnvironmentCheckService.IsSafeExternalUrl"/> 校验，
    /// 只放行 http/https 绝对地址，避免将来误填空串或 file:// 等协议被交给 shell 打开；失败只记日志、不抛异常。
    /// 【为何不自动打开】链接必须由用户显式点击；安装流程结束后只追加提示文本，绝不自动跳转浏览器。
    /// </summary>
    [RelayCommand]
    private void OpenEnvLink(EnvCheckItemViewModel? item)
    {
        var url = item?.LinkUrl;
        if (!EnvironmentCheckService.IsSafeExternalUrl(url))
        {
            AppLogService.Instance.Write("环境", $"忽略不可用的官网链接：{url}");
            return;
        }

        try
        {
            // 已通过 http/https 校验，此处 url 必非空
            Process.Start(new ProcessStartInfo { FileName = url!, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打开失败（无默认浏览器等）只记日志，不打断界面
            AppLogService.Instance.Write("环境", $"打开官网链接失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 「一键安装全部缺失项」：按依赖顺序串行执行 Node（含 npm/npx）→ dsh 本体 → dsh 依赖。
    ///
    /// 【确认方式】只弹**一个汇总确认框**，把这轮将执行的全部步骤按顺序列出。
    /// 理由：这是"一键"操作，逐步弹 N 次确认会把体验割裂成 N 次手动操作；
    /// 汇总框已让用户在动手前一次性看清全部副作用，粒度足够且更符合"一键"的预期。
    ///
    /// 【失败处理】单步失败不中断整体，只跳过依赖它的后续步骤（见 GetStagesToSkipAfterFailure），
    /// 最后汇总"成功 / 失败 / 跳过"步数；每步完成后都重新检测，下一步基于最新状态决定。
    /// 【绝不静默】必须用户点击 + 确认才会执行。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallAllMissing))]
    private async Task InstallAllMissingAsync()
    {
        if (IsEnvFixRunning) return;

        var plan = EnvironmentCheckService.BuildInstallPlan(EnvironmentChecks.Select(v => v.Item).ToList());
        if (plan.Count == 0) return;

        var stepsText = string.Join("\n", plan.Select((s, i) => $"{i + 1}. {s.Label}"));
        var confirmed = Confirm(
            $"即将按依赖顺序自动安装 / 修复以下缺失项（会修改本机 dsh 运行环境）：\n\n{stepsText}\n\n" +
            "每个步骤完成后会自动重新检测；下载 / 安装可能需要几分钟。是否继续？",
            "确认一键安装全部缺失项");
        if (!confirmed) return;

        IsEnvFixRunning = true;
        RefreshEnvCommands();
        EnvFixOutputText = string.Empty;
        HasEnvFixOutput = true;
        AppendEnvFixOutput($"一键安装开始：共 {plan.Count} 步");
        AppLogService.Instance.Write("环境", $"一键安装全部缺失项（{plan.Count} 步）：{string.Join(" → ", plan.Select(s => s.Label))}");

        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var portableNodeDone = false; // 便携版同一轮内只装一次（npm/npx 与 dsh 的修复可能都指向它）
        var skip = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var step in plan)
            {
                if (skip.Contains(step.ItemName))
                {
                    skipped++;
                    AppendEnvFixOutput($"⏭️ 跳过：{step.Label}（依赖的上一步未成功）");
                    continue;
                }

                // 从**最新**检测结果回查该项：上一步可能已把它顺带解决（如装完 Node 后 npm 变绿）
                var item = EnvironmentChecks.FirstOrDefault(v => v.Name == step.ItemName && v.IsCommandFix && v.HasFix);
                if (item is null)
                {
                    AppendEnvFixOutput($"✔️ 无需处理：{step.Label}");
                    continue;
                }

                AppendEnvFixOutput($"===== {step.Label} =====");
                bool ok;
                if (item.FixTarget == EnvironmentCheckService.InstallPortableNodeMarker)
                {
                    // 便携版同一轮只装一次：已装过就直接算成功，避免同一份约 35 MB 的 zip 被重复下载
                    ok = portableNodeDone || await ExecutePortableNodeInstallAsync();
                    portableNodeDone = true;
                }
                else
                {
                    ok = await ExecuteFixCommandAsync(item);
                }

                if (ok)
                {
                    succeeded++;
                    AppLogService.Instance.Write("环境", $"一键安装步骤成功：{step.Label}");
                }
                else
                {
                    failed++;
                    foreach (var name in EnvironmentCheckService.GetStagesToSkipAfterFailure(step.ItemName))
                        skip.Add(name);
                    AppLogService.Instance.Write("环境", $"一键安装步骤失败：{step.Label}");
                }

                // 每步完成后重新检测，下一步基于最新状态决定
                await RefreshEnvironmentAsync();
            }
        }
        finally
        {
            IsEnvFixRunning = false;
            RefreshEnvCommands();
        }

        var summary = $"一键安装结束：成功 {succeeded} 步，失败 {failed} 步"
            + (skipped > 0 ? $"，跳过 {skipped} 步" : string.Empty);
        EnvFixStatusText = summary;
        HasEnvFixOutput = true;
        AppendEnvFixOutput(summary);
        AppLogService.Instance.Write("环境", summary);

        // 安装完成后追加「临时方案」提醒与官网链接（纯文本）。
        // 绝不自动打开浏览器：链接已在上方每个检测项的按钮里提供，必须由用户自己点击。
        AppendEnvFixOutput(
            "提示：以上为面板代装的快速方案（Node 便携版 / npm 全局安装 dsh）。" +
            $"如需长期使用或自定义部署，建议按官网说明自行安装：{EnvironmentCheckService.DshOfficialRepoUrl}");
        AppLogService.Instance.Write("环境",
            $"一键安装为面板代装的快速方案，长期使用建议按官网说明自行安装：{EnvironmentCheckService.DshOfficialRepoUrl}");
    }

    /// <summary>
    /// 执行"命令型"修复的核心逻辑（已获用户确认后调用）：跑命令、回传输出、返回是否成功。
    /// 只负责单步执行，不弹确认框、不重新检测（确认与重检由调用方统一处理，供单项与批量复用）。
    /// </summary>
    private async Task<bool> ExecuteFixCommandAsync(EnvCheckItemViewModel item)
    {
        IsEnvFixRunning = true;
        HasEnvFixOutput = true;
        EnvFixStatusText = $"正在执行：{item.FixTarget}";
        AppLogService.Instance.Write("环境", $"执行修复命令：{item.FixTarget}");
        RefreshEnvCommands();

        bool ok;
        try
        {
            ok = await _envCheck.RunFixAsync(item.FixTarget, AppendEnvFixOutput);
            EnvFixStatusText = ok
                ? "✅ 修复命令执行完成，正在重新检测…"
                : "❌ 修复命令执行失败，请查看上方输出（也可能依赖正在重建，等 1 分钟后重试）";
            AppLogService.Instance.Write("环境", ok ? "修复命令执行完成" : "修复命令执行失败");
        }
        catch (Exception ex)
        {
            ok = false;
            EnvFixStatusText = $"修复命令执行异常：{ex.Message}";
        }
        finally
        {
            IsEnvFixRunning = false;
            RefreshEnvCommands();
        }
        return ok;
    }

    /// <summary>
    /// 面板内安装便携版 Node 的核心逻辑（已获用户确认后调用）：下载约 35 MB 可能很久，
    /// 放后台线程执行、进度经 Dispatcher 回传到修复输出区；不弹框、不重检（由调用方处理）。
    /// </summary>
    private async Task<bool> ExecutePortableNodeInstallAsync()
    {
        IsEnvFixRunning = true;
        HasEnvFixOutput = true;
        EnvFixStatusText = "正在安装 Node 便携版（下载中，请稍候）…";
        AppLogService.Instance.Write("环境", $"开始下载并安装 Node 便携版（目标：{_nodeRuntime.RuntimesRoot}）");
        RefreshEnvCommands();

        bool ok;
        try
        {
            // Task.Run 把下载/解压挪到后台，避免 35 MB 下载卡死 UI；进度回调内部会调度回 UI 线程
            ok = await Task.Run(() => _nodeRuntime.InstallPortableNodeAsync(AppendEnvFixOutput));
            EnvFixStatusText = ok
                ? "✅ Node 便携版安装完成，正在重新检测…"
                : "❌ Node 便携版安装失败，请查看上方输出";
            AppLogService.Instance.Write("环境", ok
                ? $"Node 便携版安装完成：{_nodeRuntime.GetPortableNodePath()}"
                : "Node 便携版安装失败");
        }
        catch (Exception ex)
        {
            ok = false;
            EnvFixStatusText = $"Node 便携版安装异常：{ex.Message}";
        }
        finally
        {
            IsEnvFixRunning = false;
            RefreshEnvCommands();
        }
        return ok;
    }

    /// <summary>
    /// 单项入口的便携版 Node 安装：先弹确认框（讲清下载什么 / 多大 / 装到哪 / 不改系统环境），再执行并重检。
    /// 下载/安装是有副作用的操作，必须用户点头。
    /// </summary>
    private async Task InstallPortableNodeFromUiAsync()
    {
        var confirmed = Confirm(
            "将从 nodejs.org 下载 Node.js 便携版（约 35 MB）并解压到：\n\n" +
            $"{_nodeRuntime.RuntimesRoot}\n\n" +
            "该便携版仅用于面板启动 dsh，不会修改系统 PATH、不写注册表、不安装系统级 Node。\n" +
            "下载耗时取决于网速，是否继续？",
            "确认下载并安装 Node 便携版", MessageBoxImage.Question);
        if (!confirmed) return;

        EnvFixOutputText = string.Empty;
        await ExecutePortableNodeInstallAsync();

        // 安装结束后自动重检，让 Node / npm 项的结论即时刷新
        await RefreshEnvironmentAsync();
    }

    /// <summary>弹一个 Yes/No 确认框（优先挂主窗口，便于居中）；返回用户是否点了「是」。</summary>
    private static bool Confirm(string message, string caption, MessageBoxImage icon = MessageBoxImage.Warning)
    {
        var window = Application.Current?.MainWindow;
        var result = window is null
            ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, icon)
            : MessageBox.Show(window, message, caption, MessageBoxButton.YesNo, icon);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>追加一行修复输出（可能在子进程线程触发 → 调度回 UI 线程），并限制总长度防止无限增长。</summary>
    private void AppendEnvFixOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        void Append()
        {
            var text = EnvFixOutputText.Length == 0 ? line : $"{EnvFixOutputText}{Environment.NewLine}{line}";
            // 只保留末尾 4000 字符：输出可能很长，界面只关心最近进度
            EnvFixOutputText = text.Length > 4000 ? text[^4000..] : text;
            HasEnvFixOutput = true;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Append();
        else dispatcher.Invoke(Append);
    }

    /// <summary>刷新环境检测相关命令的可执行性。</summary>
    private void RefreshEnvCommands()
    {
        RefreshEnvironmentCommand.NotifyCanExecuteChanged();
        RunEnvFixCommand.NotifyCanExecuteChanged();
        // 「一键安装全部缺失项」需要 1 个以上命令型修复项；执行中一并禁用，避免重复触发
        CanInstallAllMissing = !IsEnvFixRunning
            && EnvironmentCheckService.HasMultipleExecutableFixes(EnvironmentChecks.Select(v => v.Item));
        InstallAllMissingCommand.NotifyCanExecuteChanged();
    }

    // ==================== 工具方法 ====================

    /// <summary>追加一行日志，与进程服务一致最多保留 1000 行，防止长时间运行内存膨胀。</summary>
    private void AddLog(string line)
    {
        Logs.Add(line);
        while (Logs.Count > DshProcessService.MaxLogLines)
        {
            Logs.RemoveAt(0);
        }
    }

    /// <summary>从全局资源取画刷；Key 缺失时退回灰色，避免显示异常（环境检测项也复用它）。</summary>
    internal static Brush GetBrush(string key)
    {
        return Application.Current?.Resources[key] as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x7A, 0x93, 0xA8));
    }

    /// <summary>状态变化后逐个刷新所有带 CanExecute 的命令，驱动按钮禁用/启用，并同步 ToolTip 文案。</summary>
    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        OpenWebCommand.NotifyCanExecuteChanged();
        TerminateExternalCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        // 按钮被禁用时必须能从 ToolTip 看到"为什么不能点"，故与命令一起刷新派生文案
        RefreshDerivedUiText();
        RefreshEnvCommands();
    }
}

/// <summary>
/// 「启动」按钮一次点击应该执行的动作（由 <see cref="HomeViewModel.DecideStartAction"/> 纯函数推导）。
/// 拆成枚举的目的：把"点击后干什么"的决策与界面 / 进程服务解耦，便于单元测试直接覆盖，
/// 并保证"任何状态下点击都有明确反馈"这一诉求可被显式验证（不再有静默分支）。
/// </summary>
public enum StartAction
{
    /// <summary>未运行：执行面板启动流程（拉起新实例）。</summary>
    Start,

    /// <summary>已在运行（面板启动或外部启动）：直接打开网页，不重复启动。</summary>
    OpenWeb,

    /// <summary>正在启动中：只提示进度，不重复启动。</summary>
    NotifyStarting,

    /// <summary>正在停止中：只提示进度。</summary>
    NotifyStopping,
}

/// <summary>
/// 「环境检测」卡片里的一项：把只读的 <see cref="EnvCheckItem"/> 包装成可直接绑定的界面模型
/// （图标 / 状态色 / 是否有修复动作）。做成独立包装类的原因与 InstalledPluginItem 相同：
/// EnvCheckItem 是不可变快照，界面需要的派生展示属性不宜塞进服务层模型。
/// </summary>
public sealed class EnvCheckItemViewModel
{
    private readonly EnvCheckItem _item;

    public EnvCheckItemViewModel(EnvCheckItem item) => _item = item;

    /// <summary>原始只读检测项（供「一键安装全部缺失项」按名称 / FixTarget 编排步骤，避免界面层重复解析）。</summary>
    public EnvCheckItem Item => _item;

    /// <summary>检测项名称。</summary>
    public string Name => _item.Name;

    /// <summary>检测详情文本。</summary>
    public string Detail => _item.Detail;

    /// <summary>中文说明 / 建议（界面用弱化样式展示）。</summary>
    public string Hint => _item.Hint;

    /// <summary>检测结论级别。</summary>
    public EnvCheckStatus Status => _item.Status;

    /// <summary>修复方式："openUrl" | "runCommand" | "none"。</summary>
    public string FixKind => _item.FixKind;

    /// <summary>修复目标（URL 或完整命令行）。</summary>
    public string FixTarget => _item.FixTarget;

    /// <summary>修复按钮文案。</summary>
    public string FixLabel => _item.FixLabel;

    /// <summary>官网链接地址（为空表示该项没有外部链接）。</summary>
    public string LinkUrl => _item.LinkUrl;

    /// <summary>官网链接按钮文案；服务层未给文案时退化为通用的「官网」，避免出现空白按钮。</summary>
    public string LinkLabel => string.IsNullOrWhiteSpace(_item.LinkLabel) ? "官网" : _item.LinkLabel;

    /// <summary>
    /// 是否显示官网链接按钮：仅当链接通过 http/https 校验时显示，
    /// 与点击时的校验保持同一标准，避免出现点了没反应的死按钮。
    /// </summary>
    public bool HasLink => EnvironmentCheckService.IsSafeExternalUrl(_item.LinkUrl);

    /// <summary>状态图标：✅ 通过 / ⚠️ 需要处理 / ❌ 缺失。</summary>
    public string Icon => Status switch
    {
        EnvCheckStatus.Ok => "✅",
        EnvCheckStatus.Warning => "⚠️",
        _ => "❌",
    };

    /// <summary>状态对应的文字色（通过=绿，需处理=粉，缺失=浅粉）。</summary>
    public Brush StatusBrush => HomeViewModel.GetBrush(Status switch
    {
        EnvCheckStatus.Ok => "OkGreenBrush",
        EnvCheckStatus.Warning => "PinkAccentBrush",
        _ => "PinkLightBrush",
    });

    /// <summary>是否有可用的修复动作（决定修复按钮显隐）。</summary>
    public bool HasFix => (_item.FixKind is "openUrl" or "runCommand")
        && !string.IsNullOrWhiteSpace(_item.FixTarget)
        && !string.IsNullOrWhiteSpace(_item.FixLabel);

    /// <summary>是否为"执行命令"型修复（用于按钮主次样式区分）。</summary>
    public bool IsCommandFix => _item.FixKind == "runCommand";
}

