using System.IO;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// EnvironmentCheckService 单元测试。
/// 全部使用临时目录作为 DSH_HOME，并注入假命令执行器 / 假可执行文件定位器 / 假端口探测 / 假发现服务，
/// 因此**不触碰真实的 .dsh 目录、不执行任何修复命令、不联网**。
/// </summary>
public class EnvironmentCheckServiceTests : IDisposable
{
    /// <summary>每个测试独占的临时 DSH_HOME；测试结束后递归删除。</summary>
    private readonly string _tempRoot;

    public EnvironmentCheckServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DshManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    // ---------------- 测试辅助 ----------------

    /// <summary>
    /// 构造"已安装 dsh"的假发现服务：
    /// 候选里有一个可解析版本的启动器（IsInstalled=true），搜索根目录置空以隔离真实 PATH / npm / 用户目录。
    /// </summary>
    private static DshDiscoveryService CreateInstalledDiscovery(string dataDir)
    {
        const string launcher = @"C:\fake\bin\dsh.cmd";
        return new DshDiscoveryService(
            candidateProvider: () => new[] { launcher },
            dataDir: dataDir,
            pathExists: p => p == launcher || p == dataDir,
            fileContentReader: _ => null,
            versionDetector: _ => "0.1.5-rc.3",
            npxResolver: () => null,
            searchRootProvider: () => new DshSearchRoots());
    }

    /// <summary>构造"未安装 dsh"的假发现服务（全部不存在，searchRoots 也置空）。</summary>
    private static DshDiscoveryService CreateMissingDiscovery(string dataDir)
        => new(
            candidateProvider: () => Array.Empty<string>(),
            dataDir: dataDir,
            pathExists: _ => false,
            fileContentReader: _ => null,
            versionDetector: _ => null,
            npxResolver: () => null,
            searchRootProvider: () => new DshSearchRoots());

    /// <summary>假命令执行器：按命令名返回给定输出；传 null 表示该命令"未找到"。</summary>
    private static Func<string, string, string?> Runner(string? nodeVersion, string? npmVersion, string? npxVersion)
        => (file, _) => file.ToLowerInvariant() switch
        {
            "node" => nodeVersion,
            "npm" => npmVersion,
            "npx" => npxVersion,
            _ => null,
        };

    /// <summary>构造被测服务（默认一切正常，可按需覆盖单项）。</summary>
    private EnvironmentCheckService CreateService(
        string? nodeVersion = "v24.21.0",
        string? npmVersion = "10.9.0",
        string? npxVersion = "10.9.0",
        string? nodePath = @"C:\Program Files\nodejs\node.exe",
        bool portInUse = false,
        bool dshInstalled = true,
        int port = 3080,
        Func<string, string, string?>? commandRunner = null,
        NodeRuntimeService? nodeRuntime = null)
        => new(
            dshHome: _tempRoot,
            profileName: "web",
            port: port,
            discovery: dshInstalled ? CreateInstalledDiscovery(_tempRoot) : CreateMissingDiscovery(_tempRoot),
            commandRunner: commandRunner ?? Runner(nodeVersion, npmVersion, npxVersion),
            executableLocator: _ => nodePath,
            portProbe: _ => portInUse,
            // 默认注入指向临时空目录的便携版服务，隔离真实 %LOCALAPPDATA%（否则本机真装了便携版会导致用例漂移）
            nodeRuntime: nodeRuntime ?? new NodeRuntimeService(Path.Combine(_tempRoot, "runtimes", "node")));

    private static EnvCheckItem Item(IReadOnlyList<EnvCheckItem> items, string name)
        => items.Single(i => i.Name == name);

    /// <summary>在临时 DSH_HOME 下创建 profiles\node_modules\@deepseek-ai\&lt;pkg&gt; 目录。</summary>
    private void CreateSharedPackages(params string[] packages)
    {
        foreach (var package in packages)
            Directory.CreateDirectory(Path.Combine(
                _tempRoot, "profiles", "node_modules", "@deepseek-ai", package));
    }

