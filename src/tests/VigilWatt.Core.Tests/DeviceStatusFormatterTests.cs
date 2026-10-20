using VigilWatt.Core.Models;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： DeviceStatusFormatter —— 设备列表页 / 桌面小组件 / 托盘菜单三处共用的状态判定与文案口径（CR-24 / D2）。
/// 2026/10/10 17:44:33 xliu
/// 之前这三处各写一份"充电标志怎么算、没读数显示什么"，改一处漏两处；这里锁住口径。
/// </summary>
public class DeviceStatusFormatterTests
{
    [Theory]
    [InlineData(ChargingState.Charging, true)]
    [InlineData(ChargingState.Full, false)]        // "已充满"不算"充电中"
    [InlineData(ChargingState.Discharging, false)]
    [InlineData(ChargingState.Unknown, false)]
    public void F1_充电中判定_不含已充满(ChargingState state, bool expected)
    {
        Assert.Equal(expected, DeviceStatusFormatter.IsCharging(state));
    }

    [Theory]
    [InlineData(ChargingState.Charging, true)]
    [InlineData(ChargingState.Full, true)]
    [InlineData(ChargingState.Discharging, false)]
    [InlineData(ChargingState.Unknown, false)]
    public void F2_在充电口上判定_含已充满(ChargingState state, bool expected)
    {
        Assert.Equal(expected, DeviceStatusFormatter.IsChargingOrFull(state));
    }

    [Fact]
    public void F3_有读数时电量文本带百分号()
    {
        Assert.Equal("80%", DeviceStatusFormatter.PercentText(80));
        Assert.Equal("0%", DeviceStatusFormatter.PercentText(0));
    }

    [Fact]
    public void F4_无读数时用各界面自己的占位符()
    {
        Assert.Equal("--", DeviceStatusFormatter.PercentText(null));            // 设备列表页
        Assert.Equal("?", DeviceStatusFormatter.PercentText(null, "?"));        // 托盘菜单
    }

    [Theory]
    [InlineData(ChargingState.Charging, " ⚡充电中")]
    [InlineData(ChargingState.Full, " 已充满")]
    [InlineData(ChargingState.Discharging, "")]
    [InlineData(ChargingState.Unknown, "")]
    public void F5_充电后缀文案(ChargingState state, string expected)
    {
        Assert.Equal(expected, DeviceStatusFormatter.ChargeSuffix(state));
    }
}
