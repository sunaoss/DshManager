using System.IO;
using System.Threading;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>dsh 卸载结果：删除数量、失败列表与可直接展示的摘要文本。</summary>
public class UninstallResult
{
    /// <summary>成功删除的文件 / 目录数量。</summary>
    public int DeletedCount { get; set; }

    /// <summary>删除失败的项目路径列表（不中断整体流程）。</summary>
    public IReadOnlyList<string> Failures { get; set; } = Array.Empty<string>();

    /// <summary>结果摘要文本（供 UI 直接展示）。</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// dsh 卸载服务：先停止 dsh 进程，再删除启动器与数据目录。
/// 删除策略：
/// 1) 启动器：仅删除 %USERPROFILE%\.ai-manager\bin 下文件名以 "dsh" 开头的文件
///    （dsh / dsh.exe / dsh.cmd / dsh.bat / dsh.ps1 等），绝不触碰该目录其他内容
///    （.ai-manager 是多个工具共享的基础设施，必须原样保留）；
/// 2) 数据目录（~/.dsh 或 $DSH_HOME）：按规范视为 dsh 数据目录整体递归删除
///    （含 settings.yaml / .credentials.yaml / start-dsh-web.ps1 / 日志等）；
/// 3) 单个项目删除失败不中断整体流程，逐一收集到失败列表。
/// </summary>
public class DshUninstallService
{
    /// <summary>删除重试前的等待时间（应对文件被瞬时占用）。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 执行卸载：停止进程 → 删除启动器 → 删除数据目录 → 汇总结果。
    /// </summary>
    public async Task<UninstallResult> UninstallAsync()
    {
        var failures = new List<string>();
        var deletedCount = 0;
        var stopped = false;

        // 1) 停止仍在运行的 dsh（避免文件被占用导致删除失败）。
        // 【为什么不能只看 CurrentState】面板启动时若 dsh 是别人（终端/脚本）先拉起来的，
        // 服务内部状态一直是 Stopped —— 只有 RefreshExternalStateAsync 探过端口才会置 Running。
        // 旧实现只看 CurrentState 就会跳过停止步骤，直接进第 3 步递归删 ~/.dsh：
        // 正在运行的 dsh 占着 settings.yaml / 会话库，结果是"删掉一半、剩一半"，
        // 数据目录进入半损坏状态——比不删更糟。所以这里先强制探一次外部实例状态。
        var process = AppServices.Process;
        try
        {
            await process.RefreshExternalStateAsync(process.Port);
        }
        catch
        {
            // 探测失败不阻断卸载，后续仍会尝试停止并校验端口
        }

        if (process.CurrentState != DshProcessState.Stopped)
        {
            await process.StopAsync();
            stopped = true;
        }

        // 【为什么停完还要再确认一次】StopAsync 只是"发出停止请求"，进程可能还没退干净
        // （或停止失败但没抛异常）。数据目录是递归删除，必须确认真停了才敢动手：
        // 端口仍在监听就直接中止删除并如实汇报，让用户手工处理后重试。
        if (await IsPortStillListeningAsync(process.Port))
        {
            return new UninstallResult
            {
                DeletedCount = 0,
                Failures = new[] { $"端口 {process.Port} 仍在监听：dsh 尚未退出，已中止删除以免数据目录被删一半" },
                Summary = $"未删除任何文件：检测到端口 {process.Port} 仍在监听，dsh 进程尚未退出。"
                    + "请先结束该进程（可在首页点「停止」或手工结束 node 进程）后再执行卸载。",
            };
        }

        // 2) 删除启动器：仅清理 .ai-manager/bin 下文件名以 dsh 开头的文件
        deletedCount += DeleteLauncherFiles(failures);

        // 3) 删除数据目录（~/.dsh 整目录递归删除）
        deletedCount += DeleteDataDirectory(failures);

        // 4) 汇总结果摘要（含失败详情，最多列前 5 项）
        var summary = $"已删除 {deletedCount} 个文件/目录";
        if (stopped) summary = "已停止 dsh 进程；" + summary;
        if (failures.Count > 0)
        {
            summary += $"，{failures.Count} 项删除失败：" + string.Join("；", failures.Take(5));
            if (failures.Count > 5) summary += " 等";
        }

        return new UninstallResult
        {
            DeletedCount = deletedCount,
            Failures = failures,
            Summary = summary,
        };
    }