    /// <summary>创建 dsh 入口脚本 profiles\node_modules\@deepseek-ai\dsh\lib\bin.js 及其所在目录。</summary>
    private string CreateBinJs()
    {
        var binJs = Path.Combine(
            _tempRoot, "profiles", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        Directory.CreateDirectory(Path.GetDirectoryName(binJs)!);
        File.WriteAllText(binJs, "// fake dsh entry");
        return binJs;
    }

    /// <summary>创建 DSH_HOME 下的配置文件。</summary>
    private void CreateConfig(string fileName)
        => File.WriteAllText(Path.Combine(_tempRoot, fileName), "# test");

    // ---------------- 整体结构 ----------------

    /// <summary>CheckAll 固定返回 6 项，且顺序稳定（界面按此顺序展示）。</summary>
    [Fact]
    public void CheckAll_ReturnsSixItemsInFixedOrder()
    {
        var items = CreateService().CheckAll();

        Assert.Equal(6, items.Count);
        Assert.Equal(
            new[] { "Node.js", "npm / npx", "dsh 本体", "dsh 依赖完整性", "端口可用性", "配置文件" },
            items.Select(i => i.Name));
        // Detail / Hint / FixKind 永不为 null（界面直接绑定）
        Assert.All(items, i =>
        {
            Assert.NotNull(i.Detail);
            Assert.NotNull(i.Hint);
            Assert.NotNull(i.FixKind);
        });
    }

    // ---------------- 1) Node.js ----------------

    /// <summary>Node 未找到 → Missing + 一键安装便携版（FixTarget 为特殊标记，UI 特判走安装流程）。</summary>
    [Fact]
    public void CheckNode_WhenMissing_ReturnsMissingWithPortableInstallFix()
    {
        var node = Item(CreateService(nodeVersion: null).CheckAll(), "Node.js");

        Assert.Equal(EnvCheckStatus.Missing, node.Status);
        Assert.Equal("未检测到", node.Detail);
        Assert.Equal("runCommand", node.FixKind);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, node.FixTarget);
        Assert.Equal("一键安装 Node（便携版）", node.FixLabel);
        // 提示里保留"也可自行到 nodejs.org 安装"与"便携版只供面板使用"的说明
        Assert.Contains("nodejs.org", node.Hint);
        Assert.Contains("便携版", node.Hint);
        Assert.Contains("Node.js", node.Hint);
    }

    /// <summary>Node 存在 → Ok，详情同时含版本号与可执行文件路径。</summary>
    [Fact]
    public void CheckNode_WhenPresent_ReturnsOkWithVersionAndPath()
    {
        var node = Item(CreateService().CheckAll(), "Node.js");

        Assert.Equal(EnvCheckStatus.Ok, node.Status);
        Assert.Contains("v24.21.0", node.Detail);
        Assert.Contains(@"C:\Program Files\nodejs\node.exe", node.Detail);
        Assert.Equal("none", node.FixKind);
    }

    /// <summary>定位不到路径时只显示版本，不影响 Ok 结论。</summary>
    [Fact]
    public void CheckNode_WhenPathUnknown_StillOk()
    {
        var node = Item(CreateService(nodePath: null).CheckAll(), "Node.js");

        Assert.Equal(EnvCheckStatus.Ok, node.Status);
        Assert.Equal("v24.21.0", node.Detail);
    }

    // ---------------- 2) npm / npx ----------------

