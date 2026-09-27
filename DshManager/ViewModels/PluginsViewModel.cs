using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 已安装列表的一行：在 <see cref="PluginItem"/> 之上叠加「更新检查」状态。
/// 【为什么另建包装类】PluginItem 定义在不可修改的 DshPluginService.cs 里，无法追加界面状态，
/// 因此在这里用可通知的包装类承载徽章与可更新标记，供 XAML 直接绑定。
/// </summary>
public partial class InstalledPluginItem : ObservableObject
{
    /// <summary>插件包名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>本机已装版本。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>激活状态（已激活 / 未激活）。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>市场最新版本（尚未检查 / 市场未找到时为空）。</summary>
    [ObservableProperty]
    private string latestVersion = string.Empty;

    /// <summary>是否可更新（已装版本 &lt; 市场最新版本）。</summary>
    [ObservableProperty]
    private bool canUpdate;

    /// <summary>市场里查不到该包（私有包 / 已下架）——不是错误，也不尝试更新。</summary>
    [ObservableProperty]
    private bool marketNotFound;

    /// <summary>可更新徽章文本，如 "可更新到 1.7.39"。</summary>
    public string UpdateBadgeText => CanUpdate ? $"可更新到 {LatestVersion}" : string.Empty;

    /// <summary>市场未找到的提示文本。</summary>
    public string MarketNotFoundText => "市场未找到";

    // 两个只读展示文本都依赖 CanUpdate / LatestVersion → 变更时手动通知（模型未实现通知）
    partial void OnCanUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdateBadgeText));

    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(UpdateBadgeText));
}

/// <summary>
/// 插件管理页视图模型，分两个页签：
/// - 「已安装」：插件列表 + 安装输入框 + 卸载 + 检查更新 / 单个更新 / 全部更新；
/// - 「插件市场」：联网搜索 npm registry 的 dsh 插件、补 dshhub 中文元数据、排序 / 只看兼容 / 一键安装 / 卸载。
/// 服务输出在子进程线程触发，所有界面状态更新统一调度回 UI 线程。
///
/// 【排序实现取舍】npm search 的 score.detail 实测恒为 1，服务端排序无效：
/// - 「相关度」沿用它原本的服务端分页（行为不变，省流量）；
/// - 「更新时间 / 下载量」改为「拉一批 250 条 → 客户端排序 → 客户端分页」。
/// </summary>
public partial class PluginsViewModel : ObservableObject
{
    /// <summary>分类下拉里的「全部」项。</summary>
    private const string AllCategories = "全部";

    // 排序下拉的显示项（用字符串简单绑定到 ComboBox，避免额外的选项模型 / 模板）
    private const string SortRelevance = "相关度（默认）";
    private const string SortUpdatedTime = "更新时间（最新）";
    private const string SortDownloads = "下载量（最多）";

    /// <summary>客户端排序模式一次拉取的条数（npm search 接口上限 250）。</summary>
    private const int ClientSortPoolSize = 250;

    /// <summary>
    /// 插件安装/卸载/更新成功后追加的提示：插件已变更必须重启 dsh 才生效。
    /// 【为什么不自动重启】重启 dsh 有副作用（会中断正在进行的会话），必须由用户明确决定；
    /// 同时也要提醒用户"装配依赖时可能短暂导致启动失败"，避免其在 pnpm 重建 node_modules 的空窗期反复重试。
    /// </summary>
    private const string RestartDshHint =
        "插件已变更，需要重启 dsh 才能生效（装配依赖时也可能短暂导致 dsh 启动失败，请稍等 1 分钟再启动）";

    // ==================== 装卸更新前的「依赖重建」提醒 ====================
    // 【为什么需要提醒】面板的安装 / 卸载 / 更新最终都是 pnpm 在 profiles\web 里跑 install；
    // 本机 profile 用 hoisted 扁平化布局 → pnpm 不做增量，会把 node_modules 整个推倒重写。
    // 重建空窗期 dsh 解析 @deepseek-ai/* 会失败（实测复现过启动报 Cannot find package）。
    // 面板自带插件（复制到 plugins\）不走 pnpm，不触发重建，故不在此提醒范围内。

    /// <summary>dsh 运行时确认框标题。</summary>
    public const string RebuildWarningTitle = "安装/卸载插件会重建依赖目录";

    /// <summary>dsh 运行时确认框正文（说明风险与建议，并给出「是否继续」）。</summary>
    public const string RebuildWarningBody =
        "接下来会调用 pnpm 重建 dsh 的依赖目录（约需 1 分钟）。\n\n" +
        "⚠ 检测到 dsh 正在运行：\n" +
        "· 重建期间 dsh 可能短暂不可用，甚至启动失败\n" +
        "· 建议先点「停止」，装完等约 1 分钟再启动\n\n" +
        "是否继续？";

    /// <summary>dsh 未运行时的弱提示（不弹窗，只追加到状态文本 / 日志）。</summary>
    public const string RebuildDependencyNotice =
        "提示：本次会重建依赖目录（约 1 分钟），装完请等约 1 分钟再启动 dsh，否则可能启动失败。";

    /// <summary>
    /// 插件安装 / 卸载 / 更新前是否需要弹确认框。
    /// 【为什么只在 dsh 运行时弹】重建空窗期会打断正在运行的 dsh；若 dsh 本就没在跑则没有立即风险，
    /// 每次都弹会变成噪音，因此只在运行中确认，其余情况只留一行弱提示。
    /// 【为什么抽成静态纯函数】便于单元测试直接断言，不必构造 ViewModel（其构造会真的调用 CLI）或触碰 MessageBox。
    /// </summary>
    public static bool ShouldConfirmBeforePluginChange(bool dshRunning) => dshRunning;

