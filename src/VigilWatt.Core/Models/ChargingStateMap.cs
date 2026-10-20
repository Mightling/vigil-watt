namespace VigilWatt.Core.Models;

/// <summary>
/// 功 能： 充电状态的通用兜底映射（D8）。
/// 2026/10/10 17:44:33 xliu
/// 各插件协议能拿到的信息不同——迈从/北通只有"是否充电"标志， 罗技是状态码表——但"没在充电且电量已满 ⇒ 已充满"这条收尾规则是共用的，之前每个插件各写一遍。
/// </summary>
public static class ChargingStateMap
{
    /// <summary>
    /// 由"是否充电"标志 + 电量推断状态：充电中优先；否则 100% 视为已充满；其余按放电。
    /// 注意"已充满"不算"充电中"（见 <see cref="DeviceStatusFormatter.IsCharging"/>）。
    /// </summary>
    public static ChargingState FromChargingFlag(bool charging, int percent)
    {
        if (charging) return ChargingState.Charging;
        if (percent >= 100) return ChargingState.Full;
        return ChargingState.Discharging;
    }
}