    /// <summary>
    /// 探测 127.0.0.1 上的端口是否仍在监听（短超时，不抛异常）。
    /// </summary>
    /// <remarks>
    /// 【用途】卸载前用它确认 dsh 真的退了。只看服务状态不够可靠：状态可能滞后，
    /// 而删数据目录是不可逆的递归删除，宁可多花 300 毫秒探一次端口。
    /// 等待一小段时间是为了给 StopAsync 之后的进程退出留出窗口。
    /// </remarks>
    private static async Task<bool> IsPortStillListeningAsync(int port)
    {
        if (port is <= 0 or > 65535) return false;

        // 给刚发出的停止请求一点退出时间，避免把"正在退出"误判成"仍在运行"
        await Task.Delay(500).ConfigureAwait(false);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(connect, Task.Delay(500)).ConfigureAwait(false);

            // 超时未连上：视为端口未监听
            if (completed != connect) return false;

            await connect.ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            // 连接被拒绝（端口无人监听）会走这里；任何异常都按"未监听"处理
            return false;
        }
    }

    /// <summary>删除 .ai-manager/bin 下文件名以 dsh 开头的启动器文件；该目录其他内容一律保留。</summary>
    private static int DeleteLauncherFiles(List<string> failures)
    {
        var binDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ai-manager", "bin");

        // 目录不存在（未安装到共享启动器目录）则无事可做
        if (!Directory.Exists(binDir)) return 0;

        string[] files;
        try
        {
            files = Directory.GetFiles(binDir);
        }
        catch (Exception ex)
        {
            // 枚举失败（权限异常等）时整体记失败，不中断卸载流程
            failures.Add($"{binDir}（枚举失败：{ex.Message}）");
            return 0;
        }

        var deleted = 0;
        foreach (var file in files)
        {
            // 仅删除文件名以 dsh 开头的启动器，绝不删除 .ai-manager 其余共享内容
            if (!Path.GetFileName(file).StartsWith("dsh", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryDeleteFile(file)) deleted++;
            else failures.Add(file);
        }
        return deleted;
    }

    /// <summary>删除 dsh 数据目录（~/.dsh 或 $DSH_HOME），整目录递归删除。</summary>
    private static int DeleteDataDirectory(List<string> failures)
    {
        var dataDir = DshDiscoveryService.GetDefaultDshHome();

        // 目录不存在则无事可做
        if (!Directory.Exists(dataDir)) return 0;

        // 安全阀：异常配置将 DSH_HOME 指向用户主目录时拒绝删除，避免误删整个用户目录
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(dataDir, userProfile, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{dataDir}（数据目录与用户主目录重合，已跳过删除）");
            return 0;
        }

        if (TryDeleteDirectory(dataDir)) return 1;

        failures.Add(dataDir);
        return 0;
    }

    /// <summary>删除单个文件：首次失败（如被占用）短暂等待后重试一次，仍失败返回 false。</summary>
    private static bool TryDeleteFile(string path)
    {
        // 最多尝试 2 次：首次失败后延迟 300ms 再试一次
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                File.Delete(path);
                if (!File.Exists(path)) return true;
            }
            catch
            {
                // 忽略本次异常，短暂等待后重试
            }

            if (attempt == 0) Thread.Sleep(RetryDelay);
        }
        return false;
    }

    /// <summary>递归删除目录：首次失败短暂等待后重试一次，仍失败返回 false。</summary>
    private static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path)) return true;
            }
            catch
            {
                // 忽略本次异常，短暂等待后重试
            }

            if (attempt == 0) Thread.Sleep(RetryDelay);
        }
        return false;
    }
}
