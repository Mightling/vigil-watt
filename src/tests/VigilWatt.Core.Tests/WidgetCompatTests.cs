using VigilWatt.App.Services;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 小组件 OS 能力判定（W1–W4）。
/// 2026/10/08 17:10:27 xliu
/// 这两个函数决定"哪个系统走哪条渲染路径"，写错会直接让某一代的观感回退，必须有回归保护。
/// </summary>
public class WidgetCompatTests
{
    // W1：Windows 10 各版本一律走透明窗口兼容路径（19041 = 2004、19044 = 21H2、19045 = 22H2）
    [Theory]
    [InlineData(17763)]   // 1809：Acrylic 的最低要求
    [InlineData(19041)]
    [InlineData(19044)]
    [InlineData(19045)]   // 22H2，Win10 的最后一个版本
    [InlineData(21999)]   // 紧邻 Win11 之前
    public void NeedsTransparentChrome_Windows10_True(int build)
        => Assert.True(WidgetCompat.NeedsTransparentChrome(build));

    // W2：Windows 11 及以后必须保持原生路径，不能被兼容逻辑误伤
    [Theory]
    [InlineData(22000)]   // Win11 21H2 首发
    [InlineData(22621)]   // 22H2
    [InlineData(22631)]   // 23H2
    [InlineData(26100)]   // 24H2
    public void NeedsTransparentChrome_Windows11_False(int build)
        => Assert.False(WidgetCompat.NeedsTransparentChrome(build));

    // W3：两个判定必须严格互斥且完备（不存在两个都为 false 的 build）
    [Theory]
    [InlineData(17763)]
    [InlineData(21999)]
    [InlineData(22000)]
    [InlineData(26100)]
    public void ChromePredicates_AreComplementary(int build)
        => Assert.NotEqual(
            WidgetCompat.NeedsTransparentChrome(build),
            WidgetCompat.SupportsDwmRoundedCorner(build));

    // W4：DWM 原生圆角的门槛与 Win11 首发 build 一致（防止两处常量漂移）
    [Fact]
    public void SupportsDwmRoundedCorner_MatchesWindows11Build()
    {
        Assert.True(WidgetCompat.SupportsDwmRoundedCorner(WidgetCompat.Windows11Build));
        Assert.False(WidgetCompat.SupportsDwmRoundedCorner(WidgetCompat.Windows11Build - 1));
    }
}
