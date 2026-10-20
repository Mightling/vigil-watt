using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Rapoo;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： RapooCodec —— 雷柏 2.4G 状态帧（report 0x07，19 字节）的纯解析。
/// 2026/10/08 22:25:16 xliu
/// 用例帧全部取自真机日志，其中 R2 是用户给鼠标断电重连那一下抓到的断链帧。
/// </summary>
public class RapooCodecTests
{
    /// <summary>真机（21:37:12）：鼠标正常使用中，94% / 充电标志 1 / RF=0xC8（bit7=1，RSSI=72）。</summary>
    private static readonly byte[] NormalFrame =
        Convert.FromHexString("071004800C800C015E000301001246010202C8");

    /// <summary>真机（21:38:38）：鼠标断电瞬间，RF=0x7F（bit7=0，RSSI=127）——链路断开标志。</summary>
    private static readonly byte[] LinkDownFrame =
        Convert.FromHexString("071004800C800C015E0003010012460102027F");

    /// <summary>真机（21:39:06）：重连后，充电标志由 1 变 0、电量 94→95、RF=0xD2（bit7=1）。</summary>
    private static readonly byte[] ReconnectFrame =
        Convert.FromHexString("071004800C800C005F000301001246010202D2");

    /// <summary>复制一帧并改掉指定下标，用于构造边界用例。</summary>
    private static byte[] WithByte(byte[] frame, int index, byte value)
    {
        var copy = (byte[])frame.Clone();
        copy[index] = value;
        return copy;
    }

    [Fact]
    public void R1_真机正常帧解析出电量与链路在位()
    {
        var s = RapooCodec.Parse(NormalFrame);
        Assert.Equal(RapooFrameKind.Status, s.Kind);
        Assert.Equal(94, s.Percent);
        Assert.Equal(ChargingState.Discharging, s.Charging);
        Assert.Equal((byte)0x01, s.ChargeFlag);
        Assert.Equal("2.4G", s.Link);
        Assert.False(s.LinkDown);
        Assert.Equal(72, s.RfByte & 0x7F);
    }

    [Fact]
    public void R2_回归_真机断链帧必须被识别为链路断开()
    {
        var s = RapooCodec.Parse(LinkDownFrame);
        Assert.Equal(RapooFrameKind.Status, s.Kind);
        Assert.True(s.LinkDown);                 // ← 这就是"设备自己说断链了"
        Assert.Equal((byte)0x7F, s.RfByte);      // bit7=0 且 RSSI=127（无信号哨兵）
        Assert.Equal(94, s.Percent);             // 断链帧上的电量仍然可读
    }

    [Fact]
    public void R3_真机重连帧撤销断链()
    {
        var s = RapooCodec.Parse(ReconnectFrame);
        Assert.Equal(RapooFrameKind.Status, s.Kind);
        Assert.False(s.LinkDown);
        Assert.Equal(95, s.Percent);
        Assert.Equal((byte)0x00, s.ChargeFlag);  // 实测断电重连时充电标志由 1 翻 0
    }

    [Fact]
    public void R4_非0x07帧不算状态帧()
    {
        var other = WithByte(NormalFrame, 0, 0x04);
        Assert.Equal(RapooFrameKind.NotStatus, RapooCodec.Parse(other).Kind);
    }

    [Fact]
    public void R5_长度不足不算状态帧()
    {
        var shortFrame = new byte[RapooCodec.StatusReportLength - 1];
        shortFrame[0] = RapooCodec.StatusReportId;
        Assert.Equal(RapooFrameKind.NotStatus, RapooCodec.Parse(shortFrame).Kind);
    }

    [Fact]
    public void R6_兼容带reportID前缀的布局()
    {
        var prefixed = new byte[20];
        NormalFrame.CopyTo(prefixed, 1);
        var s = RapooCodec.Parse(prefixed);
        Assert.Equal(RapooFrameKind.Status, s.Kind);
        Assert.Equal(94, s.Percent);
    }

    [Fact]
    public void R7_电量归零判为无效但链路位仍可信()
    {
        var zero = WithByte(LinkDownFrame, 8, 0x00);
        var s = RapooCodec.Parse(zero);
        Assert.Equal(RapooFrameKind.StatusInvalidPercent, s.Kind);
        Assert.True(s.LinkDown);   // 越界帧上的链路位照样可信
    }

    [Fact]
    public void R8_电量越界判为无效()
    {
        var over = WithByte(NormalFrame, 8, 101);
        Assert.Equal(RapooFrameKind.StatusInvalidPercent, RapooCodec.Parse(over).Kind);
    }

    [Theory]
    [InlineData(0x00, true)]    // bit7=0 → 断链
    [InlineData(0x7F, true)]    // 真机观测到的断链哨兵
    [InlineData(0x80, false)]   // bit7=1，RSSI=0
    [InlineData(0xC8, false)]   // bit7=1，RSSI=72
    [InlineData(0xFF, false)]   // bit7=1，RSSI=127
    public void R9_链路位只看bit7(byte rfByte, bool expectedLinkDown)
        => Assert.Equal(expectedLinkDown, RapooCodec.IsLinkDown(rfByte));
}
