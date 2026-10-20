using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Pmo;

/// <summary>
/// 功 能： PMO（PMOKEY）Wave 75 等 QMK 方案键盘的 2.4G 电量插件。
/// 2026/10/18 新增
///
/// 通道：dongle 的厂商接口 <c>usagePage 0xFF60 / usage 0x61</c>（**不是** VIA 的 raw HID；
/// 该固件在 2.4G 模式下不对 VIA 应答，实测 0x01 协议版本查询无响应）。
/// 时序：开会话 0x10 → 查电量 0x30 → 关会话 0x11，与官方 pmohub.cn 驱动一致。
/// 帧结构见 <see cref="PmoCodec"/>。
/// </summary>
public sealed class PmoPlugin : BatteryPluginBase
{
    /// <summary>插件开关的键（插件开关即写入授权）。</summary>
    private const string SettingsKey = "Pmo";

    /// <summary>PMO 的 USB VID（也是引导器 36B0:3003 的厂商号）。</summary>
    public const ushort PmoVid = 0x36B0;

    /// <summary>Wave 75 的 2.4G dongle PID。</summary>
    public const ushort PmoDonglePid = 0x3002;

    public override string Name => SettingsKey;
    public override string DisplayName => "PMO";
    public override DeviceKind Kind => DeviceKind.Keyboard;

    /// <summary>本协议没有任何自发推送（实测被动监听 8 秒零帧），只能主动问。</summary>
    protected override bool SupportsPassiveListen => false;

    /// <summary>
    /// 离线策略：**故意不用熔断**。
    ///
    /// 这个键盘空闲几分钟就进深睡，此时 dongle 对整个会话帧都静音（实测 0x10 无回包）。
    /// 而基类的熔断链是 "连续失败 → TripBreaker 停写 → 被动监听的空闲 tick 里半开重试"，
    /// 本插件没有被动监听 ⇒ 半开永远不会触发 ⇒ 只要睡过一次，设备就会**一直离线到重启应用**。
    /// 所以这里把 <see cref="OfflinePolicy.CountPollFailures"/> 关掉，改由 <see cref="PollDevice"/>
    /// 自己数失败次数：够了只 <c>RequestOffline</c>（标离线、不停写），下一轮照常发帧，
    /// 键盘一醒就立刻回到在线（最坏等一个轮询周期 60 秒）。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Silence = TimeSpan.Zero,
        CountPollFailures = false,
        // 连续 10 次（约 10 分钟）没应答才标离线；短睡不该在界面上闪成"离线"
        MaxFailures = 10,
    };

    /// <summary>
    /// 探测会发 3~5 帧（10 / 30 / 11）。冷却 20 秒：键盘睡着时不会被 15 秒发现节拍反复戳，
    /// 醒来后最坏 20 秒内被认领。
    /// </summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(20);

    /// <summary>参与 15 秒快速发现：插上 dongle 后不必等满 60 秒才出现。</summary>
    public override bool SupportsDiscoveryProbe => true;

    /// <summary>
    /// 只认 PMO 的 dongle：VID + 厂商 usage + 33/33 长度三重校验。
    /// 0xFF60/0x61 是 QMK raw HID 的通用 usage，**不能只按 usage 认领**，否则会去抢别家 QMK 键盘。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != PmoVid) return false;
        if (info.UsagePage != 0xFF60 || info.Usage != 0x0061) return false;
        return info.InputReportLength == PmoCodec.FrameLength
            && info.OutputReportLength == PmoCodec.FrameLength;
    }

    /// <summary>dongle 的产品名是通用的 "Wireless 2.4G Dongle"，列表里换成好认的名字。</summary>
    protected override string BuildDisplayName(HidDeviceInfo info)
    {
        var product = info.Product;
        if (string.IsNullOrWhiteSpace(product)
            || product.Contains("Dongle", StringComparison.OrdinalIgnoreCase)
            || product.Contains("Receiver", StringComparison.OrdinalIgnoreCase))
            return "PMO Wave 75";
        return product;
    }

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        if (!IsWritesEnabled(SettingsKey))
        {
            Log.Once(LogLevel.Info, $"{handle.Info.DevicePath}|pmo-no-permit",
                $"PMO: 未开启写入许可，跳过主动查询: {handle.Info.DevicePath}");
            return null;
        }
        Log.ResetOnce($"{handle.Info.DevicePath}|pmo-no-permit");

        // 探测不敲醒（发现节拍本来就每 15 秒重试一次），只有 60 秒一轮的轮询才值得多发两帧
        var reading = QueryOnce(handle);
        if (reading == null)
        {
            Log.Debug($"PMO 探测无应答: {handle.Info.DevicePath}");
            return null;
        }

        Log.Info($"PMO 探测成功: 电量={reading.Value.Percent} 充电={reading.Value.Charging}");
        return ("pmo-qmk", reading.Value.Percent, reading.Value.Charging);
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;

        var reading = Query(dev.Handle);
        if (reading == null)
        {
            // 失败自己数（基类那条路会熔断停写，见 Offline 的注释）：够了只标离线，不停写
            if (++dev.ConsecutiveFailures >= Offline.MaxFailures) RequestOffline(dev);
            return null;
        }

        dev.ConsecutiveFailures = 0;
        return (reading.Value.Percent, reading.Value.Charging);
    }

    /// <summary>
    /// 取一次读数；没应答时先"敲一下"再试一次。
    /// 键盘深睡时 dongle 完全静音，这一下不保证能叫醒它，但成本只有 2 帧，能醒就省掉一整分钟的等待。
    /// </summary>
    private static (int Percent, ChargingState Charging)? Query(HidDeviceHandle handle)
    {
        var reading = QueryOnce(handle);
        if (reading != null) return reading;

        for (int i = 0; i < 2; i++)
        {
            if (!handle.WriteOutput(PmoCodec.BuildFrame(PmoCodec.CmdStartComm), TimeSpan.FromMilliseconds(300)))
                return null;
            // 有回包说明链路已经活了，不必再敲
            if (handle.ReadInput(TimeSpan.FromMilliseconds(80)) != null) break;
        }

        return QueryOnce(handle);
    }

    /// <summary>
    /// 一次完整读数：开会话 → 查电量 → 关会话。关会话放在 finally，
    /// 与官方驱动一致（会话是排他的，不关会让下一次查询或官方软件连不上）。
    /// </summary>
    private static (int Percent, ChargingState Charging)? QueryOnce(HidDeviceHandle handle)
    {
        // 开会话：失败不中止——个别固件不要求会话也能应答电量
        handle.WriteOutput(PmoCodec.BuildFrame(PmoCodec.CmdStartComm), TimeSpan.FromMilliseconds(400));

        try
        {
            // 先把会话应答读掉，避免它占满下一步的 600ms 窗口
            handle.ReadInput(TimeSpan.FromMilliseconds(200));

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!handle.WriteOutput(PmoCodec.BuildFrame(PmoCodec.CmdGetBattery), TimeSpan.FromMilliseconds(400)))
                    return null;

                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
                while (DateTime.UtcNow < deadline)
                {
                    var input = handle.ReadInput(TimeSpan.FromMilliseconds(200));
                    if (input == null) continue;

                    if (PmoCodec.TryParseBattery(input, out var percent, out var charging))
                    {
                        Log.Debug($"PMO 电量应答: {percent}% {charging} 原始={Log.Hex(input)}");
                        return (percent, charging);
                    }
                }
            }
            return null;
        }
        finally
        {
            handle.WriteOutput(PmoCodec.BuildFrame(PmoCodec.CmdStopComm), TimeSpan.FromMilliseconds(300));
        }
    }
}
