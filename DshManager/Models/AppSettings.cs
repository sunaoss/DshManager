namespace DshManager.Models;

/// <summary>
/// 面板自身设置，持久化到 Data/settings.json。
/// </summary>
public class AppSettings
{
    /// <summary>壁纸图片的本地路径，为空表示未选择壁纸。</summary>
    public string WallpaperPath { get; set; } = string.Empty;

    /// <summary>壁纸模糊度（0 ~ 30，对应 BlurEffect.Radius）。</summary>
    public double WallpaperBlur { get; set; } = 0.0;

    /// <summary>壁纸透明度（0.0 全透明 ~ 1.0 完全不透明）。</summary>
    public double WallpaperOpacity { get; set; } = 0.8;

    /// <summary>是否启用壁纸显示。</summary>
    public bool IsWallpaperEnabled { get; set; } = false;

    /// <summary>
    /// DeepSeek Harness Web UI 监听端口，默认 3080。
    /// 该值既用于启动 dsh（web --port），也用于反查监听端口的进程以识别主服务。
    /// </summary>
    public int Port { get; set; } = 3080;

    /// <summary>
    /// 手动指定的 dsh 安装位置（@deepseek-ai\dsh 包目录，或其上级 node_modules 目录，或启动器文件）。
    /// 为空则完全依赖自动搜索（npm 全局 / dsh profiles / npx 缓存 / ai-manager / PATH / 工作目录）。
    /// </summary>
    public string InstallPathOverride { get; set; } = string.Empty;

    /// <summary>
    /// 面板**记住（锁定）**的 dsh 安装位置：指向 <c>@deepseek-ai\dsh</c> 包目录（含 package.json 与 lib\bin.js）。
    ///
    /// 【与 <see cref="InstallPathOverride"/> 的区别】本字段由面板自动维护（首次搜索成功后自动写入，
    /// 设置页「移动 dsh 安装位置」后由面板更新），代表"上次确认可用的那个安装位置"，用于避免每次启动都重新搜索；
    /// 而 <see cref="InstallPathOverride"/> 是用户手工指定的搜索提示，用户不填就为空。
    /// 【失效处理】本字段指向的目录若被删除 / 缺 bin.js，启动时会自动重新搜索并覆盖本字段（不弹错误框）。
    /// </summary>
    public string DshInstallPathOverride { get; set; } = string.Empty;

    /// <summary>
    /// 记忆库「项目级」视图当前选定的工作目录；为空时默认用 dsh 的工作目录。
    /// </summary>
    public string ProjectScopeDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 记忆库页面上次选择的作用域：true = 项目级，false = 全局。
    /// （作用域本身无法用 ProjectScopeDirectory 是否为空表达，故单独记一个布尔字段。）
    /// </summary>
    public bool MemoryScopeIsProject { get; set; } = false;
}
