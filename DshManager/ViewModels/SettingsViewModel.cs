using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 设置页视图模型：管理壁纸相关设置。
/// 属性变更时即时持久化到 Data/settings.json，同时主窗口订阅其变更使背景实时刷新。
/// 另外负责「dsh 安装位置（锁定）」的查看 / 更改 / 重新搜索，以及「移动 dsh 安装位置」。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private const string SettingsFileName = "settings.json";

    /// <summary>连续变化属性的写盘防抖延迟（毫秒）。</summary>
    private const int SaveDebounceMilliseconds = 400;

    private readonly JsonStoreService _store;
    private readonly string _dataDirectory;
    private readonly DshDiscoveryService _discovery;
    private readonly DshProcessService _process;

    /// <summary>
    /// 锁定位置是否被"用户在设置页显式改过"。
    /// 【为什么要这个标志】锁定位置也会被首页扫描自动更新（首次自动锁定 / 失效后改指新位置）；
    /// 若设置页无脑把内存副本写回，就可能把面板刚更新好的锁定位置冲掉。
    /// 因此规定：只有用户显式改动时才用内存值，否则写盘时沿用磁盘上的最新值。
    /// </summary>
    private bool _lockedPathDirty;

    /// <summary>正在把磁盘值同步进属性（此时不算"用户改动"，不置脏）。</summary>
    private bool _syncingLockedPath;

    /// <summary>搬运任务的取消源（未在搬运时为 null）；「取消搬运」按钮据此取消。</summary>
    private CancellationTokenSource? _moveCts;

    /// <summary>
    /// 连续变化属性（模糊度 / 透明度）的防抖计时器。
    /// 拖动滑块时值每秒变化几十次，若每次都同步写盘（File.WriteAllText）会造成明显卡顿；
    /// 这里复用同一个 DispatcherTimer，每次变化 Stop() + Start() 即重置计时，
    /// 只有连续约 400ms 没有新变化时才真正写盘 —— 拖动过程只写最后一次。
    /// 使用 DispatcherTimer 是因其回调天然在 UI 线程执行，与属性变更、_store 访问同线程，无需额外加锁。
    /// </summary>
    private readonly DispatcherTimer _saveDebounceTimer;

    /// <summary>壁纸图片路径（绝对路径或相对 Data 目录）</summary>
    [ObservableProperty]
    private string wallpaperPath;

    /// <summary>是否启用壁纸</summary>
    [ObservableProperty]
    private bool isWallpaperEnabled;

    /// <summary>壁纸模糊度（0 ~ 30，直接映射 BlurEffect.Radius）</summary>
    [ObservableProperty]
    private double wallpaperBlur;

    /// <summary>壁纸透明度（0.3 ~ 1.0）</summary>
    [ObservableProperty]
    private double wallpaperOpacity;

    /// <summary>设置页预览缩略图（无壁纸时为 null）</summary>
    [ObservableProperty]
    private ImageSource? wallpaperPreview;

    /// <summary>dsh Web UI 监听端口（默认 3080，修改后需重启 dsh 生效）</summary>
    [ObservableProperty]
    private int port;

    /// <summary>手动指定的 dsh 安装位置（@deepseek-ai\dsh 包目录或其上级 node_modules，或启动器文件）；空 = 自动搜索</summary>
    [ObservableProperty]
    private string installPathOverride = string.Empty;

    /// <summary>
    /// 面板已锁定（记住）的 dsh 安装位置（@deepseek-ai\dsh 包目录）；空 = 未锁定（自动搜索并自动锁定）。
    /// </summary>
    [ObservableProperty]
    private string lockedDshInstallPath = string.Empty;

    /// <summary>「dsh 安装位置（锁定）」区域的状态 / 结果提示文本（中性文案，不弹错误框）。</summary>
    [ObservableProperty]
    private string installLocationStatusText = string.Empty;

    /// <summary>搬运任务是否进行中（驱动进度区显隐与按钮禁用）。</summary>
    [ObservableProperty]
    private bool isMoveRunning;

    /// <summary>搬运进度百分比（0~100）。</summary>
    [ObservableProperty]
    private int moveProgressPercent;

    /// <summary>搬运过程的文字进度（如「共 N 个文件，开始复制到 …」）。</summary>
    [ObservableProperty]
    private string moveProgressText = string.Empty;

    /// <summary>搬运进度区是否可见（搬运中才显示）。</summary>
    [ObservableProperty]
    private Visibility moveProgressVisibility = Visibility.Collapsed;

    /// <summary>锁定位置的展示文本（未锁定时给出明确提示，避免界面出现空白行）。</summary>
    public string LockedDshInstallPathText
        => string.IsNullOrWhiteSpace(LockedDshInstallPath) ? "（未锁定 · 自动搜索）" : LockedDshInstallPath;

    public SettingsViewModel(JsonStoreService store, DshDiscoveryService? discovery = null, DshProcessService? process = null)
    {
        _store = store;
        _dataDirectory = store.DataDirectory;
        _discovery = discovery ?? AppServices.Discovery;
        _process = process ?? AppServices.Process;

        // 启动时从磁盘加载上次保存的设置
        var settings = _store.Load<AppSettings>(SettingsFileName);
        wallpaperPath = settings.WallpaperPath;
        isWallpaperEnabled = settings.IsWallpaperEnabled;
        wallpaperBlur = settings.WallpaperBlur;
        wallpaperOpacity = settings.WallpaperOpacity;
        // 旧配置可能没有 Port 字段（反序列化后为 0）：回退到默认端口，避免出现非法端口
        port = settings.Port > 0 ? settings.Port : DshProcessService.DefaultPort;
        // 旧配置可能没有该字段：反序列化为 null 时归一为空字符串
        installPathOverride = settings.InstallPathOverride ?? string.Empty;
        lockedDshInstallPath = settings.DshInstallPathOverride ?? string.Empty;

        // 构造一次可复用的防抖计时器；到点即把挂起的最新设置落盘
        _saveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SaveDebounceMilliseconds),
        };
        _saveDebounceTimer.Tick += (_, _) => FlushPending();

        ReloadPreview();
    }

    /// <summary>壁纸文件名（用于界面展示，未选择时显示提示）</summary>
    public string WallpaperFileName =>
        string.IsNullOrWhiteSpace(WallpaperPath) ? "未选择壁纸" : Path.GetFileName(WallpaperPath);

    /// <summary>
    /// 解析壁纸绝对路径：优先使用原路径，其次尝试相对 Data 目录。
    /// 主窗口加载壁纸时复用本方法，避免两处重复实现。
    /// </summary>
    public string? ResolveWallpaperPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return path;

        // 相对路径视为相对数据目录（如 "test_wallpaper.png"）
        var dataCandidate = Path.Combine(_dataDirectory, path);
        return File.Exists(dataCandidate) ? dataCandidate : null;
    }

    /// <summary>打开文件对话框选择壁纸图片。</summary>
    [RelayCommand]
    private void PickWallpaper()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择壁纸图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            WallpaperPath = dialog.FileName;
        }
    }

    /// <summary>
    /// 浏览选择 dsh 安装位置（手动指定项）。
    /// 优先用文件夹选择对话框（通常指向 @deepseek-ai\dsh 包目录或其上级 node_modules）；
    /// 对话框不可用时回退到文件对话框（可选中 dsh.cmd 等启动器）。
    /// </summary>
    [RelayCommand]
    private void PickInstallPath()
    {
        var picked = PickInstallLocationInteractive(
            "选择 dsh 安装位置（@deepseek-ai\\dsh 包目录，或其上级 node_modules 目录）",
            "选择 dsh 启动器或包内 package.json");
        if (!string.IsNullOrWhiteSpace(picked)) InstallPathOverride = picked;
    }

    /// <summary>清除手动指定的安装位置，回到自动搜索。</summary>
    [RelayCommand]
    private void ClearInstallPath() => InstallPathOverride = string.Empty;

    // ==================== dsh 安装位置（锁定） ====================

    /// <summary>
    /// 浏览并锁定一个 dsh 安装位置。
    /// 用户可能选到包目录本身、其上级 node_modules 目录：这里统一归一为 @deepseek-ai\dsh 包目录再锁定，
    /// 因为"锁定值"必须同时含 package.json 与 lib\bin.js，否则下次启动会被判失效。
    /// </summary>
    [RelayCommand]
    private void PickLockedInstallPath()
    {
        var picked = PickInstallLocationInteractive(
            "选择要锁定的 dsh 安装位置（@deepseek-ai\\dsh 包目录，或其上级 node_modules 目录）",
            "选择 dsh 启动器或包内 package.json");
        if (string.IsNullOrWhiteSpace(picked)) return;

        var installDir = TryNormalizeToInstallDir(picked);
        if (installDir is null)
        {
            InstallLocationStatusText = "❌ 该位置没有找到可用的 dsh（需要含 package.json 与 lib\\bin.js 的 @deepseek-ai\\dsh 目录）";
            return;
        }

        LockedDshInstallPath = installDir;
        _discovery.SaveLockedInstallPath(installDir);
        InstallLocationStatusText = $"✅ 已锁定安装位置：{installDir}（下次启动直接用它，不再搜索）";
    }

    /// <summary>清除锁定，改为自动搜索（下一次启动 / 刷新时会自动搜索并重新锁定）。</summary>
    [RelayCommand]
    private void ClearLockedInstallPath()
    {
        if (IsMoveRunning) return;

        LockedDshInstallPath = string.Empty;
        _discovery.SaveLockedInstallPath(string.Empty);
        InstallLocationStatusText = "已清除锁定：下次启动 / 刷新时面板会自动搜索并重新锁定第一个可用位置。";
    }

    /// <summary>
    /// 重新搜索并锁定可用的 dsh 安装位置。
    /// 复用发现服务的锁定语义（传空锁定值 = 强制重新搜索），搜到即自动锁定；搜不到只给提示，不弹错误框。
    /// 搜索会枚举 npx 缓存 / profiles 等目录，放后台线程执行避免卡住界面。
    /// </summary>
    [RelayCommand]
    private async Task RescanInstallLocationAsync()
    {
        if (IsMoveRunning) return;

        InstallLocationStatusText = "正在搜索 dsh 安装位置…";
        DshInstallLocationResult result;
        try
        {
            result = await Task.Run(() => _discovery.ResolveInstallLocation(string.Empty));
        }
        catch (Exception ex)
        {
            InstallLocationStatusText = $"搜索失败：{ex.Message}";
            return;
        }

        if (result.EffectiveInstallation is null)
        {
            InstallLocationStatusText = string.IsNullOrWhiteSpace(result.Message)
                ? "❌ 未找到可用的 dsh 安装位置。"
                : $"❌ {result.Message}";
            return;
        }

        LockedDshInstallPath = result.EffectiveInstallation.PackageDir;
        _discovery.SaveLockedInstallPath(LockedDshInstallPath);
        InstallLocationStatusText = $"✅ {result.Message}";
    }

    /// <summary>
    /// 从磁盘重新读取锁定位置并刷新界面（设置页每次显示时调用）。
    /// 【为什么需要】首页扫描与搬运也会更新锁定位置，本 VM 缓存的副本可能已过期；
    /// 重新读取可避免把过期值再写回配置文件（冲掉面板刚更新的锁定位置）。
    /// </summary>
    public void RefreshInstallLocationInfo()
    {
        try
        {
            var disk = ReadLockedPathFromDisk();
            if (!string.Equals(disk, LockedDshInstallPath, StringComparison.OrdinalIgnoreCase))
            {
                _syncingLockedPath = true; // 这是"同步磁盘值"，不是用户显式修改
                LockedDshInstallPath = disk;
                _syncingLockedPath = false;
            }
        }
        catch { /* 读盘失败保持现状 */ }

        InstallLocationStatusText = string.IsNullOrWhiteSpace(LockedDshInstallPath)
            ? "当前未锁定：面板会自动搜索并锁定第一个可用位置。"
            : $"当前锁定：{LockedDshInstallPath}";
    }

    // ==================== 移动 dsh 安装位置 ====================

    /// <summary>「移动 dsh 安装位置」是否可用：有可用的锁定位置、且没有搬运正在进行。</summary>
    private bool CanMoveDsh() => !IsMoveRunning && DshDiscoveryService.ValidateInstallDir(LockedDshInstallPath);

    /// <summary>
    /// 把整个 dsh 安装目录搬运到用户选定的目标文件夹：
    /// 选目标 → 提示正在运行需先停止 → 确认（含实际大小）→ 后台复制（带进度、可取消）→ 校验 → 删源 → 修复链接 → 更新锁定位置。
    /// 任何失败都会给出中文原因；复制阶段失败 / 取消不会删除源目录（由服务层保证）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveDsh))]
    private async Task MoveDshInstallAsync()
    {
        var sourceDir = LockedDshInstallPath;
        if (string.IsNullOrWhiteSpace(sourceDir) || !DshDiscoveryService.ValidateInstallDir(sourceDir))
        {
            InstallLocationStatusText = "请先「重新搜索」并锁定一个可用的 dsh 安装位置，再进行搬运。";
            return;
        }

        var targetParent = PickFolder("选择 dsh 新位置的目标文件夹（会在其中创建与源同名的目录）");
        if (string.IsNullOrWhiteSpace(targetParent)) return;

        var plan = DshDiscoveryService.BuildMovePlan(sourceDir, targetParent);
        if (!plan.IsValid)
        {
            InstallLocationStatusText = $"❌ {plan.Reason}";
            return;
        }

        // 正在运行的 dsh 会占用文件，导致复制失败：先提醒用户停止，绝不擅自 kill 进程
        if (IsDshProbablyRunning())
        {
            var proceed = Confirm(
                "检测到 dsh 似乎正在运行。\n\n" +
                "搬运过程中请先停止 dsh（否则文件可能被占用导致复制失败）。\n" +
                "面板不会替你结束进程，请确认已停止后再继续。\n\n" +
                "确认已停止并继续？",
                "dsh 正在运行");
            if (!proceed) return;
        }

        MoveProgressVisibility = Visibility.Visible;
        MoveProgressText = "正在统计待搬运大小…";

        // 体积统计要递归遍历上千文件，放后台线程，避免卡住界面
        var sizeBytes = await Task.Run(() => DshDiscoveryService.GetDirectorySizeSafe(plan.SourceRoot));

        var confirmed = Confirm(
            "将把整个 dsh 安装目录复制到新位置：\n\n" +
            $"源：{plan.SourceRoot}\n" +
            $"目标：{plan.DestRoot}\n" +
            $"大小：约 {FormatSize(sizeBytes)}（含 node_modules）\n\n" +
            "搬运期间【请勿启动 dsh】。完成后：面板会更新锁定位置、修复受影响的链接，并清理源目录。\n" +
            "（采用「先复制 → 校验 → 再删源」的方式，中途失败不会导致两边都不完整）\n\n是否继续？",
            "确认移动 dsh 安装位置");
        if (!confirmed)
        {
            MoveProgressVisibility = Visibility.Collapsed;
            MoveProgressText = string.Empty;
            return;
        }

        var moveCts = new CancellationTokenSource();
        _moveCts = moveCts;
        IsMoveRunning = true;
        MoveProgressPercent = 0;
        MoveProgressText = "开始搬运…";
        InstallLocationStatusText = "正在搬运 dsh 安装目录…";
        AppLogService.Instance.Write("设置", $"开始搬运 dsh：{plan.SourceRoot} → {plan.DestRoot}");

        try
        {
            // Progress<T> 在 UI 线程创建，回调会自动切回 UI 线程；Action<string> 由 AppendMoveLog 自行调度
            var progress = new Progress<double>(p => MoveProgressPercent = (int)Math.Clamp(p * 100, 0, 100));
            var result = await Task.Run(() =>
                _discovery.MoveInstall(sourceDir, targetParent, progress, AppendMoveLog, moveCts.Token));

            if (!result.Success)
            {
                InstallLocationStatusText = result.Message;
                AppLogService.Instance.Write("设置", result.Message);
                return;
            }

            // 成功：更新锁定位置 + 把面板自身记录的旧路径引用一并重映射 + 落盘
            _lockedPathDirty = true;
            LockedDshInstallPath = result.NewPackageDir ?? LockedDshInstallPath;
            if (!string.IsNullOrWhiteSpace(InstallPathOverride))
            {
                InstallPathOverride = DshDiscoveryService.RemapPathUnder(
                    InstallPathOverride, plan.SourceRoot, plan.DestRoot);
            }
            _discovery.SaveLockedInstallPath(LockedDshInstallPath);

            InstallLocationStatusText = $"✅ {result.Message}（回首页点「刷新」即可用新位置启动）";
            AppLogService.Instance.Write("设置", result.Message);
        }
        catch (Exception ex)
        {
            InstallLocationStatusText = $"❌ 搬运失败：{ex.Message}（源目录未删除，可重试）";
            AppLogService.Instance.Write("设置", $"搬运失败：{ex.Message}");
        }
        finally
        {
            moveCts.Dispose();
            _moveCts = null;
            IsMoveRunning = false;
            MoveProgressVisibility = Visibility.Collapsed;
            MoveProgressText = string.Empty;
            MoveDshInstallCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>「取消搬运」是否可用：仅搬运进行中。</summary>
    private bool CanCancelMove() => IsMoveRunning;

    /// <summary>取消正在进行的搬运（取消后服务层会清理目标目录、保留源目录）。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelMove))]
    private void CancelMove()
    {
        try { _moveCts?.Cancel(); }
        catch { /* 已释放 / 已取消：忽略 */ }
        MoveProgressText = "正在取消…（会清理目标目录，源目录保持不动）";
    }

    // ==================== 安装位置相关的工具方法 ====================

    /// <summary>
    /// 交互式选择路径：优先文件夹对话框；对话框在个别系统上不可用时回退到文件对话框。
    /// 抽成共用实现，避免「手动指定」与「锁定位置」两处重复。
    /// 用户取消时返回 null。
    /// </summary>
    private static string? PickInstallLocationInteractive(string folderTitle, string fileTitle)
    {
        try
        {
            var folderDialog = new Microsoft.Win32.OpenFolderDialog { Title = folderTitle };
            if (folderDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(folderDialog.FolderName))
                return folderDialog.FolderName;
            return null;
        }
        catch
        {
            // OpenFolderDialog 不可用：退回文件对话框选启动器 / package.json
            var fileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = fileTitle,
                Filter = "dsh 启动器 / 包文件 (dsh*.cmd;dsh*.exe;dsh*.bat;dsh;package.json)|dsh*.cmd;dsh*.exe;dsh*.bat;dsh;package.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            return fileDialog.ShowDialog() == true ? fileDialog.FileName : null;
        }
    }

    /// <summary>选择目标文件夹（仅文件夹对话框）；用户取消或对话框不可用时返回 null。</summary>
    private static string? PickFolder(string title)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
                ? dialog.FolderName
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把用户选中的路径归一为「@deepseek-ai\dsh 包目录」：
    /// 支持直接选包目录，或选其上级 node_modules 目录；都不是则返回 null。
    /// </summary>
    private static string? TryNormalizeToInstallDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (DshDiscoveryService.ValidateInstallDir(path)) return Path.GetFullPath(path);

        var candidate = Path.Combine(path, "@deepseek-ai", "dsh");
        return DshDiscoveryService.ValidateInstallDir(candidate) ? Path.GetFullPath(candidate) : null;
    }

    /// <summary>粗略判断 dsh 是否正在运行：面板状态非 Stopped / 探测到 dsh 进程 / 配置端口有监听。
    /// 仅用于"搬运前提醒用户先停止"，绝不触发任何终止动作。</summary>
    private bool IsDshProbablyRunning()
    {
        try
        {
            if (_process.CurrentState != DshProcessState.Stopped) return true;
            if (_process.Instances.Count > 0) return true;

            var port = Port is > 0 and <= 65535 ? Port : DshProcessService.DefaultPort;
            return DshProcessService.IsPortOpen("127.0.0.1", port, 300);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把搬运过程日志追加到进度文本（可能在后台线程触发 → 调度回 UI 线程）。</summary>
    private void AppendMoveLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) MoveProgressText = line;
        else dispatcher.Invoke(() => MoveProgressText = line);
    }

    /// <summary>音量 / 体积的友好文本（"12.3 MB"）；无法统计时显示"未知"。</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        var mb = bytes / 1048576.0;
        return mb < 1 ? $"{bytes / 1024.0:0} KB" : mb < 1024 ? $"{mb:0.0} MB" : $"{mb / 1024.0:0.00} GB";
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

    // ---- 属性变更即保存（CommunityToolkit.Mvvm 生成的 partial 钩子）----
    // 离散属性（路径 / 开关 / 端口 / 安装位置）：变化频率低，保持立即写盘，逻辑直观
    // 连续属性（模糊度 / 透明度）：由滑块持续拖动产生，改走 ScheduleSave() 防抖写盘

    partial void OnWallpaperPathChanged(string value)
    {
        OnPropertyChanged(nameof(WallpaperFileName));
        ReloadPreview();
        Save();
    }

    partial void OnIsWallpaperEnabledChanged(bool value) => Save();

    partial void OnWallpaperBlurChanged(double value) => ScheduleSave();

    partial void OnWallpaperOpacityChanged(double value) => ScheduleSave();

    partial void OnPortChanged(int value) => Save();

    partial void OnInstallPathOverrideChanged(string value) => Save();

    partial void OnLockedDshInstallPathChanged(string value)
    {
        OnPropertyChanged(nameof(LockedDshInstallPathText));
        MoveDshInstallCommand.NotifyCanExecuteChanged();
        // 来自磁盘同步（RefreshInstallLocationInfo）不算"用户改动"，不置脏、不写盘
        if (_syncingLockedPath) return;

        _lockedPathDirty = true;
        Save();
    }

    partial void OnIsMoveRunningChanged(bool value) => CancelMoveCommand.NotifyCanExecuteChanged();

    /// <summary>读取磁盘上的锁定位置（写盘时用于"沿用最新值"，避免冲掉首页刚更新好的锁定位置）。</summary>
    private string ReadLockedPathFromDisk()
    {
        try { return _store.Load<AppSettings>(SettingsFileName).DshInstallPathOverride ?? string.Empty; }
        catch { return LockedDshInstallPath ?? string.Empty; }
    }

    /// <summary>
    /// 连续变化属性专用：重置防抖计时器，延迟约 400ms 再写盘。
    /// 复用同一实例（Stop + Start 即完成重新计时），避免拖动时反复创建计时器对象。
    /// </summary>
    private void ScheduleSave()
    {
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    /// <summary>
    /// 立即写出尚未落盘的修改（若有挂起的防抖写盘）。
    /// 必须在程序退出前调用（见 MainWindow.OnClosing），否则最后一次拖动结果可能丢失。
    /// </summary>
    public void FlushPending()
    {
        // 计时器未启用表示没有挂起的修改（离散属性的改动早已立即写盘），无需重复写
        if (!_saveDebounceTimer.IsEnabled)
        {
            return;
        }

        _saveDebounceTimer.Stop();
        Save();
    }

    /// <summary>将当前设置序列化保存到 Data/settings.json。</summary>
    private void Save()
    {
        // 锁定位置：用户显式改过才用内存值；否则沿用磁盘上的最新值，
        // 避免把首页扫描（首次自动锁定 / 失效后改指新位置）刚写好的结果冲掉。
        var lockedPath = _lockedPathDirty ? LockedDshInstallPath ?? string.Empty : ReadLockedPathFromDisk();
        _lockedPathDirty = false;

        // 端口非法时回退默认值，避免把 0 / 越界端口写进配置
        var port = Port is > 0 and <= 65535 ? Port : DshProcessService.DefaultPort;

        try
        {
            // 【为什么用 Update 而不是 new AppSettings + Save】本页只负责壁纸与端口这两组字段。
            // 用整对象覆盖会把它不认识的字段一起抹掉（ProjectScopeDirectory 就由记忆库页写入），
            // 表现为"在设置页改个壁纸，记忆库的项目目录就没了"。
            // Update 在写锁内先读磁盘最新值、只改本页负责的字段，其余原样保留。
            _store.Update<AppSettings>(SettingsFileName, settings =>
            {
                settings.WallpaperPath = WallpaperPath;
                settings.IsWallpaperEnabled = IsWallpaperEnabled;
                settings.WallpaperBlur = WallpaperBlur;
                settings.WallpaperOpacity = WallpaperOpacity;
                settings.Port = port;
                settings.InstallPathOverride = InstallPathOverride ?? string.Empty;
                settings.DshInstallPathOverride = lockedPath;
            });
        }
        catch (Exception ex)
        {
            // 极少数情况下首页扫描会同时更新锁定位置并写同一个文件（文件被短暂占用）：
            // 这里只记录日志，绝不让一次写盘失败把界面（或退出流程）拖崩。
            AppLogService.Instance.Write("设置", $"保存设置失败：{ex.Message}");
        }
    }

    /// <summary>加载壁纸缩略图用于设置页预览（降低解码分辨率以省内存）。</summary>
    private void ReloadPreview()
    {
        try
        {
            var resolved = ResolveWallpaperPath(WallpaperPath);
            if (resolved is null)
            {
                WallpaperPreview = null;
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 480;
            bitmap.UriSource = new Uri(resolved, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            WallpaperPreview = bitmap;
        }
        catch
        {
            // 图片损坏或不可解码：预览留空
            WallpaperPreview = null;
        }
    }
}
