namespace DshManager.Services;

/// <summary>
/// 全局服务注册表（轻量级服务定位器）：
/// 让各页面 ViewModel 共享同一批服务实例，避免重复创建导致状态丢失
/// （例如 dsh 进程句柄、日志缓冲、发现结果跨页面保持一致）。
/// 静态属性由 CLR 保证线程安全的单次初始化。
/// </summary>
public static class AppServices
{
    /// <summary>共享 JSON 存储服务（记忆库 / 人格模板 / 聊天记录等持久化）。</summary>
    public static JsonStoreService Store { get; } = new();

    /// <summary>共享 dsh 发现服务（扫描安装位置、版本、工作目录）。</summary>
    public static DshDiscoveryService Discovery { get; } = new();

    /// <summary>共享 dsh 进程服务（跨页面保持启动/停止状态与日志一致）。</summary>
    public static DshProcessService Process { get; } = new();

    /// <summary>共享 dsh 安装 / 更新服务。</summary>
    public static DshInstallService Install { get; } = new();
}
