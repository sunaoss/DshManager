using System.Diagnostics;
using System.IO;
using DshManager.Models;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// 「dsh 安装位置健壮性」相关单元测试：
/// 1) 锁定安装位置的解析（有效直接采用且不搜索 / 失效自动重搜并更新 / 未锁定自动搜索并自动锁定）；
/// 2) 安装位置校验与搬运计划等纯函数的边界；
/// 3) 搬运功能（复制 → 校验 → 删源）的成功与各类失败路径，以及链接修复。
///
/// 全程只使用 <see cref="Path.GetTempPath"/> 下的自建临时目录（测完清理），
/// 绝不触碰真实用户目录、绝不启动或终止 dsh 进程。
/// </summary>
public class DshInstallLocationTests
{
    // ==================== 安装目录校验（ValidateInstallDir） ====================

    /// <summary>同时含 package.json 与 lib\bin.js 的目录才被视为可用安装目录。</summary>
    [Fact]
    public void ValidateInstallDir_RequiresPackageJsonAndBinJs()
    {
        using var workspace = new TempWorkspace();
        var packageDir = CreateInstallTree(Path.Combine(workspace.Root, "npx", "hashA"));

        Assert.True(DshDiscoveryService.ValidateInstallDir(packageDir));

        // 缺 bin.js → 无效
        File.Delete(Path.Combine(packageDir, "lib", "bin.js"));
        Assert.False(DshDiscoveryService.ValidateInstallDir(packageDir));

        // 补回 bin.js、删掉 package.json → 仍无效
        File.WriteAllText(Path.Combine(packageDir, "lib", "bin.js"), "console.log('dsh');");
        File.Delete(Path.Combine(packageDir, "package.json"));
        Assert.False(DshDiscoveryService.ValidateInstallDir(packageDir));
    }

    /// <summary>空值 / 不存在的路径 / 普通空目录一律判为无效，且不抛异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\definitely\not\exists\dsh")]
    public void ValidateInstallDir_InvalidInputs_ReturnFalse(string? dir)
    {
        Assert.False(DshDiscoveryService.ValidateInstallDir(dir));
    }

    /// <summary>目录存在但没有 dsh 包文件 → 无效（避免把任意目录锁定成安装位置）。</summary>
    [Fact]
    public void ValidateInstallDir_EmptyDirectory_ReturnsFalse()
    {
        using var workspace = new TempWorkspace();
        var empty = Path.Combine(workspace.Root, "empty");
        Directory.CreateDirectory(empty);

        Assert.False(DshDiscoveryService.ValidateInstallDir(empty));
    }

    // ==================== 锁定位置解析（ResolveInstallLocation） ====================

    /// <summary>① 锁定路径有效 → 直接采用，且**完全不触发搜索**。</summary>
    [Fact]
    public void ResolveInstallLocation_ValidLock_UsesLockWithoutSearching()
    {
        using var workspace = new TempWorkspace();
        var lockedDir = CreateInstallTree(Path.Combine(workspace.Root, "locked", "hashX"));

        var searchCount = 0;
        var service = CreateService(
            new DshSearchRoots { NpxCacheRoots = new[] { Path.Combine(workspace.Root, "npx") } },
            workspace.Root,
            onSearch: () => searchCount++);

        var result = service.ResolveInstallLocation(lockedDir);

        Assert.NotNull(result.EffectiveInstallation);
        Assert.Equal(lockedDir, result.EffectiveInstallation!.PackageDir);
        Assert.True(result.EffectiveInstallation.IsUsable);
        Assert.Contains("使用已锁定的安装位置", result.Message);
        Assert.Contains(lockedDir, result.Message);
        Assert.False(result.WasSearched);
        Assert.Null(result.LockedPathToPersist); // 值没变，无需回写
        Assert.Equal(0, searchCount);            // 关键：一次搜索都没发生
    }

