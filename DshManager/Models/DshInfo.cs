namespace DshManager.Models;

/// <summary>
/// dsh（DeepSeek Harness）发现结果模型：描述 dsh 的安装状态与各关键位置。
/// 由 <see cref="Services.DshDiscoveryService.Scan"/> 填充。
/// </summary>
public class DshInfo
{
    /// <summary>是否已安装：至少发现一个候选位置（启动器 / 数据目录 / npm 包 / 工作目录）。</summary>
    public bool IsInstalled { get; set; }

    /// <summary>启动器路径：按搜索优先级第一个存在的 dsh 启动文件（PATH 或 .ai-manager/bin 中）。仅供界面展示位置，不保证可执行。</summary>
    public string LauncherPath { get; set; } = string.Empty;

    /// <summary>
    /// 实际启动命令（供界面与其他服务复用，已可直接交给 <see cref="Services.DshProcessService.SplitCommand"/> 拆分）。
    /// 两种格式：
    /// - 本地启动器可用：启动器的完整路径，如 C:\x\dsh.cmd（无前缀参数）；
    /// - 回退到 npx：形如 "C:\Program Files\nodejs\npx.cmd" --yes @deepseek-ai/dsh@0.1.5-rc.3（含空格路径带双引号）。
    /// 两者都不存在时为空字符串。
    /// </summary>
    public string LaunchCommand { get; set; } = string.Empty;

    /// <summary>数据目录：默认 %USERPROFILE%\.dsh（或 $DSH_HOME 环境变量）。</summary>
    public string DataDir { get; set; } = string.Empty;

    /// <summary>工作目录：解析自 start-dsh-web.ps1 中的 $workdir 变量；未找到为空字符串。</summary>
    public string WorkDir { get; set; } = string.Empty;

    /// <summary>dsh 版本号：由 dsh --version 输出解析；检测失败为 "unknown"。</summary>
    public string Version { get; set; } = "unknown";

    /// <summary>扫描过程中找到的所有相关路径清单（启动器 / 数据目录 / npm 包 / 工作目录），供"文件查看"页复用。</summary>
    public List<string> SourcesFound { get; set; } = new();

    /// <summary>
    /// 多来源探测到的 dsh 安装位置清单（手动指定 / npm 全局 / dsh profiles / npx 缓存 / ai-manager / PATH / 工作目录），
    /// 已按"版本高者优先、同版本按来源优先级"排序；供首页、文件页展示。
    /// </summary>
    public IReadOnlyList<DshInstallation> Installations { get; set; } = Array.Empty<DshInstallation>();

    /// <summary>推荐使用的安装位置（<see cref="Installations"/> 中第一个可用的）；没有可用项时为 null。</summary>
    public DshInstallation? RecommendedInstallation { get; set; }
}
