using VigilWatt.Core.Power;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： <see cref="SystemPowerReader.Interpret"/> 的翻译逻辑测试。
/// 2026/10/08 18:04:41 xliu
/// 必须单测这一段：它要区分三种"读不到电量"语义完全不同的情况——台式机根本没电池 （BatteryFlag=128）与系统状态未知（255）都不该出现在列表里，而有电池但百分比读不到 （BatteryLifePercent=255）仍算"有电池"，让界面显示降级文案而不是整行消失。
/// </summary>
public class SystemPowerReaderTests
{
    // GetSystemPowerStatus 的真实取值：0 = 电池供电，1 = 交流电，255 = 未知
    private const byte AcOff = 0;
    private const byte AcOn = 1;

    [Fact]
    public void 接电且正常电量_识别为有电池且交流在线()
    {
        var s = SystemPowerReader.Interpret(AcOn, batteryFlag: 1, batteryLifePercent: 85);

        Assert.True(s.HasBattery);
        Assert.True(s.AcOnline);
        Assert.Equal(85, s.Percent);
    }

    [Fact]
    public void 电池供电_识别为有电池且交流离线()
    {
        var s = SystemPowerReader.Interpret(AcOff, batteryFlag: 1, batteryLifePercent: 62);

        Assert.True(s.HasBattery);
        Assert.False(s.AcOnline);
        Assert.Equal(62, s.Percent);
    }

    [Fact]
    public void 台式机无电池标志_判为无电池_不产生电量()
    {
        // 128 = 无系统电池。这正是台式机场景，必须完全不出现在列表里。
        var s = SystemPowerReader.Interpret(AcOn, batteryFlag: 128, batteryLifePercent: 255);

        Assert.False(s.HasBattery);
        Assert.Null(s.Percent);
        Assert.True(s.AcOnline);   // 接电状态本身仍然有效
    }

    [Fact]
    public void 状态未知标志_判为无电池_不瞎报电量()
    {
        // 255 = 状态未知：不能拿它当 0% 电池，否则会误报"电量 0%"
        var s = SystemPowerReader.Interpret(AcOff, batteryFlag: 255, batteryLifePercent: 255);

        Assert.False(s.HasBattery);
        Assert.Null(s.Percent);
    }

    [Fact]
    public void 有电池但百分比未知_仍算有电池_电量留空()
    {
        // 这是与"无电池"最关键的区别：笔记本不该因为一次读不到百分比就假装没有电池
        var s = SystemPowerReader.Interpret(AcOn, batteryFlag: 1, batteryLifePercent: 255);

        Assert.True(s.HasBattery);
        Assert.Null(s.Percent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public void 合法百分比原样透传(byte percent)
    {
        var s = SystemPowerReader.Interpret(AcOff, batteryFlag: 1, batteryLifePercent: percent);

        Assert.Equal(percent, s.Percent);
    }

    [Fact]
    public void Unavailable快照_无电池无电量()
    {
        var s = SystemPowerSnapshot.Unavailable;

        Assert.False(s.HasBattery);
        Assert.Null(s.Percent);
        Assert.False(s.AcOnline);
    }
}