    /// <summary>② 锁定路径失效 → 自动重新搜索并更新锁定值，提示文案含「已失效」「已自动改为」，不抛异常。</summary>
    [Fact]
    public void ResolveInstallLocation_StaleLock_ResearchesAndUpdatesLock()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "npx");
        var found = CreateInstallTree(Path.Combine(npxRoot, "hashB"));
        var staleLock = Path.Combine(workspace.Root, "gone", "hashA", "node_modules", "@deepseek-ai", "dsh");

        var service = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }, workspace.Root);

        var result = service.ResolveInstallLocation(staleLock);

        Assert.True(result.WasStale);
        Assert.True(result.WasSearched);
        Assert.NotNull(result.EffectiveInstallation);
        Assert.Equal(found, result.EffectiveInstallation!.PackageDir);
        Assert.Equal(found, result.LockedPathToPersist);
        Assert.Equal(found, result.LockedPath);
        Assert.Contains("已失效", result.Message);
        Assert.Contains("已自动改为", result.Message);
        Assert.Contains(staleLock, result.Message);
    }

    /// <summary>② 锁定失效且搜索无果 → 只给出中性提示，不抛异常、不返回安装项。</summary>
    [Fact]
    public void ResolveInstallLocation_StaleLockAndNothingFound_ReturnsNeutralMessage()
    {
        using var workspace = new TempWorkspace();
        var staleLock = Path.Combine(workspace.Root, "gone", "hashA", "node_modules", "@deepseek-ai", "dsh");

        var service = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { Path.Combine(workspace.Root, "npx") } }, workspace.Root);

        var result = service.ResolveInstallLocation(staleLock);

        Assert.Null(result.EffectiveInstallation);
        Assert.Null(result.LockedPathToPersist);
        Assert.True(result.WasStale);
        Assert.Contains("已失效", result.Message);
        Assert.Contains("未找到", result.Message);
    }

    /// <summary>③ 未锁定 → 自动搜索取第一个可用结果并自动锁定。</summary>
    [Fact]
    public void ResolveInstallLocation_NoLock_SearchesAndAutoLocks()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "npx");
        var found = CreateInstallTree(Path.Combine(npxRoot, "hashC"));

        var service = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }, workspace.Root);

        var result = service.ResolveInstallLocation(null);

        Assert.NotNull(result.EffectiveInstallation);
        Assert.Equal(found, result.EffectiveInstallation!.PackageDir);
        Assert.Equal(found, result.LockedPathToPersist);
        Assert.Contains("已自动锁定", result.Message);
        Assert.False(result.WasStale);
    }

    /// <summary>③ 未锁定且搜索无果 → 中性提示，不抛异常。</summary>
    [Fact]
    public void ResolveInstallLocation_NoLockAndNothingFound_ReturnsNeutralMessage()
    {
        using var workspace = new TempWorkspace();
        var service = CreateService(new DshSearchRoots(), workspace.Root);

        var result = service.ResolveInstallLocation(string.Empty);

        Assert.Null(result.EffectiveInstallation);
        Assert.True(result.WasSearched);
        Assert.Contains("未找到可用的 dsh 安装位置", result.Message);
    }

    // ==================== Scan 与锁定的集成 ====================

    /// <summary>
    /// 已锁定且有效：Scan 直接用它构造启动命令（node &lt;绝对 bin.js&gt;），写入中性提示，并**不搜索**。
    /// </summary>
    [Fact]
    public void Scan_WithValidLock_UsesLockedBinJsAndSkipsSearch()
    {
        using var workspace = new TempWorkspace();
        var lockedDir = CreateInstallTree(Path.Combine(workspace.Root, "locked", "hashX"));
        var binJs = Path.Combine(lockedDir, "lib", "bin.js");

        var searchCount = 0;
        var saved = new List<string>();
        var service = CreateService(
            new DshSearchRoots { NpxCacheRoots = new[] { Path.Combine(workspace.Root, "npx") } },
            workspace.Root,
            onSearch: () => searchCount++,
            lockedPathProvider: () => lockedDir,
            lockedPathSaver: saved.Add);

        var info = service.Scan();

        Assert.True(info.IsInstalled);
        Assert.Equal("0.1.5-rc.3", info.Version);
        Assert.Equal(ExpectedLaunchCommand(binJs), info.LaunchCommand);
        Assert.Contains("使用已锁定的安装位置", service.LastInstallLocationMessage);
        Assert.Equal(0, searchCount);   // 锁有效 → 不搜索
        Assert.Empty(saved);            // 锁定值没变 → 不写盘
    }

    /// <summary>已锁定但失效：Scan 自动重搜、把新位置写回配置、提示含「已失效」「已自动改为」，且不抛异常。</summary>
    [Fact]
    public void Scan_WithStaleLock_ResearchesPersistsAndReportsNeutrally()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "npx");
        var found = CreateInstallTree(Path.Combine(npxRoot, "hashB"));
        var staleLock = Path.Combine(workspace.Root, "gone", "hashA", "node_modules", "@deepseek-ai", "dsh");

        var saved = new List<string>();
        var service = CreateService(
            new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } },
            workspace.Root,
            lockedPathProvider: () => staleLock,
            lockedPathSaver: saved.Add);

        var info = service.Scan();

        Assert.True(info.IsInstalled);
        Assert.Equal(ExpectedLaunchCommand(Path.Combine(found, "lib", "bin.js")), info.LaunchCommand);
        Assert.Equal(new[] { found }, saved);                       // 新位置已写回
        Assert.Contains("已失效", service.LastInstallLocationMessage);
        Assert.Contains("已自动改为", service.LastInstallLocationMessage);
    }

    /// <summary>未启用锁定（提供者为 null，即历史行为）→ Scan 依旧走多来源搜索，且不产生提示文案。</summary>
    [Fact]
    public void Scan_WithoutLockProvider_KeepsLegacyBehaviour()
    {
        using var workspace = new TempWorkspace();
        var npxRoot = Path.Combine(workspace.Root, "npx");
        var found = CreateInstallTree(Path.Combine(npxRoot, "hashB"));

        var service = CreateService(new DshSearchRoots { NpxCacheRoots = new[] { npxRoot } }, workspace.Root);

        var info = service.Scan();

        Assert.Equal(ExpectedLaunchCommand(Path.Combine(found, "lib", "bin.js")), info.LaunchCommand);
        Assert.Equal(string.Empty, service.LastInstallLocationMessage);
    }

    // ==================== 物理路径解析（真 Junction；不启动 dsh） ====================

    /// <summary>
    /// 包目录是 Junction 时，解析结果必须是它指向的**物理路径**（而不是链接路径）。
    /// 用临时目录 + 真 Junction 复现用户机器上 `~\.dsh\profiles\node_modules\@deepseek-ai\dsh` 的形态。
    /// </summary>
    [Fact]
    public void ResolvePhysicalPackageDir_Junction_ReturnsPhysicalTarget()
    {
        using var workspace = new TempWorkspace();
        var realPackageDir = CreateInstallTree(Path.Combine(workspace.Root, "real", "hashP"));
        var linkPackageDir = CreateProfilesLink(workspace.Root, realPackageDir);

        var resolved = DshDiscoveryService.ResolvePhysicalPackageDir(linkPackageDir);

        Assert.Equal(realPackageDir, resolved, ignoreCase: true);
        Assert.False(string.Equals(linkPackageDir, resolved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>普通目录（非链接）→ 原样返回；空值 / 不存在的路径也不抛异常。</summary>
    [Fact]
    public void ResolvePhysicalPackageDir_NonLinkOrInvalid_ReturnsSamePath()
    {
        using var workspace = new TempWorkspace();
        var normalDir = CreateInstallTree(Path.Combine(workspace.Root, "normal", "hashN"));

        Assert.Equal(normalDir, DshDiscoveryService.ResolvePhysicalPackageDir(normalDir), ignoreCase: true);
        Assert.Equal(string.Empty, DshDiscoveryService.ResolvePhysicalPackageDir(null));
        Assert.Equal(string.Empty, DshDiscoveryService.ResolvePhysicalPackageDir("   "));

        var missing = Path.Combine(workspace.Root, "definitely-missing", "node_modules", "@deepseek-ai", "dsh");
        Assert.Equal(missing, DshDiscoveryService.ResolvePhysicalPackageDir(missing), ignoreCase: true);
    }

    /// <summary>
    /// 链接有效、但物理目标缺 <c>lib\bin.js</c>（残缺目录）→ 必须**回退链接路径**，
    /// 否则会把"链接还能用"变成"必然启动失败"。
    /// </summary>
    [Fact]
    public void ResolvePhysicalPackageDir_JunctionTargetWithoutBinJs_FallsBackToLinkPath()
    {
        using var workspace = new TempWorkspace();
        // 物理目标只有 package.json，缺 lib\bin.js
        var incompleteDir = Path.Combine(workspace.Root, "incomplete", "hashI", "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(incompleteDir);
        File.WriteAllText(Path.Combine(incompleteDir, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.3\"}");

        var linkPackageDir = CreateProfilesLink(workspace.Root, incompleteDir);

        var resolved = DshDiscoveryService.ResolvePhysicalPackageDir(linkPackageDir);

        Assert.False(string.IsNullOrWhiteSpace(resolved));          // 仍返回可用结果
        Assert.Equal(linkPackageDir, resolved, ignoreCase: true);   // 回退链接路径（而非残缺的物理目录）
        Assert.False(string.Equals(incompleteDir, resolved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 锁定值是 Junction 时：锁定结果应指向**物理路径**（避免链接被 npx 缓存清理而悬空），
    /// 且仍需回写配置（值确实变了）；关键约束"锁有效 → 一次搜索都不发生"保持不变。
    /// </summary>
    [Fact]
    public void ResolveInstallLocation_JunctionLock_LocksPhysicalPathWithoutSearching()
    {
        using var workspace = new TempWorkspace();
        var realPackageDir = CreateInstallTree(Path.Combine(workspace.Root, "real", "hashP"));
        var linkPackageDir = CreateProfilesLink(workspace.Root, realPackageDir);

        var searchCount = 0;
        var service = CreateService(
            new DshSearchRoots { NpxCacheRoots = new[] { Path.Combine(workspace.Root, "npx") } },
            workspace.Root,
            onSearch: () => searchCount++);

        var result = service.ResolveInstallLocation(linkPackageDir);

        Assert.NotNull(result.EffectiveInstallation);
        Assert.Equal(realPackageDir, result.EffectiveInstallation!.PackageDir, ignoreCase: true);
        Assert.Equal(realPackageDir, result.LockedPath, ignoreCase: true);
        Assert.False(string.Equals(linkPackageDir, result.LockedPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(realPackageDir, result.LockedPathToPersist, ignoreCase: true);
        Assert.Contains("使用已锁定的安装位置", result.Message);
        Assert.Equal(0, searchCount);   // 锁有效 → 依旧完全不搜索
    }

    /// <summary>
    /// Junction 锁定下的启动命令：bin.js 必须是**物理路径**的绝对路径，且不出现链接路径；
    /// 同时把物理路径回写为新的锁定值。
    /// </summary>
    [Fact]
    public void Scan_WithJunctionLock_UsesPhysicalBinJsAndPersistsPhysicalPath()
    {
        using var workspace = new TempWorkspace();
        var realPackageDir = CreateInstallTree(Path.Combine(workspace.Root, "real", "hashP"));
        var linkPackageDir = CreateProfilesLink(workspace.Root, realPackageDir);
        var physicalBinJs = Path.Combine(realPackageDir, "lib", "bin.js");

        var saved = new List<string>();
        var service = CreateService(
            new DshSearchRoots { NpxCacheRoots = new[] { Path.Combine(workspace.Root, "npx") } },
            workspace.Root,
            lockedPathProvider: () => linkPackageDir,
            lockedPathSaver: saved.Add);

        var info = service.Scan();

        Assert.Equal(ExpectedLaunchCommand(physicalBinJs), info.LaunchCommand);
        Assert.DoesNotContain(linkPackageDir, info.LaunchCommand);
        // 链接 → 物理路径的改写已落盘（解析结果的大小写由文件系统给出，故忽略大小写比对）
        Assert.Single(saved);
        Assert.Equal(realPackageDir, saved[0], ignoreCase: true);
    }

    // ==================== 搬运计划（纯函数边界） ====================

    /// <summary>标准 npx 布局：搬运单位是"直接包含 node_modules 的那一层"，目标是"目标文件夹 + 源目录名"。</summary>
    [Fact]
    public void BuildMovePlan_StandardLayout_ProducesExpectedPaths()
    {
        var sourcePackageDir = @"C:\cache\_npx\99cabb\node_modules\@deepseek-ai\dsh";

        var plan = DshDiscoveryService.BuildMovePlan(sourcePackageDir, @"D:\dsh-target");

        Assert.True(plan.IsValid);
        Assert.Equal(@"C:\cache\_npx\99cabb", plan.SourceRoot);
        Assert.Equal(@"D:\dsh-target\99cabb", plan.DestRoot);
        Assert.Equal(
            @"D:\dsh-target\99cabb\node_modules\@deepseek-ai\dsh",
            plan.DestPackageDir);
    }

    /// <summary>非标准结构 / 空入参 → 无效计划，并给出中文原因（绝不抛异常）。</summary>
    [Theory]
    [InlineData("", @"D:\t", "未指定")]
    [InlineData(@"C:\x\dsh", @"D:\t", "结构不符合预期")]                              // 缺 node_modules 段
    [InlineData(@"C:\cache\hash\node_modules\dsh", @"D:\t", "结构不符合预期")]         // 缺 @scope 层
    [InlineData(@"C:\cache\hash\node_modules\@deepseek-ai\dsh", "", "未选择目标文件夹")]
    public void BuildMovePlan_InvalidInputs_ReturnReason(string packageDir, string target, string expectedFragment)
    {
        var plan = DshDiscoveryService.BuildMovePlan(packageDir, target);

        Assert.False(plan.IsValid);
        Assert.Contains(expectedFragment, plan.Reason);
    }

    /// <summary>目标与源相同 / 互相嵌套 → 一律拒绝，避免自我复制导致无限递归。</summary>
    [Fact]
    public void BuildMovePlan_NestedOrSameTarget_IsRejected()
    {
        var sourcePackageDir = @"C:\cache\_npx\99cabb\node_modules\@deepseek-ai\dsh";

        // 目标文件夹就是源的父目录 → DestRoot 与 SourceRoot 相同
        var same = DshDiscoveryService.BuildMovePlan(sourcePackageDir, @"C:\cache\_npx");
        Assert.False(same.IsValid);
        Assert.Contains("相同", same.Reason);

        // 目标位于源目录内部
        var inner = DshDiscoveryService.BuildMovePlan(sourcePackageDir, @"C:\cache\_npx\99cabb\node_modules");
        Assert.False(inner.IsValid);
        Assert.Contains("不能位于源安装目录内部", inner.Reason);
    }

    /// <summary>IsUnder：相等不算"之下"；真子路径算；仅前缀相同（a / ab）不算。</summary>
    [Theory]
    [InlineData(@"C:\a\b\c", @"C:\a\b", true)]
    [InlineData(@"C:\a\b", @"C:\a\b", false)]
    [InlineData(@"C:\ab\c", @"C:\a", false)]
    [InlineData(@"C:\A\B\C", @"c:\a\b", true)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    public void IsUnder_Boundaries(string child, string parent, bool expected)
    {
        Assert.Equal(expected, DshDiscoveryService.IsUnder(child, parent));
    }

    /// <summary>RemapPathUnder：只重映射旧根之下的路径，其余原样返回。</summary>
    [Fact]
    public void RemapPathUnder_MapsOnlyPathsUnderOldRoot()
    {
        Assert.Equal(
            @"D:\new\99cabb\node_modules\@deepseek-ai\dsh",
            DshDiscoveryService.RemapPathUnder(
                @"C:\old\99cabb\node_modules\@deepseek-ai\dsh", @"C:\old\99cabb", @"D:\new\99cabb"));

        Assert.Equal(@"C:\elsewhere\x", DshDiscoveryService.RemapPathUnder(@"C:\elsewhere\x", @"C:\old", @"D:\new"));
    }

    /// <summary>目录体积统计：正常求和，路径不存在 / 空值返回 0。</summary>
    [Fact]
    public void GetDirectorySizeSafe_SumsFilesAndToleratesMissing()
    {
        using var workspace = new TempWorkspace();
        var dir = Path.Combine(workspace.Root, "size");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[2048]);

        Assert.Equal(3072, DshDiscoveryService.GetDirectorySizeSafe(dir));
        Assert.Equal(0, DshDiscoveryService.GetDirectorySizeSafe(Path.Combine(workspace.Root, "nope")));
        Assert.Equal(0, DshDiscoveryService.GetDirectorySizeSafe(null));
    }

    // ==================== 搬运执行（真实临时目录） ====================

    /// <summary>
    /// 成功路径：复制到目标 → 校验通过 → 删除源；返回的新包目录可用、消息含新旧位置与「锁定位置已更新为」。
    /// 用临时目录真跑小文件复制来验证"复制成功后才删源"的语义。
    /// </summary>
    [Fact]
    public void MoveInstall_Success_CopiesThenDeletesSource()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = Path.Combine(workspace.Root, "src", "hashA");
        var sourcePackageDir = CreateInstallTree(sourceRoot);
        var targetParent = Path.Combine(workspace.Root, "target");
        Directory.CreateDirectory(targetParent);

        var progress = new RecordingProgress();
        var service = CreateService(new DshSearchRoots(), workspace.Root);

        var result = service.MoveInstall(sourcePackageDir, targetParent, progress);

        Assert.True(result.Success);
        var destPackageDir = Path.Combine(targetParent, "hashA", "node_modules", "@deepseek-ai", "dsh");
        Assert.Equal(destPackageDir, result.NewPackageDir);
        Assert.True(DshDiscoveryService.ValidateInstallDir(destPackageDir));
        Assert.Contains("已从", result.Message);
        Assert.Contains("锁定位置已更新为", result.Message);

        // 兄弟包也一起搬过去（dsh 需要靠它们解析 @deepseek-ai/*）
        var destSibling = Path.Combine(targetParent, "hashA", "node_modules", "@deepseek-ai", "dsh-llm", "index.js");
        Assert.True(File.Exists(destSibling));

        // 校验通过后才删源
        Assert.False(Directory.Exists(sourceRoot));

        // 进度按"已复制文件数 / 总文件数"推进，最终上报 100%
        Assert.Equal(1.0, progress.Last, 3);
    }

    /// <summary>目标已存在同名目录 → 拒绝并给出中文原因，源目录保持不动。</summary>
    [Fact]
    public void MoveInstall_TargetAlreadyExists_FailsAndKeepsSource()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = Path.Combine(workspace.Root, "src", "hashA");
        var sourcePackageDir = CreateInstallTree(sourceRoot);
        var targetParent = Path.Combine(workspace.Root, "target");
        Directory.CreateDirectory(Path.Combine(targetParent, "hashA")); // 同名目录已存在

        var service = CreateService(new DshSearchRoots(), workspace.Root);
        var result = service.MoveInstall(sourcePackageDir, targetParent);

        Assert.False(result.Success);
        Assert.Contains("已存在同名目录", result.Message);
        Assert.True(Directory.Exists(sourceRoot));
        Assert.True(File.Exists(Path.Combine(sourcePackageDir, "lib", "bin.js")));
    }

    /// <summary>源缺 lib\bin.js → 拒绝搬运，并保证不删除源目录。</summary>
    [Fact]
    public void MoveInstall_SourceMissingBin_FailsAndKeepsSource()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = Path.Combine(workspace.Root, "src", "hashA");
        var sourcePackageDir = CreateInstallTree(sourceRoot);
        File.Delete(Path.Combine(sourcePackageDir, "lib", "bin.js"));
        var targetParent = Path.Combine(workspace.Root, "target");
        Directory.CreateDirectory(targetParent);

        var service = CreateService(new DshSearchRoots(), workspace.Root);
        var result = service.MoveInstall(sourcePackageDir, targetParent);

        Assert.False(result.Success);
        Assert.Contains("源安装位置无效", result.Message);
        Assert.True(Directory.Exists(sourceRoot));
    }

    /// <summary>复制阶段失败（目标父目录其实是文件）→ 给出中文原因、清理目标、**不删除源**。</summary>
    [Fact]
    public void MoveInstall_CopyFailure_ReportsReasonAndKeepsSource()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = Path.Combine(workspace.Root, "src", "hashA");
        var sourcePackageDir = CreateInstallTree(sourceRoot);
        // 把"目标文件夹"造成一个文件：其下无法创建同名子目录 → 必然复制失败
        var targetParent = Path.Combine(workspace.Root, "target-file");
        File.WriteAllText(targetParent, "not a directory");

        var service = CreateService(new DshSearchRoots(), workspace.Root);
        var result = service.MoveInstall(sourcePackageDir, targetParent);

        Assert.False(result.Success);
        Assert.Contains("复制失败", result.Message);
        Assert.Contains("源目录未改动", result.Message);
        Assert.True(Directory.Exists(sourceRoot)); // 源必须完好
        Assert.True(File.Exists(Path.Combine(sourcePackageDir, "lib", "bin.js")));
    }

    /// <summary>复制阶段被取消 → 报告已取消、清理目标目录、保留源目录。</summary>
    [Fact]
    public void MoveInstall_Cancelled_CleansTargetAndKeepsSource()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = Path.Combine(workspace.Root, "src", "hashA");
        var sourcePackageDir = CreateInstallTree(sourceRoot);
        var targetParent = Path.Combine(workspace.Root, "target");
        Directory.CreateDirectory(targetParent);

        var service = CreateService(new DshSearchRoots(), workspace.Root);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 进入复制阶段即取消

        var result = service.MoveInstall(sourcePackageDir, targetParent, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("已取消", result.Message);
        Assert.False(Directory.Exists(Path.Combine(targetParent, "hashA"))); // 目标已清理
        Assert.True(Directory.Exists(sourceRoot));                          // 源未改动
    }

    /// <summary>
    /// 搬运后修复 <c>&lt;DSH_HOME&gt;\profiles\node_modules\@deepseek-ai\*</c> 中指向旧位置的 Junction：
    /// 链接应被重建为指向新位置，并在结果里报告修复数量。
    /// （用临时 DSH_HOME + 真 Junction 验证，不触碰真实 ~\.dsh）
    /// </summary>
    [Fact]
    public void MoveInstall_RepairsProfileJunctionsPointingToOldRoot()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = Path.Combine(workspace.Root, "src", "hashA");
        var sourcePackageDir = CreateInstallTree(sourceRoot);
        var targetParent = Path.Combine(workspace.Root, "target");
        Directory.CreateDirectory(targetParent);

        // 伪造 DSH_HOME：profiles\node_modules\@deepseek-ai\dsh-llm 指向源安装里的兄弟包
        var dshHome = Path.Combine(workspace.Root, "dshhome");
        var linkDir = Path.Combine(dshHome, "profiles", "node_modules", "@deepseek-ai");
        var linkPath = Path.Combine(linkDir, "dsh-llm");
        var oldTarget = Path.Combine(sourceRoot, "node_modules", "@deepseek-ai", "dsh-llm");
        CreateJunction(linkPath, oldTarget);

        var logs = new List<string>();
        var service = CreateService(new DshSearchRoots(), dshHome);
        var result = service.MoveInstall(sourcePackageDir, targetParent, log: logs.Add);

        Assert.True(result.Success);
        Assert.Equal(1, result.RepairedLinks);

        var expectedTarget = Path.Combine(targetParent, "hashA", "node_modules", "@deepseek-ai", "dsh-llm");
        Assert.Equal(expectedTarget, new DirectoryInfo(linkPath).LinkTarget, ignoreCase: true);
        Assert.True(Directory.Exists(linkPath)); // 链接仍可用
        Assert.Contains(logs, l => l.Contains("已修复链接"));
    }

    /// <summary>没有需要修复的链接时返回 0（不报错、不影响搬运结果）。</summary>
    [Fact]
    public void RepairLinks_WhenNoMatchingLinks_ReturnsZero()
    {
        using var workspace = new TempWorkspace();
        var service = CreateService(new DshSearchRoots(), Path.Combine(workspace.Root, "dshhome"));

        Assert.Equal(0, service.RepairLinks(
            Path.Combine(workspace.Root, "old"),
            Path.Combine(workspace.Root, "new")));
    }

    // ==================== 测试辅助 ====================

    /// <summary>按 DshDiscoveryService.BuildLaunchCommand 的规则生成期望的启动命令（含空格路径带双引号）。</summary>
    private static string ExpectedLaunchCommand(string binJs)
        => binJs.Contains(' ') ? $"node \"{binJs}\"" : $"node {binJs}";

    /// <summary>同步记录进度的假 IProgress：避免 Progress&lt;T&gt; 在无 UI 上下文时异步回调导致的断言竞态。</summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        public double Last { get; private set; }

        public void Report(double value) => Last = value;
    }

    /// <summary>构造解析/搬运用服务：候选位与 npx 均置空，只由注入的搜索根 / 锁定回调驱动，隔离真实环境。</summary>
    private static DshDiscoveryService CreateService(
        DshSearchRoots roots,
        string dataDir,
        Action? onSearch = null,
        Func<string?>? lockedPathProvider = null,
        Action<string>? lockedPathSaver = null)
        => new(
            candidateProvider: () => Array.Empty<string>(),
            dataDir: dataDir,
            // 本测试在临时目录里造了真实的安装树（含 package.json），故需真读文件才能解析出版本号；
            // 文件不存在时返回 null，与隔离文件系统的语义一致。
            fileContentReader: path => File.Exists(path) ? File.ReadAllText(path) : null,
            versionDetector: _ => null,
            npxResolver: () => null,
            searchRootProvider: () =>
            {
                onSearch?.Invoke();
                return roots;
            },
            lockedPathProvider: lockedPathProvider,
            lockedPathSaver: lockedPathSaver);

    /// <summary>
    /// 在指定安装根目录下造一份"像 npx 缓存"的安装树：
    /// &lt;root&gt;\node_modules\@deepseek-ai\dsh（含 package.json 与 lib\bin.js）+ 一个兄弟包 dsh-llm。
    /// 返回 dsh 包目录。
    /// </summary>
    private static string CreateInstallTree(string installRoot)
    {
        var nodeModules = Path.Combine(installRoot, "node_modules");
        var packageDir = Path.Combine(nodeModules, "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(packageDir, "lib"));
        File.WriteAllText(Path.Combine(packageDir, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.3\"}");
        File.WriteAllText(Path.Combine(packageDir, "lib", "bin.js"), "console.log('dsh');");

        var sibling = Path.Combine(nodeModules, "@deepseek-ai", "dsh-llm");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "index.js"), "export const x = 1;");
        return packageDir;
    }

    /// <summary>
    /// 在临时工作区里造一份"dsh profiles"形态的 Junction：
    /// <c>&lt;workspace&gt;\profiles\node_modules\@deepseek-ai\dsh</c> → <paramref name="targetPackageDir"/>。
    /// 返回链接路径（即用户配置里那种"看似安装位置、实为链接"的写法）。
    /// </summary>
    private static string CreateProfilesLink(string workspaceRoot, string targetPackageDir)
    {
        var linkPackageDir = Path.Combine(
            workspaceRoot, "profiles", "node_modules", "@deepseek-ai", "dsh");
        CreateJunction(linkPackageDir, targetPackageDir);
        return linkPackageDir;
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

    /// <summary>测试用临时工作区（系统临时目录下的自建目录，测完递归清理）。</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; }

        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DshInstallLocTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 清理失败不影响测试结论 */ }
        }
    }
}
