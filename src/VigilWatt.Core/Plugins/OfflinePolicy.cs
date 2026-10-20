namespace VigilWatt.Core.Plugins;

/// <summary>
/// 功 能： 插件"离线判定策略"（第③层）参数的集中载体 —— 与协议事实、安全声明分开。
/// 2026/10/08 22:25:16 xliu
/// 分层理由、五条离线路径与各插件取值见 docs/project/code-flow.md §4.2。
/// </summary>
public sealed record OfflinePolicy
{
    /// <summary>基类默认：不按沉默判离线、不延迟确认，但把"轮询空读"当失败（传统查询型插件）。</summary>
    public static readonly OfflinePolicy Default = new();

    /// <summary>
    /// 被动"沉默"兜底：被动监听超过这么久收不到任何报文 ⇒ 离线。
    /// 判据是"接口有没有在说话"，不是"能否解析出本协议帧"。
    /// <see cref="TimeSpan.Zero"/> = 不按沉默判离线（推送稀疏、或压根没有推流的协议）。
    /// </summary>
    public TimeSpan Silence { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// 延迟确认窗口：插件标记"疑似离线"候选后，等这么久仍无恢复证据才真判离线。
    /// <see cref="TimeSpan.Zero"/> = 不启用。
    /// 检查点是**被动监听的空闲 tick**（约 500ms 一次），不是 60 秒轮询——否则窗口会被
    /// 轮询相位放大成"窗口 + 最多 60 秒"。
    /// </summary>
    public TimeSpan Confirm { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// 连续多少次"发过帧却没应答" ⇒ 熔断停写 + 标离线。
    /// 只在 <see cref="CountPollFailures"/> 为 true、或插件自己在 <c>PollDevice</c> 里调用
    /// 失败记账入口（<c>ReportPollFailure</c>）时才用得上。
    /// </summary>
    public int MaxFailures { get; init; } = 3;

    /// <summary>
    /// 轮询返回 null 是否**直接**计入失败。默认 true（传统查询型插件）；
    /// 置 false 的两种情况：协议根本没有查询命令（雷柏），或 null 与"未开写入 / 熔断中 /
    /// 纯推送协议"不可区分、只有插件自己知道这一轮发没发帧（迈从、北通）。
    /// **必须**与"要不要起被动监听"分开：曾经把两者合成一个布尔量，导致迈从键盘关机后
    /// 永远不显示离线（2026-10-08 用户复报）。
    /// </summary>
    public bool CountPollFailures { get; init; } = true;

    /// <summary>
    /// 离线候选是否已超过 <see cref="Confirm"/> 窗口。<paramref name="candidateSince"/> 为
    /// <c>default</c> 表示当前没有候选；<see cref="Confirm"/> ≤ 0 表示未启用延迟确认。
    /// 两者都返回 false。边界取**严格大于**：窗口语义是"至少要等这么久"。
    /// </summary>
    public bool IsConfirmExpired(DateTimeOffset candidateSince, DateTimeOffset now)
        => candidateSince != default
        && Confirm > TimeSpan.Zero
        && now - candidateSince > Confirm;
}
