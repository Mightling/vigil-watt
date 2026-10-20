using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Power;

/// <summary>
/// 功 能： 本机电源（系统电池）监视器：仿 <see cref="Ble.BluetoothClassicWatcher"/> 的事件式数据源（不实现 <see cref="Plugins.IBatteryPlugin"/>——系统电量无接口可认领），由 <see cref="Devices.DeviceManager"/> 直接订阅。
/// 2026/10/08 18:04:41 xliu
/// 详见 docs/project/code-flow.md §3.1。
/// </summary>
public sealed class SystemBatteryWatcher : IDisposable
{
    /// <summary>本机电源在设备体系里的稳定 Id。</summary>
    public const string DeviceId = "sys:battery";

    /// <summary>充满判定：接电状态下读到 100% 就当作已充满。</summary>
    private const int FullPercent = 100;

    private readonly object _sync = new();
    private DeviceStatus? _status;
    private bool _started;

    /// <summary>状态有实质变化时推送（新增/上线/电量变/充电标志变）。</summary>
    public event Action<DeviceStatus>? DeviceUpdated;

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        Log.Info("系统电量监视器已启动");
    }

    /// <summary>读一次并推送变化（轮询尾部调用，与 <c>_classic.RefreshBattery()</c> 并排）；冷却由外部轮询周期保证，不做节流。</summary>
    public void Refresh()
    {
        lock (_sync)
        {
            if (!_started) return;
        }

        var snapshot = SystemPowerReader.Read();
        Apply(snapshot);
    }

    /// <summary>停止监控并撤下设备条目（用户关掉开关时调用）：DeviceManager 按 <see cref="DeviceId"/> 撤除条目，这里只把自己标记为"不再推送"，否则 Refresh 会把条目加回来。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _started = false;
        }
        Log.Info("系统电量监视器已停止");
    }

    private void Apply(SystemPowerSnapshot snapshot)
    {
        // 无电池（台式机 / 电池被禁用）：完全不进列表。
        // 用户明确要的是"检测不到就不显示"，不要占位行。
        if (!snapshot.HasBattery)
        {
            if (_status != null)
            {
                _status.IsOnline = false;
                _status.LastUpdated = DateTimeOffset.Now;
                var offline = _status;
                _status = null;
                DeviceUpdated?.Invoke(offline);
            }
            return;
        }

        bool isNew = _status == null;
        var status = _status ??= new DeviceStatus
        {
            Id = DeviceId,
            Name = "本机电池",
            Source = DeviceSource.System,
            Kind = DeviceKind.Computer,
            Protocol = "系统电源 (GetSystemPowerStatus)",
        };

        var charging = DeriveCharging(snapshot);

        bool changed = isNew
                    || status.BatteryPercent != snapshot.Percent
                    || status.Charging != charging
                    || !status.IsOnline;

        // 无电池分支刚判断过，所以这里 Percent 为 null 只可能是"有电池但读数未知"，
        // 该情况保留上一次的百分比，不要用 null 覆盖掉已有读数。
        if (snapshot.Percent.HasValue) status.BatteryPercent = snapshot.Percent;
        status.Charging = charging;
        status.IsOnline = true;
        status.LastUpdated = DateTimeOffset.Now;

        if (changed)
        {
            if (isNew)
                Log.Info($"系统电量已接入: {status.DisplayName} {status.BatteryPercent?.ToString() ?? "--"}% {status.Charging}");
            DeviceUpdated?.Invoke(status);
        }
    }

    /// <summary>接电+满电=已充满；接电未满=充电中；未接电=放电中。抽成 internal 纯函数以便单测（真机没法随时切这些组合）。</summary>
    internal static ChargingState DeriveCharging(SystemPowerSnapshot snapshot)
    {
        if (!snapshot.AcOnline)
            return snapshot.Percent is null ? ChargingState.Unknown : ChargingState.Discharging;

        if (snapshot.Percent == FullPercent) return ChargingState.Full;
        return snapshot.Percent is null ? ChargingState.Unknown : ChargingState.Charging;
    }

    /// <summary>当前快照（测试/诊断用）。</summary>
    public DeviceStatus? Current
    {
        get
        {
            lock (_sync) return _status;
        }
    }

    public void Dispose() => Stop();
}
