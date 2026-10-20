using VigilWatt.Core.Plugins;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： WriteBackoff —— 熔断停写后的递增退避进度表。
/// 2026/10/11 21:05:04 xliu
/// 起因见 docs/reviews/BACKLOG.md CR-53。
/// </summary>
public class WriteBackoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 20, 25, 9, TimeSpan.FromHours(8));

    // ── 进度表 ───────────────────────────────────────────────

    [Fact]
    public void B1_首次熔断退避最短()
        => Assert.Equal(TimeSpan.FromSeconds(30), WriteBackoff.For(1));

    [Fact]
    public void B2_逐次翻倍()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), WriteBackoff.For(2));
        Assert.Equal(TimeSpan.FromSeconds(120), WriteBackoff.For(3));
        Assert.Equal(TimeSpan.FromSeconds(240), WriteBackoff.For(4));
    }

    [Fact]
    public void B3_封顶5分钟()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), WriteBackoff.For(5));
        Assert.Equal(TimeSpan.FromMinutes(5), WriteBackoff.For(50));
    }

    [Fact]
    public void B4_退避永不超过上限()
    {
        for (int trips = 1; trips <= 100; trips++)
            Assert.True(WriteBackoff.For(trips) <= WriteBackoff.Max);
    }

    [Fact]
    public void B5_非正次数按首次算()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WriteBackoff.For(0));
        Assert.Equal(TimeSpan.FromSeconds(30), WriteBackoff.For(-3));
    }

    // ── 到期判据 ─────────────────────────────────────────────

    [Fact]
    public void B6_退避未满不放行()
        => Assert.False(WriteBackoff.IsExpired(1, T0, T0 + TimeSpan.FromSeconds(29)));

    [Fact]
    public void B7_恰好到期即放行_边界取达到()
        => Assert.True(WriteBackoff.IsExpired(1, T0, T0 + TimeSpan.FromSeconds(30)));

    [Fact]
    public void B8_档位越高等得越久()
        => Assert.False(WriteBackoff.IsExpired(4, T0, T0 + TimeSpan.FromSeconds(60)));

    // ── 回归：真机时序 ───────────────────────────────────────

    [Fact]
    public void B9_回归_熔断期内的唤醒信号窗口()
    {
        // 2026-10-11 实测：20:25:09 熔断（第 1 次）→ 20:28:31 键盘已唤醒并推来状态通知。
        // 旧口径固定 10 分钟，那一刻离放行还差约 6.5 分钟；新口径第 1 次退避 30 秒，早已可重试。
        var wake = T0 + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(22);
        Assert.True(WriteBackoff.IsExpired(1, T0, wake));
        Assert.False(WriteBackoff.IsExpired(1, T0, T0 + TimeSpan.FromSeconds(29)));
    }
}
