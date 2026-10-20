using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Rapoo;

/// <summary>
/// 功 能： 雷柏 (Rapoo) 2.4G 插件 —— 纯被动实现，不向设备写任何字节。
/// 2026/10/06 15:53:36 xliu
/// 帧布局、充电标志编码与离线两条腿见 <see cref="RapooCodec"/> 及 docs/hardware/protocols.md §3。
/// </summary>
public sealed class RapooPlugin : BatteryPluginBase
{
    public const ushort RapooVid = 0x24AE;

    public override string Name => "Rapoo";
    public override string DisplayName => "雷柏";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>
    /// 纯被动零写入，探测失败**不是错误**，只说明"鼠标睡着了"（休眠时 dongle 全静音，一动即恢复）。
    /// 必须配合 15 秒快速发现节拍（<see cref="SupportsFastDiscovery"/>）保证每拍都能重探：
    /// 旧值 3 分钟是"键盘出来了、鼠标不出来"的根因（日志实测鼠标要 4 分 09 秒才进列表）。
    /// </summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(5);

    /// <summary>纯被动零写入 → 允许 DeviceManager 用 15 秒节拍做发现（不发任何帧，无风险）。</summary>
    public override bool SupportsFastDiscovery => true;

    /// <summary>本插件不存在任何写入路径；置 false 作为纵深防御（CanWrite 恒 false）。</summary>
    protected override bool WritesEnabled => false;

    /// <summary>探测只被动听心跳、不发任何字节——不受写入开关约束，否则关掉写入会连鼠标都发现不了。</summary>
    protected override bool ProbeWrites => false;

    /// <summary>认领后后台只读心跳帧，插拔电/电量变化立即 StatusPushed。</summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>
    /// 本插件的离线判定策略（见 <see cref="OfflinePolicy"/>）。
    ///
    /// <b>Confirm = 6 秒</b>：心跳间隔实测 3.04 秒，6 秒 ≈ 2 个周期。不取 3 秒是因为那只有 1 个周期，
    /// 正常丢一帧就会误判，且"断链后无新帧"与"停手静音"在被动路径上无法区分。恢复帧一到候选立即撤销。
    /// <b>Silence = 90 秒</b>：**兜底，不是主要判据**。0x07 是活动驱动的心跳，一停手就静音，
    /// 4 分钟内实测有 5 次 &gt;10 秒的正常空档，短阈值必然误报；90 秒只留给"真关机/没电"。
    /// <b>CountPollFailures = false</b>：零写入，<see cref="BatteryPluginBase.PollDevice"/> 恒返回 null
    /// 是**协议形态**（没有查询命令），不是失联。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Confirm = TimeSpan.FromSeconds(6),
        Silence = TimeSpan.FromSeconds(90),
        CountPollFailures = false,
    };

    /// <summary>
    /// 候选接口：vendor 通道（雷柏把 0xFF00 放在 UsagePage 或 Usage 字段，两边都查），
    /// 且输入长度**正好 19 字节**（状态报表 0x07 的长度）。
    /// 收敛到 <c>==19</c> 而非"≥19 就试试"：本机 dongle 的 32B/33B 两条 vendor 通道从不发 0x07，
    /// 放行它们只会每次首扫各白等一整个探测窗口。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != RapooVid) return false;
        if (info.UsagePage != 0xFF00 && info.Usage != 0xFF00) return false;
        return info.InputReportLength == RapooCodec.StatusReportLength;
    }

    /// <summary>
    /// 探测 = 被动听 5 秒心跳，听不到 = 鼠标睡着，返回 null 进冷却，下一轮再试。
    /// 窗口 5 秒 = 实测心跳间隔 3.04 秒的 1.6 倍余量。探测本身不发任何字节。
    /// </summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => ProbeAsync(handle).GetAwaiter().GetResult();

    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
    {
        var read = await ListenStatusAsync(handle, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!read.HasValue) return null;
        return ("status-report7", read.Value.Percent, read.Value.Charging);
    }

    /// <summary>
    /// 读数由后台被动监听负责；轮询不再占 ReadInput（避免与监听抢同一悬空读）。
    /// 沉默离线见 <see cref="OfflinePolicy.Silence"/>。
    /// </summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice _) => null;

    /// <summary>解析被动 input：0x07 状态帧 → 电量/充电，并维护 RF 断链候选；其它帧忽略。</summary>
    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
    {
        var status = RapooCodec.Parse(input);
        if (status.Kind == RapooFrameKind.NotStatus) return null;

        // CR-28：心跳约 3 秒一条，逐帧 Info + Hex 会冲到约 2.8 万行/天、把 2MB 滚动历史冲掉。
        // 改成只在链路/充电/电量变化时记一条；原始帧不再进 Info（协议排错看 Debug 档的"收到报文"）。
        Log.InfoOnChange($"rapoo-status:{dev.Status.Id}",
            $"{status.Link}|{status.ChargeFlag}|{status.Percent}",
            $"雷柏 状态帧: 连接={status.Link} 充电标志={status.ChargeFlag} 电量={status.Percent}% " +
            $"RF=0x{status.RfByte:X2}(bit7连接={!status.LinkDown} RSSI={status.RfByte & 0x7F})");

        // 链路位与电量无关，先处理——电量越界的帧（休眠瞬态）同样要能标记/撤销候选。
        if (status.LinkDown) MarkOfflineCandidate(dev);
        else ClearOfflineCandidate(dev);

        if (status.Kind == RapooFrameKind.StatusInvalidPercent)
        {
            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug($"雷柏 电量越界/归零（疑似休眠瞬态），丢弃: {Log.Hex(input)}");
            return null;
        }

        return (status.Percent, status.Charging, Offline: false);
    }

    /// <summary>在截止时间前收 input report，解析 0x07 状态帧。</summary>
    private static async Task<(int Percent, ChargingState Charging)?> ListenStatusAsync(
        HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < TimeSpan.FromMilliseconds(400) ? remaining : TimeSpan.FromMilliseconds(400);
            if (slice <= TimeSpan.Zero) break;
            var input = await handle.ReadInputAsync(slice).ConfigureAwait(false);
            if (input == null) continue;
            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"雷柏 收到报文: {Log.Hex(input)}");

            var status = RapooCodec.Parse(input);
            if (status.Kind == RapooFrameKind.Status) return (status.Percent, status.Charging);
        }
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrEmpty(info.Product) ? $"雷柏 ({info.VidPid})" : $"雷柏 {info.Product}";
}
