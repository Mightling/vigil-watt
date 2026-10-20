using VigilWatt.Core.Plugins;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： OfflinePolicy —— 插件第③层"离线判定策略"参数的集中载体。
/// 2026/10/08 22:25:16 xliu
/// 覆盖默认值（不按沉默、 不延迟确认、轮询空读算失败），以及延迟确认窗口的边界语义（标记候选后要等满窗口才判离线， 期间任何恢复证据都能撤销它）。
/// </summary>
public class OfflinePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 21, 38, 38, TimeSpan.FromHours(8));

    /// <summary>
    /// 雷柏当前取值（`RapooPlugin.Offline.Confirm = 6s`）。RapooPlugin 带 WinRT 依赖、进不了本测试
    /// 项目，所以这里按同一数值验语义——改了插件里的窗口这条用例**不会自动跟着变**，是已知松动点。
    /// </summary>
    private static readonly OfflinePolicy Rapoo = new() { Confirm = TimeSpan.FromSeconds(6) };

    // ── 默认值 ───────────────────────────────────────────────

    [Fact]
    public void O1_默认不按沉默判离线也不延迟确认()
    {
        Assert.Equal(TimeSpan.Zero, OfflinePolicy.Default.Silence);
        Assert.Equal(TimeSpan.Zero, OfflinePolicy.Default.Confirm);
    }

    [Fact]
    public void O2_默认把轮询空读算失败且阈值为3()
    {
    // 默认面向"传统查询型插件"：轮询发了帧却没读数就是失联。
    // 雷柏 / 迈从 / 北通必须显式置 false —— 这正是把"轮询空读算不算失败"从 SupportsPassiveListen
    // 里拆出来的目的（曾经合成一个布尔量，导致迈从键盘关机后永不显示离线）。
        Assert.True(OfflinePolicy.Default.CountPollFailures);
        Assert.Equal(3, OfflinePolicy.Default.MaxFailures);
    }

    [Fact]
    public void O3_按值相等_同参实例可互换()
        => Assert.Equal(new OfflinePolicy { Confirm = TimeSpan.FromSeconds(6) },
                        new OfflinePolicy { Confirm = TimeSpan.FromSeconds(6) });

    // ── 延迟确认窗口 ─────────────────────────────────────────

    [Fact]
    public void P1_没有候选时不判离线()
        => Assert.False(Rapoo.IsConfirmExpired(default, T0));

    [Fact]
    public void P2_未启用确认窗口时不判离线()
        => Assert.False(OfflinePolicy.Default.IsConfirmExpired(T0, T0 + TimeSpan.FromMinutes(1)));

    [Fact]
    public void P3_窗口内不判离线()
        => Assert.False(Rapoo.IsConfirmExpired(T0, T0 + TimeSpan.FromSeconds(5)));

    [Fact]
    public void P4_恰好等于窗口不判离线_边界取严格大于()
        => Assert.False(Rapoo.IsConfirmExpired(T0, T0 + TimeSpan.FromSeconds(6)));

    [Fact]
    public void P5_超过窗口才判离线()
        => Assert.True(Rapoo.IsConfirmExpired(T0, T0 + TimeSpan.FromSeconds(6) + TimeSpan.FromMilliseconds(500)));

    [Fact]
    public void P6_回归_真机时序_断链后27点9秒静音应判离线()
    {
        // 21:38:38.564 收到 bit7=0 的帧 → 标记候选；此后静音，21:39:06.454 才恢复。
        // 期间任何一次 tick 都已远超 6 秒窗口。
        Assert.True(Rapoo.IsConfirmExpired(T0, T0 + TimeSpan.FromSeconds(27.9)));
    }

    [Fact]
    public void P7_回归_抖动场景_恢复帧撤销候选后不再判离线()
    {
        // 收到一帧 bit7=0（标记候选），3.04 秒后下一帧 bit7=1（撤销候选 = 重置为 default）。
        // 撤销后再怎么过时间都不该判离线。
        Assert.False(Rapoo.IsConfirmExpired(default, T0 + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void P8_回归_一个心跳周期内不判离线()
    {
        // 6 秒 ≈ 2 个心跳周期（实测 3.04s）。只过了一个周期时必须还不判——
        // "抖动一下马上回来"的恢复帧正好落在这个区间，这正是 3 秒窗口被否掉的原因。
        Assert.False(Rapoo.IsConfirmExpired(T0, T0 + TimeSpan.FromSeconds(3.04)));
    }
}
