using System.Net;
using System.Net.Sockets;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// DshProcessProbe 单元测试：
/// 只覆盖可离线验证的纯逻辑（命令行甄别、端口解析、CPU 差分、端口反查的失败容错），
/// 全程不依赖真实 dsh 进程，也不依赖真实 WMI 查询。
/// </summary>
public class DshProcessProbeTests
{
    /// <summary>固定基准时刻，避免测试依赖当前时钟。</summary>
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    // ---------------- LooksLikeDsh：命中 ----------------

    /// <summary>npx 临时执行形式（@deepseek-ai/dsh@版本号）应被识别为 dsh。</summary>
    [Fact]
    public void LooksLikeDsh_NpxWithScopedPackage_ReturnsTrue()
    {
        Assert.True(DshProcessProbe.LooksLikeDsh(
            "npx --yes @deepseek-ai/dsh@0.1.5-rc.3 web --port 3080"));
    }

    /// <summary>本地安装形式（node 直接跑 @deepseek-ai\dsh\lib\bin.js）应被识别为 dsh。</summary>
    [Fact]
    public void LooksLikeDsh_LocalBinJsPath_ReturnsTrue()
    {
        Assert.True(DshProcessProbe.LooksLikeDsh(
            @"node C:\Users\me\AppData\Local\npm-cache\_npx\abc123\node_modules\@deepseek-ai\dsh\lib\bin.js web"));
    }

    /// <summary>全局命令形式（dsh web / dsh.cmd web / dsh.exe web）应被识别为 dsh。</summary>
    [Theory]
    [InlineData("\"C:\\Users\\me\\AppData\\Roaming\\npm\\dsh.cmd\" web --port 3080")]
    [InlineData("dsh web")]
    [InlineData(@"C:\tools\node\dsh.exe web")]
    public void LooksLikeDsh_GlobalCommandWeb_ReturnsTrue(string commandLine)
    {
        Assert.True(DshProcessProbe.LooksLikeDsh(commandLine));
    }

    // ---------------- LooksLikeDsh：排除项 ----------------

    /// <summary>dsh- 前缀的配套包不是 dsh 本体，必须排除（否则会把无关 node 进程算进来）。</summary>
    [Theory]
    [InlineData(@"node C:\x\node_modules\@deepseek-ai\dsh-agent-presets\lib\index.js")]
    [InlineData(@"node C:\x\node_modules\@deepseek-ai\dsh-subprocess-local\lib\index.js --port 3080")]
    [InlineData("npx --yes @deepseek-ai/dsh-agent-presets web")]
    public void LooksLikeDsh_CompanionPackages_ReturnsFalse(string commandLine)
    {
        Assert.False(DshProcessProbe.LooksLikeDsh(commandLine));
    }

    /// <summary>无关 node 进程、空值、以及 dsh 的非 web 子命令都不算 dsh 服务（只有 web 才起服务）。</summary>
    [Theory]
    [InlineData(@"node C:\x\project\server.js")]
    [InlineData(@"node C:\x\node_modules\vite\bin\vite.js")]
    [InlineData("dsh install")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeDsh_Unrelated_ReturnsFalse(string? commandLine)
    {
        Assert.False(DshProcessProbe.LooksLikeDsh(commandLine));
    }

    // ---------------- ParsePort ----------------

    /// <summary>--port 与 --port= 两种写法都要能解析出端口。</summary>
    [Theory]
    [InlineData("npx @deepseek-ai/dsh web --port 3080", 3080)]
    [InlineData("node bin.js web --port=4000", 4000)]
    [InlineData("node bin.js web --port   5678", 5678)]
    public void ParsePort_WithFlag_ReturnsPort(string commandLine, int expected)
    {
        Assert.Equal(expected, DshProcessProbe.ParsePort(commandLine));
    }

    /// <summary>没有 --port 参数（或参数后没有数字）时返回 0。</summary>
    [Theory]
    [InlineData("npx @deepseek-ai/dsh web")]
    [InlineData("npx @deepseek-ai/dsh web --port")]
    [InlineData("")]
    [InlineData(null)]
    public void ParsePort_WithoutPort_ReturnsZero(string? commandLine)
    {
        Assert.Equal(0, DshProcessProbe.ParsePort(commandLine));
    }

    // ---------------- ComputeCpuPercent ----------------

    /// <summary>正常差分：1 秒内累计 CPU 增加 1 秒 → 占全部逻辑核的 100/核数 %。</summary>
    [Fact]
    public void ComputeCpuPercent_NormalDelta_ReturnsPercentage()
    {
        var percent = DshProcessProbe.ComputeCpuPercent(1.0, BaseTime, 2.0, BaseTime.AddSeconds(1));

        Assert.NotNull(percent);
        Assert.Equal(100.0 / Environment.ProcessorCount, percent!.Value, 1);
    }

    /// <summary>采样间隔不足 0.2 秒时差分噪声过大，返回 null（含等于 0.2 的边界）。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.2)]
    public void ComputeCpuPercent_IntervalTooShort_ReturnsNull(double elapsedSeconds)
    {
        var percent = DshProcessProbe.ComputeCpuPercent(
            0, BaseTime, 5, BaseTime.AddSeconds(elapsedSeconds));

        Assert.Null(percent);
    }

    /// <summary>间隔刚超过阈值（0.21 秒）即可正常计算。</summary>
    [Fact]
    public void ComputeCpuPercent_JustAboveThreshold_ReturnsValue()
    {
        var percent = DshProcessProbe.ComputeCpuPercent(0, BaseTime, 0.5, BaseTime.AddSeconds(0.21));

        Assert.NotNull(percent);
    }

    /// <summary>累计 CPU 时间倒退（进程重启 / 时钟回拨）按 0 处理，不出现负数。</summary>
    [Fact]
    public void ComputeCpuPercent_NegativeDelta_ReturnsZero()
    {
        var percent = DshProcessProbe.ComputeCpuPercent(10, BaseTime, 1, BaseTime.AddSeconds(1));

        Assert.Equal(0.0, percent!.Value);
    }

    /// <summary>超大增量被夹到 100，不会出现超过 100% 的荒谬值。</summary>
    [Fact]
    public void ComputeCpuPercent_HugeDelta_IsClampedTo100()
    {
        var percent = DshProcessProbe.ComputeCpuPercent(0, BaseTime, 10_000, BaseTime.AddSeconds(1));

        Assert.Equal(100.0, percent!.Value);
    }

    // ---------------- FindPortOwner：失败容错路径 ----------------

    /// <summary>非法端口（0 / 负数 / 越界）直接返回 null，不做内核查询也不抛异常。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void FindPortOwner_InvalidPort_ReturnsNull(int port)
    {
        Assert.Null(DshProcessProbe.FindPortOwner(port));
    }

    /// <summary>无人监听的端口返回 null（P/Invoke 查询失败容错），不抛异常。</summary>
    [Fact]
    public void FindPortOwner_UnusedPort_ReturnsNull()
    {
        // 先占用系统分配的随机端口再释放，确保该端口当前无人监听
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Null(DshProcessProbe.FindPortOwner(port));
    }
}
