using System.IO;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>
/// dsh 相关文件位置清单项：供"文件"页面展示与打开。
/// </summary>
public class DshFileLocation
{
    /// <summary>绝对路径（文件或目录）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>是否为目录（决定"打开位置"是选中文件还是直接打开目录）。</summary>
    public bool IsDirectory { get; set; }

    /// <summary>类型标签（启动器 / 数据目录 / 配置 / 日志 / 脚本 / 文件等）。</summary>
    public string KindLabel { get; set; } = "文件";

    /// <summary>中文说明（来源与用途）。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// dsh 文件清单服务：汇总 dsh 相关的启动器、数据目录、配置与日志等位置，
/// 供"文件"页面只读展示。仅做扫描，不修改任何内容。
/// </summary>
public class DshFileService
{
    /// <summary>递归遍历数据目录的最大深度（0 表示仅根目录下的文件）。</summary>
    private const int MaxDataDirDepth = 2;

    /// <summary>
    /// 采集 dsh 相关文件位置清单。
    /// 来源：
    /// 1) 发现服务扫描到的候选位置（启动器 / 数据目录 / npm 包 / 工作目录）；
    /// 2) 数据目录下的已知关键文件（settings.yaml / .credentials.yaml / cordis.patch.yml / start-dsh-web.ps1）；
    /// 3) 数据目录内递归遍历（深度限制 2 层），按扩展名标注类型（.yaml/.yml→配置、.log→日志、其他→文件）。
    /// 结果按路径去重（不区分大小写），保证每个位置只出现一次。
    /// </summary>
    public IReadOnlyList<DshFileLocation> Collect()
    {
        var info = AppServices.Discovery.Scan();
        var items = new List<DshFileLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 去重添加：同一路径仅保留首次出现（已知文件的专属描述优先于递归扫描）
        void Add(string path, bool isDir, string kind, string description)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!seen.Add(path)) return;
            items.Add(new DshFileLocation
            {
                Path = path,
                IsDirectory = isDir,
                KindLabel = kind,
                Description = description,
            });
        }

        // 1) 发现服务扫描到的候选位置（启动器 / 数据目录 / npm 包 / 工作目录）
        foreach (var path in info.SourcesFound)
        {
            var (kind, description) = DescribeSourcePath(path, info);
            Add(path, Directory.Exists(path), kind, description);
        }

        var dataDir = info.DataDir;
        if (string.IsNullOrWhiteSpace(dataDir)) return items;

        // 2) 数据目录下的已知关键文件（存在才列出）
        AddKnownFiles(dataDir, Add);

        // 3) 数据目录存在时递归遍历（深度限制 2 层），按扩展名标注类型
        if (Directory.Exists(dataDir))
        {
            EnumerateDataDirFiles(dataDir, depth: 0, Add);
        }

        return items;
    }

    /// <summary>将发现服务扫描到的路径映射为类型标签与中文说明。</summary>
    private static (string Kind, string Description) DescribeSourcePath(string path, DshInfo info)
    {
        // 数据目录本身
        if (PathsEqual(path, info.DataDir))
            return ("数据目录", "dsh 数据目录（~/.dsh，含配置、日志与启动脚本）");

        // 工作目录（start-dsh-web.ps1 中 $workdir 指向）
        if (PathsEqual(path, info.WorkDir))
            return ("工作目录", "dsh 工作目录（start-dsh-web.ps1 中 $workdir 指向）");

        // npm 全局包目录
        if (Directory.Exists(path) && path.Contains("@deepseek-ai", StringComparison.OrdinalIgnoreCase))
            return ("npm 包", "npm 全局包 @deepseek-ai/dsh 安装目录");

        // 启动器文件：文件名以 dsh 开头
        if (!Directory.Exists(path) && Path.GetFileName(path).StartsWith("dsh", StringComparison.OrdinalIgnoreCase))
            return ("启动器", "dsh 启动器（PATH 或 .ai-manager/bin 中发现的启动文件）");

        // 其余按扩展名归类
        return (KindLabelFor(path), $"扫描发现的 dsh 相关位置（{path}）");
    }

    /// <summary>添加数据目录下的已知关键文件（存在才列出，附专属说明）。</summary>
    private static void AddKnownFiles(string dataDir, Action<string, bool, string, string> add)
    {
        AddIfExists(Path.Combine(dataDir, "settings.yaml"), "配置", "数据目录 settings.yaml，dsh 主要配置", add);
        AddIfExists(Path.Combine(dataDir, ".credentials.yaml"), "凭据", "数据目录 .credentials.yaml，API 密钥等敏感凭据", add);
        AddIfExists(Path.Combine(dataDir, "cordis.patch.yml"), "配置", "数据目录 cordis.patch.yml，dsh 补丁配置", add);
        AddIfExists(Path.Combine(dataDir, "start-dsh-web.ps1"), "脚本", "数据目录 start-dsh-web.ps1，dsh Web 启动脚本", add);
    }

    private static void AddIfExists(string path, string kind, string description, Action<string, bool, string, string> add)
    {
        if (File.Exists(path))
            add(path, false, kind, description);
    }

    /// <summary>
    /// 递归遍历数据目录下的文件（深度限制 2 层）。
    /// 目录不可访问（权限 / 占用）时静默跳过，保证 Collect 永不抛异常。
    /// </summary>
    private static void EnumerateDataDirFiles(string dir, int depth, Action<string, bool, string, string> add)
    {
        // 当前目录下的所有文件
        foreach (var file in SafeGetFiles(dir))
        {
            add(file, false, KindLabelFor(file), DescribeDataFile(file));
        }

        // 深度到达上限则不再下探
        if (depth >= MaxDataDirDepth) return;

        foreach (var subDir in SafeGetDirectories(dir))
        {
            EnumerateDataDirFiles(subDir, depth + 1, add);
        }
    }

    /// <summary>按扩展名为文件标注类型标签（.yaml/.yml→配置、.log→日志、其他→文件）。</summary>
    private static string KindLabelFor(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".yaml" or ".yml" => "配置",
            ".log" => "日志",
            ".ps1" or ".cmd" or ".bat" or ".sh" => "脚本",
            _ => "文件",
        };
    }

    /// <summary>为数据目录下的文件生成中文描述。</summary>
    private static string DescribeDataFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".yaml" or ".yml" => $"{name}，数据目录下的配置文件",
            ".log" => $"{name}，dsh 运行日志",
            ".ps1" or ".cmd" or ".bat" or ".sh" => $"{name}，数据目录下的脚本文件",
            _ => $"{name}，数据目录下的文件",
        };
    }

    /// <summary>Windows 路径比较（不区分大小写）。</summary>
    private static bool PathsEqual(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>安全获取目录下所有文件（一次性物化，异常时返回空数组）。</summary>
    private static string[] SafeGetFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>安全获取目录下所有子目录（异常时返回空数组）。</summary>
    private static string[] SafeGetDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }
}
