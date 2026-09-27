namespace DshManager.Models;

/// <summary>
/// dsh 进程生命周期状态（面板 UI 状态灯的数据源）。
/// </summary>
public enum DshProcessState
{
    /// <summary>未启动（初始态 / 停止完成）。</summary>
    Stopped = 0,

    /// <summary>启动中（进程已拉起，Web 端口尚未就绪）。</summary>
    Starting = 1,

    /// <summary>运行中（Web 端口探测成功）。</summary>
    Running = 2,

    /// <summary>停止中（正在结束进程树）。</summary>
    Stopping = 3,
}
