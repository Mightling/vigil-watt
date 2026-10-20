using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins;

/// <summary>
/// 功 能： 2.4G 品牌插件接口。
/// 2026/10/06 15:53:36 xliu
/// 每个品牌一套私有协议：DeviceManager 每轮扫描喂给它 HID 接口列表，插件认领匹配设备并轮询电量；实现类由反射自动发现（新增品牌 = 加一个类文件）。
/// </summary>
public interface IBatteryPlugin
{
    /// <summary>品牌标识，与 AppSettings.PluginEnabled 的 key 对应。</summary>
    string Name { get; }

    string DisplayName { get; }

    /// <summary>本插件处理的设备类型（UI 选图标用）。拿不准就 Unknown，UI 会退回按名字猜。</summary>
    DeviceKind Kind { get; }

    /// <summary>是否允许 DeviceManager 用更快的节拍（15 秒）额外跑一轮"只认领、不轮询"的发现。**只有纯被动零写入的插件可以返回 true**（如雷柏鼠标）；会写帧的插件用 <see cref="SupportsDiscoveryProbe"/>，别用这个。</summary>
    bool SupportsFastDiscovery { get; }

    /// <summary>
    /// 探测要写帧、但仍希望参与 15 秒发现节拍的插件（如迈从键盘：否则唤醒/开机后最坏要等 60 秒才被认领）。
    /// 与 <see cref="SupportsFastDiscovery"/> 的区别与**安全前提**：这条路径真的会发探测帧，**写入频率必须由插件自己的 <c>ProbeCooldown</c> 兜住**（设成明显小于 15 秒），最坏写入 ≈ 3 帧/次探测 × 4 次/分钟；认领成功后 Attach 对已知设备直接返回、零写入。
    /// 默认 false；不确定就别开。
    /// </summary>
    bool SupportsDiscoveryProbe { get; }

    /// <summary>从扫描结果中认领属于自己的设备（幂等：新设备补上，消失的设备标记离线并释放认领）。
    /// 认领前必须经 claims.TryClaim，防止与其他插件抢同一接口。返回认领的设备。
    /// 各插件可并行 await；同一插件内候选接口仍串行探测。</summary>
    Task<IReadOnlyList<DeviceStatus>> AttachAsync(IReadOnlyList<HidDeviceInfo> availableDevices, DeviceClaimRegistry claims);

    /// <summary>轮询一轮电量，返回本次有更新的设备状态。</summary>
    IReadOnlyList<DeviceStatus> Poll();

    /// <summary>插件在轮询之外主动推送的状态更新（如罗技 HID++ 电量/充电自发通知）；不推送可永不触发，DeviceManager 订阅后走与 Poll 相同的 Upsert。</summary>
    event Action<DeviceStatus>? StatusPushed;

    /// <summary>释放打开的句柄并释放全部认领（插件被禁用或退出时调用）。</summary>
    void Detach();
}
