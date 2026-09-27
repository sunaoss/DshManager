using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DshManager.Models;
using DshManager.Services;
using DshManager.ViewModels;

namespace DshManager;

/// <summary>
/// 应用入口：创建共享存储服务与设置视图模型，注入主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 单实例互斥体名（Local\ 前缀 = 每用户一个实例）。
    /// 【为什么需要】两个面板实例会同时读改 %LOCALAPPDATA%\DshManager\Data\settings.json，
    /// 互相冲掉对方写入的锁定安装路径，并各自争抢同一个 dsh 端口。
    /// </summary>
    private const string SingleInstanceMutexName = @"Local\DshManager.SingleInstance.8F3C2A64";

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ===== 1. 单实例保护 =====
        // 命名互斥体由操作系统持有：进程异常退出时自动释放，不会留下"僵尸锁"。
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        bool acquired;
        try
        {
            // 已有实例时等待 3 秒；此前实例若正忙（如弹了对话框）也能拿到超时结果而不是永久卡住
            acquired = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者异常退出（未释放互斥体）：按"抢到锁"处理，继续启动
            acquired = true;
        }

        if (!acquired)
        {
            MessageBox.Show(
                "「鲸鱼娘 · DeepSeek Harness 管理面板」已经在运行了。\n请在任务栏中切换到已打开的窗口。",
                "面板已在运行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // ===== 2. 全局异常兜底 =====
        // 【为什么必须有】没有这两条时，任何漏网的 UI 线程异常或后台任务异常都会直接终止进程：
        // 用户看到的是"面板突然消失"，不弹窗、不留日志、无现场可查。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // ===== 3. 统一日志服务接线：把 dsh 进程输出与状态变化转发进日志页 =====
        // 注意：这些事件都在后台线程触发；AppLogService 内部已加锁，故此处只写日志、绝不触碰 UI
        var log = AppLogService.Instance;
        AppServices.Process.LogReceived += (_, line) => log.Write("dsh", line);
        AppServices.Process.StateChanged += (_, state) => log.Write("面板", $"进程状态：{TranslateState(state)}");
        // 启动时记录一行，并给出日志目录（日志页「打开日志目录」按钮指向同一路径）
        log.Info($"面板已启动，日志目录：{Path.GetDirectoryName(DshProcessService.DefaultLogFilePath)}");

        // 存储服务 + 设置视图模型（单例共享，设置页与主窗口复用同一实例）
        var store = new JsonStoreService();
        var settingsViewModel = new SettingsViewModel(store);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(settingsViewModel),
        };

        // 主窗口订阅设置变更，壁纸/模糊/透明度修改即时反映到背景层
        window.BindSettings(settingsViewModel);
        window.Show();
    }

    /// <summary>
    /// UI 线程未捕获异常：记日志 + 标记已处理，避免面板直接消失。
    /// 【为什么标记 Handled】绝大多数异常来自单次操作（打开浏览器失败、绑定转换器出错等），
    /// 让用户看到提示并继续用，比直接退出体验好得多；会话级严重错误由用户自行重启面板。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var message = $"UI 线程未处理异常：{e.Exception.GetType().Name} - {e.Exception.Message}";
        try
        {
            AppLogService.Instance.Write("面板", message);
        }
        catch
        {
            // 日志本身失败也不能再抛
        }

        try
        {
            MessageBox.Show(
                $"面板遇到一个内部错误，但不影响继续使用：\n\n{e.Exception.Message}\n\n详细信息已写入日志页。",
                "面板内部错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 弹窗失败（如已在关闭流程）时静默
        }

        e.Handled = true;
    }

    /// <summary>后台任务未观察异常：仅记日志，绝不让它升级为进程崩溃。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            AppLogService.Instance.Write("面板", $"后台任务未处理异常：{e.Exception.GetBaseException().Message}");
        }
        catch
        {
            // 忽略
        }

        // 标记为已观察，避免 .NET 在 GC 时把它升级为进程终止
        e.SetObserved();
    }

    /// <summary>非 UI 线程的致命异常：尽力留一条日志，至少让用户知道发生了什么。</summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var text = (e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject?.ToString() ?? "未知错误";
            AppLogService.Instance.Write("面板", $"未处理异常（隔离级别 {e.IsTerminating}）：{text}");
        }
        catch
        {
            // 忽略
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退订全局处理器，避免退出流程中被自己的日志/弹窗再次触发
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;

        // 释放单实例互斥体，让下次启动能正常接管
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未持有该互斥体（例如 AbandonedMutex 分支之后又被系统回收）时忽略
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    /// <summary>进程状态 → 中文文本（与首页状态卡文案保持一致）。</summary>
    private static string TranslateState(DshProcessState state) => state switch
    {
        DshProcessState.Starting => "启动中",
        DshProcessState.Running => "运行中",
        DshProcessState.Stopping => "停止中",
        _ => "已停止",
    };
}