    private readonly DshPluginService _service;
    private readonly DshPluginMarketService _marketService;

    /// <summary>当前已加载的原始数据集：相关度模式 = 服务端当前页；客户端排序模式 = 一次拉取的整批（≤250）。</summary>
    private readonly List<PluginMarketItem> _datasetItems = new();

    /// <summary>当前页的展示条目（兼容过滤 + 客户端分页之后，分类过滤前的数据源）。</summary>
    private readonly List<PluginMarketItem> _pageItems = new();

    /// <summary>最近一次实际提交给接口的搜索词（翻页 / 刷新复用）。</summary>
    private string _appliedQuery = string.Empty;

    /// <summary>本机 dsh 版本缓存（首次用到时读取，避免每次搜索都读文件）。</summary>
    private string? _localDshVersion;

    /// <summary>市场页是否已首次加载过（避免空结果页签来回切换时反复联网）。</summary>
    private bool _marketLoadedOnce;

    /// <summary>服务端分页模式下的匹配总数（客户端排序模式下总数取过滤后的可见集合大小）。</summary>
    private int _serverTotal;

    /// <summary>本次「只看兼容」过滤掉的条数（用于状态行提示）。</summary>
    private int _hiddenByCompatibility;

    public PluginsViewModel(DshPluginService? service = null, DshPluginMarketService? marketService = null)
    {
        _service = service ?? new DshPluginService();
        _marketService = marketService ?? new DshPluginMarketService();

        // 服务输出事件在子进程线程触发 → 调度到 UI 线程显示为状态文本
        _service.OutputReceived += (_, line) => AppendStatus(line);

        // 构造后自动刷新一次已安装列表（只读清单，不做任何有副作用的网络/安装动作）
        _ = RefreshAsync();
    }

    // ==================== 页签 ====================

    /// <summary>当前页签：0 = 已安装，1 = 插件市场。</summary>
    [ObservableProperty]
    private int activeTabIndex;

    /// <summary>切换到「已安装」页签。</summary>
    [RelayCommand]
    private void ShowInstalledTab() => ActiveTabIndex = 0;

    /// <summary>切换到「插件市场」页签；首次进入时自动加载第一页（浏览全部）。</summary>
    [RelayCommand]
    private void ShowMarketTab()
    {
        ActiveTabIndex = 1;
        if (!_marketLoadedOnce && !IsMarketBusy) _ = LoadMarketPageAsync();
    }

    // ==================== 页签 1：已安装 ====================

    /// <summary>已安装插件列表（UI 绑定；每行带更新检查状态）。</summary>
    public ObservableCollection<InstalledPluginItem> Plugins { get; } = new();

    /// <summary>安装输入：插件名或本地路径，可一次多个（空格分隔）。</summary>
    [ObservableProperty]
    private string packageInput = string.Empty;

    /// <summary>已安装页签的状态提示文本（操作结果 / 服务输出 / 降级说明）。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>忙态：为 true 时禁用刷新 / 安装 / 卸载 / 更新按钮。</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>当前操作的 profile 说明（如 "profile：web"）。</summary>
    public string ProfileLabel => $"profile：{_service.ProfileName}";

    /// <summary>是否没有插件（驱动空态占位显示）。</summary>
    public bool HasNoPlugins => Plugins.Count == 0;

