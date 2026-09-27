using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using DshManager.Models;

namespace DshManager.Services;

/// <summary>
/// dsh 进程探测器（纯查询、无副作用、可单元测试）：
/// 枚举 node 进程 → 读命令行 → 用 <see cref="LooksLikeDsh"/> 甄别 → 补全端口 / 父进程 / 内存 / CPU / 启动时间。
/// 所有外部调用（WMI、Win32 API）失败时统一退化为空结果或 null，绝不向上抛异常导致界面崩溃。
/// </summary>
public static class DshProcessProbe
{
    /// <summary>两次 CPU 采样之间的最小间隔（秒）：间隔过短时差分被调度抖动放大，结果不可信。</summary>
    private const double MinCpuSampleIntervalSeconds = 0.2;

    /// <summary>内核 TCP 表查询类型：TCP_TABLE_OWNER_PID_LISTENER，行内带所属进程 PID。</summary>
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>GetExtendedTcpTable 首轮调用（缓冲区为 0）用于探测所需大小的返回码。</summary>
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>AF_INET：只查 IPv4 表（桌面本机场景足够）。</summary>
    private const int AddressFamilyInterNetwork = 2;

    /// <summary>CPU 采样字典的访问锁（定时刷新与手动刷新可能并发）。</summary>
    private static readonly object SampleLock = new();

    /// <summary>上一次采样：PID → 累计 CPU 秒数与采样时刻，用于计算 CPU 百分比。</summary>
    private static readonly Dictionary<int, CpuSample> LastCpuSamples = new();

    /// <summary>一次 CPU 采样点。</summary>
    private readonly record struct CpuSample(double CpuSeconds, DateTime At);

