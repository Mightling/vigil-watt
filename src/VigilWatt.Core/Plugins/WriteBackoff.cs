namespace VigilWatt.Core.Plugins;

/// <summary>
/// 功 能： 熔断停写后的"递增退避"进度表（纯函数，有单测）。
/// 2026/10/11 21:05:04 xliu
/// 退避必须**起点短、逐次变长**：设备（尤其键盘）休眠是常态，长退避会把"唤醒检测"一起挡死， 详见 docs/reviews/BACKLOG.md CR-53。
/// </summary>
public static class WriteBackoff
{
    /// <summary>首次熔断的退避：绝大多数"查询失败"其实是设备在睡，必须尽快重试。</summary>
    public static readonly TimeSpan First = TimeSpan.FromSeconds(30);

    /// <summary>退避上限（2026-10-11 用户定的上限）。</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    /// <summary>第 <paramref name="trips"/> 次熔断的退避时长：30s → 1m → 2m → 4m → 5m（封顶）。</summary>
    public static TimeSpan For(int trips)
    {
        var span = First;
        for (int i = 1; i < trips && span < Max; i++) span += span;
        return span < Max ? span : Max;
    }

    /// <summary>停写是否已到期、可以重试一轮（边界取"达到即到期"）。</summary>
    public static bool IsExpired(int trips, DateTimeOffset disabledAt, DateTimeOffset now)
        => now - disabledAt >= For(trips);
}
