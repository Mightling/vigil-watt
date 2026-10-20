using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Pmo;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： PmoCodec —— PMO Wave 75 / 2.4G dongle 的帧构造与应答解析。
/// 2026/10/18 新增
///
/// 用例 P2 用的是**真机原始帧**（36B0:3002，2026-10-18 抓于本机），
/// 其余用例锁住边界：命令不符、电量占位值、带/不带 report ID 两种布局。
/// </summary>
public class PmoCodecTests
{
    /// <summary>真机应答（含 report ID 首字节）：00 AA 30 00 00 18 00 00 55 40 00 …</summary>
    private static byte[] RealBatteryFrame()
    {
        var frame = new byte[PmoCodec.FrameLength];
        frame[0] = 0x00;   // report ID
        frame[1] = 0xAA;   // tag
        frame[2] = 0x30;   // cmd
        frame[5] = 0x18;   // 24
        frame[8] = 0x55;   // 状态字节（两帧都出现，非电量）
        frame[9] = 0x40;   // 电量 64%
        frame[10] = 0x00;  // 放电
        return frame;
    }

    [Fact]
    public void P1_请求帧是33字节且首字节为reportId()
    {
        var frame = PmoCodec.BuildFrame(PmoCodec.CmdGetBattery);

        Assert.Equal(33, frame.Length);
        Assert.Equal(0x00, frame[0]);   // report ID
        Assert.Equal(0xAA, frame[1]);   // tag
        Assert.Equal(0x30, frame[2]);   // cmd
        Assert.Equal(0x00, frame[3]);   // 偏移低字节
        Assert.Equal(0x00, frame[4]);   // 偏移高字节
        Assert.Equal(24, frame[5]);     // 读取长度
        Assert.Equal("00AA300000180000", Convert.ToHexString(frame)[..16]);
    }

    [Fact]
    public void P2_真机帧解析出64percent放电()
    {
        Assert.True(PmoCodec.TryParseBattery(RealBatteryFrame(), out int percent, out var charging));
        Assert.Equal(64, percent);
        Assert.Equal(ChargingState.Discharging, charging);
    }

    [Fact]
    public void P3_不带reportId的纯数据层也能解析()
    {
        var withId = RealBatteryFrame();
        var dataOnly = new byte[PmoCodec.DataLength];
        Array.Copy(withId, 1, dataOnly, 0, dataOnly.Length);   // 去掉首字节

        Assert.True(PmoCodec.TryParseBattery(dataOnly, out int percent, out _));
        Assert.Equal(64, percent);
    }

    [Theory]
    [InlineData(0)]     // 未就绪 / 占位
    [InlineData(101)]
    [InlineData(255)]
    public void P4_越界电量一律拒绝(int bad)
    {
        var frame = RealBatteryFrame();
        frame[9] = (byte)bad;
        Assert.False(PmoCodec.TryParseBattery(frame, out _, out _));
    }

    [Fact]
    public void P5_命令不匹配的帧不被当成电量()
    {
        // 会话应答（cmd=0x10）里 [9] 恰好是 0x00，若按 0x30 解析会读出 0%，必须靠 cmd 区分
        var frame = RealBatteryFrame();
        frame[2] = PmoCodec.CmdStartComm;
        Assert.False(PmoCodec.TryParseBattery(frame, out _, out _));
    }

    [Fact]
    public void P6_充电标志映射()
    {
        var frame = RealBatteryFrame();

        frame[10] = 0x01;
        Assert.True(PmoCodec.TryParseBattery(frame, out _, out var charging));
        Assert.Equal(ChargingState.Charging, charging);

        frame[10] = 0x02;
        Assert.True(PmoCodec.TryParseBattery(frame, out _, out var full));
        Assert.Equal(ChargingState.Full, full);

        frame[10] = 0x07;
        Assert.True(PmoCodec.TryParseBattery(frame, out _, out var unknown));
        Assert.Equal(ChargingState.Unknown, unknown);
    }

    [Fact]
    public void P7_满电但标志仍是充电时归为Full()
    {
        var frame = RealBatteryFrame();
        frame[9] = 100;
        frame[10] = 0x01;

        Assert.True(PmoCodec.TryParseBattery(frame, out int percent, out var charging));
        Assert.Equal(100, percent);
        Assert.Equal(ChargingState.Full, charging);
    }

    [Fact]
    public void P8_设备信息帧里的VID可自证偏移()
    {
        // 真机会话应答：00 AA 10 00 00 18 00 00 55 00 B0 36 …  → payload[1..2] = B0 36 = 0x36B0
        var frame = new byte[PmoCodec.FrameLength];
        frame[1] = 0xAA;
        frame[2] = PmoCodec.CmdGetDeviceInfo;
        frame[5] = 0x18;
        frame[8] = 0x55;
        frame[9] = 0x00;
        frame[10] = 0xB0;
        frame[11] = 0x36;

        Assert.True(PmoCodec.TryParseVid(frame, out ushort vid));
        Assert.Equal((ushort)0x36B0, vid);
    }

    [Fact]
    public void P9_短帧不越界()
    {
        Assert.False(PmoCodec.TryParseBattery(new byte[5], out _, out _));
        Assert.False(PmoCodec.TryParseBattery(Array.Empty<byte>(), out _, out _));
    }
}
