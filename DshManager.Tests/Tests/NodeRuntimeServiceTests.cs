using System.IO;
using System.Net;
using System.Net.Http;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// NodeRuntimeService 单元测试。
/// 全部在临时目录作业，并注入假 HTTP 处理器 / 假解压器 / 假版本探测器，
/// 因此**不联网、不下载 35 MB、不真的解压安装、不触碰真实 %LOCALAPPDATA% 与用户目录**。
/// </summary>
public class NodeRuntimeServiceTests : IDisposable
{
    /// <summary>每个测试独占的临时根目录（便携版安装根目录挂在它下面）。</summary>
    private readonly string _tempRoot;

    /// <summary>便携版安装根目录（测试内不会真实安装）。</summary>
    private readonly string _runtimesRoot;

    public NodeRuntimeServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
        _runtimesRoot = Path.Combine(_tempRoot, "runtimes", "node");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    // ---------------- 测试辅助 ----------------

    /// <summary>假 HTTP 处理器：按请求返回预置响应，避免真实联网。</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Responder is null) throw new InvalidOperationException("未配置 Responder");
            // 同步抛异常（模拟网络故障）也能被上层捕获，无需包 try
            return Task.FromResult(Responder(request));
        }
    }

    /// <summary>模拟的官方版本索引：一个非 LTS 新版本 + 两个 LTS，用于验证"只取最新 LTS"。</summary>
    private const string IndexJson =
        "[{\"version\":\"v25.0.0\",\"lts\":false}," +
        "{\"version\":\"v24.12.0\",\"lts\":\"Krypton\"}," +
        "{\"version\":\"v22.20.0\",\"lts\":\"Jod\"}]";

    /// <summary>构造被测服务；默认什么都不注入也能创建（仅路径查询用）。</summary>
    private NodeRuntimeService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        Action<string, string>? zipExtractor = null,
        Func<string, string?>? nodeVersionProbe = null)
        => new(
            runtimesRoot: _runtimesRoot,
            messageHandler: new FakeHttpMessageHandler { Responder = responder },
            zipExtractor: zipExtractor,
            nodeVersionProbe: nodeVersionProbe ?? (_ => "v24.12.0"));

    private static HttpResponseMessage OkText(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage OkBytes(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentLength = data.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>假的 Node 官方镜像：index.json 返回版本索引，其余（zip）返回给定字节。</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> NodeServer(byte[] zipBytes, string indexJson)
        => request => request.RequestUri!.AbsoluteUri.Contains("index.json", StringComparison.OrdinalIgnoreCase)
            ? OkText(indexJson)
            : OkBytes(zipBytes);

    /// <summary>假解压器：在目标目录里造出官方 zip 的布局 &lt;dest&gt;\node-&lt;ver&gt;-win-x64\node.exe。</summary>
    private static Action<string, string> FakeExtractor(string version, bool includeNodeExe = true)
        => (_, destDir) =>
        {
            var payload = Path.Combine(destDir, $"node-{version}-win-x64");
            Directory.CreateDirectory(payload);
            if (!includeNodeExe) return;
            File.WriteAllText(Path.Combine(payload, "node.exe"), "fake-node");
            File.WriteAllText(Path.Combine(payload, "npm.cmd"), "fake-npm");
            File.WriteAllText(Path.Combine(payload, "npx.cmd"), "fake-npx");
        };

    // ---------------- 路径查询 ----------------

    /// <summary>未安装时三个路径查询都返回 null。</summary>
    [Fact]
    public void PathQueries_WhenNotInstalled_ReturnNull()
    {
        var service = CreateService();

        Assert.Null(service.GetPortableNodePath());
        Assert.Null(service.GetPortableNpmPath());
        Assert.Null(service.GetPreferredNodePath());
    }

    /// <summary>安装目录里存在 node.exe / npm.cmd 时返回其绝对路径。</summary>
    [Fact]
    public void PathQueries_WhenFilesExist_ReturnAbsolutePaths()
    {
        Directory.CreateDirectory(_runtimesRoot);
        File.WriteAllText(Path.Combine(_runtimesRoot, "node.exe"), "fake");
        File.WriteAllText(Path.Combine(_runtimesRoot, "npm.cmd"), "fake");

        var service = CreateService();

        Assert.Equal(Path.Combine(_runtimesRoot, "node.exe"), service.GetPortableNodePath());
        Assert.Equal(Path.Combine(_runtimesRoot, "npm.cmd"), service.GetPortableNpmPath());

        // 首选路径策略是「系统已有环境优先」：
        // 系统有 node → 返回 null，表示保持裸 node（沿用系统 PATH，保证 dsh 子进程环境一致）
        Assert.Null(new NodeRuntimeService(_runtimesRoot, systemNodeProbe: () => true).GetPreferredNodePath());
        // 系统没有 node → 才回退到面板便携版
        Assert.Equal(Path.Combine(_runtimesRoot, "node.exe"),
                     new NodeRuntimeService(_runtimesRoot, systemNodeProbe: () => false).GetPreferredNodePath());
    }

    /// <summary>运行目录用空格路径时 RuntimesRoot 仍原样保留（供确认框展示）。</summary>
    [Fact]
    public void RuntimesRoot_IsPreservedAsGiven()
    {
        var service = new NodeRuntimeService(runtimesRoot: _runtimesRoot);

        Assert.Equal(_runtimesRoot, service.RuntimesRoot);
    }

    // ---------------- 版本获取与回退 ----------------

    /// <summary>index.json 里最新的是非 LTS，应取最新的 LTS 版本。</summary>
    [Fact]
    public async Task GetLatestLtsVersionAsync_PicksNewestLts_SkippingNonLts()
    {
        var service = CreateService(responder: NodeServer(Array.Empty<byte>(), IndexJson));

        var version = await service.GetLatestLtsVersionAsync();

        Assert.Equal("v24.12.0", version);
    }

    /// <summary>HTTP 失败（网络不可用）→ 回退内置安全版本，保证离线也能装。</summary>
    [Fact]
    public async Task GetLatestLtsVersionAsync_WhenNetworkFails_FallsBackToBuiltin()
    {
        var service = CreateService(responder: _ => throw new HttpRequestException("no network"));

        var version = await service.GetLatestLtsVersionAsync();

        Assert.Equal(NodeRuntimeService.FallbackLtsVersion, version);
    }

    /// <summary>索引 JSON 损坏 → 同样回退内置版本，不抛异常。</summary>
    [Fact]
    public async Task GetLatestLtsVersionAsync_WhenIndexMalformed_FallsBackToBuiltin()
    {
        var service = CreateService(responder: NodeServer(Array.Empty<byte>(), "{ not a json array"));

        var version = await service.GetLatestLtsVersionAsync();

        Assert.Equal(NodeRuntimeService.FallbackLtsVersion, version);
    }

    // ---------------- 安装流程 ----------------

    /// <summary>成功路径：下载 → 解压 → 规整到 RuntimesRoot → 验证，并回传中文进度。</summary>
    [Fact]
    public async Task InstallPortableNodeAsync_WhenSuccessful_InstallsAndReportsProgress()
    {
        var zipBytes = new byte[64 * 1024];
        var service = CreateService(
            responder: NodeServer(zipBytes, IndexJson),
            zipExtractor: FakeExtractor("v24.12.0"),
            nodeVersionProbe: _ => "v24.12.0");

        var progress = new List<string>();
        var ok = await service.InstallPortableNodeAsync(progress.Add);

        Assert.True(ok);
        // 最终 node.exe 必须落在 RuntimesRoot 下（而不是嵌套的 node-vX-win-x64 目录）
        var nodeExe = service.GetPortableNodePath();
        Assert.NotNull(nodeExe);
        Assert.Equal(Path.Combine(_runtimesRoot, "node.exe"), nodeExe);
        Assert.True(File.Exists(nodeExe));
        Assert.NotNull(service.GetPortableNpmPath());

        Assert.Contains(progress, l => l.Contains("正在获取") && l.Contains("LTS"));
        Assert.Contains(progress, l => l.Contains("正在下载") && l.Contains("MB") && l.Contains("(100%)"));
        Assert.Contains(progress, l => l.Contains("正在解压"));
        Assert.Contains(progress, l => l.Contains("安装完成") && l.Contains("v24.12.0"));
    }

    /// <summary>解压结果里没有 node.exe → 判定失败并给出中文原因。</summary>
    [Fact]
    public async Task InstallPortableNodeAsync_WhenPayloadMissingNodeExe_ReturnsFalse()
    {
        var service = CreateService(
            responder: NodeServer(new byte[1024], IndexJson),
            zipExtractor: FakeExtractor("v24.12.0", includeNodeExe: false));

        var progress = new List<string>();
        var ok = await service.InstallPortableNodeAsync(progress.Add);

        Assert.False(ok);
        Assert.Contains(progress, l => l.Contains("未找到 node.exe"));
        Assert.Null(service.GetPortableNodePath());
    }

    /// <summary>下载返回非 2xx → 失败并回传中文原因，不抛异常。</summary>
    [Fact]
    public async Task InstallPortableNodeAsync_WhenDownloadFails_ReturnsFalse()
    {
        var service = CreateService(responder: _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var progress = new List<string>();
        var ok = await service.InstallPortableNodeAsync(progress.Add);

        Assert.False(ok);
        Assert.Contains(progress, l => l.Contains("安装失败"));
    }

    /// <summary>安装后 node.exe 探测失败（无法执行）→ 判定失败，不留下"看起来装好了"的假象。</summary>
    [Fact]
    public async Task InstallPortableNodeAsync_WhenNodeNotExecutable_ReturnsFalse()
    {
        var service = CreateService(
            responder: NodeServer(new byte[1024], IndexJson),
            zipExtractor: FakeExtractor("v24.12.0"),
            nodeVersionProbe: _ => null);

        var progress = new List<string>();
        var ok = await service.InstallPortableNodeAsync(progress.Add);

        Assert.False(ok);
        Assert.Contains(progress, l => l.Contains("无法执行"));
    }

    /// <summary>已取消的令牌 → 失败并回传"已取消"，不抛异常。</summary>
    [Fact]
    public async Task InstallPortableNodeAsync_WhenCancelled_ReturnsFalse()
    {
        var service = CreateService(responder: NodeServer(new byte[1024], IndexJson));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var progress = new List<string>();
        var ok = await service.InstallPortableNodeAsync(progress.Add, cts.Token);

        Assert.False(ok);
        Assert.Contains(progress, l => l.Contains("取消"));
    }

    /// <summary>重复安装应清空旧目录：旧文件不会被保留（保证新版本干净）。</summary>
    [Fact]
    public async Task InstallPortableNodeAsync_ClearsStaleInstallDirectory()
    {
        // 预置一个"上一版"的残留文件
        Directory.CreateDirectory(_runtimesRoot);
        File.WriteAllText(Path.Combine(_runtimesRoot, "stale.txt"), "old");

        var service = CreateService(
            responder: NodeServer(new byte[1024], IndexJson),
            zipExtractor: FakeExtractor("v24.12.0"),
            nodeVersionProbe: _ => "v24.12.0");

        var ok = await service.InstallPortableNodeAsync(_ => { });

        Assert.True(ok);
        Assert.False(File.Exists(Path.Combine(_runtimesRoot, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(_runtimesRoot, "node.exe")));
    }
}