    /// <summary>刷新插件列表（带忙态保护与异常兜底）。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await LoadAsync();
            StatusText = $"共 {Plugins.Count} 个插件（{ProfileLabel}）";
        }
        catch (OperationCanceledException)
        {
            StatusText = "刷新已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>安装输入框中的插件（可为多个包，空格分隔），完成后自动刷新列表。</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (IsBusy) return;
        var spec = PackageInput.Trim();
        if (spec.Length == 0) return;

        // 安装 = pnpm add → 会重建依赖目录，执行前先过提醒闸门
        if (!ConfirmRebuildRisk("安装", AppendInstalledRebuildNotice))
        {
            StatusText = $"已取消安装：{spec}";
            return;
        }

        IsBusy = true;
        try
        {
            AppLogService.Instance.Write("插件", $"开始安装插件 {spec}（将重建依赖目录）");
            StatusText = $"正在安装：{spec} …";
            var ok = await _service.InstallAsync(spec);
            await LoadAsync();
            StatusText = ok
                ? $"✅ 安装完成：{spec}"
                : $"❌ 安装失败：{spec}（详见上方输出）";
            if (ok)
            {
                PackageInput = string.Empty;
                AppendRestartHintToStatus();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = $"安装已取消：{spec}";
        }
        catch (Exception ex)
        {
            StatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>卸载指定插件，完成后自动刷新列表。</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync(InstalledPluginItem? item)
    {
        if (IsBusy || item is null) return;

        // 卸载 = pnpm remove → 同样会重建依赖目录，执行前先过提醒闸门
        if (!ConfirmRebuildRisk("卸载", AppendInstalledRebuildNotice))
        {
            StatusText = $"已取消卸载：{item.Name}";
            return;
        }

        IsBusy = true;
        try
        {
            AppLogService.Instance.Write("插件", $"开始卸载插件 {item.Name}（将重建依赖目录）");
            StatusText = $"正在卸载：{item.Name} …";
            var ok = await _service.UninstallAsync(item.Name);
            await LoadAsync();
            StatusText = ok
                ? $"✅ 卸载完成：{item.Name}"
                : $"❌ 卸载失败：{item.Name}（详见上方输出）";
            if (ok) AppendRestartHintToStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText = $"卸载已取消：{item.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"卸载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>检查更新：逐个查询市场最新版，标记「可更新到 x.y.z」；查不到显示「市场未找到」。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await CheckUpdatesCoreAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyUpdateCommands();
        }
    }

    /// <summary>
    /// 全部更新：对所有可更新插件**串行**执行安装，显示进度；单个失败不中断，最后汇总成功 / 失败数。
    /// 只由用户点击触发，绝无自动 / 静默更新。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (IsBusy) return;
        var targets = Plugins.Where(p => p.CanUpdate).ToList();
        if (targets.Count == 0)
        {
            StatusText = "没有可更新的插件";
            return;
        }

        // 全部更新会让 pnpm 反复重建依赖目录 → 整批只弹「一次」确认，避免每个包弹一次打断用户
        if (!ConfirmRebuildRisk("批量更新", AppendInstalledRebuildNotice))
        {
            StatusText = "已取消全部更新";
            return;
        }

        IsBusy = true;
        try
        {
            AppLogService.Instance.Write("插件", $"开始批量更新 {targets.Count} 个插件（将重建依赖目录）");
            var succeeded = 0;
            var failed = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                StatusText = $"正在更新 {i + 1}/{targets.Count}：{target.Name} → {target.LatestVersion}";
                AppendStatus($"▶ 更新 {target.Name} → {target.LatestVersion}");
                if (await InstallForUpdateAsync(target)) succeeded++;
                else failed++;
            }

            await LoadAsync();
            await CheckUpdatesCoreAsync();   // 刷新徽章状态
            StatusText = $"更新结束：成功 {succeeded} 个 / 失败 {failed} 个";
            // 只要有插件真正被改动，就提示需要重启 dsh（全部失败时不提示，避免误导）
            if (succeeded > 0) AppendRestartHintToStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"批量更新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyUpdateCommands();
        }
    }

    /// <summary>更新单个插件。</summary>
    [RelayCommand(CanExecute = nameof(CanUpdatePlugin))]
    private async Task UpdatePluginAsync(InstalledPluginItem? item)
    {
        if (IsBusy || item is null || !item.CanUpdate) return;

        // 单个更新 = pnpm add 指定版本 → 会重建依赖目录，执行前先过提醒闸门
        if (!ConfirmRebuildRisk("更新", AppendInstalledRebuildNotice))
        {
            StatusText = $"已取消更新：{item.Name}";
            return;
        }

        IsBusy = true;
        try
        {
            AppLogService.Instance.Write("插件", $"开始更新插件 {item.Name} → {item.LatestVersion}（将重建依赖目录）");
            StatusText = $"正在更新：{item.Name} → {item.LatestVersion} …";
            AppendStatus($"▶ 更新 {item.Name} → {item.LatestVersion}");
            var ok = await InstallForUpdateAsync(item);

            await LoadAsync();
            await CheckUpdatesCoreAsync();   // 刷新徽章状态
            StatusText = ok
                ? $"✅ 更新完成：{item.Name} → {item.LatestVersion}"
                : $"❌ 更新失败：{item.Name}（详见上方输出）";
            if (ok) AppendRestartHintToStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"更新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyUpdateCommands();
        }
    }

    private bool CanRefresh() => !IsBusy;
    private bool CanInstall() => !IsBusy && !string.IsNullOrWhiteSpace(PackageInput);
    private bool CanUninstall() => !IsBusy;
    private bool CanCheckUpdates() => !IsBusy && Plugins.Count > 0;
    private bool CanUpdateAll() => !IsBusy && Plugins.Any(p => p.CanUpdate);
    private bool CanUpdatePlugin(InstalledPluginItem? item) => !IsBusy && item?.CanUpdate == true;

    /// <summary>刷新「已安装」页签相关命令的可执行性。</summary>
    private void NotifyUpdateCommands()
    {
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
        UpdatePluginCommand.NotifyCanExecuteChanged();
    }

    /// <summary>执行一次插件更新（指定最新版本），输出实时回传状态行；异常也转为失败而非冒泡。</summary>
    private async Task<bool> InstallForUpdateAsync(InstalledPluginItem item)
    {
        try
        {
            return await _marketService.InstallAsync(item.Name, item.LatestVersion, AppendStatus);
        }
        catch (Exception ex)
        {
            AppendStatus($"❌ 更新 {item.Name} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>检查更新的实际逻辑（更新完成后复用它刷新徽章）。</summary>
    private async Task CheckUpdatesCoreAsync()
    {
        if (Plugins.Count == 0)
        {
            StatusText = "没有已安装的插件";
            return;
        }

        StatusText = $"正在检查更新 0/{Plugins.Count}…";
        var names = Plugins.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var latest = await _marketService.GetLatestVersionsAsync(names, default,
            (done, total) => OnUi(() => StatusText = $"正在检查更新 {done}/{total}…"));

        var updatable = 0;
        var notFound = 0;
        foreach (var plugin in Plugins)
        {
            if (latest.TryGetValue(plugin.Name, out var version) && !string.IsNullOrWhiteSpace(version))
            {
                plugin.MarketNotFound = false;
                plugin.LatestVersion = version;
                // 已装 < 最新才算可更新；版本无法解析时 IsUpdateAvailable 返回 false（不误报）
                plugin.CanUpdate = DshPluginMarketService.IsUpdateAvailable(plugin.Version, version);
                if (plugin.CanUpdate) updatable++;
            }
            else
            {
                // 私有包 / 已下架 → 标记「市场未找到」，不视为错误，也不尝试更新
                plugin.MarketNotFound = true;
                plugin.LatestVersion = string.Empty;
                plugin.CanUpdate = false;
                notFound++;
            }
        }

        var notFoundNote = notFound > 0 ? $"；{notFound} 个市场未找到" : string.Empty;
        StatusText = updatable > 0
            ? $"检查完成：{updatable} 个插件可更新{notFoundNote}"
            : $"已是最新：没有可更新的插件{notFoundNote}";
    }

    /// <summary>实际加载列表（刷新 / 安装后 / 卸载后复用；不管理 IsBusy）。</summary>
    private async Task LoadAsync()
    {
        var items = await _service.ListAsync();
        Plugins.Clear();
        foreach (var item in items)
        {
            // 包装为可通知行；更新徽章在「检查更新」后填充（刷新会重建行、徽章复位，符合预期）
            Plugins.Add(new InstalledPluginItem
            {
                Name = item.Name,
                Version = item.Version,
                Status = item.Status,
            });
        }
        OnPropertyChanged(nameof(HasNoPlugins));
        NotifyUpdateCommands();
    }

    /// <summary>把服务输出行调度到 UI 线程显示为状态文本。</summary>
    private void AppendStatus(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        OnUi(() => StatusText = line);
    }

    /// <summary>
    /// 在「已安装」页签状态文本末尾追加"需重启 dsh"提示。
    /// 只提示不自动重启：重启 dsh 有副作用，必须由用户自行决定时机。
    /// </summary>
    private void AppendRestartHintToStatus()
        => StatusText = $"{StatusText}{Environment.NewLine}⚠️ {RestartDshHint}";

    // ==================== 装卸更新前的提醒闸门（两个页签共用） ====================

    /// <summary>
    /// dsh 是否在运行（把「启动中」也算在内：此时进程已拉起，重建依赖同样可能让它启动失败）。
    /// 直接复用面板共享的进程服务状态，不另起探测。
    /// </summary>
    private static bool IsDshRunning() =>
        AppServices.Process.CurrentState is DshProcessState.Running or DshProcessState.Starting;

    /// <summary>
    /// 插件变更（会触发 pnpm 重建依赖目录）前的提醒闸门。
    /// 返回 false 表示用户在确认框里选了「否」，调用方必须立即中止并写「已取消」状态。
    /// 【智能分级】dsh 在运行 → 弹确认框（有立即风险）；未运行 → 不弹窗，仅留一行弱提示。
    /// </summary>
    /// <param name="actionLabel">动作名（安装 / 卸载 / 更新 / 批量更新），用于文案与日志。</param>
    /// <param name="appendNotice">未运行时弱提示的落地方式（已安装页签写状态文本，市场页签写状态文本 + 日志）。</param>
    private static bool ConfirmRebuildRisk(string actionLabel, Action<string> appendNotice)
    {
        if (!ShouldConfirmBeforePluginChange(IsDshRunning()))
        {
            appendNotice(RebuildDependencyNotice);   // 未运行：不打断用户，只留提示
            return true;
        }

        var choice = MessageBox.Show(RebuildWarningBody, RebuildWarningTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes) return true;

        // 用户拒绝 → 记统一日志留痕，便于排查"为什么操作没执行"
        AppLogService.Instance.Write("插件", $"用户取消了插件{actionLabel}（避免依赖重建期间影响 dsh）");
        return false;
    }

    /// <summary>把弱提示追加到「已安装」页签状态文本，并写入统一日志。</summary>
    private void AppendInstalledRebuildNotice(string notice)
    {
        StatusText = StatusText.Length == 0 ? notice : $"{StatusText}{Environment.NewLine}{notice}";
        AppLogService.Instance.Write("插件", notice);
    }

    // ==================== 页签 2：插件市场 ====================

    /// <summary>当前页展示的条目（已按分类过滤）。</summary>
    public ObservableCollection<PluginMarketItem> MarketItems { get; } = new();

    /// <summary>分类下拉项（从当前数据集聚合去重，首项固定为「全部」）。</summary>
    public ObservableCollection<string> MarketCategories { get; } = new();

    /// <summary>排序下拉项：「相关度」走服务端分页；其余两项走客户端排序 + 分页。</summary>
    public ObservableCollection<string> MarketSortOptions { get; } = new() { SortRelevance, SortUpdatedTime, SortDownloads };

    /// <summary>市场页签的安装 / 卸载实时输出（逐行）。</summary>
    public ObservableCollection<string> MarketLogLines { get; } = new();

    /// <summary>搜索关键词（回车或点搜索触发）。</summary>
    [ObservableProperty]
    private string marketQuery = string.Empty;

    /// <summary>市场页签状态文本（加载进度 / 结果统计 / 中文错误）。</summary>
    [ObservableProperty]
    private string marketStatusText = "点「搜索」或「刷新」开始浏览插件市场";

    /// <summary>市场页签忙态（搜索 / 安装 / 卸载期间禁用相关按钮）。</summary>
    [ObservableProperty]
    private bool isMarketBusy;

    /// <summary>当前选中的插件（驱动右侧详情面板）。</summary>
    [ObservableProperty]
    private PluginMarketItem? selectedMarketItem;

    /// <summary>选中的分类筛选（「全部」表示不过滤）。</summary>
    [ObservableProperty]
    private string? selectedCategory = AllCategories;

    /// <summary>当前排序选择（默认「相关度」，行为与之前一致）。</summary>
    [ObservableProperty]
    private string selectedMarketSortOption = SortRelevance;

    /// <summary>是否只显示兼容本机 dsh 的插件（在已加载数据集内过滤，不额外联网）。</summary>
    [ObservableProperty]
    private bool onlyCompatible;

    /// <summary>当前页码（0 基，界面显示时为 +1）。</summary>
    [ObservableProperty]
    private int marketPage;

    /// <summary>匹配到的插件总数（相关度模式取接口 total；客户端排序模式取过滤后的可见集合大小）。</summary>
    [ObservableProperty]
    private int marketTotal;

    /// <summary>每页条数。</summary>
    [ObservableProperty]
    private int marketPageSize = 20;

    /// <summary>本机 dsh 版本（首次访问时读取并缓存，供兼容筛选文案显示）。</summary>
    public string LocalDshVersion => _localDshVersion ??= _marketService.GetLocalDshVersion();

    /// <summary>兼容筛选复选框文案（带本机实际 dsh 版本号，不写死）。</summary>
    public string CompatibleFilterLabel => $"只看兼容本机 dsh（{LocalDshVersion}）";

    /// <summary>页码显示文本。</summary>
    public string MarketPageText => $"第 {MarketPage + 1} 页";

    /// <summary>是否可上一页。</summary>
    public bool HasPrevPage => MarketPage > 0;

    /// <summary>是否可下一页（按总数判断）。</summary>
    public bool HasNextPage => (MarketPage + 1) * MarketPageSize < MarketTotal;

    /// <summary>当前是否没有任何可展示条目（驱动空态占位）。</summary>
    public bool HasNoMarketItems => MarketItems.Count == 0;

    /// <summary>是否已选中插件（驱动详情 / 占位切换）。</summary>
    public bool HasSelectedMarketItem => SelectedMarketItem is not null;

    /// <summary>当前排序模式。</summary>
    private MarketSortMode CurrentSortMode => SelectedMarketSortOption switch
    {
        SortUpdatedTime => MarketSortMode.UpdatedTime,
        SortDownloads => MarketSortMode.Downloads,
        _ => MarketSortMode.Relevance,
    };

    /// <summary>是否处于客户端排序模式（需要自己排序 + 分页）。</summary>
    private bool IsClientSortMode => CurrentSortMode != MarketSortMode.Relevance;

    /// <summary>搜索（把输入框内容固化为"已提交关键词"，并重置到第一页）。</summary>
    [RelayCommand(CanExecute = nameof(CanSearchMarket))]
    private async Task SearchMarketAsync()
    {
        if (IsMarketBusy) return;
        _appliedQuery = MarketQuery?.Trim() ?? string.Empty;
        MarketPage = 0;
        await LoadMarketPageAsync();
    }

    /// <summary>刷新：重新读取输入框关键词并回到第一页重新联网获取。</summary>
    [RelayCommand(CanExecute = nameof(CanSearchMarket))]
    private async Task RefreshMarketAsync()
    {
        if (IsMarketBusy) return;
        _appliedQuery = MarketQuery?.Trim() ?? string.Empty;
        MarketPage = 0;
        await LoadMarketPageAsync();
    }

    /// <summary>上一页：客户端排序模式直接切页（不再联网），相关度模式重新请求服务端。</summary>
    [RelayCommand(CanExecute = nameof(CanPrevMarketPage))]
    private async Task PrevMarketPageAsync()
    {
        if (IsMarketBusy || !HasPrevPage) return;
        MarketPage--;
        if (IsClientSortMode)
        {
            RecomputePage();
            RefreshMarketList();
            UpdateMarketStatus();
            return;
        }
        await LoadMarketPageAsync();
    }

    /// <summary>下一页：客户端排序模式直接切页（不再联网），相关度模式重新请求服务端。</summary>
    [RelayCommand(CanExecute = nameof(CanNextMarketPage))]
    private async Task NextMarketPageAsync()
    {
        if (IsMarketBusy || !HasNextPage) return;
        MarketPage++;
        if (IsClientSortMode)
        {
            RecomputePage();
            RefreshMarketList();
            UpdateMarketStatus();
            return;
        }
        await LoadMarketPageAsync();
    }

    /// <summary>选中条目并展示详情；若 dshhub 详情尚未获取则单条补全。</summary>
    [RelayCommand]
    private async Task SelectMarketItemAsync(PluginMarketItem? item)
    {
        if (item is null) return;
        SelectedMarketItem = item;

        if (item.HasDshHub || IsMarketBusy) return;
        IsMarketBusy = true;
        try
        {
            await _marketService.EnrichAsync(new[] { item });
            ApplyCompatibility(new[] { item });
            RecomputePage();
            RefreshMarketList();
            RefreshSelectedMarketItem();
        }
        catch (Exception ex)
        {
            MarketStatusText = $"获取详情失败：{ex.Message}";
        }
        finally
        {
            IsMarketBusy = false;
            NotifyMarketCommands();
        }
    }

    /// <summary>一键安装：add &lt;包名&gt;@&lt;版本&gt;，完成后刷新该条目的已安装状态。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateMarketItem))]
    private async Task InstallMarketPluginAsync(PluginMarketItem? item)
    {
        if (IsMarketBusy || item is null) return;

        // 市场安装同样是 pnpm add → 会重建依赖目录，执行前先过提醒闸门
        if (!ConfirmRebuildRisk("安装", AppendMarketRebuildNotice))
        {
            MarketStatusText = $"已取消安装：{item.Name}";
            return;
        }

        IsMarketBusy = true;
        try
        {
            AppLogService.Instance.Write("插件", $"开始安装插件 {item.Name}（将重建依赖目录）");
            MarketStatusText = $"正在安装：{item.Name}@{item.Version} …";
            AppendMarketLog($"▶ 开始安装 {item.Name}@{item.Version}");
            var ok = await _marketService.InstallAsync(item.Name, item.Version, AppendMarketLog);
            ApplyInstalledState();
            RefreshMarketList();
            RefreshSelectedMarketItem();
            MarketStatusText = ok ? $"✅ 安装完成：{item.Name}" : $"❌ 安装失败：{item.Name}（详见日志）";
            if (ok) AppendRestartHintToMarket();
        }
        catch (Exception ex)
        {
            MarketStatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsMarketBusy = false;
            NotifyMarketCommands();
        }
    }

    /// <summary>一键卸载，完成后刷新该条目的已安装状态。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateMarketItem))]
    private async Task UninstallMarketPluginAsync(PluginMarketItem? item)
    {
        if (IsMarketBusy || item is null) return;

        // 市场卸载同样是 pnpm remove → 会重建依赖目录，执行前先过提醒闸门
        if (!ConfirmRebuildRisk("卸载", AppendMarketRebuildNotice))
        {
            MarketStatusText = $"已取消卸载：{item.Name}";
            return;
        }

        IsMarketBusy = true;
        try
        {
            AppLogService.Instance.Write("插件", $"开始卸载插件 {item.Name}（将重建依赖目录）");
            MarketStatusText = $"正在卸载：{item.Name} …";
            AppendMarketLog($"▶ 开始卸载 {item.Name}");
            var ok = await _marketService.UninstallAsync(item.Name, AppendMarketLog);
            ApplyInstalledState();
            RefreshMarketList();
            RefreshSelectedMarketItem();
            MarketStatusText = ok ? $"✅ 卸载完成：{item.Name}" : $"❌ 卸载失败：{item.Name}（详见日志）";
            if (ok) AppendRestartHintToMarket();
        }
        catch (Exception ex)
        {
            MarketStatusText = $"卸载失败：{ex.Message}";
        }
        finally
        {
            IsMarketBusy = false;
            NotifyMarketCommands();
        }
    }

    /// <summary>用系统默认浏览器打开详情里的链接（仓库 / 主页 / npm）。</summary>
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MarketStatusText = $"打开链接失败：{ex.Message}";
        }
    }

    private bool CanSearchMarket() => !IsMarketBusy;
    private bool CanOperateMarketItem() => !IsMarketBusy;
    private bool CanPrevMarketPage() => !IsMarketBusy && HasPrevPage;
    private bool CanNextMarketPage() => !IsMarketBusy && HasNextPage;

    /// <summary>
    /// 加载市场页：按当前排序模式分支。
    /// 任何失败都只写中文状态文本，不抛异常。
    /// </summary>
    private async Task LoadMarketPageAsync()
    {
        if (IsMarketBusy) return;
        var modeAtStart = CurrentSortMode;   // 记录本次加载用的排序，结束后比对是否被用户中途改过
        IsMarketBusy = true;
        _marketLoadedOnce = true;
        try
        {
            MarketStatusText = "正在联网获取…";
            if (IsClientSortMode) await LoadClientSortedAsync();
            else await LoadServerPageAsync();
        }
        catch (Exception ex)
        {
            MarketStatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsMarketBusy = false;
            NotifyMarketCommands();
            // 加载过程中用户切换了排序（排序下拉在忙时未禁用）→ 用新排序再加载一次，避免数据与下拉不一致
            if (CurrentSortMode != modeAtStart) _ = LoadMarketPageAsync();
        }
    }

    /// <summary>
    /// 相关度模式：保持原有服务端分页行为（每页 20，from = page * size），排序交给 npm。
    /// </summary>
    private async Task LoadServerPageAsync()
    {
        // 翻页时沿用「已提交关键词」而非输入框当前内容，避免用户改了输入框没点搜索却影响翻页
        var result = await _marketService.SearchAsync(_appliedQuery, MarketPage, MarketPageSize);
        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowMarketError(result.Error);
            return;
        }

        _serverTotal = result.Total;
        _datasetItems.Clear();
        _datasetItems.AddRange(result.Items);

        ApplyInstalledState();
        RecomputePage();
        RebuildCategories();
        RefreshMarketList();
        UpdateMarketStatus();

        if (_datasetItems.Count == 0) return;

        // 搜索接口不含 dshhub → 逐页补中文简介 / 分类 / 权限，界面同步显示进度
        MarketStatusText = $"正在获取详情 0/{_datasetItems.Count}…";
        await _marketService.EnrichAsync(_datasetItems, default,
            (done, total) => OnUi(() => MarketStatusText = $"正在获取详情 {done}/{total}…"));

        ApplyCompatibility(_datasetItems);
        // 详情是在原地补全的（模型无变更通知）→ 清空选中，让用户重新点「详情」看新数据
        SelectedMarketItem = null;
        RecomputePage();
        RebuildCategories();
        RefreshMarketList();
        UpdateMarketStatus();
    }

    /// <summary>
    /// 客户端排序模式：一次拉一整批（250 条，1 个请求）→ 排序 → 客户端分页。
    /// 「下载量」还要先分批补齐下载量再排序；「更新时间」直接用 search 返回的发布时间。
    /// </summary>
    private async Task LoadClientSortedAsync()
    {
        var result = await _marketService.SearchAsync(_appliedQuery, 0, ClientSortPoolSize);
        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowMarketError(result.Error);
            return;
        }

        _serverTotal = result.Total;
        _datasetItems.Clear();
        _datasetItems.AddRange(result.Items);
        MarketPage = 0;   // 换排序 / 换关键词 → 始终回到第 1 页

        ApplyInstalledState();
        RecomputePage();
        RebuildCategories();
        RefreshMarketList();
        UpdateMarketStatus();

        if (_datasetItems.Count == 0) return;

        MarketStatusText = $"正在获取详情 0/{_datasetItems.Count}…";
        await _marketService.EnrichAsync(_datasetItems, default,
            (done, total) => OnUi(() => MarketStatusText = $"正在获取详情 {done}/{total}…"));
        ApplyCompatibility(_datasetItems);

        // 下载量排序：先分批（≤60）串行拉最近一周下载量，单批失败降级为 0
        var downloadsIncomplete = false;
        var missingPackages = 0;
        if (CurrentSortMode == MarketSortMode.Downloads)
        {
            var names = _datasetItems.Select(i => i.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var downloads = await _marketService.FetchWeeklyDownloadsAsync(names,
                (done, total) => OnUi(() => MarketStatusText = $"正在获取下载量 {done}/{total}…"),
                default,
                (failedBatches, failedCount) => OnUi(() =>
                {
                    // 失败批次的包下载量会停在 0，必须让用户知道这些 0 不是真实数据
                    downloadsIncomplete = true;
                    missingPackages = failedCount;
                    AppLogService.Instance.Info($"下载量获取不完整：{failedBatches} 批失败，约 {failedCount} 个包的下载量缺失（显示为 0）");
                }));
            foreach (var item in _datasetItems)
            {
                if (downloads.TryGetValue(item.Name, out var count)) item.Downloads = count;
            }
        }

        // 排序后整批回填（排序稳定，同值时保持 npm 原顺序）
        var sorted = DshPluginMarketService.SortByMode(_datasetItems, CurrentSortMode);
        _datasetItems.Clear();
        _datasetItems.AddRange(sorted);

        SelectedMarketItem = null;
        RecomputePage();
        RebuildCategories();
        RefreshMarketList();
        UpdateMarketStatus();

        // 把"下载量数据不完整"的说明补在状态栏，避免把缺失当成真实排名
        if (downloadsIncomplete && !string.IsNullOrWhiteSpace(MarketStatusText))
        {
            MarketStatusText += $"（注意：约 {missingPackages} 个包的下载量未取到，可能排在后面）";
        }
    }

    /// <summary>把已有数据清空并展示错误（加载失败时用，保持列表与选中不残留过期内容）。</summary>
    private void ShowMarketError(string error)
    {
        _datasetItems.Clear();
        _pageItems.Clear();
        _serverTotal = 0;
        _hiddenByCompatibility = 0;
        SelectedMarketItem = null;
        MarketTotal = 0;
        RefreshMarketList();
        MarketStatusText = error;
    }

    /// <summary>
    /// 由数据集重算当前页：先按「只看兼容」过滤，再（客户端排序模式）做客户端分页。
    /// 相关度模式的数据集本身就是服务端分页结果，此处只做兼容过滤。
    /// </summary>
    private void RecomputePage()
    {
        var visible = DshPluginMarketService.FilterByCompatibility(_datasetItems, OnlyCompatible);
        _hiddenByCompatibility = _datasetItems.Count - visible.Count;

        if (IsClientSortMode)
        {
            var size = Math.Max(1, MarketPageSize);
            var maxPage = visible.Count == 0 ? 0 : (visible.Count - 1) / size;
            if (MarketPage > maxPage) MarketPage = maxPage;   // 过滤后页数变少 → 页码夹回末页
            if (MarketPage < 0) MarketPage = 0;

            _pageItems.Clear();
            _pageItems.AddRange(DshPluginMarketService.PageItems(visible, MarketPage, MarketPageSize));
            MarketTotal = visible.Count;   // 客户端模式下"总数"= 过滤后的可见集合大小，驱动分页按钮
        }
        else
        {
            _pageItems.Clear();
            _pageItems.AddRange(visible);
            MarketTotal = _serverTotal;
        }
    }

    /// <summary>拼装市场状态行：结果统计 + 排序生效范围 + 兼容过滤提示。</summary>
    private void UpdateMarketStatus()
    {
        var parts = new List<string>();

        if (IsClientSortMode)
        {
            var sortName = CurrentSortMode == MarketSortMode.Downloads ? "下载量" : "更新时间";
            // 明确告知排序只作用于这次拉取到的这一批，避免用户误以为全局排序
            parts.Add($"已按{sortName}排序（在拉取到的前 {_datasetItems.Count} 个结果内）");
            parts.Add($"共 {_serverTotal} 个匹配，本页 {_pageItems.Count} 个");
        }
        else
        {
            parts.Add($"共 {_serverTotal} 个插件，本页 {_pageItems.Count} 个");
        }

        if (OnlyCompatible && _hiddenByCompatibility > 0)
        {
            // 相关度模式是服务端分页 → 过滤只作用于「当前页」；客户端模式作用于整批
            parts.Add(IsClientSortMode
                ? $"已隐藏 {_hiddenByCompatibility} 个不兼容插件"
                : $"已隐藏 {_hiddenByCompatibility} 个不兼容插件（仅当前页内）");
        }

        MarketStatusText = string.Join("；", parts);
    }

    /// <summary>用 profile 的 dependencies + bundles 标记数据集条目的已安装状态与版本。</summary>
    private void ApplyInstalledState()
    {
        var installed = _marketService.GetInstalledPlugins();
        foreach (var item in _datasetItems)
        {
            if (installed.TryGetValue(item.Name, out var version))
            {
                item.IsInstalled = true;
                item.InstalledVersion = version;
            }
            else
            {
                item.IsInstalled = false;
                item.InstalledVersion = string.Empty;
            }
        }
    }

    /// <summary>按本机 dsh 版本判定条目兼容性（无要求 / 无法解析 → 兼容）。</summary>
    private void ApplyCompatibility(IEnumerable<PluginMarketItem> items)
    {
        _localDshVersion ??= _marketService.GetLocalDshVersion();
        foreach (var item in items)
            item.SetCompatibility(DshPluginMarketService.IsDshCompatible(item.DshRequirement, _localDshVersion));
    }

    /// <summary>从可见数据集聚合分类到下拉（保留原顺序、去重，首项「全部」）。</summary>
    private void RebuildCategories()
    {
        var categories = new List<string> { AllCategories };
        foreach (var item in DshPluginMarketService.FilterByCompatibility(_datasetItems, OnlyCompatible))
        {
            foreach (var category in item.Categories)
            {
                if (string.IsNullOrWhiteSpace(category)) continue;
                if (!categories.Contains(category, StringComparer.Ordinal)) categories.Add(category);
            }
        }

        MarketCategories.Clear();
        foreach (var category in categories) MarketCategories.Add(category);

        // 选中的分类在新数据集里可能已不存在 → 回到「全部」（赋值会触发重新过滤）
        if (SelectedCategory is null || !categories.Contains(SelectedCategory, StringComparer.Ordinal))
            SelectedCategory = AllCategories;
    }

    /// <summary>按分类筛选重建展示集合（清空重加会触发界面刷新，使版本 / 安装状态等非通知属性同步）。</summary>
    private void RefreshMarketList()
    {
        var filter = string.IsNullOrWhiteSpace(SelectedCategory) ? AllCategories : SelectedCategory;
        MarketItems.Clear();
        foreach (var item in _pageItems)
        {
            if (filter != AllCategories && !item.Categories.Contains(filter, StringComparer.Ordinal)) continue;
            MarketItems.Add(item);
        }

        OnPropertyChanged(nameof(HasNoMarketItems));
        OnPropertyChanged(nameof(MarketPageText));
        OnPropertyChanged(nameof(HasPrevPage));
        OnPropertyChanged(nameof(HasNextPage));
    }

    /// <summary>
    /// 条目属性是原地更新的，而模型未实现 INotifyPropertyChanged →
    /// 通过「置空再赋回」同一实例，强制详情面板重新求值所有绑定。
    /// </summary>
    private void RefreshSelectedMarketItem()
    {
        var current = SelectedMarketItem;
        if (current is null) return;
        SelectedMarketItem = null;
        SelectedMarketItem = current;
    }

    /// <summary>追加一行市场日志（可能来自子进程线程 → 调度到 UI 线程）。</summary>
    private void AppendMarketLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        OnUi(() => MarketLogLines.Add(line));
    }

    /// <summary>在市场页签的状态行与日志区同时提示"需重启 dsh"（同样不自动重启）。</summary>
    private void AppendRestartHintToMarket()
    {
        MarketStatusText = $"{MarketStatusText}{Environment.NewLine}⚠️ {RestartDshHint}";
        AppendMarketLog($"⚠️ {RestartDshHint}");
    }

    /// <summary>把弱提示追加到市场页签状态文本与日志区，并写入统一日志。</summary>
    private void AppendMarketRebuildNotice(string notice)
    {
        MarketStatusText = $"{MarketStatusText}{Environment.NewLine}{notice}";
        AppendMarketLog(notice);
        AppLogService.Instance.Write("插件", notice);
    }

    /// <summary>把动作调度到 UI 线程；无 Application（单元测试 / 设计器）时直接执行。</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    /// <summary>刷新市场相关命令的可执行性。</summary>
    private void NotifyMarketCommands()
    {
        SearchMarketCommand.NotifyCanExecuteChanged();
        RefreshMarketCommand.NotifyCanExecuteChanged();
        PrevMarketPageCommand.NotifyCanExecuteChanged();
        NextMarketPageCommand.NotifyCanExecuteChanged();
        InstallMarketPluginCommand.NotifyCanExecuteChanged();
        UninstallMarketPluginCommand.NotifyCanExecuteChanged();
    }

    // ---- 属性变化 → 刷新命令可执行性 / 列表（CommunityToolkit.Mvvm 生成的 partial 钩子）----

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        NotifyUpdateCommands();
    }

    partial void OnPackageInputChanged(string value)
    {
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMarketBusyChanged(bool value) => NotifyMarketCommands();

    partial void OnMarketPageChanged(int value)
    {
        OnPropertyChanged(nameof(MarketPageText));
        OnPropertyChanged(nameof(HasPrevPage));
        OnPropertyChanged(nameof(HasNextPage));
        NotifyMarketCommands();
    }

    partial void OnMarketTotalChanged(int value)
    {
        OnPropertyChanged(nameof(HasNextPage));
        NotifyMarketCommands();
    }

    partial void OnSelectedMarketItemChanged(PluginMarketItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedMarketItem));
    }

    partial void OnSelectedCategoryChanged(string? value) => RefreshMarketList();

    /// <summary>切换排序：重置到第 1 页并重新加载（客户端排序模式需重新拉取整批数据）。</summary>
    partial void OnSelectedMarketSortOptionChanged(string value)
    {
        if (!_marketLoadedOnce) return;                       // 首次加载由页签逻辑负责
        if (!MarketSortOptions.Contains(value)) return;       // 忽略 ComboBox 初始化时的非法 / null 值
        MarketPage = 0;
        _ = LoadMarketPageAsync();
    }

    /// <summary>
    /// 勾选 / 取消「只看兼容」：只对已加载的数据集重新过滤，不重新联网。
    /// 相关度模式下的过滤仅作用于当前页（数据集 = 当前页）。
    /// </summary>
    partial void OnOnlyCompatibleChanged(bool value)
    {
        if (!_marketLoadedOnce) return;
        RecomputePage();
        RebuildCategories();
        RefreshMarketList();
        UpdateMarketStatus();
        NotifyMarketCommands();
    }
}
