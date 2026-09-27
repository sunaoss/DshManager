using System.Diagnostics;
using System.IO;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// DshDiscoveryService 单元测试。
/// 通过注入"候选位置提供者 / 路径存在性检查 / 脚本内容读取 / 版本检测器"模拟环境，
/// 不依赖真实文件系统、PATH 与 npm。
/// </summary>
public class DshDiscoveryServiceTests
{
    /// <summary>候选位置存在时判定已安装，且 LauncherPath 指向第一个存在的启动器候选。</summary>
    [Fact]
    public void Scan_WhenCandidateExists_MarksInstalledAndSetsLauncherPath()
    {
        var fakeLauncher = @"C:\fake\bin\dsh.cmd";
        var fakeDataDir = @"C:\fake\.dsh";
        var existing = new HashSet<string> { fakeLauncher, fakeDataDir };

        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { fakeLauncher, fakeDataDir },
            dataDir: fakeDataDir,
            pathExists: existing.Contains,
            npxResolver: () => null); // 注入空 npx，避免触碰真实 PATH / npm

        var info = service.Scan();

        Assert.True(info.IsInstalled);
        Assert.Equal(fakeLauncher, info.LauncherPath);
        Assert.Equal(fakeDataDir, info.DataDir);
        // SourcesFound 应包含所有找到的路径（供"文件查看"页复用）
        Assert.Equal(2, info.SourcesFound.Count);
        Assert.Contains(fakeLauncher, info.SourcesFound);
        Assert.Contains(fakeDataDir, info.SourcesFound);
    }

    /// <summary>候选位置全部不存在时返回 IsInstalled=false，且不抛异常。</summary>
    [Fact]
    public void Scan_WhenNoCandidateExists_ReturnsNotInstalled()
    {
        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { @"C:\missing\dsh.exe", @"C:\missing\.dsh" },
            dataDir: @"C:\missing\.dsh",
            pathExists: _ => false,
            npxResolver: () => null);

        var info = service.Scan();

        Assert.False(info.IsInstalled);
        Assert.Empty(info.SourcesFound);
        Assert.Equal(string.Empty, info.LauncherPath);
    }

    /// <summary>WorkDir 能从 start-dsh-web.ps1 内容中的 $workdir 变量正确解析。</summary>
    [Fact]
    public void Scan_ParsesWorkDirFromStartScript()
    {
        // 模拟一小段真实的 start-dsh-web.ps1 内容
        const string fakeScript = """
            $ErrorActionPreference = 'Stop'
            $workdir = "D:\al1969"
            if (-not (Test-Path $workdir)) { exit 1 }
            & "$HOME\.ai-manager\bin\dsh" web --cwd $workdir
            """;

        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { @"C:\fake\.dsh" },
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == @"C:\fake\.dsh",
            fileContentReader: p => p.EndsWith("start-dsh-web.ps1", StringComparison.OrdinalIgnoreCase) ? fakeScript : null,
            npxResolver: () => null);

        var info = service.Scan();

        Assert.Equal(@"D:\al1969", info.WorkDir);
    }

    /// <summary>start-dsh-web.ps1 不存在或无法读取时，WorkDir 为空且不抛异常。</summary>
    [Fact]
    public void Scan_WhenStartScriptMissing_WorkDirEmptyAndNoException()
    {
        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { @"C:\fake\bin\dsh.cmd" },
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == @"C:\fake\bin\dsh.cmd",
            fileContentReader: _ => null,
            npxResolver: () => null);

        var info = service.Scan();

        Assert.Equal(string.Empty, info.WorkDir);
        Assert.True(info.IsInstalled);
    }

    /// <summary>候选为空、文件全部不存在、脚本不可读、版本检测失败时，Scan 全程不抛异常。</summary>
    [Fact]
    public void Scan_WhenEverythingMissing_DoesNotThrow()
    {
        var service = new DshDiscoveryService(
            candidateProvider: () => Array.Empty<string>(),
            dataDir: @"C:\missing\.dsh",
            pathExists: _ => false,
            fileContentReader: _ => null,
            versionDetector: _ => null,
            npxResolver: () => null);

        var info = service.Scan(); // 不应抛异常

        Assert.False(info.IsInstalled);
        Assert.Equal(string.Empty, info.WorkDir);
        Assert.Equal("unknown", info.Version);
    }

    /// <summary>注入版本检测器时，Version 字段带出检测结果。</summary>
    [Fact]
    public void Scan_UsesInjectedVersionDetector()
    {
        var fakeLauncher = @"C:\fake\bin\dsh.cmd";
        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { fakeLauncher },
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == fakeLauncher,
            versionDetector: _ => "0.1.5-rc.3");

        var info = service.Scan();

        Assert.Equal("0.1.5-rc.3", info.Version);
    }

    // ---------------- 启动器可用性校验与 npx 回退 ----------------

    /// <summary>① 启动器存在但 --version 返回报错文本：该候选作废，Version 不能是报错文本。</summary>
    [Fact]
    public void Scan_WhenLauncherVersionOutputIsErrorText_InvalidatesCandidate()
    {
        var fakeLauncher = @"C:\fake\bin\dsh.cmd";
        const string errorText = "'dsh' 不是内部或外部命令，也不是可运行的程序";

        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { fakeLauncher },
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == fakeLauncher,
            fileContentReader: _ => null,
            versionDetector: _ => errorText,   // 模拟启动器报错
            npxResolver: () => null);          // 也没有 npx

        var info = service.Scan();

        // 候选文件仍被记为启动器（展示用），但不可用于启动
        Assert.Equal(fakeLauncher, info.LauncherPath);
        Assert.Equal(string.Empty, info.LaunchCommand);
        Assert.Equal("unknown", info.Version);
        Assert.DoesNotContain("不是内部或外部命令", info.Version);
    }

    /// <summary>② 有可用启动器（--version 能解析出版本）时，LaunchCommand 等于该启动器路径。</summary>
    [Fact]
    public void Scan_WhenLauncherUsable_SetsLaunchCommandToLauncherPath()
    {
        var fakeLauncher = @"C:\fake\bin\dsh.cmd";

        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { fakeLauncher },
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == fakeLauncher,
            versionDetector: _ => "0.1.5-rc.3",
            npxResolver: () => @"C:\Program Files\nodejs\npx.cmd"); // 有 npx 也不该被使用

        var info = service.Scan();

        Assert.Equal(fakeLauncher, info.LaunchCommand);
        Assert.Equal("0.1.5-rc.3", info.Version);
    }

    /// <summary>③ 无可用启动器但存在 npx：回退为 npx 启动，含空格路径带双引号，包规格用默认值。</summary>
    [Fact]
    public void Scan_WhenNoUsableLauncherButNpxExists_FallsBackToNpx()
    {
        var fakeLauncher = @"C:\fake\bin\dsh.cmd";
        const string fakeNpx = @"C:\Program Files\nodejs\npx.cmd";

        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { fakeLauncher },
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == fakeLauncher,
            fileContentReader: _ => null,           // 无 start-dsh-web.ps1 → 用默认包规格
            versionDetector: _ => "命令无法识别",     // 启动器与 npx 都拿不到版本
            npxResolver: () => fakeNpx);

        var info = service.Scan();

        Assert.Equal("\"C:\\Program Files\\nodejs\\npx.cmd\" --yes @deepseek-ai/dsh", info.LaunchCommand);
        Assert.Equal("unknown", info.Version);
    }

    /// <summary>③' npx 回退时包规格优先取 start-dsh-web.ps1 的 $pkg，并从中带出版本号。</summary>
    [Fact]
    public void Scan_WhenNpxFallback_PrefersPackageSpecFromStartScript()
    {
        const string fakeScript = """
            $pkg = '@deepseek-ai/dsh@0.1.5-rc.3'
            npx --yes $pkg web --port 3080
            """;

        var service = new DshDiscoveryService(
            candidateProvider: () => new[] { @"C:\fake\.dsh" }, // 只有数据目录，无启动器
            dataDir: @"C:\fake\.dsh",
            pathExists: p => p == @"C:\fake\.dsh",
            fileContentReader: _ => fakeScript,
            versionDetector: _ => null,
            npxResolver: () => @"C:\fake\npx.cmd"); // 无空格 → 不加引号

        var info = service.Scan();

        Assert.Equal(@"C:\fake\npx.cmd --yes @deepseek-ai/dsh@0.1.5-rc.3", info.LaunchCommand);
        Assert.Equal("0.1.5-rc.3", info.Version);
    }

    /// <summary>④ 版本号正则校验：x.y.z（含 -rc.3 后缀）可识别，报错文本一律判为无效。</summary>
    [Theory]
    [InlineData("0.1.5-rc.3", "0.1.5-rc.3")]
    [InlineData("dsh 1.2.3", "1.2.3")]
    [InlineData("@deepseek-ai/dsh@0.1.5-rc.3", "0.1.5-rc.3")]
    [InlineData("'dsh' 不是内部或外部命令", null)]
    [InlineData("command not found", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ValidateVersion_ExtractsVersionOrNull(string? output, string? expected)
    {
        Assert.Equal(expected, DshDiscoveryService.ValidateVersion(output));
    }

    /// <summary>④' $pkg 包规格解析：能取到值，缺失时返回空字符串。</summary>
    [Fact]
    public void ParsePackageSpec_ReadsPkgVariableOrEmpty()
    {
        Assert.Equal("@deepseek-ai/dsh@0.1.5-rc.3",
            DshDiscoveryService.ParsePackageSpec("$pkg = '@deepseek-ai/dsh@0.1.5-rc.3'"));
        Assert.Equal(string.Empty, DshDiscoveryService.ParsePackageSpec(null));
        Assert.Equal(string.Empty, DshDiscoveryService.ParsePackageSpec("$workdir = 'D:\\x'"));
    }

    /// <summary>默认数据目录指向 %USERPROFILE%\.dsh（Windows 上 USERPROFILE 必然存在）。</summary>
    [Fact]
    public void GetDefaultDshHome_ReturnsNonEmptyPath()
    {
        var home = DshDiscoveryService.GetDefaultDshHome();

        Assert.False(string.IsNullOrWhiteSpace(home));
        Assert.EndsWith(".dsh", home, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== 多来源安装位置探测（临时目录驱动，不触碰真实环境） ====================

    /// <summary>npx 缓存：每个 hash 子目录下的 @deepseek-ai\dsh 都能被搜到，且版本 / bin.js / Web 配套 / 可用性正确。</summary>
    [Fact]
    public void DetectInstallations_FindsNpxCachePackage()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "_npx");
        var packageDir = workspace.CreatePackage(
            Path.Combine(npxRoot, "1e7f6d9597241db0", "node_modules"), "0.1.5-rc.3", withWebApp: true);

        var installs = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }).DetectInstallations();

        var install = Assert.Single(installs);
        Assert.Equal(DshInstallSource.NpxCache, install.Source);
        Assert.Equal("npx 缓存", install.SourceText);
        Assert.Equal("0.1.5-rc.3", install.Version);
        Assert.Equal(packageDir, install.PackageDir);
        Assert.Equal(Path.Combine(packageDir, "lib", "bin.js"), install.BinJsPath);
        Assert.True(install.HasWebApp);
        Assert.True(install.IsUsable);
        Assert.Equal(300, install.Rank);
    }

    /// <summary>npx 缓存多个 hash 子目录（多版本）都能被搜到。</summary>
    [Fact]
    public void DetectInstallations_NpxCacheEnumeratesEachHashDirectory()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "_npx");
        workspace.CreatePackage(Path.Combine(npxRoot, "hashA", "node_modules"), "0.1.5-rc.3");
        workspace.CreatePackage(Path.Combine(npxRoot, "hashB", "node_modules"), "0.1.6");

        var installs = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }).DetectInstallations();

        Assert.Equal(2, installs.Count);
        Assert.Equal("0.1.6", installs[0].Version); // 版本更高者在前
    }

    /// <summary>npm 全局 / dsh profiles / ai-manager / PATH 启动器四类来源都能被搜到。</summary>
    [Fact]
    public void DetectInstallations_FindsAllOtherSources()
    {
        using var workspace = new TempWorkspace();
        var npmNodeModules = Path.Combine(workspace.Root, "npm", "node_modules");
        var profileNodeModules = Path.Combine(workspace.Root, ".dsh", "profiles", "node_modules");
        var profileWebNodeModules = Path.Combine(workspace.Root, ".dsh", "profiles", "web", "node_modules");
        var aiNodeModules = Path.Combine(workspace.Root, ".ai-manager", "npm-global", "node-v24", "node_modules");
        var aiBinDir = Path.Combine(workspace.Root, ".ai-manager", "bin");
        var pathDir = Path.Combine(workspace.Root, "pathdir");

        workspace.CreatePackage(npmNodeModules, "1.0.0");
        workspace.CreatePackage(profileNodeModules, "1.0.0");
        workspace.CreatePackage(profileWebNodeModules, "1.0.0");
        workspace.CreatePackage(aiNodeModules, "1.0.0");
        var aiShim = workspace.CreateLauncher(aiBinDir);
        var pathShim = workspace.CreateLauncher(pathDir);

        var installs = CreateService(new DshSearchRoots
        {
            NpmGlobalNodeModules = new[] { npmNodeModules },
            DshProfilesNodeModules = new[] { profileNodeModules, profileWebNodeModules },
            AiManagerNodeModules = new[] { aiNodeModules },
            AiManagerBinDirs = new[] { aiBinDir },
            PathDirs = new[] { pathDir },
        }).DetectInstallations();

        Assert.Contains(installs, i => i.Source == DshInstallSource.NpmGlobal);
        Assert.Equal(2, installs.Count(i => i.Source == DshInstallSource.DshProfiles));
        Assert.Contains(installs, i => i.Source == DshInstallSource.AiManager && i.PackageDir == aiShim);
        Assert.Contains(installs, i => i.Source == DshInstallSource.PathLauncher && i.PackageDir == pathShim);
    }

    /// <summary>脚本来源：$workdir 指向的工作目录下存在包时会作为 WorkDir 来源被搜到。</summary>
    [Fact]
    public void DetectInstallations_FindsWorkDirFromStartScript()
    {
        using var workspace = new TempWorkspace();
        var workDir = Path.Combine(workspace.Root, "work");
        var dataDir = Path.Combine(workspace.Root, ".dsh");
        Directory.CreateDirectory(dataDir);
        workspace.CreatePackage(Path.Combine(workDir, "node_modules"), "0.2.0");
        File.WriteAllText(Path.Combine(dataDir, "start-dsh-web.ps1"), $"$workdir = '{workDir}'");

        var installs = CreateService(new DshSearchRoots(), dataDir).DetectInstallations();

        var install = Assert.Single(installs);
        Assert.Equal(DshInstallSource.WorkDir, install.Source);
        Assert.Equal("0.2.0", install.Version);
        Assert.Equal(100, install.Rank);
    }

    /// <summary>package.json 损坏时版本降级为 unknown，并由 $pkg 里的钉住版本兜底；全程不抛异常。</summary>
    [Fact]
    public void DetectInstallations_WorkDirVersionFallsBackToPkgSpec()
    {
        using var workspace = new TempWorkspace();
        var workDir = Path.Combine(workspace.Root, "work");
        var dataDir = Path.Combine(workspace.Root, ".dsh");
        Directory.CreateDirectory(dataDir);

        var packageDir = workspace.CreatePackage(Path.Combine(workDir, "node_modules"), "0.1.5-rc.3");
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{ 这不是合法 JSON ");
        File.WriteAllText(Path.Combine(dataDir, "start-dsh-web.ps1"),
            $"$pkg = '@deepseek-ai/dsh@0.3.1'\n$workdir = '{workDir}'");

        var installs = CreateService(new DshSearchRoots(), dataDir).DetectInstallations();

        var install = Assert.Single(installs);
        Assert.Equal("0.3.1", install.Version);
    }

    /// <summary>手动指定包目录：即使是低版本也永远排第一（Rank=1000）。</summary>
    [Fact]
    public void DetectInstallations_ManualOverridePackageDir_RanksFirst()
    {
        using var workspace = new TempWorkspace();
        var npmNodeModules = Path.Combine(workspace.Root, "npm", "node_modules");
        var manualPackageDir = workspace.CreatePackage(
            Path.Combine(workspace.Root, "manual", "node_modules"), "0.1.0");
        workspace.CreatePackage(npmNodeModules, "9.9.9"); // npm 全局版本更高

        var installs = CreateService(new DshSearchRoots
        {
            InstallPathOverride = manualPackageDir,
            NpmGlobalNodeModules = new[] { npmNodeModules },
        }).DetectInstallations();

        Assert.Equal(DshInstallSource.Manual, installs[0].Source);
        Assert.Equal(manualPackageDir, installs[0].PackageDir);
        Assert.Equal("0.1.0", installs[0].Version);
        Assert.Equal(1000, installs[0].Rank);
    }

    /// <summary>手动指定启动器文件：按约定把 shim 路径填入 PackageDir，并记为可用。</summary>
    [Fact]
    public void DetectInstallations_ManualOverrideLauncherFile_RecordedAsShim()
    {
        using var workspace = new TempWorkspace();
        var shim = workspace.CreateLauncher(Path.Combine(workspace.Root, "manualbin"));

        var installs = CreateService(new DshSearchRoots { InstallPathOverride = shim }).DetectInstallations();

        var install = Assert.Single(installs);
        Assert.Equal(DshInstallSource.Manual, install.Source);
        Assert.Equal(shim, install.PackageDir);
        Assert.Equal(shim, install.LauncherPath);
        Assert.Null(install.BinJsPath);
        Assert.True(install.IsUsable);
    }

    /// <summary>排序规则①：版本更高者优先，即使来源优先级更低（"更新过的 npm 全局"压过旧缓存的反面）。</summary>
    [Fact]
    public void DetectInstallations_HigherVersionWinsRegardlessOfSourceRank()
    {
        using var workspace = new TempWorkspace();
        var npmNodeModules = Path.Combine(workspace.Root, "npm", "node_modules");
        var npxRoot = Path.Combine(workspace.Root, "_npx");
        workspace.CreatePackage(npmNodeModules, "1.0.0");                                  // 来源优先级高
        workspace.CreatePackage(Path.Combine(npxRoot, "hash", "node_modules"), "2.0.0");   // 版本更高

        var installs = CreateService(new DshSearchRoots
        {
            NpmGlobalNodeModules = new[] { npmNodeModules },
            NpxCacheRoots = new[] { npxRoot },
        }).DetectInstallations();

        Assert.Equal(2, installs.Count);
        Assert.Equal(DshInstallSource.NpxCache, installs[0].Source);
        Assert.Equal(DshInstallSource.NpmGlobal, installs[1].Source);
    }

    /// <summary>排序规则②：主次版本相同时按来源优先级（npm 全局 &gt; dsh profiles &gt; npx 缓存 &gt; ai-manager &gt; PATH）。</summary>
    [Fact]
    public void DetectInstallations_SameVersionOrderedBySourcePriority()
    {
        using var workspace = new TempWorkspace();
        var npmNodeModules = Path.Combine(workspace.Root, "npm", "node_modules");
        var profileNodeModules = Path.Combine(workspace.Root, ".dsh", "profiles", "node_modules");
        var npxRoot = Path.Combine(workspace.Root, "_npx");
        var aiNodeModules = Path.Combine(workspace.Root, ".ai-manager", "npm-global", "node-v24", "node_modules");
        var pathDir = Path.Combine(workspace.Root, "pathdir");

        workspace.CreatePackage(npmNodeModules, "1.0.0");
        workspace.CreatePackage(profileNodeModules, "1.0.0");
        workspace.CreatePackage(Path.Combine(npxRoot, "hash1", "node_modules"), "1.0.0");
        workspace.CreatePackage(aiNodeModules, "1.0.0");
        workspace.CreateLauncher(pathDir); // 只有启动器，版本 unknown

        var installs = CreateService(new DshSearchRoots
        {
            NpmGlobalNodeModules = new[] { npmNodeModules },
            DshProfilesNodeModules = new[] { profileNodeModules },
            NpxCacheRoots = new[] { npxRoot },
            AiManagerNodeModules = new[] { aiNodeModules },
            PathDirs = new[] { pathDir },
        }).DetectInstallations();

        Assert.Equal(new[]
        {
            DshInstallSource.NpmGlobal,
            DshInstallSource.DshProfiles,
            DshInstallSource.NpxCache,
            DshInstallSource.AiManager,
            DshInstallSource.PathLauncher, // 版本 unknown 视为 0.0，排在所有 1.0.0 之后
        }, installs.Select(i => i.Source));
    }

    /// <summary>SelectBest：跳过不可用项；全不可用或输入为空 / null 时返回 null。</summary>
    [Fact]
    public void SelectBest_SkipsUnusableAndReturnsNullWhenNoneUsable()
    {
        var service = CreateService(new DshSearchRoots());
        var unusable = new DshInstallation
        {
            PackageDir = "unusable", Source = DshInstallSource.NpxCache, Rank = 300,
            IsUsable = false, Version = "9.0.0",
        };
        var usable = new DshInstallation
        {
            PackageDir = "usable", Source = DshInstallSource.NpmGlobal, Rank = 500,
            IsUsable = true, Version = "1.0.0",
        };

        Assert.Equal(usable, service.SelectBest(new[] { unusable, usable }));
        Assert.Null(service.SelectBest(new[] { unusable }));
        Assert.Null(service.SelectBest(Array.Empty<DshInstallation>()));
        Assert.Null(service.SelectBest(null));
    }

    /// <summary>Scan：推荐位置可用且版本已知时，LaunchCommand 用 node + bin.js，Version 直接取推荐位置版本。</summary>
    [Fact]
    public void Scan_LaunchCommandPrefersRecommendedInstallation()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "_npx");
        var packageDir = workspace.CreatePackage(Path.Combine(npxRoot, "hash", "node_modules"), "0.1.5-rc.3");
        var binJs = Path.Combine(packageDir, "lib", "bin.js");

        var info = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }).Scan();

        Assert.True(info.IsInstalled);
        Assert.NotNull(info.RecommendedInstallation);
        Assert.Equal("0.1.5-rc.3", info.Version);
        var expected = binJs.Contains(' ') ? $"node \"{binJs}\"" : $"node {binJs}";
        Assert.Equal(expected, info.LaunchCommand);
    }

    /// <summary>Scan：仅 npx 缓存可用、没有任何候选启动器时，IsInstalled 仍应判为已安装。</summary>
    [Fact]
    public void Scan_WhenOnlyRecommendedInstallationExists_MarksInstalled()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "_npx");
        workspace.CreatePackage(Path.Combine(npxRoot, "hash", "node_modules"), "0.1.5-rc.3");

        var info = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }).Scan();

        Assert.Empty(info.SourcesFound);
        Assert.True(info.IsInstalled);
    }

    /// <summary>
    /// 目录符号链接检测：profiles 下的包目录是指向 npx 缓存的 Junction 时，
    /// 该条安装位置应标记 IsLink / LinkTarget，并给出「快捷方式 → 目标」的 LinkText。
    /// </summary>
    [Fact]
    public void DetectInstallations_WhenPackageDirIsJunction_MarksLinkAndTarget()
    {
        using var workspace = new TempWorkspace();
        // 真实场景：npx 缓存里放真正的包，dsh profiles 下建 Junction 指过去
        var npxPackageDir = workspace.CreatePackage(
            Path.Combine(workspace.Root, "_npx", "99cabb0ceac85b86", "node_modules"), "0.1.5-rc.3");
        var profilesNodeModules = Path.Combine(workspace.Root, ".dsh", "profiles", "node_modules");
        var linkPackageDir = Path.Combine(profilesNodeModules, "@deepseek-ai", "dsh");
        workspace.CreateJunction(linkPackageDir, npxPackageDir);

        var installs = CreateService(new DshSearchRoots
        {
            DshProfilesNodeModules = new[] { profilesNodeModules },
        }).DetectInstallations();

        var install = Assert.Single(installs);
        Assert.Equal(DshInstallSource.DshProfiles, install.Source);
        Assert.True(install.IsLink);
        Assert.Equal(Path.GetFullPath(npxPackageDir), Path.GetFullPath(install.LinkTarget!), ignoreCase: true);
        Assert.Contains("快捷方式 →", install.LinkText);
    }

    /// <summary>普通目录（非链接）的安装位置不应被标记，LinkText 为空字符串（界面据此隐藏标注行）。</summary>
    [Fact]
    public void DetectInstallations_WhenPackageDirIsNormalDirectory_LinkTextEmpty()
    {
        using var workspace = new TempWorkspace();
        var nodeModules = Path.Combine(workspace.Root, "npm", "node_modules");
        workspace.CreatePackage(nodeModules, "1.0.0");

        var installs = CreateService(new DshSearchRoots
        {
            NpmGlobalNodeModules = new[] { nodeModules },
        }).DetectInstallations();

        var install = Assert.Single(installs);
        Assert.False(install.IsLink);
        Assert.Null(install.LinkTarget);
        Assert.Equal(string.Empty, install.LinkText);
    }

    /// <summary>所有搜索根目录都不存在时返回空清单，且不抛异常。</summary>
    [Fact]
    public void DetectInstallations_WhenRootsMissing_ReturnsEmptyWithoutThrowing()
    {
        using var workspace = new TempWorkspace();
        var roots = new DshSearchRoots
        {
            InstallPathOverride = Path.Combine(workspace.Root, "nope"),
            NpmGlobalNodeModules = new[] { Path.Combine(workspace.Root, "missing-nm") },
            DshProfilesNodeModules = new[] { Path.Combine(workspace.Root, "missing-profiles") },
            NpxCacheRoots = new[] { Path.Combine(workspace.Root, "missing-npx") },
            AiManagerNodeModules = new[] { Path.Combine(workspace.Root, "missing-ai") },
            AiManagerBinDirs = new[] { Path.Combine(workspace.Root, "missing-bin") },
            PathDirs = new[] { Path.Combine(workspace.Root, "missing-path") },
        };

        Assert.Empty(CreateService(roots).DetectInstallations());
    }

    /// <summary>搜索根提供者抛异常时，探测安全降级为空清单，不向调用方冒泡。</summary>
    [Fact]
    public void DetectInstallations_WhenRootProviderThrows_ReturnsEmpty()
    {
        var service = new DshDiscoveryService(
            candidateProvider: () => Array.Empty<string>(),
            dataDir: Path.Combine(Path.GetTempPath(), "no_such_dsh_home"),
            npxResolver: () => null,
            searchRootProvider: () => throw new InvalidOperationException("boom"));

        Assert.Empty(service.DetectInstallations());
    }

    /// <summary>模型展示属性：来源中文名 / 体积 / 可用性 / 短名称 / 版本文本。</summary>
    [Fact]
    public void DshInstallation_DisplayProperties_AreLocalized()
    {
        var install = new DshInstallation
        {
            Source = DshInstallSource.NpxCache,
            Version = "0.1.5-rc.3",
            SizeBytes = 12 * 1024 * 1024,
            IsUsable = true,
        };

        Assert.Equal("npx 缓存", install.SourceText);
        Assert.Equal("12.0 MB", install.SizeText);
        Assert.Equal("可用", install.UsableText);
        Assert.Equal("npx 缓存 · 0.1.5-rc.3", install.DisplayName);
        Assert.Equal("未知", new DshInstallation { Version = "unknown" }.VersionText);
        Assert.Equal("不可用", new DshInstallation { IsUsable = false }.UsableText);
    }

    /// <summary>构造测试用服务：候选位置与 npx 均为空，只由注入的临时根目录驱动探测。</summary>
    private static DshDiscoveryService CreateService(DshSearchRoots roots, string? dataDir = null)
        => new(
            candidateProvider: () => Array.Empty<string>(),
            dataDir: dataDir ?? Path.Combine(Path.GetTempPath(), "no_such_dsh_home"),
            npxResolver: () => null,
            searchRootProvider: () => roots);

    /// <summary>临时目录工作区：伪造各来源的包结构与启动器，测试结束自动清理。</summary>
    private sealed class TempWorkspace : IDisposable
    {
        /// <summary>本次测试独占的临时根目录。</summary>
        public string Root { get; }

        public TempWorkspace()
        {
            Root = Path.Combine(GetProbeBaseDirectory(), "dshprobe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>
        /// 选择探测根目录的父目录。
        /// 优先 C:\ProgramData：其绝对路径不含单引号，便于把路径原样写进 start-dsh-web.ps1 的 $workdir
        /// （$workdir 正则按 [^"'] 取值，路径含引号字符会被截断）；无写权限时退回系统临时目录。
        /// </summary>
        private static string GetProbeBaseDirectory()
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
            {
                try
                {
                    var probe = Path.Combine(programData, "dshprobe_probe_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(probe);
                    Directory.Delete(probe);
                    return programData;
                }
                catch { /* 无写权限：退回系统临时目录 */ }
            }
            return Path.GetTempPath();
        }

        /// <summary>在指定 node_modules 目录下创建 @deepseek-ai\dsh 包（含 package.json 与可选 lib\bin.js）。</summary>
        public string CreatePackage(string nodeModulesDir, string version, bool withBin = true, bool withWebApp = false)
        {
            var packageDir = Path.Combine(nodeModulesDir, "@deepseek-ai", "dsh");
            Directory.CreateDirectory(packageDir);
            File.WriteAllText(
                Path.Combine(packageDir, "package.json"),
                $"{{\"name\":\"@deepseek-ai/dsh\",\"version\":\"{version}\"}}");

            if (withBin)
            {
                Directory.CreateDirectory(Path.Combine(packageDir, "lib"));
                File.WriteAllText(Path.Combine(packageDir, "lib", "bin.js"), "console.log('dsh');");
            }
            if (withWebApp)
            {
                var webApp = Path.Combine(nodeModulesDir, "dsh-web-app");
                Directory.CreateDirectory(webApp);
                File.WriteAllText(Path.Combine(webApp, "package.json"), "{\"name\":\"dsh-web-app\"}");
            }
            return packageDir;
        }

        /// <summary>创建 dsh 启动器 shim 文件（内容无关紧要，只需存在且为文件）。</summary>
        public string CreateLauncher(string dir, string name = "dsh.cmd")
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "@echo off\r\n");
            return path;
        }

        /// <summary>
        /// 在 linkPath 处创建指向 targetPath 的目录 Junction（等价于 mklink /J）。
        /// 用 Junction 而非符号链接：符号链接需要 SeCreateSymbolicLinkPrivilege（管理员或开发者模式），
        /// 普通开发机 / CI 往往没有该权限；Junction 只要求同一卷，稳定可创建。
        /// 目标目录必须位于同一工作区根下，确保 Dispose 递归删除时不会波及工作区外的真实文件。
        /// </summary>
        public string CreateJunction(string linkPath, string targetPath)
        {
            var parent = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !Directory.Exists(linkPath))
                throw new InvalidOperationException($"创建 Junction 失败：{error}");
            return linkPath;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* 清理失败不影响测试结果 */ }
        }
    }
}
