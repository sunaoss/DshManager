using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// DshProcessService 单元测试：
/// 通过注入假进程启动器、假端口探测、假 PID 解析与假进程终止，验证状态机流转、
/// 日志文件增量回调、netstat 解析与外部实例接管逻辑，
/// 全程不触碰真实操作系统进程（不启动 dsh、不执行 taskkill、不访问真实用户目录）。
/// </summary>
public class DshProcessServiceTests : IDisposable
{
    /// <summary>每个测试独占的临时目录：注入日志文件路径，测试结束后清理。</summary>
    private readonly string _tempRoot;

    /// <summary>无 BOM 的 UTF-8：模拟 cmd `&gt;&gt;` 追加写入（不会写入 BOM）。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public DshProcessServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* 文件仍被轮询句柄占用时忽略清理失败 */ }
    }

    /// <summary>生成一个位于临时目录内的日志文件路径。</summary>
    private string NewLogPath() => Path.Combine(_tempRoot, $"{Guid.NewGuid():N}-dsh-web.log");

    // ---------------- 测试辅助类型 ----------------

    /// <summary>测试用假进程句柄：模拟生命周期，不触碰真实系统进程。</summary>
    private sealed class FakeProcessHandle : IProcessHandle
    {
        private readonly TaskCompletionSource _exitedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; init; } = 12345;
        public bool HasExited => _exitedTcs.Task.IsCompleted;
        public int ExitCode { get; private set; }
        public bool KillCalled { get; private set; }

        public event EventHandler? Exited;

        /// <summary>模拟进程被终止：完成退出等待并触发 Exited 事件。</summary>
        public void KillEntireTree()
        {
            KillCalled = true;
            ExitCode = 1;
            if (_exitedTcs.TrySetResult())
                Exited?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>模拟进程自行退出（如启动即崩溃）。</summary>
        public void SimulateExit(int exitCode)
        {
            ExitCode = exitCode;
            if (_exitedTcs.TrySetResult())
                Exited?.Invoke(this, EventArgs.Empty);
        }

        public Task WaitForExitAsync() => _exitedTcs.Task;

        /// <summary>假句柄无真实系统资源，只需满足释放契约。</summary>
        public void Dispose()
        {
            Exited = null;
        }
    }

    /// <summary>测试用假启动器：记录启动参数，按工厂产出假进程句柄。</summary>
    private sealed class FakeLauncher : IProcessLauncher
    {
        public Func<IProcessHandle>? HandleFactory { get; set; }
        public bool ThrowOnLaunch { get; set; }
        public int LaunchCount { get; private set; }
        public ProcessStartInfo? LastStartInfo { get; private set; }

        public IProcessHandle Launch(ProcessStartInfo startInfo)
        {
            if (ThrowOnLaunch)
                throw new InvalidOperationException("模拟启动失败");

            LaunchCount++;
            LastStartInfo = startInfo;
            // 未配置工厂时返回一个存活句柄（多数用例无需关心进程本身）
            return HandleFactory?.Invoke() ?? new FakeProcessHandle();
        }
    }

    /// <summary>轮询等待条件满足（带超时，供异步日志断言使用）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                break;
            await Task.Delay(20);
        }
    }

    // ---------------- 状态机流转 ----------------

    /// <summary>端口就绪：Stopped → Starting → Running，且启动参数（cmd 重定向）正确、不持有管道。</summary>
    [Fact]
    public async Task Start_PortReady_TransitionsToRunning()
    {
        var handle = new FakeProcessHandle();
        var launcher = new FakeLauncher { HandleFactory = () => handle };
        var states = new List<DshProcessState>();
        var logLines = new List<string>();
        var logPath = NewLogPath();

        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: logPath); // 端口探测：总是就绪
        service.StateChanged += (_, s) => states.Add(s);
        service.LogReceived += (_, line) => { lock (logLines) logLines.Add(line); };

        await service.StartAsync("dsh.exe", @"D:\al1969", 3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.Equal(new[] { DshProcessState.Starting, DshProcessState.Running }, states);
        Assert.Equal("http://127.0.0.1:3080", service.WebUrl);
        Assert.Equal(3080, service.Port);
        Assert.Equal(1, launcher.LaunchCount);

        // 关键：改为 cmd 重定向到日志文件，全程不重定向任何管道
        var startInfo = launcher.LastStartInfo!;
        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Contains("chcp 65001", startInfo.Arguments);
        Assert.Contains("\"dsh.exe\" web --port 3080", startInfo.Arguments);
        Assert.Contains(logPath, startInfo.Arguments);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(@"D:\al1969", startInfo.WorkingDirectory);
        Assert.Contains(service.LogLines, l => l.Contains("正在启动 dsh"));
    }

    /// <summary>端口就绪前进程已退出（启动即崩溃）：回 Stopped 并触发 ProcessExited。</summary>
    [Fact]
    public async Task Start_ProcessExitsBeforePortReady_FiresProcessExited()
    {
        var handle = new FakeProcessHandle();
        handle.SimulateExit(2); // 启动前已退出
        var launcher = new FakeLauncher { HandleFactory = () => handle };
        var exited = false;
        var states = new List<DshProcessState>();

        var service = new DshProcessService(launcher, (_, _, _) => false, logFilePath: NewLogPath());
        service.ProcessExited += (_, _) => exited = true;
        service.StateChanged += (_, s) => states.Add(s);

        await service.StartAsync("dsh.exe", @"D:\al1969", 3080);

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.True(exited);
        Assert.Contains(DshProcessState.Starting, states);
        Assert.Contains(DshProcessState.Stopped, states);
    }

    /// <summary>启动器抛异常（如可执行文件不存在）：回 Stopped，不抛给调用方。</summary>
    [Fact]
    public async Task Start_WhenLaunchThrows_TransitionsToStopped()
    {
        var launcher = new FakeLauncher { ThrowOnLaunch = true };
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: NewLogPath());

        await service.StartAsync("not-exist.exe", @"D:\al1969", 3080);

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.Contains(service.LogLines, l => l.Contains("启动 dsh 失败"));
    }

    /// <summary>已处于运行中时重复启动：幂等忽略，启动器只被调用一次。</summary>
    [Fact]
    public async Task Start_WhenAlreadyRunning_IsNoOp()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: NewLogPath());

        await service.StartAsync("dsh.exe", "", 3080);
        Assert.Equal(DshProcessState.Running, service.CurrentState);

        await service.StartAsync("dsh.exe", "", 3080); // 第二次启动应被忽略

        Assert.Equal(1, launcher.LaunchCount);
    }

    // ---------------- 停止 / 重启 ----------------

    /// <summary>运行中停止：Stopped → Starting → Running → Stopping → Stopped，结束进程树且不触发 ProcessExited。</summary>
    [Fact]
    public async Task Stop_WhileRunning_TransitionsThroughStoppingToStopped()
    {
        var handle = new FakeProcessHandle();
        var launcher = new FakeLauncher { HandleFactory = () => handle };
        var states = new List<DshProcessState>();
        var exited = false;
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: NewLogPath());
        service.StateChanged += (_, s) => states.Add(s);
        service.ProcessExited += (_, _) => exited = true;

        await service.StartAsync("dsh.exe", "", 3080);
        Assert.Equal(DshProcessState.Running, service.CurrentState);

        await service.StopAsync();

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.Contains(DshProcessState.Stopping, states);
        // Stopping 必须出现在 Stopped 之前
        Assert.True(states.IndexOf(DshProcessState.Stopping) < states.IndexOf(DshProcessState.Stopped));
        Assert.True(handle.KillCalled);
        Assert.False(exited); // 主动停止不触发 ProcessExited
    }

    /// <summary>启动轮询期间停止：轮询被中断，最终回 Stopped。</summary>
    [Fact]
    public async Task Stop_WhileStarting_CancelsStartup()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var states = new List<DshProcessState>();
        // 端口永不就绪且进程存活
        var service = new DshProcessService(launcher, (_, _, _) => false, logFilePath: NewLogPath());
        service.StateChanged += (_, s) => states.Add(s);

        var startTask = service.StartAsync("dsh.exe", "", 3080);

        // 等待进入 Starting 状态后立即停止
        while (service.CurrentState != DshProcessState.Starting)
            await Task.Delay(10);
        await service.StopAsync();
        await startTask;

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.Contains(DshProcessState.Stopping, states);
    }

    /// <summary>重启 = 先停止再启动：启动器被调用两次，最终 Running。</summary>
    [Fact]
    public async Task Restart_StopsThenStarts()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: NewLogPath());

        await service.StartAsync("dsh.exe", "", 3080);
        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.Equal(1, launcher.LaunchCount);

        await service.RestartAsync("dsh.exe", "", 3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.Equal(2, launcher.LaunchCount);
    }

    // ---------------- 日志文件增量轮询 ----------------

    /// <summary>日志文件新增内容（含中文，验证 UTF-8 解码）逐行触发 LogReceived，且支持增量两次写入。</summary>
    [Fact]
    public async Task LogReceived_FiresForEachOutputLine_Incrementally()
    {
        var logPath = NewLogPath();
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var received = new List<string>();
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: logPath);
        service.LogReceived += (_, line) => { lock (received) received.Add(line); };

        await service.StartAsync("dsh.exe", "", 3080);

        // 第一次写入 → 等第一行被采集
        File.AppendAllText(logPath, "开始启动服务\n", Utf8NoBom);
        await WaitUntilAsync(() => { lock (received) return received.Contains("开始启动服务"); },
            TimeSpan.FromSeconds(3));

        // 第二次写入 → 验证增量轮询（不是一次性读完即止）
        File.AppendAllText(logPath, "server listening on :3080\n", Utf8NoBom);
        await WaitUntilAsync(() => { lock (received) return received.Contains("server listening on :3080"); },
            TimeSpan.FromSeconds(3));

        Assert.Contains("开始启动服务", received);
        Assert.Contains("server listening on :3080", received);
    }

    /// <summary>日志缓冲有上限：超量输出时仅保留最新 MaxLogLines 行。</summary>
    [Fact]
    public async Task LogLines_CappedAtLimit()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 1100; i++)
            sb.AppendLine($"line {i}");
        var logPath = NewLogPath();
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: logPath);

        await service.StartAsync("dsh.exe", "", 3080);
        File.AppendAllText(logPath, sb.ToString(), Utf8NoBom);

        // 等待轮询把缓冲填满（达到上限即说明裁剪生效）
        await WaitUntilAsync(() => service.LogLines.Count >= DshProcessService.MaxLogLines, TimeSpan.FromSeconds(3));

        Assert.InRange(service.LogLines.Count, 0, DshProcessService.MaxLogLines);
        Assert.Contains(service.LogLines, l => l == "line 1099"); // 最新行必须保留
        Assert.DoesNotContain(service.LogLines, l => l == "line 0"); // 最旧行已被裁剪
    }

    // ---------------- 启动命令拆分（SplitCommand） ----------------

    /// <summary>双引号包裹且路径含空格：空格不被当作分隔符，参数前缀原样保留。</summary>
    [Fact]
    public void SplitCommand_QuotedPathWithSpaces_SplitsPathAndArguments()
    {
        DshProcessService.SplitCommand(
            "\"C:\\Program Files\\nodejs\\npx.cmd\" --yes @deepseek-ai/dsh@0.1.5-rc.3",
            out var fileName, out var arguments);

        Assert.Equal(@"C:\Program Files\nodejs\npx.cmd", fileName);
        Assert.Equal("--yes @deepseek-ai/dsh@0.1.5-rc.3", arguments);
    }

    /// <summary>无引号、无空格的普通路径：整个命令作为可执行文件，参数前缀为空。</summary>
    [Fact]
    public void SplitCommand_PlainPath_HasEmptyArguments()
    {
        DshProcessService.SplitCommand(@"C:\x\dsh.cmd", out var fileName, out var arguments);

        Assert.Equal(@"C:\x\dsh.cmd", fileName);
        Assert.Equal(string.Empty, arguments);
    }

    /// <summary>空字符串：文件名与参数均为空，不抛异常。</summary>
    [Fact]
    public void SplitCommand_EmptyString_ReturnsEmptyParts()
    {
        DshProcessService.SplitCommand(string.Empty, out var fileName, out var arguments);

        Assert.Equal(string.Empty, fileName);
        Assert.Equal(string.Empty, arguments);
    }

    /// <summary>仅可执行文件（无参数、无引号）时参数为空。</summary>
    [Fact]
    public void SplitCommand_ExecutableOnly_HasEmptyArguments()
    {
        DshProcessService.SplitCommand("dsh.exe", out var fileName, out var arguments);

        Assert.Equal("dsh.exe", fileName);
        Assert.Equal(string.Empty, arguments);
    }

    /// <summary>StartAsync 传入带引号的 npx 命令：cmd 命令串中正确保留拆分后的可执行文件与参数前缀。</summary>
    [Fact]
    public async Task Start_WithQuotedNpxCommand_SplitsFileNameAndArguments()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(launcher, (_, _, _) => true, logFilePath: NewLogPath());

        await service.StartAsync(
            "\"C:\\Program Files\\nodejs\\npx.cmd\" --yes @deepseek-ai/dsh@0.1.5-rc.3",
            @"D:\al1969", 3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.Equal("cmd.exe", launcher.LastStartInfo!.FileName);
        Assert.Contains(
            "\"C:\\Program Files\\nodejs\\npx.cmd\" --yes @deepseek-ai/dsh@0.1.5-rc.3 web --port 3080",
            launcher.LastStartInfo.Arguments);
    }

    // ---------------- 便携版 Node 替换 ----------------

    /// <summary>启动命令是裸 node 且面板已装便携版 → 换成便携版绝对路径（含空格路径由既有引号处理保证正确）。</summary>
    [Fact]
    public async Task Start_WithBareNodeCommand_UsesPortableNodePathWhenAvailable()
    {
        var portable = @"C:\Users\some user\AppData\Local\DshManager\runtimes\node\node.exe";
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => portable);

        await service.StartAsync("node \"C:\\x\\dsh\\lib\\bin.js\"", @"D:\al1969", 3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        var args = launcher.LastStartInfo!.Arguments;
        // 裸 node 被替换为便携版绝对路径（引号包裹，路径含空格也不会被截断）
        Assert.Contains($"\"{portable}\" \"C:\\x\\dsh\\lib\\bin.js\" web --port 3080", args);
        Assert.DoesNotContain("& \"node\" ", args);
    }

    /// <summary>未装便携版（解析返回 null）→ 保持裸 node，行为与引入本功能前完全一致。</summary>
    [Fact]
    public async Task Start_WithBareNodeCommand_KeepsBareNodeWhenNoPortable()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => null);

        await service.StartAsync("node \"C:\\x\\bin.js\"", "", 3080);

        Assert.Contains("& \"node\" \"C:\\x\\bin.js\" web --port 3080", launcher.LastStartInfo!.Arguments);
    }

    /// <summary>解析便携版路径时抛异常 → 等价于未装便携版，仍按裸 node 启动（不中断启动流程）。</summary>
    [Fact]
    public async Task Start_WhenPortableResolverThrows_KeepsBareNode()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => throw new InvalidOperationException("boom"));

        await service.StartAsync("node \"C:\\x\\bin.js\"", "", 3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.Contains("& \"node\" \"C:\\x\\bin.js\" web --port 3080", launcher.LastStartInfo!.Arguments);
    }

    /// <summary>启动命令已带完整路径的 node（不依赖 PATH）→ 不做替换，避免破坏既有行为。</summary>
    [Fact]
    public async Task Start_WithPathQualifiedNode_IsNotReplaced()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => @"C:\portable\node.exe");

        await service.StartAsync(
            "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\x\\bin.js\"", "", 3080);

        Assert.Contains(
            "& \"C:\\Program Files\\nodejs\\node.exe\" \"C:\\x\\bin.js\" web --port 3080",
            launcher.LastStartInfo!.Arguments);
    }

    // ---------------- 端口就绪判定（独立静态方法） ----------------

    /// <summary>真实监听端口探测成功。</summary>
    [Fact]
    public void IsPortOpen_OpenPort_ReturnsTrue()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(DshProcessService.IsPortOpen("127.0.0.1", port, 2000));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>未监听端口探测失败。</summary>
    [Fact]
    public void IsPortOpen_ClosedPort_ReturnsFalse()
    {
        // 先占用一个随机端口再释放，该端口大概率无人监听
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.False(DshProcessService.IsPortOpen("127.0.0.1", port, 1000));
    }

    // ---------------- netstat 输出解析（外部实例 PID） ----------------

    /// <summary>受关注的监听端点（IPv4 回环 / 通配 / IPv6 通配）都能解析出 PID。</summary>
    [Theory]
    [InlineData("  TCP    127.0.0.1:3080         0.0.0.0:0              LISTENING       4242")]
    [InlineData("  TCP    0.0.0.0:3080           0.0.0.0:0              LISTENING       4242")]
    [InlineData("  TCP    [::]:3080              [::]:0                 LISTENING       4242")]
    public void ParseListeningProcessId_MatchesWatchedEndpoints(string line)
    {
        Assert.Equal(4242, DshProcessService.ParseListeningProcessId(line, 3080));
    }

    /// <summary>表头、空行与其它端口 / 主机的行应被忽略，只在匹配行取 PID。</summary>
    [Fact]
    public void ParseListeningProcessId_SkipsHeaderAndOtherEndpoints()
    {
        var output =
            "活动连接\r\n\r\n  协议  本地地址          外部地址        状态           PID\r\n" +
            "  TCP    192.168.1.10:3080      0.0.0.0:0              LISTENING       1111\r\n" +
            "  TCP    127.0.0.1:9999         0.0.0.0:0              LISTENING       2222\r\n" +
            "  TCP    127.0.0.1:3080         0.0.0.0:0              LISTENING       3333\r\n";

        Assert.Equal(3333, DshProcessService.ParseListeningProcessId(output, 3080));
    }

    /// <summary>非 LISTENING 状态（ESTABLISHED / TIME_WAIT）不视为在运行实例。</summary>
    [Fact]
    public void ParseListeningProcessId_IgnoresNonListeningRows()
    {
        var output =
            "  TCP    127.0.0.1:3080         127.0.0.1:50000        ESTABLISHED     4242\r\n" +
            "  TCP    127.0.0.1:3080         0.0.0.0:0              TIME_WAIT       4242\r\n";

        Assert.Null(DshProcessService.ParseListeningProcessId(output, 3080));
    }

    /// <summary>畸形行 / 空输出 / PID 非数字：均返回 null，不抛异常。</summary>
    [Fact]
    public void ParseListeningProcessId_MalformedInput_ReturnsNull()
    {
        Assert.Null(DshProcessService.ParseListeningProcessId(null, 3080));
        Assert.Null(DshProcessService.ParseListeningProcessId(string.Empty, 3080));
        Assert.Null(DshProcessService.ParseListeningProcessId("  TCP  LISTENING\n", 3080));
        Assert.Null(DshProcessService.ParseListeningProcessId("not-a-netstat-line", 3080));
        Assert.Null(DshProcessService.ParseListeningProcessId(
            "  TCP    127.0.0.1:3080         0.0.0.0:0              LISTENING       ???\n", 3080));
    }

    // ---------------- 外部实例接管 / 停止 ----------------

    /// <summary>当前 Stopped 且端口在监听 → 判定为外部实例并切 Running。</summary>
    [Fact]
    public async Task RefreshExternalStateAsync_PortListening_TakesOverAsRunning()
    {
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => true, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242);

        await service.RefreshExternalStateAsync(3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.True(service.IsExternalInstance);
        Assert.Equal("http://127.0.0.1:3080", service.WebUrl);
        Assert.Contains(service.LogLines, l => l.Contains("外部启动"));
    }

    /// <summary>端口未监听且当前是外部实例 → 回到 Stopped 并清除外部标记。</summary>
    [Fact]
    public async Task RefreshExternalStateAsync_PortClosed_ReturnsToStopped()
    {
        var listening = true;
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => listening, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242);

        await service.RefreshExternalStateAsync(3080);
        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.True(service.IsExternalInstance);

        listening = false;
        await service.RefreshExternalStateAsync(3080);

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.False(service.IsExternalInstance);
    }

    /// <summary>未监听的端口不应把 Stopped 状态错误地改成外部运行。</summary>
    [Fact]
    public async Task RefreshExternalStateAsync_PortNotListening_StaysStopped()
    {
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => false, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242);

        await service.RefreshExternalStateAsync(3080);

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.False(service.IsExternalInstance);
    }

    /// <summary>停止外部实例：按解析出的 PID 结束进程树，状态回 Stopped。</summary>
    [Fact]
    public async Task StopAsync_ExternalInstance_KillsResolvedPid()
    {
        var killed = new List<int>();
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => true, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242,
            processTreeKiller: pid => { killed.Add(pid); return true; });

        await service.RefreshExternalStateAsync(3080);
        Assert.True(service.IsExternalInstance);

        await service.StopAsync();

        Assert.Equal(new[] { 4242 }, killed);
        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.False(service.IsExternalInstance);
    }

    /// <summary>外部实例识别不得覆盖面板自己启动的实例状态（IsExternalInstance 保持 false）。</summary>
    [Fact]
    public async Task RefreshExternalStateAsync_DoesNotOverrideSelfStartedInstance()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242);

        await service.StartAsync("dsh.exe", "", 3080);
        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.False(service.IsExternalInstance);

        await service.RefreshExternalStateAsync(3080);

        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.False(service.IsExternalInstance);
    }

    // ---------------- 启动失败诊断（DiagnoseStartupFailure） ----------------

    /// <summary>日志报 Cannot find package 且涉及 @deepseek-ai/* → 命中"依赖不完整"诊断，并带出具体包名。</summary>
    [Fact]
    public void Diagnose_CannotFindDeepseekPackage_ReturnsDependencyHint()
    {
        var lines = new[]
        {
            "Error: dsh: plugin tree failed to load",
            "Error: failed to import loader entry llm (@deepseek-ai/dsh-llm): " +
            @"Cannot find package '@deepseek-ai/dsh-llm' imported from C:\Users\x\.dsh\profiles\web\",
        };

        var hint = DshProcessService.DiagnoseStartupFailure(lines, 3080);

        Assert.NotNull(hint);
        Assert.Contains("依赖不完整", hint);
        Assert.Contains("@deepseek-ai/dsh-llm", hint);
        Assert.Contains("修复依赖", hint);
    }

    /// <summary>ERR_MODULE_NOT_FOUND 同样按"依赖不完整"处理（表里与之同义）。</summary>
    [Fact]
    public void Diagnose_ErrModuleNotFound_ReturnsDependencyHint()
    {
        var lines = new[] { @"Error [ERR_MODULE_NOT_FOUND]: Cannot find module 'C:\x\y.js'" };

        var hint = DshProcessService.DiagnoseStartupFailure(lines, 3080);

        Assert.NotNull(hint);
        Assert.Contains("依赖不完整", hint);
    }

    /// <summary>Cannot find package 但不涉及 @deepseek-ai/* → 不误报（返回 null）。</summary>
    [Fact]
    public void Diagnose_CannotFindOtherPackage_ReturnsNull()
    {
        var lines = new[] { "Error: Cannot find package 'lodash' imported from /app/index.js" };

        Assert.Null(DshProcessService.DiagnoseStartupFailure(lines, 3080));
    }

    /// <summary>EADDRINUSE → 命中"端口被占用"诊断，并带出实际端口号。</summary>
    [Fact]
    public void Diagnose_EaddrInUse_ReturnsPortHintWithPort()
    {
        var lines = new[] { "Error: listen EADDRINUSE: address already in use 127.0.0.1:3080" };

        var hint = DshProcessService.DiagnoseStartupFailure(lines, 3080);

        Assert.NotNull(hint);
        Assert.Contains("3080", hint);
        Assert.Contains("占用", hint);
    }

    /// <summary>仅出现 "address already in use" 也能命中端口诊断。</summary>
    [Fact]
    public void Diagnose_AddressAlreadyInUse_ReturnsPortHint()
    {
        var lines = new[] { "Error: address already in use :::3080" };

        var hint = DshProcessService.DiagnoseStartupFailure(lines, 3080);

        Assert.NotNull(hint);
        Assert.Contains("3080", hint);
    }

    /// <summary>cmd 中文"不是内部或外部命令" → 命中 Node 未找到诊断。</summary>
    [Fact]
    public void Diagnose_NodeNotRecognizedChinese_ReturnsNodeHint()
    {
        var lines = new[] { "'node' 不是内部或外部命令，也不是可运行的程序或批处理文件。" };

        var hint = DshProcessService.DiagnoseStartupFailure(lines, 3080);

        Assert.NotNull(hint);
        Assert.Contains("Node.js", hint);
    }

    /// <summary>sh 英文 "node: not found" → 命中 Node 未找到诊断。</summary>
    [Fact]
    public void Diagnose_NodeNotFoundEnglish_ReturnsNodeHint()
    {
        var lines = new[] { "sh: node: not found" };

        var hint = DshProcessService.DiagnoseStartupFailure(lines, 3080);

        Assert.NotNull(hint);
        Assert.Contains("Node.js", hint);
    }

    /// <summary>"is not recognized" 但说的不是 node（如 pnpm 缺失）→ 不误判为 Node 缺失。</summary>
    [Fact]
    public void Diagnose_IsNotRecognizedForOtherCommand_ReturnsNull()
    {
        var lines = new[] { "'pnpm' is not recognized as an internal or external command" };

        Assert.Null(DshProcessService.DiagnoseStartupFailure(lines, 3080));
    }

    /// <summary>空 / 无关键字日志 → 返回 null，不追加任何噪音。</summary>
    [Fact]
    public void Diagnose_EmptyOrUnknown_ReturnsNull()
    {
        Assert.Null(DshProcessService.DiagnoseStartupFailure(Array.Empty<string>(), 3080));
        Assert.Null(DshProcessService.DiagnoseStartupFailure(null!, 3080));
        Assert.Null(DshProcessService.DiagnoseStartupFailure(new[] { "dsh 已启动", "server listening on :3080" }, 3080));
    }

    /// <summary>端到端：进程因依赖缺失退出后，日志区自动追加一条中文诊断（在收尾读取之后）。</summary>
    [Fact]
    public async Task Start_ProcessExitsWithDependencyError_AppendsDiagnosisLine()
    {
        var logPath = NewLogPath();
        var handle = new FakeProcessHandle();
        var launcher = new FakeLauncher { HandleFactory = () => handle };
        var service = new DshProcessService(launcher, (_, _, _) => false, logFilePath: logPath);

        var startTask = service.StartAsync("dsh.exe", "", 3080);

        // 等启动日志出现，确认日志轮询已经开始
        await WaitUntilAsync(() => service.LogLines.Any(l => l.Contains("正在启动 dsh")), TimeSpan.FromSeconds(3));

        // 模拟 dsh 先写入错误、再退出；先等该行被轮询读到，确保诊断为"整体日志"匹配而非时序侥幸
        File.AppendAllText(logPath,
            @"Error: Cannot find package '@deepseek-ai/dsh-session' imported from C:\x\.dsh\profiles\web\" + "\n",
            Utf8NoBom);
        await WaitUntilAsync(() => service.LogLines.Any(l => l.Contains("Cannot find package")), TimeSpan.FromSeconds(3));

        handle.SimulateExit(1);
        await startTask;

        // 诊断在"日志轮询收尾"之后追加，属异步 → 轮询等待其出现
        await WaitUntilAsync(() => service.LogLines.Any(l => l.Contains("依赖不完整")), TimeSpan.FromSeconds(3));

        Assert.Contains(service.LogLines, l => l.Contains("依赖不完整") && l.Contains("@deepseek-ai/dsh-session"));
        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
    }

    // ---------------- 启动超时（修「启动中不消失」） ----------------

    /// <summary>
    /// 端口始终不就绪、进程也不退出 → 到达启动超时后停止等待并回 Stopped，
    /// 日志含「启动超时（N 秒）：端口 X 未就绪」与中文诊断，且**不得**擅自结束进程（无副作用）。
    /// 用假时钟把"已等待"瞬间推到超时值，避免真的等 120 秒。
    /// </summary>
    [Fact]
    public async Task Start_PortNeverReady_TimesOutToStoppedWithDiagnosis()
    {
        var handle = new FakeProcessHandle();
        var launcher = new FakeLauncher { HandleFactory = () => handle };
        var states = new List<DshProcessState>();
        // 每次读时钟跳 1000 秒：第一次作为起点，第二次即判定"已超过 120 秒超时"
        var ticks = 0;
        var clock = () => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ticks++ * 1000);
        var service = new DshProcessService(
            launcher, (_, _, _) => false, logFilePath: NewLogPath(),
            startupTimeout: TimeSpan.FromSeconds(120), clock: clock);
        service.StateChanged += (_, s) => states.Add(s);

        await service.StartAsync("dsh.exe", @"D:\al1969", 3080);

        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.Contains(DshProcessState.Starting, states);
        Assert.Contains(DshProcessState.Stopped, states);
        // 日志必须能一眼看出"超时"与"哪个端口没起来"，而不是静默卡死
        Assert.Contains(service.LogLines, l => l.Contains("启动超时（120 秒）") && l.Contains("端口 3080 未就绪"));
        // 诊断建议：给用户明确的下一步动作
        Assert.Contains(service.LogLines, l => l.Contains("修复依赖"));
        // 关键约束：超时只"停止等待 + 报告"，绝不擅自杀掉进程
        Assert.False(handle.KillCalled);
    }

    /// <summary>超时值的默认常量为 120 秒（给 dsh 装配插件留足余量）。</summary>
    [Fact]
    public void DefaultStartupTimeout_Is120Seconds()
    {
        Assert.Equal(120, DshProcessService.DefaultStartupTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(120), DshProcessService.DefaultStartupTimeout);
        Assert.Contains("120", DshProcessService.BuildStartupTimeoutHint(3080, DshProcessService.DefaultStartupTimeout));
    }

    // ---------------- 启动进度估算（纯静态） ----------------

    /// <summary>进度估算边界：0 秒 → 0；接近超时 / 达到或超过超时 → 封顶 95%；超时非正数 → 95%。</summary>
    [Theory]
    [InlineData(0, 120, 0)]
    [InlineData(60, 120, 47)]
    [InlineData(119, 120, 94)]
    [InlineData(120, 120, 95)]
    [InlineData(240, 120, 95)]
    [InlineData(30, 0, 95)]
    public void EstimateStartProgress_ClampsWithinBounds(double elapsed, double timeout, int expected)
    {
        Assert.Equal(expected, DshProcessService.EstimateStartProgress(elapsed, timeout));
    }

    // ---------------- 终止外部实例（TerminateExternalAsync） ----------------

    /// <summary>
    /// 终止外部实例成功分支：按反查到的 PID 结束其进程树 → 端口释放 → 清空外部标记并回 Stopped，
    /// 使界面「启动」按钮恢复可用。全程使用假 killer，不真的杀进程。
    /// </summary>
    [Fact]
    public async Task TerminateExternalAsync_Success_KillsPidAndReturnsToStopped()
    {
        var alive = true; // 端口是否仍被占用，由假 killer 切换
        var killed = new List<int>();
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => alive, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242,
            processTreeKiller: pid => { killed.Add(pid); alive = false; return true; });

        await service.RefreshExternalStateAsync(3080);
        Assert.True(service.IsExternalInstance);
        Assert.Equal(4242, service.ExternalProcessId);

        var ok = await service.TerminateExternalAsync();

        Assert.True(ok);
        Assert.Equal(new[] { 4242 }, killed); // 只结束该实例（其 PID）的进程树
        Assert.Equal(DshProcessState.Stopped, service.CurrentState);
        Assert.False(service.IsExternalInstance);
        Assert.Null(service.ExternalProcessId);
        Assert.Contains(service.LogLines, l => l.Contains("已终止外部启动的 dsh 实例"));
    }

    /// <summary>
    /// 终止外部实例失败分支：killer 未能结束进程（端口仍被占用）→ 返回 false，
    /// 状态与外部标记保持不变（用户仍可重试或改用其它方式），并给出中文原因。
    /// </summary>
    [Fact]
    public async Task TerminateExternalAsync_PortStillListening_ReturnsFalseAndKeepsState()
    {
        var killed = new List<int>();
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => true, logFilePath: NewLogPath(), // 端口始终在监听
            listeningPidResolver: _ => 4242,
            processTreeKiller: pid => { killed.Add(pid); return false; });

        await service.RefreshExternalStateAsync(3080);

        var ok = await service.TerminateExternalAsync();

        Assert.False(ok);
        Assert.Equal(new[] { 4242 }, killed);
        Assert.Equal(DshProcessState.Running, service.CurrentState);
        Assert.True(service.IsExternalInstance);
        Assert.Equal(4242, service.ExternalProcessId);
        Assert.Contains(service.LogLines, l => l.Contains("终止外部 dsh 实例失败"));
    }

    /// <summary>没有外部实例时调用终止：不误杀任何进程，直接返回 false 并给出中文原因。</summary>
    [Fact]
    public async Task TerminateExternalAsync_NoExternalInstance_ReturnsFalse()
    {
        var killed = new List<int>();
        var service = new DshProcessService(
            new FakeLauncher(), (_, _, _) => false, logFilePath: NewLogPath(),
            listeningPidResolver: _ => 4242,
            processTreeKiller: pid => { killed.Add(pid); return true; });

        var ok = await service.TerminateExternalAsync();

        Assert.False(ok);
        Assert.Empty(killed);
        Assert.Contains(service.LogLines, l => l.Contains("没有检测到外部启动的 dsh 实例"));
    }

    // ---------------- 启动参数：--no-open + --port（由面板负责开浏览器） ----------------

    /// <summary>
    /// 启动参数必须同时包含 --no-open 与 --port &lt;port&gt;：
    /// --no-open 让 dsh 不自己弹浏览器（更快就绪、避免抢浏览器），浏览器改由面板在 Running 后打开。
    /// </summary>
    [Fact]
    public async Task Start_IncludesNoOpenAndPortArguments()
    {
        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => null);

        await service.StartAsync("dsh.exe", @"D:\al1969", 3081);

        var args = launcher.LastStartInfo!.Arguments;
        Assert.Contains(DshProcessService.NoOpenArgument, args);
        Assert.Contains("web --port 3081", args);
        // --no-open 紧跟在端口参数之后（不影响任何既有参数顺序）
        Assert.Contains("--port 3081 --no-open", args);
    }

    // ---------------- NODE_PATH 推导（纯静态，不触碰文件系统） ----------------

    /// <summary>标准 _npx\&lt;hash&gt; 安装位置：返回该 hash 下的 node_modules（不是 @deepseek-ai 那层）。</summary>
    [Fact]
    public void ResolveNodePathFromBinScript_NpxCachePath_ReturnsInstallNodeModules()
    {
        var binJs = @"C:\Users\u\AppData\Local\npm-cache\_npx\99cabb0ceac85b86\node_modules\@deepseek-ai\dsh\lib\bin.js";

        Assert.Equal(
            @"C:\Users\u\AppData\Local\npm-cache\_npx\99cabb0ceac85b86\node_modules",
            DshProcessService.ResolveNodePathFromBinScript(binJs));
    }

    /// <summary>npm 全局安装位置：返回全局目录下的 node_modules。</summary>
    [Fact]
    public void ResolveNodePathFromBinScript_NpmGlobalPath_ReturnsGlobalNodeModules()
    {
        var binJs = @"C:\Program Files\nodejs\node_modules\@deepseek-ai\dsh\lib\bin.js";

        Assert.Equal(
            @"C:\Program Files\nodejs\node_modules",
            DshProcessService.ResolveNodePathFromBinScript(binJs));
    }

    /// <summary>非标准 / 缺少 \node_modules\ 段 / 空值：一律返回 null，且不抛异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-path")]
    [InlineData(@"C:\x\dsh\lib\bin.js")]
    public void ResolveNodePathFromBinScript_NonStandardPath_ReturnsNull(string? path)
    {
        Assert.Null(DshProcessService.ResolveNodePathFromBinScript(path));
    }

    /// <summary>分隔符（/ 与 \ 等价）与大小写都不敏感：正斜杠路径与全大写 NODE_MODULES 也能正确推导。</summary>
    [Fact]
    public void ResolveNodePathFromBinScript_IsSeparatorAndCaseInsensitive()
    {
        Assert.Equal(
            @"C:\x\node_modules",
            DshProcessService.ResolveNodePathFromBinScript("C:/x/node_modules/@deepseek-ai/dsh/lib/bin.js"));

        Assert.Equal(
            @"C:\x\NODE_MODULES",
            DshProcessService.ResolveNodePathFromBinScript(@"C:\x\NODE_MODULES\@deepseek-ai\dsh\lib\bin.js"));
    }

    // ---------------- bin.js 真实路径解析（注入假解析器，不触碰文件系统） ----------------

    /// <summary>注入"解析成功"的委托：返回真实路径（用于绕开 profiles 下易失效的符号链接）。</summary>
    [Fact]
    public void TryResolveRealBinScript_ResolverReturnsRealPath_ReturnsRealPath()
    {
        var linkPath = @"C:\Users\u\.dsh\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js";
        var realPath = @"C:\Users\u\AppData\Local\npm-cache\_npx\hash\node_modules\@deepseek-ai\dsh\lib\bin.js";

        Assert.Equal(realPath, DshProcessService.TryResolveRealBinScript(linkPath, _ => realPath));
    }

    /// <summary>注入"解析失败"的委托（返回 null / 空 / 抛异常）：一律回退原路径且不抛异常。</summary>
    [Fact]
    public void TryResolveRealBinScript_ResolverFails_ReturnsOriginalPath()
    {
        var linkPath = @"C:\Users\u\.dsh\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js";

        Assert.Equal(linkPath, DshProcessService.TryResolveRealBinScript(linkPath, _ => null));
        Assert.Equal(linkPath, DshProcessService.TryResolveRealBinScript(linkPath, _ => string.Empty));
        // 解析器抛异常也不得影响启动：仍回退原路径
        Assert.Equal(linkPath, DshProcessService.TryResolveRealBinScript(
            linkPath, _ => throw new IOException("模拟解析失败")));
    }

    // ---------------- 兜底环境变量（不覆盖用户已有值） ----------------

    /// <summary>已存在非空同名环境变量（用户显式设置）→ 不覆盖，尊重用户配置。</summary>
    [Fact]
    public void ApplyStartupEnvironment_WhenAlreadySet_DoesNotOverwrite()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.EnvironmentVariables["NODE_PATH"] = @"C:\user\custom\node_modules";
        startInfo.EnvironmentVariables["DSH_HOME"] = @"D:\user\.dsh";

        DshProcessService.ApplyStartupEnvironment(startInfo, @"C:\derived\node_modules", @"C:\derived\.dsh");

        Assert.Equal(@"C:\user\custom\node_modules", startInfo.EnvironmentVariables["NODE_PATH"]);
        Assert.Equal(@"D:\user\.dsh", startInfo.EnvironmentVariables["DSH_HOME"]);
    }

    /// <summary>环境里没有同名变量 → 写入推导出的值；取值为空（null）时该变量不设置。</summary>
    [Fact]
    public void ApplyStartupEnvironment_WhenAbsent_SetsDerivedValues()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.EnvironmentVariables.Remove("NODE_PATH");
        startInfo.EnvironmentVariables.Remove("DSH_HOME");

        DshProcessService.ApplyStartupEnvironment(startInfo, @"C:\x\node_modules", @"C:\Users\u\.dsh");

        Assert.Equal(@"C:\x\node_modules", startInfo.EnvironmentVariables["NODE_PATH"]);
        Assert.Equal(@"C:\Users\u\.dsh", startInfo.EnvironmentVariables["DSH_HOME"]);

        // 取不到值（null）时不写入
        var another = new ProcessStartInfo();
        another.EnvironmentVariables.Remove("NODE_PATH");
        another.EnvironmentVariables.Remove("DSH_HOME");
        DshProcessService.ApplyStartupEnvironment(another, null, null);
        Assert.False(another.EnvironmentVariables.ContainsKey("NODE_PATH"));
        Assert.False(another.EnvironmentVariables.ContainsKey("DSH_HOME"));
    }

    // ---------------- 启动流程：bin.js 真实路径 + NODE_PATH 端到端（全注入，无真实进程 / 文件） ----------------

    /// <summary>
    /// 启动命令形如 `node "&lt;profiles 链接路径&gt;/bin.js"` 时：
    /// 1) 命令改用解析出的真实路径；2) NODE_PATH 由真实路径推导为 npx 缓存的 node_modules；
    /// 3) 启动日志记录实际命令 + NODE_PATH + DSH_HOME + 工作目录。
    /// </summary>
    [Fact]
    public async Task Start_WithProfilesLinkBinScript_UsesRealPathAndDerivesNodePath()
    {
        var linkBinJs = @"C:\Users\u\.dsh\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js";
        var realBinJs = @"C:\Users\u\AppData\Local\npm-cache\_npx\99cabb\node_modules\@deepseek-ai\dsh\lib\bin.js";

        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => null,
            realBinScriptResolver: path => path == linkBinJs ? realBinJs : null);

        await service.StartAsync($"node \"{linkBinJs}\"", @"D:\al1969", 3080);

        var startInfo = launcher.LastStartInfo!;
        Assert.Contains(realBinJs, startInfo.Arguments);
        Assert.Contains("web --port 3080 --no-open", startInfo.Arguments);
        Assert.DoesNotContain(linkBinJs, startInfo.Arguments);
        Assert.Equal(
            @"C:\Users\u\AppData\Local\npm-cache\_npx\99cabb\node_modules",
            startInfo.EnvironmentVariables["NODE_PATH"]);
        Assert.False(string.IsNullOrWhiteSpace(startInfo.EnvironmentVariables["DSH_HOME"]));

        var logText = string.Join("\n", service.LogLines);
        Assert.Contains("NODE_PATH：", logText);
        Assert.Contains("DSH_HOME：", logText);
        Assert.Contains("工作目录：D:\\al1969", logText);
    }

    // ---------------- 启动命令最终形态：绝对 bin.js + NODE_PATH + --no-open ----------------

    /// <summary>
    /// 用「锁定安装位置」拼出的启动命令（node + 绝对 bin.js）必须满足：
    /// ① 命令里是**绝对**的 bin.js 路径（不再走 ~\.dsh\profiles 下易悬空的链接）；
    /// ② 含 web --port &lt;port&gt; 与 --no-open；
    /// ③ NODE_PATH 为该安装根的 **node_modules 这一层**（实测：写成 node_modules\@deepseek-ai 会多一层，
    ///    对 dsh 内部的 @deepseek-ai/* 作用域导入完全无效）；
    /// ④ DSH_HOME 非空（与面板使用一致的 dsh home）。
    /// </summary>
    [Fact]
    public async Task Start_WithLockedInstallBinScript_UsesAbsolutePathNodePathAndNoOpen()
    {
        const string installRoot = @"D:\dsh-moved\99cabb0ceac85b86";
        var binJs = installRoot + @"\node_modules\@deepseek-ai\dsh\lib\bin.js";

        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => null);

        await service.StartAsync($"node \"{binJs}\"", @"D:\al1969", 3099);

        var startInfo = launcher.LastStartInfo!;
        Assert.Contains(binJs, startInfo.Arguments);
        Assert.Contains("web --port 3099 --no-open", startInfo.Arguments);

        var nodePath = startInfo.EnvironmentVariables["NODE_PATH"];
        Assert.Equal(installRoot + @"\node_modules", nodePath);
        Assert.False(nodePath!.EndsWith("@deepseek-ai", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(startInfo.EnvironmentVariables["DSH_HOME"]));
    }

    // ---------------- 物理路径启动：真 Junction 端到端（默认解析器，不启动 dsh） ----------------

    /// <summary>
    /// 启动命令里的 bin.js 指向 profiles 下的 Junction 时，必须改用**物理路径**启动，且：
    /// ① 命令行里是物理 bin.js（不再出现易悬空的链接路径）；② 含 <c>web --port &lt;port&gt; --no-open</c>；
    /// ③ NODE_PATH 由物理路径推导为 <c>&lt;真实安装根&gt;\node_modules</c>（不是 @deepseek-ai 那一层）。
    /// 本用例用**真实临时目录 + 真 Junction**，且不注入解析器（走默认的 ResolveLinkTarget 实现），
    /// 从而在真实文件系统上端到端验证这套解析链路。
    /// </summary>
    [Fact]
    public async Task Start_WithRealJunctionBinScript_UsesPhysicalPathNodePathAndNoOpen()
    {
        const int port = 3097;
        var realInstallRoot = Path.Combine(_tempRoot, "npx", "99cabb0ceac85b86");
        var realPackageDir = CreatePackageTree(realInstallRoot);

        // 复现用户机器形态：profiles\node_modules\@deepseek-ai\dsh 是指向 npx 缓存的 Junction
        var linkPackageDir = Path.Combine(_tempRoot, ".dsh", "profiles", "node_modules", "@deepseek-ai", "dsh");
        CreateJunction(linkPackageDir, realPackageDir);

        var linkBinJs = Path.Combine(linkPackageDir, "lib", "bin.js");
        var realBinJs = Path.Combine(realPackageDir, "lib", "bin.js");

        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => null);

        await service.StartAsync($"node \"{linkBinJs}\"", _tempRoot, port);

        var startInfo = launcher.LastStartInfo!;
        Assert.Contains(realBinJs, startInfo.Arguments);                                // 物理 bin.js
        Assert.DoesNotContain(linkPackageDir, startInfo.Arguments);                     // 不再用链接路径
        Assert.Contains($"web --port {port} --no-open", startInfo.Arguments);           // 端口 + 不弹浏览器

        var nodePath = startInfo.EnvironmentVariables["NODE_PATH"];
        Assert.Equal(Path.Combine(realInstallRoot, "node_modules"), nodePath);           // 安装根下的 node_modules
        Assert.False(nodePath!.EndsWith("@deepseek-ai", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 链接有效、但物理目标缺 <c>lib\bin.js</c>（残缺目录）时，必须**回退链接路径**：
    /// 宁可继续用链接，也不能给 dsh 一个不存在的物理 bin.js（那会把"能用"变成"必然失败"）。
    /// </summary>
    [Fact]
    public async Task Start_WhenPhysicalBinMissing_FallsBackToLinkPath()
    {
        const int port = 3098;
        // 物理目标只有 package.json，故意不建 lib\bin.js
        var incompleteDir = Path.Combine(_tempRoot, "incomplete", "hashI", "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(incompleteDir);
        File.WriteAllText(Path.Combine(incompleteDir, "package.json"), "{\"name\":\"@deepseek-ai/dsh\"}");

        var linkPackageDir = Path.Combine(_tempRoot, "broken-profile", "node_modules", "@deepseek-ai", "dsh");
        CreateJunction(linkPackageDir, incompleteDir);
        var linkBinJs = Path.Combine(linkPackageDir, "lib", "bin.js");

        var launcher = new FakeLauncher { HandleFactory = () => new FakeProcessHandle() };
        var service = new DshProcessService(
            launcher, (_, _, _) => true, logFilePath: NewLogPath(),
            portableNodePathResolver: () => null);

        await service.StartAsync($"node \"{linkBinJs}\"", _tempRoot, port);

        var args = launcher.LastStartInfo!.Arguments;
        Assert.Contains(linkBinJs, args);                                        // 回退链接路径
        Assert.DoesNotContain(Path.Combine(incompleteDir, "lib", "bin.js"), args); // 不指向不存在的物理 bin.js
        Assert.Contains($"web --port {port} --no-open", args);
    }

    /// <summary>
    /// 造一份 npx 形态的安装树：&lt;installRoot&gt;\node_modules\@deepseek-ai\dsh（含 package.json + lib\bin.js）。
    /// 返回 dsh 包目录。
    /// </summary>
    private static string CreatePackageTree(string installRoot)
    {
        var packageDir = Path.Combine(installRoot, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(packageDir, "lib"));
        File.WriteAllText(Path.Combine(packageDir, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.3\"}");
        File.WriteAllText(Path.Combine(packageDir, "lib", "bin.js"), "console.log('dsh');");
        return packageDir;
    }

    /// <summary>在 linkPath 处创建指向 targetPath 的目录 Junction（等价 mklink /J，不需要管理员权限）。</summary>
    private static void CreateJunction(string linkPath, string targetPath)
    {
        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
            throw new InvalidOperationException($"创建 Junction 失败：{error}");
    }
}
