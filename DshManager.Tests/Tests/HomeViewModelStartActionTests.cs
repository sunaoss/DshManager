using DshManager.Models;
using DshManager.ViewModels;

namespace DshManager.Tests;

/// <summary>
/// 「启动」按钮的点击决策与文案映射单元测试。
///
/// 【为什么单独测这两个纯函数】用户反复反馈"点启动没反应"：根因是外部实例在场时按钮被置灰。
/// 修复的核心就是"任何状态点击都有明确反馈"，故把决策逻辑抽成纯函数 <see cref="HomeViewModel.DecideStartAction"/>
/// 与文案映射 <see cref="HomeViewModel.BuildStartButtonText"/> 直接覆盖。
/// 用例只传入状态与布尔值，不构造 HomeViewModel、不触达任何 WPF 资源或真实进程。
/// </summary>
public class HomeViewModelStartActionTests
{
    // ---------------- DecideStartAction：点击行为分派 ----------------

    [Theory]
    [InlineData(DshProcessState.Stopped, false, StartAction.Start)]          // 未运行 → 走启动流程
    [InlineData(DshProcessState.Running, false, StartAction.OpenWeb)]        // 运行中（面板）→ 打开网页
    [InlineData(DshProcessState.Running, true, StartAction.OpenWeb)]         // 运行中（外部）→ 也打开网页（本次修复重点）
    [InlineData(DshProcessState.Starting, false, StartAction.NotifyStarting)]// 启动中 → 只提示，不重复启动
    [InlineData(DshProcessState.Stopping, false, StartAction.NotifyStopping)]// 停止中 → 只提示
    public void DecideStartAction_ShouldDispatchByState(DshProcessState state, bool isExternal, StartAction expected)
    {
        // 断言：任何状态都有确定动作，绝不返回"什么都不做"（枚举里不存在该分支）
        Assert.Equal(expected, HomeViewModel.DecideStartAction(state, isExternal));
    }

    // ---------------- BuildStartButtonText：5 种状态 → 5 个文案 ----------------

    [Theory]
    [InlineData(DshProcessState.Stopped, false, "▶ 启动")]
    [InlineData(DshProcessState.Running, false, "🌐 打开网页")]
    [InlineData(DshProcessState.Running, true, "▶ 使用已运行的实例")]
    [InlineData(DshProcessState.Starting, false, "⏳ 启动中…")]
    [InlineData(DshProcessState.Stopping, false, "⏳ 停止中…")]
    public void BuildStartButtonText_ShouldMapStateToText(DshProcessState state, bool isExternal, string expected)
    {
        // 断言：5 个状态各对应唯一文案，用户"一看就知道点了会怎样"
        Assert.Equal(expected, HomeViewModel.BuildStartButtonText(state, isExternal));
    }

    [Fact]
    public void BuildStartButtonText_ShouldNeverBeBlank()
    {
        // 兜底断言：遍历状态 × 来源的组合，文案永远非空（避免出现"空白按钮"这种情况）
        foreach (var state in Enum.GetValues<DshProcessState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(HomeViewModel.BuildStartButtonText(state, false)));
            Assert.False(string.IsNullOrWhiteSpace(HomeViewModel.BuildStartButtonText(state, true)));
        }
    }
}
