using System.Diagnostics;
using System.Text;

namespace DshManager.Services;

/// <summary>
/// 安装完成事件参数：向订阅方报告命令执行结果。
/// </summary>
public class InstallCompletedEventArgs : EventArgs
{
    /// <summary>是否成功（退出码为 0）。</summary>
    public bool Success { get; init; }

    /// <summary>子进程退出码；启动失败时为 -1。</summary>
    public int ExitCode { get; init; }

    /// <summary>安装过程累计输出（stdout + stderr）。</summary>
    public string Output { get; init; } = string.Empty;
}

/// <summary>
/// dsh 安装 / 更新服务：通过 npm（或注入的自定义命令）安装 @deepseek-ai/dsh。
/// 子进程运行，逐行实时触发 <see cref="OutputReceived"/>，结束触发 <see cref="Completed"/>；
/// 默认超时 10 分钟（超时或取消时强制结束进程树并判定失败）。
/// </summary>
public class DshInstallService
{
    /// <summary>输出行事件：安装过程中 stdout/stderr 每输出一行即触发（在子进程线程上触发，UI 订阅方需自行调度到 UI 线程）。</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>完成事件：命令执行结束（成功 / 失败 / 超时 / 取消）后触发。</summary>
    public event EventHandler<InstallCompletedEventArgs>? Completed;

    // 注：这两个字段由构造函数里的 SplitCommand 以 out 参数形式赋值，不能用 readonly
    private string _commandFile;
    private string _commandArgs;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// 创建安装服务实例。
    /// </summary>
    /// <param name="command">安装命令；为空时默认 "npm install -g @deepseek-ai/dsh"。</param>
    /// <param name="timeout">超时时间；为空时默认 10 分钟。</param>
    public DshInstallService(string? command = null, TimeSpan? timeout = null)
    {
        // 默认使用 npm 全局安装 @deepseek-ai/dsh（npm install -g 亦会更新到最新版）
        var cmd = string.IsNullOrWhiteSpace(command) ? "npm install -g @deepseek-ai/dsh" : command;

        // 拆分命令：首个 token 为可执行文件，其余为参数。
        // 【为什么不能 Split(' ')】自定义安装命令里的路径常带空格并被引号包住，
        // 例如 "C:\Program Files\nodejs\npm.cmd" install -g x。按空格裸切会得到
        // parts[0] = "C:\Program（含开头引号、缺结尾），FileName 与 Arguments 双双被切坏，
        // Process.Start 直接抛异常，用户只看到一句含糊的"启动失败"。
        // SplitCommand 是项目内既有的、认识引号的拆分器（DshProcessService 也在用），直接复用。
        DshProcessService.SplitCommand(cmd, out _commandFile, out _commandArgs);

        // 兜底：命令只剩空白或拆分不出可执行文件时，退回默认 npm 命令，避免起一个空 FileName 的进程
        if (string.IsNullOrWhiteSpace(_commandFile))
        {
            DshProcessService.SplitCommand("npm install -g @deepseek-ai/dsh", out _commandFile, out _commandArgs);
        }

        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>安装 dsh（异步执行安装命令）。</summary>
    public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(cancellationToken);

    /// <summary>更新 dsh 到最新版（异步执行；默认与安装使用同一命令）。</summary>
    public Task<bool> UpdateAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(cancellationToken);

    /// <summary>
    /// 在子进程中运行安装命令：实时捕获输出、支持超时与取消。
    /// </summary>
    private async Task<bool> RunCommandAsync(CancellationToken cancellationToken)
    {
        var output = new StringBuilder();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _commandFile,
            Arguments = _commandArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // 逐行捕获 stdout / stderr：既累计到 output，也实时触发 OutputReceived 供 UI 展示进度
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);

        // 进程是否在超时/取消前正常退出（在 try 外声明，便于 catch 之后继续使用）
        var exited = false;

        try
        {
            if (!process.Start())
            {
                RaiseCompleted(success: false, exitCode: -1, output.ToString());
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 叠加超时与调用方取消信号：任一先到即停止等待
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            // 等待进程退出（WaitForExitAsync 在进程退出后会排空异步输出事件）
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exited = true; // 超时/取消前正常退出
            }
            catch (OperationCanceledException)
            {
                exited = false; // 超时或调用方取消：走下方强制结束分支
            }

            if (!exited)
            {
                // 超时或取消：强制结束整个进程树，并视为失败
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 启动失败（如 npm 不在 PATH）：报告失败而非崩溃
            RaiseCompleted(success: false, exitCode: -1, output.ToString());
            return false;
        }

        var success = exited && process.ExitCode == 0;
        RaiseCompleted(success, process.ExitCode, output.ToString());
        return success;
    }

    private void AppendLine(StringBuilder output, string? line)
    {
        if (line == null) return;
        output.AppendLine(line);
        OutputReceived?.Invoke(this, line);
    }

    private void RaiseCompleted(bool success, int exitCode, string output)
    {
        Completed?.Invoke(this, new InstallCompletedEventArgs
        {
            Success = success,
            ExitCode = exitCode,
            Output = output,
        });
    }
}
