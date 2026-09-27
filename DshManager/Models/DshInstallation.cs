namespace DshManager.Models;

/// <summary>
/// dsh 安装来源。枚举顺序不代表优先级，优先级见 <see cref="DshInstallation.Rank"/>。
/// </summary>
public enum DshInstallSource
{
    /// <summary>用户在设置页手动指定的位置（最高优先，不受版本影响）。</summary>
    Manual,

    /// <summary>npm 全局安装（%APPDATA%\npm\node_modules 或 npm root -g 输出）。</summary>
    NpmGlobal,

    /// <summary>~/.dsh/profiles 下的 node_modules。</summary>
    DshProfiles,

    /// <summary>%LOCALAPPDATA%\npm-cache\_npx\<hash>\node_modules（npx 缓存，可能有多份多版本）。</summary>
    NpxCache,

    /// <summary>%USERPROFILE%\.ai-manager 下的全局包或 bin 启动器。</summary>
    AiManager,

    /// <summary>PATH 环境变量中的 dsh 启动器。</summary>
    PathLauncher,

    /// <summary>start-dsh-web.ps1 中 $workdir 指向的工作目录下的 node_modules。</summary>
    WorkDir,
}

/// <summary>
/// 一个 dsh 安装位置（自动探测或手动指定）。
/// 由 <see cref="Services.DshDiscoveryService.DetectInstallations"/> 填充，
/// 该服务只做"查找 + 展示 + 打开位置"，不会修改任何 dsh 文件。
/// </summary>
public class DshInstallation
{
    /// <summary>
    /// @deepseek-ai\dsh 包目录；若只找到启动器 shim 而没有包目录，则此处填 shim 所在路径（并标记来源）。
    /// </summary>
    public string PackageDir { get; set; } = string.Empty;

    /// <summary>包入口 &lt;PackageDir&gt;\lib\bin.js；不存在为 null。</summary>
    public string? BinJsPath { get; set; }

    /// <summary>可执行的启动器路径（如 dsh.cmd / PATH 中的 dsh）；没有则为 null。</summary>
    public string? LauncherPath { get; set; }

    /// <summary>版本号：读 package.json 的 version，读不到（缺失 / 损坏 / 仅 shim）为 "unknown"。</summary>
    public string Version { get; set; } = "unknown";

    /// <summary>
    /// 包目录体积（字节，**非递归**：只统计 PackageDir 下的直接文件）。
    /// 之所以不递归：包目录的上级常是 pnpm 的巨型 node_modules，递归扫描会拖死界面。统计失败为 0。
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>是否存在 Web 配套包（同级 node_modules 下的 dsh-web-app 或 @deepseek-ai\dsh-web-app）。</summary>
    public bool HasWebApp { get; set; }

    /// <summary>来源枚举。</summary>
    public DshInstallSource Source { get; set; }

    /// <summary>来源优先级打分（数值越大越优先），用于同主次版本时的排序：手动 1000 &gt; npm 全局 500 &gt; dsh profiles 400 &gt; npx 缓存 300 &gt; ai-manager 200 &gt; PATH 150 &gt; 工作目录 100。</summary>
    public int Rank { get; set; }

    /// <summary>能否真正启动：<see cref="BinJsPath"/> 或 <see cref="LauncherPath"/> 至少有一个存在（可执行）。</summary>
    public bool IsUsable { get; set; }

    /// <summary>
    /// 该安装位置是否为符号链接 / Junction（Windows 重解析点）。
    /// 为什么要标注：真实场景下 ~/.dsh/profiles/node_modules/@deepseek-ai/dsh 常是指向 npx 缓存的 Junction，
    /// 于是「安装位置」清单里会同时出现「dsh profiles」与「npx 缓存」两条，
    /// 看起来像重复安装，实为同一物理位置的两个访问入口（都应保留）；
    /// 标注后用户才能明白其中一条只是"快捷方式"。
    /// </summary>
    public bool IsLink { get; set; }

    /// <summary>链接指向的目标路径（<see cref="IsLink"/> 为 true 时才有值）；非链接或取不到目标时为 null。</summary>
    public string? LinkTarget { get; set; }

    /// <summary>来源的中文名（供界面徽章展示）。</summary>
    public string SourceText => Source switch
    {
        DshInstallSource.Manual => "手动指定",
        DshInstallSource.NpmGlobal => "npm 全局",
        DshInstallSource.DshProfiles => "dsh profiles",
        DshInstallSource.NpxCache => "npx 缓存",
        DshInstallSource.AiManager => "ai-manager",
        DshInstallSource.PathLauncher => "PATH 启动器",
        DshInstallSource.WorkDir => "工作目录",
        _ => "未知来源",
    };

    /// <summary>体积的友好文本，如 "12.3 MB"；无法统计时显示 "—"。</summary>
    public string SizeText
    {
        get
        {
            if (SizeBytes <= 0) return "—";
            var mb = SizeBytes / 1048576.0;
            if (mb < 1) return (SizeBytes / 1024.0).ToString("0") + " KB";
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }
    }

    /// <summary>版本的展示文本；无法识别时显示 "未知"。</summary>
    public string VersionText => string.IsNullOrWhiteSpace(Version) || Version == "unknown" ? "未知" : Version;

    /// <summary>可用性文本（绿色可用 / 粉色不可用）。</summary>
    public string UsableText => IsUsable ? "可用" : "不可用";

    /// <summary>
    /// 链接标注文本，如 "快捷方式 → C:\...\_npx\99cabb...\node_modules\@deepseek-ai\dsh"。
    /// 非链接或链接目标取不到时返回空字符串，界面据此隐藏该行。
    /// </summary>
    public string LinkText => IsLink && !string.IsNullOrWhiteSpace(LinkTarget)
        ? $"快捷方式 → {LinkTarget}"
        : string.Empty;

    /// <summary>列表展示用的短名称，如 "npx 缓存 · 0.1.5-rc.3"。</summary>
    public string DisplayName => $"{SourceText} · {VersionText}";
}
