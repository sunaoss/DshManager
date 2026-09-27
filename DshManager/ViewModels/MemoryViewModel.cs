using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 记忆库页视图模型，分三个分区：
/// A) 全局记忆：管理 &lt;DSH_HOME&gt;\AGENTS.md（"##" 小节 + 条目），沿用既有逻辑；
/// B) 项目级记忆：用 <see cref="AgentInstructionsService"/> 展示「项目根 → 工作目录」的指令链，
///    并可调整记忆预算（写入生效人格卡）；
/// C) 扩展记忆（大容量）：用 <see cref="LongMemoryPluginService"/> 一键安装 / 卸载插件，
///    直接编辑 &lt;DSH_HOME&gt;\MEMORY.md、调整容量与查看运行日志。
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    private readonly MemoryLibraryService _service;
    private readonly AgentInstructionsService _instructions;
    private readonly LongMemoryPluginService _longMemory;
    private readonly JsonStoreService _store;

    /// <summary>面板设置文件名（与设置页一致，用于记住项目目录）。</summary>
    private const string SettingsFileName = "settings.json";

    /// <summary>项目目录写盘防抖间隔（毫秒）：与设置页的 ScheduleSave 保持同一节奏。</summary>
    private const int ProjectDirectoryDebounceMs = 400;

    /// <summary>
    /// 项目目录写盘防抖计时器。
    /// 【为什么需要】OnProjectDirectoryChanged 是逐键触发的：每敲一个字符就同步
    /// Load + Save 一遍 settings.json，在 UI 线程上做两次磁盘 I/O，输入明显卡顿。
    /// 复用同一实例（Stop + Start 即重新计时），回调天然在 UI 线程执行，无需加锁。
    /// </summary>
    private readonly DispatcherTimer _projectDirectoryDebounceTimer;

    /// <summary>项目目录最近一次防抖到期的挂起值。</summary>
    private string _pendingProjectDirectory = string.Empty;

    /// <summary>最近一次从磁盘加载的文档模型（作为条目列表的数据来源）。</summary>
    private MemoryDocument _document = new();

    /// <summary>B 分区最近一次发现的项目根（供「打开位置」用）。</summary>
    private string _projectRoot = string.Empty;

    /// <summary>全部小节标题（文件中的出现顺序）。</summary>
    public ObservableCollection<string> Sections { get; } = [];

    /// <summary>当前小节下的条目（可逐条编辑 / 删除）。</summary>
    public ObservableCollection<MemoryItemViewModel> Items { get; } = [];

    // ==================== 分区 A：全局记忆 ====================

    /// <summary>当前选中的小节标题。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameSectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSectionCommand))]
    private string? selectedSection;

    /// <summary>小节名称输入框（用于「新建小节」和「重命名」）。</summary>
    [ObservableProperty]
    private string newSectionName = string.Empty;

    /// <summary>新增条目输入框。</summary>
    [ObservableProperty]
    private string newItemText = string.Empty;

    /// <summary>底部状态提示（已保存 / 已删除 / 校验失败等）。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>AGENTS.md 的完整路径（顶部展示）。</summary>
    [ObservableProperty]
    private string memoryFilePath = string.Empty;

    /// <summary>预算占用文本，如「已用 3.2 KB / 64 KB（5%）」。</summary>
    [ObservableProperty]
    private string budgetText = string.Empty;

    /// <summary>预算占用百分比（0~100，用于进度条）。</summary>
    [ObservableProperty]
    private double budgetPercent;

    /// <summary>是否已超出 64KB 预算（超出时进度条转为警示色）。</summary>
    [ObservableProperty]
    private bool isOverBudget;

    // ==================== 分区 B：项目级记忆 ====================

    /// <summary>项目目录（dsh 的工作目录），默认取面板设置里保存过的值，否则取 dsh 的工作目录。</summary>
    [ObservableProperty]
    private string projectDirectory = string.Empty;

    /// <summary>项目根路径说明（含是否由 .git 标记确认）。</summary>
    [ObservableProperty]
    private string projectRootText = string.Empty;

    /// <summary>指令链合计说明。</summary>
    [ObservableProperty]
    private string projectTotalText = string.Empty;

    /// <summary>当前记忆预算说明（maxBytes / maxSourceBytes / 来源）。</summary>
    [ObservableProperty]
    private string projectBudgetText = string.Empty;

    /// <summary>预算调整输入：maxBytes。</summary>
    [ObservableProperty]
    private string projectMaxBytesInput = string.Empty;

    /// <summary>预算调整输入：maxSourceBytes。</summary>
    [ObservableProperty]
    private string projectMaxSourceBytesInput = string.Empty;

    /// <summary>B 分区状态提示。</summary>
    [ObservableProperty]
    private string projectStatusText = string.Empty;

    /// <summary>项目指令链文件清单（从宽泛到具体）。</summary>
    public ObservableCollection<InstructionFile> ProjectFiles { get; } = [];

    // ==================== 分区 C：扩展记忆（大容量） ====================

    /// <summary>插件是否已安装。</summary>
    [ObservableProperty]
    private bool isLongMemoryInstalled;

    /// <summary>插件安装状态文本（已安装 / 未安装）。</summary>
    [ObservableProperty]
    private string longMemoryInstallStateText = string.Empty;

    /// <summary>插件部署说明：未安装时提示「本地部署、不联网」；已安装时展示部署位置。</summary>
    [ObservableProperty]
    private string longMemoryDeployHintText = string.Empty;

    /// <summary>记忆文件路径（配置里的 file 或 &lt;DSH_HOME&gt;\MEMORY.md）。</summary>
    [ObservableProperty]
    private string longMemoryMemoryPath = string.Empty;

    /// <summary>记忆文件全文（可直接编辑后保存）。</summary>
    [ObservableProperty]
    private string longMemoryText = string.Empty;

    /// <summary>记忆文件当前字节数说明。</summary>
    [ObservableProperty]
    private string longMemoryBytesText = string.Empty;

    /// <summary>当前生效的容量设置说明。</summary>
    [ObservableProperty]
    private string longMemoryConfigText = string.Empty;

    /// <summary>容量设置输入：maxBytes。</summary>
    [ObservableProperty]
    private string longMemoryMaxBytesInput = string.Empty;

    /// <summary>容量设置输入：maxSourceBytes。</summary>
    [ObservableProperty]
    private string longMemoryMaxSourceBytesInput = string.Empty;

    /// <summary>是否启用该插件。</summary>
    [ObservableProperty]
    private bool longMemoryEnabled;

    /// <summary>最近一次注入摘要（无日志时给出提示）。</summary>
    [ObservableProperty]
    private string longMemoryLogText = string.Empty;

    /// <summary>C 分区状态提示。</summary>
    [ObservableProperty]
    private string longMemoryStatusText = string.Empty;

    /// <summary>页面默认构造：使用真实环境的数据目录。</summary>
    public MemoryViewModel()
        : this(new MemoryLibraryService(), new AgentInstructionsService(), new LongMemoryPluginService())
    {
    }

    /// <summary>注入记忆库服务（便于测试）。</summary>
    public MemoryViewModel(MemoryLibraryService service)
        : this(service, new AgentInstructionsService(), new LongMemoryPluginService())
    {
    }

    /// <summary>注入全部三个分区的服务（便于测试 / 定制 dshHome）。</summary>
    public MemoryViewModel(
        MemoryLibraryService service,
        AgentInstructionsService instructions,
        LongMemoryPluginService longMemory)
    {
        _service = service;
        _instructions = instructions;
        _longMemory = longMemory;
        _store = AppServices.Store;

        // 项目目录写盘防抖计时器：构造一次复用，到点把最新值落盘
        _projectDirectoryDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ProjectDirectoryDebounceMs),
        };
        _projectDirectoryDebounceTimer.Tick += (_, _) => FlushProjectDirectory();

        // ---- 分区 A ----
        MemoryFilePath = service.MemoryFilePath;
        ReloadDocument();
        StatusText = _document.Sections.Count > 0
            ? $"已加载 AGENTS.md（{_document.Sections.Count} 个小节）"
            : "AGENTS.md 还没有内容，点「新增小节」开始记录";

        // ---- 分区 B：项目目录优先用面板设置里记住的值，没有则用 dsh 的工作目录 ----
        ProjectDirectory = ResolveDefaultProjectDirectory(LoadSavedProjectDirectory());
        RefreshProject();

        // ---- 分区 C ----
        RefreshLongMemory();
        LongMemoryStatusText = IsLongMemoryInstalled
            ? "插件已安装（patch 方式挂载，不出现在 dsh plugin list）"
            : "尚未安装：点「一键安装」把插件复制到 profile 并写入 patch";
    }

    /// <summary>切换小节时刷新右侧条目列表。</summary>
    partial void OnSelectedSectionChanged(string? value) => RefreshItems();

    /// <summary>项目目录变更即排队持久化（防抖 400ms，不在此处刷新指令链，避免每次敲键都读盘）。</summary>
    partial void OnProjectDirectoryChanged(string value) => ScheduleProjectDirectorySave(value);

    /// <summary>重置防抖计时器：用户停止输入约 400ms 后才真正写盘。</summary>
    private void ScheduleProjectDirectorySave(string value)
    {
        _pendingProjectDirectory = value ?? string.Empty;
        _projectDirectoryDebounceTimer.Stop();
        _projectDirectoryDebounceTimer.Start();
    }

    /// <summary>把挂起的项目目录立即落盘（防抖到期或页面离开时调用）。</summary>
    public void FlushProjectDirectory()
    {
        if (!_projectDirectoryDebounceTimer.IsEnabled)
            return;

        _projectDirectoryDebounceTimer.Stop();
        PersistProjectDirectory(_pendingProjectDirectory);
    }

    /// <summary>把项目目录写回面板设置（保留设置里的其它字段）。</summary>
    private void PersistProjectDirectory(string value)
    {
        try
        {
            // 读-改-写必须在 JsonStoreService 的写锁内完成，否则会覆盖同期写入的其它设置项
            _store.Update<AppSettings>(SettingsFileName, settings =>
            {
                settings.ProjectScopeDirectory = value ?? string.Empty;
            });
        }
        catch
        {
            // 设置写盘失败不影响记忆库主功能
        }
    }

    // ---- 小节命令 ----

    /// <summary>用输入框里的名称新建小节（追加到文件末尾）。</summary>
    [RelayCommand]
    private void AddSection()
    {
        var name = NewSectionName.Trim();
        if (name.Length == 0)
        {
            StatusText = "请先输入小节名称";
            return;
        }

        if (!_service.AddSection(name))
        {
            StatusText = $"新建失败：小节「{name}」已存在，或文件写入失败";
            return;
        }

        NewSectionName = string.Empty;
        ReloadDocument(keepSection: name);
        StatusText = $"已新建小节「{name}」{SaveSuffix}";
    }

    /// <summary>把当前选中小节重命名为输入框里的名称。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedSection))]
    private void RenameSection()
    {
        var oldName = SelectedSection!;
        var name = NewSectionName.Trim();
        if (name.Length == 0)
        {
            StatusText = "请先输入新的小节名称";
            return;
        }

        if (!_service.RenameSection(oldName, name))
        {
            StatusText = $"重命名失败：名称「{name}」已被占用，或文件写入失败";
            return;
        }

        NewSectionName = string.Empty;
        ReloadDocument(keepSection: name);
        StatusText = $"已将「{oldName}」重命名为「{name}」{SaveSuffix}";
    }

    /// <summary>删除当前选中的小节（连同其下条目）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedSection))]
    private void DeleteSection()
    {
        var name = SelectedSection!;
        if (!_service.RemoveSection(name))
        {
            StatusText = $"删除失败：小节「{name}」不存在";
            return;
        }

        ReloadDocument();
        StatusText = $"已删除小节「{name}」{SaveSuffix}";
    }

    private bool HasSelectedSection() => SelectedSection is not null;

    // ---- 条目命令 ----

    /// <summary>把输入框里的内容加入当前小节（支持一次输入多行）。</summary>
    [RelayCommand]
    private void AddItem()
    {
        if (SelectedSection is null)
        {
            StatusText = "请先在左侧选择或新建一个小节";
            return;
        }

        var text = NewItemText.Trim();
        if (text.Length == 0)
        {
            StatusText = "请先输入条目内容";
            return;
        }

        if (!_service.AddItem(SelectedSection, text))
        {
            StatusText = "添加失败：文件写入失败";
            return;
        }

        NewItemText = string.Empty;
        ReloadDocument(keepSection: SelectedSection);
        StatusText = $"已添加条目{SaveSuffix}";
    }

    /// <summary>保存某条被内联编辑的条目（由条目视图模型回调）。</summary>
    private void SaveItem(MemoryItemViewModel item)
    {
        if (SelectedSection is null) return;

        var text = item.Text.Trim();
        if (text.Length == 0)
        {
            StatusText = "条目内容不能为空";
            return;
        }
        if (string.Equals(text, item.OriginalText, StringComparison.Ordinal))
        {
            StatusText = "内容没有变化";
            return;
        }

        var section = SelectedSection;
        if (!_service.UpdateItem(section, item.OriginalText, text))
        {
            StatusText = "更新失败：条目可能已被删除";
            return;
        }

        ReloadDocument(keepSection: section);
        StatusText = $"已更新条目{SaveSuffix}";
    }

    /// <summary>删除某条条目（由条目视图模型回调）。</summary>
    private void DeleteItem(MemoryItemViewModel item)
    {
        if (SelectedSection is null) return;

        var section = SelectedSection;
        if (!_service.RemoveItem(section, item.OriginalText))
        {
            StatusText = "删除失败：条目可能已被删除";
            return;
        }

        ReloadDocument(keepSection: section);
        StatusText = $"已删除条目{SaveSuffix}";
    }

    // ---- 分区 B 命令：项目级记忆 ----

    /// <summary>浏览选择项目目录；选中后立即刷新指令链。</summary>
    [RelayCommand]
    private void BrowseProjectDirectory()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择项目目录（dsh 的工作目录）" };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

            ProjectDirectory = dialog.FolderName;   // 变更会触发持久化
            RefreshProject();
            ProjectStatusText = $"已切换到项目目录：{dialog.FolderName}";
        }
        catch (Exception ex)
        {
            ProjectStatusText = $"打开目录选择失败：{ex.Message}";
        }
    }

    /// <summary>重新发现指令链并刷新预算信息。</summary>
    [RelayCommand]
    private void RefreshProject()
    {
        var dir = ProjectDirectory?.Trim() ?? string.Empty;
        ProjectFiles.Clear();

        if (dir.Length == 0)
        {
            ProjectRootText = "未设置项目目录：请输入，或用「📂 浏览…」选择 dsh 的工作目录";
            ProjectTotalText = string.Empty;
            ProjectBudgetText = string.Empty;
            return;
        }
        if (!Directory.Exists(dir))
        {
            ProjectRootText = $"目录不存在：{dir}";
            ProjectTotalText = string.Empty;
            ProjectBudgetText = string.Empty;
            return;
        }

        var chain = _instructions.DiscoverChain(dir);
        foreach (var file in chain.Files) ProjectFiles.Add(file);

        _projectRoot = chain.ProjectRoot;
        ProjectRootText = chain.ProjectRootFound
            ? $"项目根：{chain.ProjectRoot}（由 .git 标记确认）"
            : $"项目根：{chain.ProjectRoot}（未找到 .git 标记，以输入目录为起点）";
        ProjectTotalText = chain.Files.Count == 0
            ? "该项目目录链下没有 AGENTS.md / CLAUDE.md 指令文件"
            : $"指令链合计 {AgentInstructionsService.FormatSize(chain.TotalBytes)} · 共 {chain.Files.Count} 个文件";

        // 预算：读当前生效值填入输入框，方便用户在此基础上调整
        var budget = _instructions.ReadBudget();
        ProjectBudgetText = $"当前预算：maxBytes {AgentInstructionsService.FormatSize(budget.MaxBytes)} · "
                          + $"maxSourceBytes {AgentInstructionsService.FormatSize(budget.MaxSourceBytes)} · 来源：{budget.SourceDescription}"
                          + (budget.IsDefault ? "（未在生效人格卡里显式配置）" : string.Empty);
        ProjectMaxBytesInput = budget.MaxBytes.ToString(CultureInfo.InvariantCulture);
        ProjectMaxSourceBytesInput = budget.MaxSourceBytes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>保存记忆预算：写入当前生效的人格卡（如实提示结果与需重启）。</summary>
    [RelayCommand]
    private void SaveProjectBudget()
    {
        if (!int.TryParse(ProjectMaxBytesInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxBytes))
        {
            ProjectStatusText = "maxBytes 必须是整数";
            return;
        }
        if (!int.TryParse(ProjectMaxSourceBytesInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxSourceBytes))
        {
            ProjectStatusText = "maxSourceBytes 必须是整数";
            return;
        }

        var ok = _instructions.WriteBudget(maxBytes, maxSourceBytes, out var message);
        // 服务层的 message 已说明写到哪张人格卡 / 为何拒绝；成功时补上「需重启」提示
        ProjectStatusText = ok ? message + "；需重启 dsh 生效" : message;
        RefreshProject();
    }

    /// <summary>在资源管理器中打开项目根（找不到项目根时用输入目录）。</summary>
    [RelayCommand]
    private void OpenProjectRoot()
    {
        var target = string.IsNullOrWhiteSpace(_projectRoot) ? ProjectDirectory : _projectRoot;
        ProjectStatusText = TryOpenInExplorer(target, out var error)
            ? $"已在资源管理器中打开：{target}"
            : error;
    }

    /// <summary>在资源管理器中定位某个指令文件。</summary>
    [RelayCommand]
    private void OpenInstructionFile(InstructionFile? file)
    {
        if (file is null) return;
        ProjectStatusText = TryOpenInExplorer(file.Path, out var error) ? $"已定位：{file.Path}" : error;
    }

    // ---- 分区 C 命令：扩展记忆（大容量插件） ----

    /// <summary>一键安装插件（复制目录 + 写 patch insert）。</summary>
    [RelayCommand]
    private void InstallLongMemory()
    {
        _longMemory.Install(out var message);   // 成功/失败消息服务层已是中文说明
        LongMemoryStatusText = message;
        RefreshLongMemory();
    }

    /// <summary>卸载插件（移除 patch insert + 删除插件目录与日志）。</summary>
    [RelayCommand]
    private void UninstallLongMemory()
    {
        _longMemory.Uninstall(out var message);
        LongMemoryStatusText = message;
        RefreshLongMemory();
    }

    /// <summary>保存大文本编辑器里的记忆全文（写前自动备份）。</summary>
    [RelayCommand]
    private void SaveLongMemoryText()
    {
        _longMemory.WriteMemory(LongMemoryText ?? string.Empty, out var message);
        LongMemoryStatusText = message;
        RefreshLongMemory();
    }

    /// <summary>保存容量设置到 settings.yaml（热生效，无需重启）。</summary>
    [RelayCommand]
    private void SaveLongMemoryConfig()
    {
        if (!int.TryParse(LongMemoryMaxBytesInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxBytes))
        {
            LongMemoryStatusText = "maxBytes 必须是整数";
            return;
        }
        if (!int.TryParse(LongMemoryMaxSourceBytesInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxSourceBytes))
        {
            LongMemoryStatusText = "maxSourceBytes 必须是整数";
            return;
        }

        _longMemory.WriteConfig(maxBytes, maxSourceBytes, LongMemoryEnabled, out var message);
        LongMemoryStatusText = message;
        RefreshLongMemory();
    }

    /// <summary>重新读取插件状态、记忆文件、容量与日志。</summary>
    [RelayCommand]
    private void RefreshLongMemory()
    {
        IsLongMemoryInstalled = _longMemory.IsInstalled();
        LongMemoryInstallStateText = IsLongMemoryInstalled ? "已安装" : "未安装";
        // 部署说明随安装状态切换：已装则显示实际部署位置，未装则说明这是内置插件的本地部署
        LongMemoryDeployHintText = IsLongMemoryInstalled
            ? $"已部署到 {_longMemory.PluginInstallDirectory}"
            : "面板已内置此插件，点「一键安装」会从面板程序目录本地部署到 dsh（不联网、不走市场）";
        LongMemoryMemoryPath = _longMemory.MemoryFilePath;

        var bytes = _longMemory.GetMemoryBytes();
        LongMemoryBytesText = $"当前 {AgentInstructionsService.FormatSize(bytes)}（{bytes} 字节）";
        LongMemoryText = _longMemory.ReadMemory();

        var config = _longMemory.ReadConfig();
        LongMemoryMaxBytesInput = config.MaxBytes.ToString(CultureInfo.InvariantCulture);
        LongMemoryMaxSourceBytesInput = config.MaxSourceBytes.ToString(CultureInfo.InvariantCulture);
        LongMemoryEnabled = config.Enabled;
        LongMemoryConfigText = $"当前生效：maxBytes {AgentInstructionsService.FormatSize(config.MaxBytes)} · "
                             + $"maxSourceBytes {AgentInstructionsService.FormatSize(config.MaxSourceBytes)} · "
                             + (config.Enabled ? "已启用" : "已停用") + " · 来源：" + config.SourceDescription;

        LongMemoryLogText = BuildLogSummary();
    }

    /// <summary>在资源管理器中定位记忆文件（文件不存在时打开其所在目录）。</summary>
    [RelayCommand]
    private void OpenLongMemoryFile()
    {
        var path = _longMemory.MemoryFilePath;
        var target = File.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        LongMemoryStatusText = TryOpenInExplorer(target, out var error)
            ? $"已在资源管理器中定位：{target}"
            : error;
    }

    /// <summary>在资源管理器中定位插件运行日志（日志不存在时打开其所在目录）。</summary>
    [RelayCommand]
    private void OpenLongMemoryLog()
    {
        var path = _longMemory.LogFilePath;
        var target = File.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        LongMemoryStatusText = TryOpenInExplorer(target, out var error)
            ? $"已在资源管理器中定位：{target}"
            : error;
    }

    // ---- 数据刷新 ----

    /// <summary>
    /// 从磁盘重新加载文档并刷新小节 / 条目列表与预算信息。
    /// </summary>
    /// <param name="keepSection">要恢复选中的小节标题；为空则尽量沿用原选中，否则选第一个。</param>
    private void ReloadDocument(string? keepSection = null)
    {
        _document = _service.Load();

        var target = keepSection ?? SelectedSection;
        Sections.Clear();
        foreach (var section in _document.Sections) Sections.Add(section.Heading);

        // 选中项：优先保留目标小节 → 原选中 → 第一个；值未变化时不会触发 OnChanged，下面再显式刷新一次
        SelectedSection = target is not null && Sections.Contains(target)
            ? target
            : Sections.Count > 0 ? Sections[0] : null;

        RefreshItems();
        RefreshBudget();
    }

    /// <summary>按当前选中刷新右侧条目列表（每条包成可编辑的条目视图模型）。</summary>
    private void RefreshItems()
    {
        Items.Clear();
        var section = SelectedSection is null
            ? null
            : _document.Sections.FirstOrDefault(s => s.Heading == SelectedSection);
        if (section is null) return;

        foreach (var text in section.Items)
        {
            Items.Add(new MemoryItemViewModel(text, text, SaveItem, DeleteItem));
        }
    }

    /// <summary>刷新预算进度条与文本。</summary>
    private void RefreshBudget()
    {
        var info = _service.GetBudgetInfo();
        BudgetPercent = Math.Min(100, info.Percent);   // 进度条封顶 100%
        IsOverBudget = info.IsOverBudget;
        BudgetText = $"已用 {info.Bytes / 1024.0:0.0} KB / {info.BudgetBytes / 1024.0:0} KB（{info.Percent:0}%）";
    }

    /// <summary>状态提示后缀：曾生成备份时标注「已备份」。</summary>
    private string SaveSuffix => _service.LastBackupPath is null ? " ✓" : " ✓（已备份）";

    // ---- 项目目录持久化 ----

    /// <summary>读取面板设置里记住的项目目录；失败返回空串。</summary>
    private string LoadSavedProjectDirectory()
    {
        try
        {
            return _store.Load<AppSettings>(SettingsFileName).ProjectScopeDirectory ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 默认项目目录：优先用面板设置里的值；为空时退回 dsh 的工作目录（找不到则留空由用户选择）。
    /// </summary>
    private static string ResolveDefaultProjectDirectory(string saved)
    {
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        try
        {
            return AppServices.Discovery.Scan().WorkDir ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---- 扩展记忆辅助 ----

    /// <summary>
    /// 解析最近一次注入摘要：从尾部往前找 [inject] 行，提取 turn / step / 注入字节数；
    /// 没有日志时给出提示文案。
    /// </summary>
    private string BuildLogSummary()
    {
        var lines = _longMemory.ReadRecentLogLines(50);
        if (lines.Count == 0) return "暂无运行记录（启动 dsh 并发一条消息后会出现）";

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("[inject]", StringComparison.Ordinal)) continue;
            return "上次注入：" + ParseInjectSummary(lines[i]);
        }
        return "插件已加载，但还没有注入记录（发一条消息后会出现）";
    }

    /// <summary>从一行日志里提取注入摘要；格式不符时原样返回，避免丢信息。</summary>
    private static string ParseInjectSummary(string line)
    {
        var turn = Regex.Match(line, "\"turn\":(\\d+)");
        var step = Regex.Match(line, "\"step\":(\\d+)");
        var bytes = Regex.Match(line, "\"injectedBytes\":(\\d+)");
        if (!turn.Success || !step.Success || !bytes.Success) return line;

        var truncated = line.Contains("\"truncated\":true", StringComparison.Ordinal) ? "（已截断）" : string.Empty;
        return $"turn {turn.Groups[1].Value} / step {step.Groups[1].Value} / 注入 {bytes.Groups[1].Value} 字节{truncated}";
    }

    /// <summary>用资源管理器打开目录 / 定位文件；异常安全，失败给出中文原因。</summary>
    private static bool TryOpenInExplorer(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空，无法打开";
            return false;
        }

        try
        {
            // 目录直接打开；文件用 /select 在资源管理器中选中
            var arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"打开位置失败：{ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// 单条记忆条目的视图模型：内联编辑正文，并携带原文标识用于定位更新 / 删除。
/// </summary>
public partial class MemoryItemViewModel : ObservableObject
{
    private readonly Action<MemoryItemViewModel> _save;
    private readonly Action<MemoryItemViewModel> _delete;

    /// <summary>编辑框中的条目正文（不含 "- " 前缀）。</summary>
    [ObservableProperty]
    private string text;

    /// <summary>该条目在文件中的原文（不含 "- " 前缀），用于定位更新 / 删除。</summary>
    [ObservableProperty]
    private string originalText;

    public MemoryItemViewModel(
        string text,
        string originalText,
        Action<MemoryItemViewModel> save,
        Action<MemoryItemViewModel> delete)
    {
        this.text = text;
        this.originalText = originalText;
        _save = save;
        _delete = delete;
    }

    /// <summary>保存本条编辑结果（回调父视图模型写盘）。</summary>
    [RelayCommand]
    private void Save() => _save(this);

    /// <summary>删除本条（回调父视图模型写盘）。</summary>
    [RelayCommand]
    private void Delete() => _delete(this);
}