    /// <summary>
    /// 探测当前所有 dsh 相关进程，按「主服务在前，其次 PID 升序」排序。
    /// 此处的主服务仅按命令行是否显式带 --port 粗判，
    /// 最终由 <see cref="DshProcessService.RefreshInstances"/> 按配置端口重新裁定。
    /// </summary>
    public static IReadOnlyList<DshInstanceInfo> Detect()
    {
        var list = new List<DshInstanceInfo>();
        var now = DateTime.Now;

        try
        {
            // dsh 是 Node.js 程序，宿主进程名固定为 node
            foreach (var process in Process.GetProcessesByName("node"))
            {
                try
                {
                    var pid = process.Id;
                    var commandLine = GetCommandLine(pid);
                    // 读不到命令行（权限不足 / 进程已退出）或不像 dsh 的 node 进程一律跳过
                    if (string.IsNullOrEmpty(commandLine) || !LooksLikeDsh(commandLine))
                        continue;

                    var instance = new DshInstanceInfo
                    {
                        Pid = pid,
                        Name = process.ProcessName,
                        CommandLine = commandLine,
                        Port = ParsePort(commandLine),
                        ParentPid = GetParentPid(pid),
                    };
                    // 命令行里没写 --port 的服务（如手动 npx dsh web）此处不视为主服务，
                    // 由 DshProcessService 通过端口占用者反查补判。
                    instance.IsMain = instance.Port > 0;

                    try { instance.StartTime = process.StartTime; } catch { /* 权限不足：保持默认值 */ }
                    try { instance.MemMB = Math.Round(process.WorkingSet64 / 1048576.0, 1); } catch { }
                    try
                    {
                        var cpuSeconds = process.TotalProcessorTime.TotalSeconds;
                        instance.CpuSeconds = Math.Round(cpuSeconds, 1);
                        instance.CpuPercent = CaptureCpuPercent(pid, cpuSeconds, now);
                    }
                    catch { instance.CpuPercent = 0; }

                    list.Add(instance);
                }
                catch
                {
                    // 单个进程读取失败不影响整体探测
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // 进程枚举整体失败：返回空列表
        }

        // 清掉已退出 PID 的采样，避免字典无限增长
        TrimSamples(list);

        list.Sort((a, b) => a.IsMain != b.IsMain
            ? (a.IsMain ? -1 : 1)
            : a.Pid.CompareTo(b.Pid));
        return list;
    }

    /// <summary>
    /// 判断命令行是否属于 DSH 本体（可单元测试）。
    /// 命中条件：包含 @deepseek-ai/dsh 包路径，或出现 "dsh / dsh.cmd / dsh.exe" 且紧随 "web" 的全局命令形式；
    /// 必须排除 dsh- 前缀的配套包（dsh-subprocess-local / dsh-agent-presets 等），否则会误认无关 node 进程。
    /// </summary>
    public static bool LooksLikeDsh(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        // ① 包路径形式：...\@deepseek-ai\dsh\lib\bin.js 或 @deepseek-ai/dsh@0.1.5-rc.3
        if (ContainsDshPackage(commandLine))
            return true;

        // ② 全局命令形式："dsh web" / "dsh.cmd web" / "...\node_modules\.bin\dsh.cmd" web
        var tokens = commandLine.Split(
            new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var leaf = GetLeafName(tokens[i]);
            if (!IsDshExecutableName(leaf))
                continue;

            // 只有紧跟 web 子命令才算 dsh 服务本体（dsh 其它子命令不属于 Web UI）
            if (i + 1 < tokens.Length &&
                tokens[i + 1].Trim().Equals("web", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>从命令行里解析 --port 参数（同时支持 "--port 3080" 与 "--port=3080"）；无则返回 0。</summary>
    public static int ParsePort(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return 0;

        const string flag = "--port";
        var index = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return 0;

        // 跳过参数名与端口号之间的分隔符（空格 / 制表符 / 等号）
        var i = index + flag.Length;
        while (i < commandLine.Length &&
               (commandLine[i] == ' ' || commandLine[i] == '\t' || commandLine[i] == '='))
            i++;

        var start = i;
        while (i < commandLine.Length && char.IsDigit(commandLine[i]))
            i++;

        if (i == start)
            return 0;

        return int.TryParse(commandLine.AsSpan(start, i - start), out var port) ? port : 0;
    }

    /// <summary>
    /// 反查监听指定端口的进程 PID；查不到或任何失败返回 null（可单元测试失败容错路径）。
    /// 实现说明：本机没有 MSFT_NetTCPConnection 这个 CIM 类，走 WMI 查端口会失败，
    /// 因此只能用 Win32 API iphlpapi!GetExtendedTcpTable 直接取内核 TCP 表，
    /// 也不能用 netstat 文本解析（易受本地化与输出格式影响）。
    /// </summary>
    public static int? FindPortOwner(int port)
    {
        // 非法端口直接返回，避免无意义的内核查询
        if (port is <= 0 or > 65535)
            return null;

        var size = 0;
        try
        {
            // 首轮传 null 缓冲区：预期返回 ERROR_INSUFFICIENT_BUFFER 并带出所需字节数
            var result = GetExtendedTcpTable(
                IntPtr.Zero, ref size, false, AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0);
            if (result != ErrorInsufficientBuffer && result != 0)
                return null;
            if (size <= 0)
                return null;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(
                    buffer, ref size, false, AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0);
                if (result != 0)
                    return null;

                // 表头是一个 DWORD 行数，其后紧跟连续的 MIB_TCPROW_OWNER_PID 行
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                for (var i = 0; i < count; i++)
                {
                    var rowPtr = IntPtr.Add(buffer, sizeof(int) + i * rowSize);
                    var row = (MibTcpRowOwnerPid)Marshal.PtrToStructure(rowPtr, typeof(MibTcpRowOwnerPid))!;
                    if (NetworkToHostPort(row.LocalPort) == port)
                        return (int)row.OwningPid;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // P/Invoke 异常（缺 DLL、内存不足等）一律视为未找到
        }
        return null;
    }

    /// <summary>
    /// 纯函数版 CPU 百分比计算（便于单测，无共享状态）：
    /// 百分比 = ΔCPU秒 ÷ Δ墙钟秒 ÷ 逻辑核数 × 100，结果夹到 0 ~ 100。
    /// 间隔不足 <see cref="MinCpuSampleIntervalSeconds"/> 秒时返回 null（差分噪声过大，不展示）。
    /// </summary>
    /// <param name="previousCpuSeconds">上一次采样的累计 CPU 秒数。</param>
    /// <param name="previousAt">上一次采样时刻。</param>
    /// <param name="currentCpuSeconds">本次采样的累计 CPU 秒数。</param>
    /// <param name="now">本次采样时刻。</param>
    public static double? ComputeCpuPercent(
        double previousCpuSeconds, DateTime previousAt, double currentCpuSeconds, DateTime now)
    {
        var elapsed = (now - previousAt).TotalSeconds;
        if (elapsed <= MinCpuSampleIntervalSeconds)
            return null;

        // 进程重启或时钟回拨会导致差值为负：按 0 处理
        var used = currentCpuSeconds - previousCpuSeconds;
        if (used < 0)
            used = 0;

        // 换算成占全部逻辑核的百分比（与任务管理器口径一致）
        var percent = used / elapsed / Environment.ProcessorCount * 100.0;
        return Math.Round(Math.Clamp(percent, 0, 100), 1);
    }

    // ==================== CPU 采样状态 ====================

    /// <summary>记录本次采样并与上一次差分，返回 CPU 百分比（首次采样或间隔过短返回 0）。</summary>
    private static double CaptureCpuPercent(int pid, double cpuSeconds, DateTime now)
    {
        double? percent = null;
        lock (SampleLock)
        {
            if (LastCpuSamples.TryGetValue(pid, out var previous))
                percent = ComputeCpuPercent(previous.CpuSeconds, previous.At, cpuSeconds, now);

            // 无论是否算出百分比都要刷新采样点，供下一轮差分
            LastCpuSamples[pid] = new CpuSample(cpuSeconds, now);
        }
        return percent ?? 0;
    }

    /// <summary>清理已不存在 PID 的采样，防止字典随进程反复启停无限增长。</summary>
    private static void TrimSamples(List<DshInstanceInfo> alive)
    {
        lock (SampleLock)
        {
            if (LastCpuSamples.Count == 0)
                return;

            var alivePids = new HashSet<int>(alive.Select(instance => instance.Pid));
            var deadPids = LastCpuSamples.Keys.Where(pid => !alivePids.Contains(pid)).ToList();
            foreach (var pid in deadPids)
                LastCpuSamples.Remove(pid);
        }
    }

    // ==================== 命令行甄别辅助 ====================

    /// <summary>
    /// 命令行是否包含 @deepseek-ai/dsh 包路径。
    /// 实现要点：作用域与包名之间的分隔符在 npm 安装规格里是 '/'，但在 Windows 本地安装路径里是 '\'
    /// （如 ...\node_modules\@deepseek-ai\dsh\lib\bin.js），所以两种分隔符都要认，否则真实 dsh 进程会被漏判。
    /// 同时，包名 dsh 后必须紧跟路径分隔符 / @版本 / 空白等边界，
    /// 否则 @deepseek-ai/dsh-subprocess-local、@deepseek-ai/dsh-agent-presets 这类配套包会被误判为 dsh 本体。
    /// </summary>
    private static bool ContainsDshPackage(string commandLine)
    {
        const string scope = "@deepseek-ai";
        const string packageName = "dsh";
        var index = commandLine.IndexOf(scope, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var afterScope = index + scope.Length;
            // 作用域后必须紧跟路径分隔符（'/' 或 '\'），再接包名 dsh
            var hasSeparator = afterScope < commandLine.Length &&
                (commandLine[afterScope] == '/' || commandLine[afterScope] == '\\');
            var nameStart = afterScope + 1;
            if (hasSeparator && nameStart + packageName.Length <= commandLine.Length &&
                string.Compare(commandLine, nameStart, packageName, 0, packageName.Length,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                var afterName = nameStart + packageName.Length;
                // 到达串尾，或后面是包名边界 → 命中；若是 "-" 则说明是 dsh- 前缀配套包，继续往后找
                if (afterName >= commandLine.Length || IsPackageNameBoundary(commandLine[afterName]))
                    return true;
            }

            index = commandLine.IndexOf(scope, afterScope, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>包名边界字符：出现这些字符说明 @deepseek-ai/dsh 是一个完整包名而非更长的包名前缀。</summary>
    private static bool IsPackageNameBoundary(char c)
        => c is '\\' or '/' or '@' or '"' or '\'' or ' ' or '\t' or '\r' or '\n' or ';';

    /// <summary>取路径的最后一段（叶子名），如 "C:\a\b\dsh.cmd" → "dsh.cmd"。</summary>
    private static string GetLeafName(string token)
    {
        var trimmed = token.Trim('\\', '/');
        var slash = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>叶子名是否为 dsh 可执行入口。</summary>
    private static bool IsDshExecutableName(string leaf)
        => leaf.Equals("dsh", StringComparison.OrdinalIgnoreCase)
           || leaf.Equals("dsh.cmd", StringComparison.OrdinalIgnoreCase)
           || leaf.Equals("dsh.exe", StringComparison.OrdinalIgnoreCase);

    // ==================== WMI 读取 ====================

    /// <summary>
    /// 用 WMI 读取进程命令行。
    /// .NET 8 没有跨进程读取命令行的托管 API，Win32_Process.CommandLine 是最可靠的来源；
    /// 读不到（进程已退出 / 权限不足 / WMI 异常）返回空串，由调用方跳过该进程。
    /// </summary>
    private static string GetCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                var value = mo["CommandLine"];
                if (value is not null)
                    return value.ToString() ?? string.Empty;
            }
        }
        catch
        {
            // WMI 不可用 / 查询异常：按读不到处理
        }
        return string.Empty;
    }

    /// <summary>用 WMI 读取父进程 PID；失败返回 0。</summary>
    private static int GetParentPid(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                var value = mo["ParentProcessId"];
                if (value is not null)
                    return Convert.ToInt32(value);
            }
        }
        catch
        {
            // 同 GetCommandLine：失败按未知处理
        }
        return 0;
    }

    // ==================== Win32 TCP 表互操作 ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;   // 网络字节序
        public uint RemoteAddr;
        public uint RemotePort;  // 网络字节序
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable, ref int size, bool order, int ipVersion, int tableClass, int reserved);

    /// <summary>网络字节序端口 → 本机整数。</summary>
    private static int NetworkToHostPort(uint networkPort)
        => (int)(((networkPort & 0xFF) << 8) | ((networkPort >> 8) & 0xFF));
}
