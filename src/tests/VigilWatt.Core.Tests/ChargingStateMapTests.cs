using VigilWatt.Core.Models;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： ChargingStateMap —— 迈从 / 北通共用的"充电标志 + 电量 → 状态"收尾规则（D8）。
/// 2026/10/10 17:44:33 xliu
/// 之前两个插件各写一份，改一处漏一处。
/// </summary>
public class ChargingStateMapTests
{
    [Fact]
    public void M1_标志为充电中时优先_不看电量()
    {
        Assert.Equal(ChargingState.Charging, ChargingStateMap.FromChargingFlag(charging: true, percent: 35));
        Assert.Equal(ChargingState.Charging, ChargingStateMap.FromChargingFlag(charging: true, percent: 100));
    }

    [Fact]
    public void M2_未充电且满电记为已充满()
    {
        Assert.Equal(ChargingState.Full, ChargingStateMap.FromChargingFlag(charging: false, percent: 100));
        Assert.Equal(ChargingState.Full, ChargingStateMap.FromChargingFlag(charging: false, percent: 101));
    }

    [Fact]
    public void M3_未充电且未满记为放电()
    {
        Assert.Equal(ChargingState.Discharging, ChargingStateMap.FromChargingFlag(charging: false, percent: 99));
        Assert.Equal(ChargingState.Discharging, ChargingStateMap.FromChargingFlag(charging: false, percent: 0));
    }
}