    /// <summary>
    /// npm 缺失 → Warning + 一键安装便携版 Node（便携版自带 npm/npx）；Hint 保留"也可自行到 nodejs.org 安装"的说明。
    /// </summary>
    [Fact]
    public void CheckNpm_WhenNpmMissing_ReturnsRunCommandWithPortableInstall()
    {
        var npm = Item(CreateService(npmVersion: null).CheckAll(), "npm / npx");

        Assert.Equal(EnvCheckStatus.Warning, npm.Status);
        Assert.Equal("runCommand", npm.FixKind);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, npm.FixTarget);
        Assert.Equal("一键安装 Node（便携版，含 npm/npx）", npm.FixLabel);
        // Hint 说明"便携版自带 npm/npx"，也保留"可自行到 nodejs.org 安装"的兜底
        Assert.Contains("npm", npm.Hint);
        Assert.Contains("nodejs.org", npm.Hint);
        Assert.Contains("未检测到", npm.Detail);
    }

    /// <summary>npx 缺失同样报 Warning，并复用同一套便携版安装修复动作。</summary>
    [Fact]
    public void CheckNpm_WhenNpxMissing_ReturnsWarningWithPortableFix()
    {
        var npm = Item(CreateService(npxVersion: null).CheckAll(), "npm / npx");

        Assert.Equal(EnvCheckStatus.Warning, npm.Status);
        Assert.Contains("npx", npm.Detail);
        Assert.Equal("runCommand", npm.FixKind);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, npm.FixTarget);
    }

    /// <summary>npm / npx 都正常 → Ok。</summary>
    [Fact]
    public void CheckNpm_WhenPresent_ReturnsOk()
    {
        var npm = Item(CreateService().CheckAll(), "npm / npx");

        Assert.Equal(EnvCheckStatus.Ok, npm.Status);
        Assert.Contains("npm 10.9.0", npm.Detail);
        Assert.Contains("npx 10.9.0", npm.Detail);
    }

    // ---------------- 3) dsh 本体 ----------------

    /// <summary>未安装 dsh → Missing + 一键安装命令。</summary>
    [Fact]
    public void CheckDsh_WhenNotInstalled_ReturnsMissingWithInstallCommand()
    {
        var dsh = Item(CreateService(dshInstalled: false).CheckAll(), "dsh 本体");

        Assert.Equal(EnvCheckStatus.Missing, dsh.Status);
        Assert.Equal("runCommand", dsh.FixKind);
        Assert.Equal("npm install -g @deepseek-ai/dsh", dsh.FixTarget);
        Assert.Equal("一键安装 dsh", dsh.FixLabel);
    }

    /// <summary>已安装 dsh → Ok，详情含版本与推荐安装位置 / 启动方式。</summary>
    [Fact]
    public void CheckDsh_WhenInstalled_ReturnsOkWithVersion()
    {
        var dsh = Item(CreateService().CheckAll(), "dsh 本体");

        Assert.Equal(EnvCheckStatus.Ok, dsh.Status);
        Assert.Contains("0.1.5-rc.3", dsh.Detail);
        Assert.Equal("none", dsh.FixKind);
    }

    // ---------------- 4) dsh 依赖完整性 ----------------

    /// <summary>关键包齐全 → Ok。</summary>
    [Fact]
    public void CheckDependencies_WhenAllPresent_ReturnsOk()
    {
        CreateSharedPackages("dsh-llm", "dsh-session", "dsh-base", "dsh-web-app");

        var item = Item(CreateService().CheckAll(), "dsh 依赖完整性");

        Assert.Equal(EnvCheckStatus.Ok, item.Status);
        Assert.Equal("none", item.FixKind);
        Assert.Contains("dsh-llm", item.Detail);
    }

    /// <summary>缺包且 dsh 入口存在 → Warning + 用 dsh 入口在 profile 目录执行 install（命令内含 cd /d）。</summary>
    [Fact]
    public void CheckDependencies_WhenPackageMissing_ReturnsWarningWithDshInstallCommand()
    {
        CreateSharedPackages("dsh-llm", "dsh-session", "dsh-base"); // 故意缺 dsh-web-app
        CreateBinJs();

        var item = Item(CreateService().CheckAll(), "dsh 依赖完整性");

        Assert.Equal(EnvCheckStatus.Warning, item.Status);
        Assert.Contains("dsh-web-app", item.Detail);
        Assert.Equal("runCommand", item.FixKind);
        Assert.Equal("修复依赖", item.FixLabel);
        // 可靠写法：cd 到 profile 目录后由 dsh 入口转发 pnpm install
        Assert.Contains("cd /d", item.FixTarget);
        Assert.Contains(Path.Combine(_tempRoot, "profiles", "web"), item.FixTarget);
        Assert.Contains("plugin --profile web install", item.FixTarget);
        Assert.Contains("1 分钟", item.Hint);
    }

    /// <summary>缺包且找不到 dsh 入口 → 回退为在 profile 目录直接跑 pnpm install。</summary>
    [Fact]
    public void CheckDependencies_WhenBinJsMissing_FallsBackToPnpmInstall()
    {
        CreateSharedPackages("dsh-llm");

        var item = Item(CreateService().CheckAll(), "dsh 依赖完整性");

        Assert.Equal(EnvCheckStatus.Warning, item.Status);
        Assert.Contains("pnpm install", item.FixTarget);
        Assert.DoesNotContain("plugin --profile", item.FixTarget);
        Assert.Contains("cd /d", item.FixTarget);
    }

    /// <summary>profiles 目录整体缺失（dsh 尚未初始化）→ Warning 且不给修复命令。</summary>
    [Fact]
    public void CheckDependencies_WhenProfilesDirMissing_ReturnsWarningWithoutFix()
    {
        var item = Item(CreateService().CheckAll(), "dsh 依赖完整性");

        Assert.Equal(EnvCheckStatus.Warning, item.Status);
        Assert.Equal("none", item.FixKind);
        Assert.Contains("profiles", item.Detail);
    }

    // ---------------- 5) 端口可用性 ----------------

    /// <summary>端口被占用 → Warning，详情带端口号，且不提供自动修复（有副作用）。</summary>
    [Fact]
    public void CheckPort_WhenInUse_ReturnsWarning()
    {
        var item = Item(CreateService(portInUse: true, port: 3080).CheckAll(), "端口可用性");

        Assert.Equal(EnvCheckStatus.Warning, item.Status);
        Assert.Contains("3080", item.Detail);
        Assert.Equal("none", item.FixKind);
        Assert.Contains("设置页", item.Hint);
    }

    /// <summary>端口空闲 → Ok。</summary>
    [Fact]
    public void CheckPort_WhenFree_ReturnsOk()
    {
        var item = Item(CreateService(portInUse: false, port: 4001).CheckAll(), "端口可用性");

        Assert.Equal(EnvCheckStatus.Ok, item.Status);
        Assert.Contains("4001", item.Detail);
    }

    // ---------------- 6) 配置文件 ----------------

    /// <summary>两个配置文件都缺失 → Warning，详情列出两者。</summary>
    [Fact]
    public void CheckConfig_WhenBothMissing_ReturnsWarning()
    {
        var item = Item(CreateService().CheckAll(), "配置文件");

        Assert.Equal(EnvCheckStatus.Warning, item.Status);
        Assert.Contains("settings.yaml", item.Detail);
        Assert.Contains(".credentials.yaml", item.Detail);
        Assert.Equal("none", item.FixKind);
    }

    /// <summary>仅缺凭据文件 → Warning，提示可能无法调用模型（提到 API Key）。</summary>
    [Fact]
    public void CheckConfig_WhenCredentialsMissing_ReturnsWarningAboutApiKey()
    {
        CreateConfig("settings.yaml");

        var item = Item(CreateService().CheckAll(), "配置文件");

        Assert.Equal(EnvCheckStatus.Warning, item.Status);
        Assert.Contains(".credentials.yaml", item.Detail);
        Assert.Contains("API Key", item.Hint);
    }

    /// <summary>两个配置文件都在 → Ok。</summary>
    [Fact]
    public void CheckConfig_WhenBothPresent_ReturnsOk()
    {
        CreateConfig("settings.yaml");
        CreateConfig(".credentials.yaml");

        var item = Item(CreateService().CheckAll(), "配置文件");

        Assert.Equal(EnvCheckStatus.Ok, item.Status);
    }

    // ---------------- 异常安全 ----------------

    /// <summary>命令执行器 / 定位器 / 端口探测全部抛异常时，CheckAll 仍返回 6 项且不抛异常。</summary>
    [Fact]
    public void CheckAll_WhenDependenciesThrow_DoesNotThrow()
    {
        var service = new EnvironmentCheckService(
            dshHome: _tempRoot,
            profileName: "web",
            port: 3080,
            discovery: CreateInstalledDiscovery(_tempRoot),
            commandRunner: (_, _) => throw new InvalidOperationException("boom"),
            executableLocator: _ => throw new InvalidOperationException("boom"),
            portProbe: _ => throw new InvalidOperationException("boom"));

        var items = service.CheckAll(); // 不应抛异常

        Assert.Equal(6, items.Count);
        // Node / npm 检测因命令执行失败而降级为 Missing / Warning
        Assert.Equal(EnvCheckStatus.Missing, Item(items, "Node.js").Status);
        Assert.Equal(EnvCheckStatus.Warning, Item(items, "npm / npx").Status);
        // 端口探测失败按"可用"处理，避免误报
        Assert.Equal(EnvCheckStatus.Ok, Item(items, "端口可用性").Status);
    }

    // ---------------- 修复命令执行 ----------------

    /// <summary>空白命令直接返回 false，不启动任何进程。</summary>
    [Fact]
    public async Task RunFixAsync_WhenCommandBlank_ReturnsFalse()
    {
        var service = CreateService();

        Assert.False(await service.RunFixAsync(string.Empty));
        Assert.False(await service.RunFixAsync("   "));
        Assert.False(await service.RunFixAsync(null!));
    }

    /// <summary>
    /// 无害命令（echo）应执行成功并逐行回传输出——只验证 RunFixAsync 的输出管道，
    /// 不执行任何真正的修复 / 安装命令。
    /// </summary>
    [Fact]
    public async Task RunFixAsync_EchoCommand_StreamsOutputAndSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return; // cmd.exe 仅 Windows 存在

        var service = CreateService();
        var lines = new List<string>();

        var ok = await service.RunFixAsync("echo dsh-env-check", lines.Add);

        Assert.True(ok);
        Assert.Contains(lines, l => l.Contains("dsh-env-check"));
    }

    /// <summary>
    /// 便携版安装标记不是 shell 命令：RunFixAsync 必须拒绝执行（避免交给 cmd 报错），
    /// 真正的安装由 UI 调用 NodeRuntimeService 完成。
    /// </summary>
    [Fact]
    public async Task RunFixAsync_WithPortableInstallMarker_IsRejected()
    {
        var service = CreateService();
        var lines = new List<string>();

        var ok = await service.RunFixAsync(EnvironmentCheckService.InstallPortableNodeMarker, lines.Add);

        Assert.False(ok);
        Assert.Contains(lines, l => l.Contains("便携版 Node 安装流程"));
    }

    // ---------------- 便携版 Node 优先 ----------------

    /// <summary>
    /// 在临时目录里造出一个"已安装的便携版 Node"（node.exe / npm.cmd / npx.cmd），
    /// 并返回指向它的运行时服务；避免把真实便携版卷进用例。
    /// relativeRoot 可指定带空格等特殊字符的相对目录，用于验证命令里的路径加引号逻辑。
    /// </summary>
    private NodeRuntimeService CreateInstalledPortableRuntime(string relativeRoot = "runtimes\\node")
    {
        var runtimesRoot = Path.Combine(_tempRoot, relativeRoot);
        Directory.CreateDirectory(runtimesRoot);
        foreach (var name in new[] { "node.exe", "npm.cmd", "npx.cmd" })
            File.WriteAllText(Path.Combine(runtimesRoot, name), "fake");
        return new NodeRuntimeService(runtimesRoot);
    }

    /// <summary>
    /// 只有便携版可用的命令执行器：裸 node/npm/npx（PATH）一律视为不存在，
    /// 只有便携版的 node.exe / npm.cmd / npx.cmd 才返回版本，用于证明检测确实优先便携版。
    /// </summary>
    private static Func<string, string, string?> PortableOnlyRunner() => (file, _) =>
    {
        var name = Path.GetFileName(file.Trim('"')).ToLowerInvariant();
        return name switch
        {
            "node.exe" => "v24.12.0",
            "npm.cmd" => "11.0.0",
            "npx.cmd" => "11.0.0",
            _ => null,
        };
    };

    /// <summary>已装便携版 Node → Node 项 Ok，详情注明"面板内置便携版"并显示路径与版本。</summary>
    [Fact]
    public void CheckNode_WhenPortableInstalled_ReturnsOkWithPortableNote()
    {
        var nodeRuntime = CreateInstalledPortableRuntime();
        // PATH 里的 node 不可用，只有便携版可用 → 应回退使用便携版
        var service = new EnvironmentCheckService(
            dshHome: _tempRoot,
            profileName: "web",
            port: 3080,
            discovery: CreateInstalledDiscovery(_tempRoot),
            commandRunner: PortableOnlyRunner(),
            executableLocator: _ => @"C:\Program Files\nodejs\node.exe",
            portProbe: _ => false,
            nodeRuntime: nodeRuntime);

        var node = Item(service.CheckAll(), "Node.js");

        Assert.Equal(EnvCheckStatus.Ok, node.Status);
        Assert.Equal("none", node.FixKind);
        Assert.Contains("v24.12.0", node.Detail);
        Assert.Contains("回退使用面板便携版", node.Detail);
        Assert.Contains(Path.Combine(nodeRuntime.RuntimesRoot, "node.exe"), node.Detail);
        // 便携版命中时不应再展示 PATH 定位结果
        Assert.DoesNotContain(@"C:\Program Files\nodejs\node.exe", node.Detail);
    }

    /// <summary>PATH 里没有 npm/npx，但便携版自带 → npm / npx 项 Ok（证明优先用便携版）。</summary>
    [Fact]
    public void CheckNpm_WhenOnlyPortableAvailable_ReturnsOk()
    {
        var nodeRuntime = CreateInstalledPortableRuntime();
        var service = new EnvironmentCheckService(
            dshHome: _tempRoot,
            profileName: "web",
            port: 3080,
            discovery: CreateInstalledDiscovery(_tempRoot),
            commandRunner: PortableOnlyRunner(),
            executableLocator: _ => null,
            portProbe: _ => false,
            nodeRuntime: nodeRuntime);

        var items = service.CheckAll();

        Assert.Equal(EnvCheckStatus.Ok, Item(items, "Node.js").Status);
        var npm = Item(items, "npm / npx");
        Assert.Equal(EnvCheckStatus.Ok, npm.Status);
        Assert.Contains("npm 11.0.0", npm.Detail);
        Assert.Contains("便携版", npm.Hint);
    }

    /// <summary>便携版存在但无法执行（探测无输出）→ 回退 PATH 检测，仍缺失时给出 Missing。</summary>
    [Fact]
    public void CheckNode_WhenPortableNotExecutable_FallsBackToPathAndMissing()
    {
        var nodeRuntime = CreateInstalledPortableRuntime();
        // 所有命令（含便携版路径）都返回 null → 便携版不可用，PATH 也没有
        var service = new EnvironmentCheckService(
            dshHome: _tempRoot,
            profileName: "web",
            port: 3080,
            discovery: CreateInstalledDiscovery(_tempRoot),
            commandRunner: (_, _) => null,
            executableLocator: _ => null,
            portProbe: _ => false,
            nodeRuntime: nodeRuntime);

        var node = Item(service.CheckAll(), "Node.js");

        Assert.Equal(EnvCheckStatus.Missing, node.Status);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, node.FixTarget);
    }

    // ---------------- dsh 安装命令的 npm 动态选择（缺口 2） ----------------

    /// <summary>系统有 npm → "一键安装 dsh"沿用系统环境，命令为裸 npm。</summary>
    [Fact]
    public void CheckDsh_WhenSystemNpmAvailable_UsesBareNpm()
    {
        var dsh = Item(CreateService(dshInstalled: false).CheckAll(), "dsh 本体");

        Assert.Equal("runCommand", dsh.FixKind);
        Assert.Equal("npm install -g @deepseek-ai/dsh", dsh.FixTarget);
        Assert.Equal("一键安装 dsh", dsh.FixLabel);
    }

    /// <summary>
    /// 系统没有 npm 但面板便携版存在 → 命令用便携版 npm.cmd 的**绝对路径**；
    /// 路径带空格时必须加引号，否则 cmd 会把路径截断成多个 token（已实测的坑）。
    /// </summary>
    [Fact]
    public void CheckDsh_WhenNoSystemNpm_WithPortable_UsesQuotedPortableNpmPath()
    {
        var nodeRuntime = CreateInstalledPortableRuntime("run times\\node"); // 目录名故意带空格
        var service = CreateService(
            dshInstalled: false,
            commandRunner: PortableOnlyRunner(), // 裸 npm 不可用，只有便携版 npm.cmd 可用
            nodeRuntime: nodeRuntime);

        var dsh = Item(service.CheckAll(), "dsh 本体");

        var expected = $"\"{Path.Combine(nodeRuntime.RuntimesRoot, "npm.cmd")}\" install -g @deepseek-ai/dsh";
        Assert.Equal("runCommand", dsh.FixKind);
        Assert.Equal(expected, dsh.FixTarget);
        Assert.Equal("一键安装 dsh", dsh.FixLabel);
    }

    /// <summary>
    /// 系统没有 npm 且没有便携版 → 修复动作改为"先装便携版 Node"，且 Hint 说明需要先安装 Node（含 npm）。
    /// </summary>
    [Fact]
    public void CheckDsh_WhenNoNpmAndNoPortable_AsksToInstallNodeFirst()
    {
        // nodeRuntime 默认指向临时空目录 → 无便携版
        var service = CreateService(
            nodeVersion: null, npmVersion: null, npxVersion: null,
            dshInstalled: false);

        var dsh = Item(service.CheckAll(), "dsh 本体");

        Assert.Equal(EnvCheckStatus.Missing, dsh.Status);
        Assert.Equal("runCommand", dsh.FixKind);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, dsh.FixTarget);
        Assert.Equal("一键安装 Node（便携版，含 npm/npx）", dsh.FixLabel);
        Assert.Contains("先安装 Node", dsh.Hint);
    }

    // ---------------- 依赖修复命令里的 node 选择 ----------------

    /// <summary>系统有 node → 依赖修复命令用裸 node。</summary>
    [Fact]
    public void CheckDependencies_WhenSystemNodeAvailable_UsesBareNode()
    {
        CreateSharedPackages("dsh-llm");
        var binJs = CreateBinJs();

        var item = Item(CreateService().CheckAll(), "dsh 依赖完整性");

        Assert.Contains($"&& node \"{binJs}\"", item.FixTarget);
        Assert.DoesNotContain("node.exe", item.FixTarget);
    }

    /// <summary>系统没有 node 但便携版存在 → 依赖修复命令用便携版 node.exe 绝对路径（带空格加引号）。</summary>
    [Fact]
    public void CheckDependencies_WhenNoSystemNode_UsesPortableNodeAbsolutePath()
    {
        CreateSharedPackages("dsh-llm");
        var binJs = CreateBinJs();
        var nodeRuntime = CreateInstalledPortableRuntime("run times\\node"); // 目录名故意带空格

        var service = CreateService(commandRunner: PortableOnlyRunner(), nodeRuntime: nodeRuntime);

        var item = Item(service.CheckAll(), "dsh 依赖完整性");

        var portableNode = Path.Combine(nodeRuntime.RuntimesRoot, "node.exe");
        Assert.Contains($"&& \"{portableNode}\" \"{binJs}\"", item.FixTarget);
    }

    // ---------------- 「一键安装全部缺失项」编排（纯计算） ----------------

    /// <summary>构造一个命令型修复项（仅用于编排逻辑测试，不会执行任何命令）。</summary>
    private static EnvCheckItem FixItem(string name, string target, string label = "修复")
        => new()
        {
            Name = name,
            Status = EnvCheckStatus.Missing,
            FixKind = "runCommand",
            FixTarget = target,
            FixLabel = label,
        };

    /// <summary>执行计划严格按依赖顺序：Node → dsh 本体 → dsh 依赖。</summary>
    [Fact]
    public void BuildInstallPlan_OrdersNodeThenDshThenDependencies()
    {
        var plan = EnvironmentCheckService.BuildInstallPlan(new[]
        {
            FixItem(EnvironmentCheckService.DshDepsItemName, "cmd-deps"),
            FixItem(EnvironmentCheckService.DshItemName, "npm install -g @deepseek-ai/dsh"),
            FixItem(EnvironmentCheckService.NodeItemName, EnvironmentCheckService.InstallPortableNodeMarker),
        });

        Assert.Equal(
            new[] { EnvironmentCheckService.NodeItemName, EnvironmentCheckService.DshItemName, EnvironmentCheckService.DshDepsItemName },
            plan.Select(s => s.ItemName));
    }

    /// <summary>Node 与 npm/npx 缺一都只登记一步便携版安装（避免同一份约 35 MB 的 zip 被下载两次）。</summary>
    [Fact]
    public void BuildInstallPlan_MergesNodeAndNpmIntoSingleStep()
    {
        var plan = EnvironmentCheckService.BuildInstallPlan(new[]
        {
            FixItem(EnvironmentCheckService.NodeItemName, EnvironmentCheckService.InstallPortableNodeMarker, "一键安装 Node（便携版）"),
            FixItem(EnvironmentCheckService.NpmItemName, EnvironmentCheckService.InstallPortableNodeMarker, "一键安装 Node（便携版，含 npm/npx）"),
        });

        var step = Assert.Single(plan);
        Assert.Equal(EnvironmentCheckService.NodeItemName, step.ItemName);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, step.FixTarget);
        Assert.Contains("安装 Node", step.Label);
    }

    /// <summary>系统无 npm 时 dsh 的修复动作是装便携版 Node → 计划先补一步安装 Node，再装 dsh。</summary>
    [Fact]
    public void BuildInstallPlan_WhenDshNeedsPortableNode_AddsNodeStepFirst()
    {
        var plan = EnvironmentCheckService.BuildInstallPlan(new[]
        {
            FixItem(EnvironmentCheckService.DshItemName, EnvironmentCheckService.InstallPortableNodeMarker, "一键安装 Node（便携版，含 npm/npx）"),
        });

        Assert.Equal(2, plan.Count);
        Assert.Equal(EnvironmentCheckService.NodeItemName, plan[0].ItemName);
        Assert.Equal(EnvironmentCheckService.InstallPortableNodeMarker, plan[0].FixTarget);
        Assert.Equal(EnvironmentCheckService.DshItemName, plan[1].ItemName);
    }

    /// <summary>没有可执行修复项（全 Ok 或只有 openUrl）时不产生计划。</summary>
    [Fact]
    public void BuildInstallPlan_WhenNothingFixable_IsEmpty()
    {
        var ok = new EnvCheckItem { Name = EnvironmentCheckService.NodeItemName, Status = EnvCheckStatus.Ok, FixKind = "none" };
        var urlFix = new EnvCheckItem
        {
            Name = EnvironmentCheckService.NpmItemName,
            Status = EnvCheckStatus.Warning,
            FixKind = "openUrl",
            FixTarget = "https://nodejs.org",
            FixLabel = "打开下载页",
        };

        Assert.Empty(EnvironmentCheckService.BuildInstallPlan(new[] { ok, urlFix }));
    }

    /// <summary>「一键安装全部缺失项」可用性：需要 1 个以上命令型修复项（0 或 1 项都不可用）。</summary>
    [Fact]
    public void HasMultipleExecutableFixes_RequiresTwoOrMoreRunCommandItems()
    {
        Assert.False(EnvironmentCheckService.HasMultipleExecutableFixes(Array.Empty<EnvCheckItem>()));
        Assert.False(EnvironmentCheckService.HasMultipleExecutableFixes(new[] { FixItem("dsh 本体", "cmd") }));
        Assert.True(EnvironmentCheckService.HasMultipleExecutableFixes(new[]
        {
            FixItem(EnvironmentCheckService.DshItemName, "cmd-a"),
            FixItem(EnvironmentCheckService.DshDepsItemName, "cmd-b"),
        }));
    }

    /// <summary>单步失败后的跳过策略：Node/npm 失败跳过 dsh 与依赖；dsh 本体失败只跳过依赖。</summary>
    [Fact]
    public void GetStagesToSkipAfterFailure_SkipsDependentStages()
    {
        var afterNode = EnvironmentCheckService.GetStagesToSkipAfterFailure(EnvironmentCheckService.NodeItemName);
        Assert.Contains(EnvironmentCheckService.DshItemName, afterNode);
        Assert.Contains(EnvironmentCheckService.DshDepsItemName, afterNode);

        var afterNpm = EnvironmentCheckService.GetStagesToSkipAfterFailure(EnvironmentCheckService.NpmItemName);
        Assert.Contains(EnvironmentCheckService.DshItemName, afterNpm);

        var afterDsh = EnvironmentCheckService.GetStagesToSkipAfterFailure(EnvironmentCheckService.DshItemName);
        Assert.Equal(new[] { EnvironmentCheckService.DshDepsItemName }, afterDsh);

        // 依赖修复是最后一步，失败后没有可跳过的步骤
        Assert.Empty(EnvironmentCheckService.GetStagesToSkipAfterFailure(EnvironmentCheckService.DshDepsItemName));
    }

    // ---------------- 官网链接与「临时方案」提醒 ----------------

    /// <summary>系统 Node 就绪 → Ok 项带 Node 官网链接（文案与地址均正确）。</summary>
    [Fact]
    public void CheckNode_WhenPresent_HasNodeOfficialLink()
    {
        var node = Item(CreateService().CheckAll(), "Node.js");

        Assert.Equal("Node 官网", node.LinkLabel);
        Assert.Equal(EnvironmentCheckService.NodeDownloadUrl, node.LinkUrl);
        Assert.Equal("https://nodejs.org/zh-cn/download", node.LinkUrl);
    }

    /// <summary>Node 缺失 → 同样给 Node 官网链接，Hint 提示便携版属快速方案、长期建议装正式版。</summary>
    [Fact]
    public void CheckNode_WhenMissing_HasLinkAndFastTrackHint()
    {
        var node = Item(CreateService(nodeVersion: null).CheckAll(), "Node.js");

        Assert.Equal(EnvironmentCheckService.NodeDownloadUrl, node.LinkUrl);
        Assert.Contains("快速方案", node.Hint);
        Assert.Contains("Node 官网", node.Hint);
    }

    /// <summary>
    /// 回退使用面板便携版时 → Hint 明确说明"便携版属快速方案、长期建议到 Node 官网装正式版"，
    /// 且链接非空（供用户点开官网自行安装）。
    /// </summary>
    [Fact]
    public void CheckNode_WhenPortable_HintRemindsPortableAndOfficialSite()
    {
        var service = new EnvironmentCheckService(
            dshHome: _tempRoot,
            profileName: "web",
            port: 3080,
            discovery: CreateInstalledDiscovery(_tempRoot),
            commandRunner: PortableOnlyRunner(),
            executableLocator: _ => @"C:\Program Files\nodejs\node.exe",
            portProbe: _ => false,
            nodeRuntime: CreateInstalledPortableRuntime());

        var node = Item(service.CheckAll(), "Node.js");

        Assert.Equal(EnvCheckStatus.Ok, node.Status);
        // 提醒内容：便携版 + 官网（正式版）
        Assert.Contains("便携版", node.Hint);
        Assert.Contains("快速方案", node.Hint);
        Assert.Contains("官网", node.Hint);
        // 链接非空且指向 Node 官网
        Assert.False(string.IsNullOrWhiteSpace(node.LinkUrl));
        Assert.Equal(EnvironmentCheckService.NodeDownloadUrl, node.LinkUrl);
    }

    /// <summary>系统 Node 就绪时 Hint 保持"已就绪"说明，不追加临时方案提醒。</summary>
    [Fact]
    public void CheckNode_WhenSystemReady_HintHasNoFastTrackReminder()
    {
        var node = Item(CreateService().CheckAll(), "Node.js");

        Assert.Contains("已就绪", node.Hint);
        Assert.DoesNotContain("快速方案", node.Hint);
    }

    /// <summary>dsh 未安装 → 链接指向官方仓库，Hint 含 npm 全局安装与"快速方式"提醒。</summary>
    [Fact]
    public void CheckDsh_WhenNotInstalled_HasOfficialRepoLinkAndFastTrackHint()
    {
        var dsh = Item(CreateService(dshInstalled: false).CheckAll(), "dsh 本体");

        Assert.Equal("dsh 官方仓库", dsh.LinkLabel);
        Assert.Equal("https://github.com/deepseek-ai/deepseek-harness", dsh.LinkUrl);
        Assert.Equal(EnvironmentCheckService.DshOfficialRepoUrl, dsh.LinkUrl);
        // 提醒说明面板用 npm 全局安装 dsh 属快速方式
        Assert.Contains("npm install -g", dsh.Hint);
        Assert.Contains("快速方式", dsh.Hint);
    }

    /// <summary>dsh 已安装 → 同样带官方仓库链接（供长期使用 / 自定义部署参考）。</summary>
    [Fact]
    public void CheckDsh_WhenInstalled_HasOfficialRepoLink()
    {
        var dsh = Item(CreateService().CheckAll(), "dsh 本体");

        Assert.Equal(EnvCheckStatus.Ok, dsh.Status);
        Assert.Equal(EnvironmentCheckService.DshOfficialRepoUrl, dsh.LinkUrl);
        Assert.False(string.IsNullOrWhiteSpace(dsh.LinkLabel));
    }

    /// <summary>依赖 / 端口 / 配置项不提供外部链接（LinkUrl 为空，界面自动不显示链接按钮）。</summary>
    [Fact]
    public void CheckOtherItems_HaveNoLink()
    {
        var items = CreateService(portInUse: true).CheckAll();

        Assert.Equal(string.Empty, Item(items, "dsh 依赖完整性").LinkUrl);
        Assert.Equal(string.Empty, Item(items, "端口可用性").LinkUrl);
        Assert.Equal(string.Empty, Item(items, "配置文件").LinkUrl);
    }

    /// <summary>链接校验：只放行 http/https 绝对地址，空串 / 相对地址 / 其它协议一律拒绝。</summary>
    [Fact]
    public void IsSafeExternalUrl_OnlyAllowsHttpAndHttps()
    {
        // 放行：http / https 绝对地址
        Assert.True(EnvironmentCheckService.IsSafeExternalUrl(EnvironmentCheckService.NodeDownloadUrl));
        Assert.True(EnvironmentCheckService.IsSafeExternalUrl(EnvironmentCheckService.DshOfficialRepoUrl));
        Assert.True(EnvironmentCheckService.IsSafeExternalUrl("http://example.com/a"));

        // 拒绝：空串 / 空白 / 相对地址
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl(null));
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl(string.Empty));
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl("   "));
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl("nodejs.org"));

        // 拒绝：可能带来风险的其它协议（交给 shell 打开 file:// / javascript: 并不安全）
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl("ftp://example.com"));
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl("javascript:alert(1)"));
        Assert.False(EnvironmentCheckService.IsSafeExternalUrl("file:///C:/Windows/System32/calc.exe"));
    }
}
