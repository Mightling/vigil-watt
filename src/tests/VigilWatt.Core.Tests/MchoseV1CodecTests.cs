using VigilWatt.Core.Plugins.Mchose;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： MchoseV1Codec —— 迈从键盘 V1（report 0x13）帧编解码。
/// 2026/10/06 15:53:36 xliu
/// 用例全部使用真机原始帧 （见 <see cref="TestFrames"/>），其中 B3 是"休眠帧被误读成电量 1%"这个 bug 的回归用例。
/// </summary>
public class MchoseV1CodecTests
{
    [Fact]
    public void B1_真机帧校验和都自洽()
    {
        Assert.True(MchoseV1Codec.IsChecksumValid(TestFrames.SleepNotify));
        Assert.True(MchoseV1Codec.IsChecksumValid(TestFrames.Push66));
        Assert.True(MchoseV1Codec.IsChecksumValid(TestFrames.Response66));
        Assert.True(MchoseV1Codec.IsChecksumValid(TestFrames.SessionAck));
    }

    [Fact]
    public void B2_构造帧与真机一致()
    {
        byte[] built = MchoseV1Codec.BuildFrame(MchoseV1Codec.BatteryQueryCmd, 0x01);
        Assert.Equal(20, built.Length);
        Assert.Equal("134A01" + new string('0', 32) + "5E", Convert.ToHexString(built));
        Assert.True(MchoseV1Codec.IsChecksumValid(built));
    }

    [Fact]
    public void B3_回归_休眠通知帧不能再被读成电量1()
    {
        var push = MchoseV1Codec.ParsePush(TestFrames.SleepNotify);
        Assert.Equal(V1PushKind.StateNotify, push.Kind);
        Assert.Equal((byte)0x02, push.Type);
        Assert.Equal((byte)0x01, push.Payload); // 这个 0x01 不是电量
        Assert.Equal(0, push.Percent);          // 不得产出电量读数
    }

    [Fact]
    public void B4_正常电量推送照常解析()
    {
        var push = MchoseV1Codec.ParsePush(TestFrames.Push66);
        Assert.Equal(V1PushKind.Battery, push.Kind);
        Assert.Equal(66, push.Percent);
        Assert.Equal((byte)0x01, push.ChargeFlag);
    }

    [Fact]
    public void B5_四A电量响应照常解析()
    {
        var kind = MchoseV1Codec.ParseBatteryResponse(TestFrames.Response66, out int pct, out byte flag);
        Assert.Equal(V1ResponseKind.Battery, kind);
        Assert.Equal(66, pct);
        Assert.Equal((byte)0x01, flag);
    }

    [Fact]
    public void B6_其他帧不会被误判()
    {
        Assert.Equal(V1PushKind.NotPush, MchoseV1Codec.ParsePush(TestFrames.SessionAck).Kind);
        Assert.Equal(V1ResponseKind.NotResponse,
            MchoseV1Codec.ParseBatteryResponse(TestFrames.SessionAck, out _, out _));
        Assert.Equal(V1PushKind.NotPush, MchoseV1Codec.ParsePush(new byte[] { 0x13, 0x0A }).Kind); // 太短
    }

    [Fact]
    public void B7_兼容ReadInput带reportId前缀_整体右移一字节()
    {
        byte[] shifted = new byte[TestFrames.Push66.Length + 1];
        Array.Copy(TestFrames.Push66, 0, shifted, 1, TestFrames.Push66.Length);

        var push = MchoseV1Codec.ParsePush(shifted);
        Assert.Equal(66, push.Percent);
        Assert.Equal(V1PushKind.Battery, push.Kind);
    }

    [Fact]
    public void B8_类型0x05但电量越界_判BatteryInvalid()
    {
        byte[] bad = TestFrames.Hex("13 0A 01 00 04 05 00 01 00 00 00 00 00 00 00 00 00 00 00 25");
        Assert.Equal(V1PushKind.BatteryInvalid, MchoseV1Codec.ParsePush(bad).Kind);
    }

    [Fact]
    public void B9_未知类型位_判Other并保留类型位待校准()
    {
        byte[] unknown = TestFrames.Hex("13 0A 01 00 04 07 42 01 00 00 00 00 00 00 00 00 00 00 00 68");
        var push = MchoseV1Codec.ParsePush(unknown);
        Assert.Equal(V1PushKind.Other, push.Kind);
        Assert.Equal((byte)0x07, push.Type);
    }
}
