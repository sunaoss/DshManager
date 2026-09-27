namespace DshManager.Models;

/// <summary>
/// 一个正在运行的 dsh 相关进程（由进程探测服务填充，供首页进程列表展示）。
/// 该模型是纯数据载体：每次刷新整体替换，不做属性变更通知，避免逐字段比较的开销。
/// </summary>
public class DshInstanceInfo
{
    /// <summary>操作系统进程 ID。</summary>
    public int Pid { get; set; }

    /// <summary>进程名（node 进程一律为 node）。</summary>
    public string Name { get; set; } = "node";

    /// <summary>完整命令行（来自 WMI Win32_Process.CommandLine）。</summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>命令行里 --port 解析出的端口；未显式指定时为 0（表示使用默认端口）。</summary>
    public int Port { get; set; }

    /// <summary>是否被判定为主服务进程（占用配置端口的那个）。</summary>
    public bool IsMain { get; set; }

    /// <summary>父进程 PID（用于把子进程归到主服务名下）。</summary>
    public int ParentPid { get; set; }

    /// <summary>工作集内存占用（MB）。</summary>
    public double MemMB { get; set; }

    /// <summary>CPU 占用百分比（两次采样的差分值，0 ~ 100）。</summary>
    public double CpuPercent { get; set; }

    /// <summary>累计 CPU 时间（秒）。</summary>
    public double CpuSeconds { get; set; }

    /// <summary>进程启动时间；读取失败时保持 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime StartTime { get; set; } = DateTime.MinValue;

    /// <summary>界面显示用的角色文本。</summary>
    public string RoleText => IsMain ? "主服务" : "子进程";

    /// <summary>界面显示用的启动时间文本；读取不到时用占位符。</summary>
    public string StartTimeText => StartTime == DateTime.MinValue
        ? "—"
        : StartTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>界面显示用的 CPU 文本。</summary>
    public string CpuText => $"{CpuPercent:0.0}%";

    /// <summary>界面显示用的内存文本。</summary>
    public string MemText => $"{MemMB:0.0} MB";
}
