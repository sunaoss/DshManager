using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.ViewModels;

/// <summary>
/// 文件页视图模型：展示 dsh 相关文件清单，支持打开位置、刷新与一键卸载。
/// </summary>
public partial class FilesViewModel : ObservableObject
{
    private readonly DshFileService _fileService = new();
    private readonly DshUninstallService _uninstallService = new();

    /// <summary>当前数据目录（供"打开数据目录"使用，刷新时更新）。</summary>
    private string _dataDir = string.Empty;

    /// <summary>dsh 相关文件位置清单（启动器 / 数据目录 / 配置 / 日志等）。</summary>
    public ObservableCollection<DshFileLocation> Locations { get; } = new();

    /// <summary>多来源探测到的 dsh 安装位置清单（手动 / npm 全局 / dsh profiles / npx 缓存 / ai-manager / PATH / 工作目录）。</summary>
    public ObservableCollection<DshInstallation> Installations { get; } = new();

    /// <summary>底部状态文本（刷新结果 / 卸载结果）。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>是否正在执行卸载（耗时操作），用于禁用卸载按钮。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool isBusy;

    /// <summary>是否已安装 dsh（驱动卸载按钮可用性）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool isInstalled;

    public FilesViewModel()
    {
        // 构造时立即扫描并展示 dsh 相关文件
        Refresh();
    }

    /// <summary>重新扫描 dsh 相关文件与安装位置并刷新界面数据（两块清单共用一个刷新入口）。</summary>
    [RelayCommand]
    private void Refresh()
    {
        var info = AppServices.Discovery.Scan();
        IsInstalled = info.IsInstalled;
        _dataDir = info.DataDir;

        // 安装位置清单：直接用发现服务的多来源探测结果（已按优先级排序）
        Installations.Clear();
        foreach (var installation in info.Installations)
        {
            Installations.Add(installation);
        }

        Locations.Clear();
        foreach (var location in _fileService.Collect())
        {
            Locations.Add(location);
        }

        StatusText = Locations.Count == 0
            ? "未发现 dsh 相关文件"
            : $"共发现 {Locations.Count} 个 dsh 相关位置、{Installations.Count} 个安装位置";
    }

    /// <summary>打开安装位置：包目录直接打开，仅找到启动器 shim 时在资源管理器中定位选中。</summary>
    [RelayCommand]
    private void OpenInstallation(DshInstallation? installation)
    {
        var target = installation?.PackageDir;
        if (string.IsNullOrWhiteSpace(target)) return;

        var arguments = Directory.Exists(target) ? $"\"{target}\"" : $"/select,\"{target}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    /// <summary>复制安装位置路径到剪贴板（剪贴板被占用时仅提示，不抛异常）。</summary>
    [RelayCommand]
    private void CopyInstallationPath(DshInstallation? installation)
    {
        var target = installation?.PackageDir;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            Clipboard.SetText(target);
            StatusText = $"已复制路径：{target}";
        }
        catch
        {
            // 剪贴板偶发被其他进程占用，忽略即可
            StatusText = "复制失败，请稍后再试";
        }
    }

    /// <summary>打开指定位置：目录直接打开，文件在资源管理器中定位选中。</summary>
    [RelayCommand]
    private void OpenLocation(DshFileLocation? location)
    {
        if (location is null || string.IsNullOrWhiteSpace(location.Path)) return;

        // 目录直接用 explorer 打开；文件用 /select 参数在资源管理器中选中
        var arguments = location.IsDirectory
            ? $"\"{location.Path}\""
            : $"/select,\"{location.Path}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    /// <summary>用资源管理器打开 dsh 数据目录。</summary>
    [RelayCommand]
    private void OpenDataDir()
    {
        if (string.IsNullOrWhiteSpace(_dataDir) || !Directory.Exists(_dataDir))
        {
            StatusText = $"数据目录不存在：{_dataDir}";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_dataDir}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>一键卸载 dsh：强确认后停止进程、删除启动器与数据目录，并刷新清单与状态。</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;

        // 强确认框：卸载不可恢复，必须用户明确确认
        var confirm = MessageBox.Show(window,
            "将停止 dsh 进程并删除其相关文件（启动器、~/.dsh 数据目录）。此操作不可恢复，确定继续？",
            "确认卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            // 执行卸载 → 重新扫描清单与安装状态 → 展示结果摘要
            var result = await _uninstallService.UninstallAsync();
            Refresh();
            StatusText = result.Summary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>卸载按钮可用条件：已安装且当前没有卸载任务在执行。</summary>
    private bool CanUninstall() => IsInstalled && !IsBusy;
}
