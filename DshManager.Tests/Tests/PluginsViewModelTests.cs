using DshManager.ViewModels;

namespace DshManager.Tests;

/// <summary>
/// PluginsViewModel 中「安装/卸载/更新前提醒」纯逻辑的单元测试。
/// 只验证静态判定函数与文案常量：
/// - 不构造 PluginsViewModel（其构造函数会真的调用 dsh-plugin 列出已装插件）；
/// - 不触碰 MessageBox / UI（判定与弹窗分离，弹窗只发生在 ViewModel 私有闸门里）。
/// </summary>
public class PluginsViewModelTests
{
    // ==================== 是否需要弹确认框 ====================

    /// <summary>dsh 正在运行时 → 需要弹确认框（有立即风险）。</summary>
    [Fact]
    public void ShouldConfirmBeforePluginChange_DshRunning_ReturnsTrue()
        => Assert.True(PluginsViewModel.ShouldConfirmBeforePluginChange(dshRunning: true));

    /// <summary>dsh 未运行 → 不弹窗（无立即风险，只留弱提示）。</summary>
    [Fact]
    public void ShouldConfirmBeforePluginChange_DshStopped_ReturnsFalse()
        => Assert.False(PluginsViewModel.ShouldConfirmBeforePluginChange(dshRunning: false));

    // ==================== 文案常量 ====================

    /// <summary>确认框正文需同时说明「dsh 正在运行」的风险并给出「是否继续」。</summary>
    [Fact]
    public void RebuildWarningBody_MentionsRunningRiskAndContinuePrompt()
    {
        Assert.Contains("重建", PluginsViewModel.RebuildWarningBody);
        Assert.Contains("dsh 正在运行", PluginsViewModel.RebuildWarningBody);
        Assert.Contains("是否继续", PluginsViewModel.RebuildWarningBody);
    }

    /// <summary>未运行时的弱提示需包含「约 1 分钟」与「再启动 dsh」的关键建议。</summary>
    [Fact]
    public void RebuildDependencyNotice_RemindsToWaitBeforeStartingDsh()
    {
        Assert.Contains("约 1 分钟", PluginsViewModel.RebuildDependencyNotice);
        Assert.Contains("再启动 dsh", PluginsViewModel.RebuildDependencyNotice);
    }
}
