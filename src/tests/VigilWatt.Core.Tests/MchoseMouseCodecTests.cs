using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Mchose;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： MchoseMouseCodec —— 迈从鼠标四套私有协议的帧构造与应答解析。
/// 2026/10/18 补丁
///
/// 用例分两组：
///  · M*：解析/构造的字节级正确性（含"全 0 应答不得读成 255%"这类回归）
///  · R*：匹配规则回归——**键盘 dongle 41E4:2001 必须仍然返回 None**，
///        这是"不破坏原有功能"的可执行证明。
/// </summary>
public class MchoseMouseCodecTests
{
    // ── 构造工具 ──────────────────────────────────────────────────────
    private static MchoseMouseCodec.MouseMatchInput MouseInfo(
        ushort vid, ushort pid, string path = @"\\?\hid#vid_0000&pid_0000#x",
        ushort usagePage = 0xFF00, ushort usage = 0x0001, ushort featLen = 65,
        string product = "", string manufacturer = "")
        => new(vid, pid, usagePage, usage, featLen, path, product, manufacturer);

    // ══════════════════════════════════════════════════════════════════
    //  A5 / AX5 Pro
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void M1_A5查询帧_是65字节且命令位正确()
    {
        var frame = MchoseMouseCodec.BuildA5BatteryQuery();

        Assert.Equal(65, frame.Length);
        Assert.Equal(0x00, frame[0]);   // 无编号 feature 报告
        Assert.Equal(0x02, frame[3]);   // 驱动固定值
        Assert.Equal(0x02, frame[4]);
        Assert.Equal(0x83, frame[6]);   // GetBat
    }

    [Fact]
    public void M2_A5应答_7是充电态8是电量()
    {
        var response = new byte[65];
        response[7] = 0x01;   // 充电中
        response[8] = 77;

        Assert.True(MchoseMouseCodec.TryParseA5(response, out var reading));
        Assert.Equal(77, reading.Percent);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void M3_A5充满态映射为Full()
    {
        var response = new byte[65];
        response[7] = 0x02;
        response[8] = 100;

        Assert.True(MchoseMouseCodec.TryParseA5(response, out var reading));
        Assert.Equal(100, reading.Percent);
        Assert.Equal(ChargingState.Full, reading.Charging);
    }

    // ══════════════════════════════════════════════════════════════════
    //  旧 A7
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void M4_A7查询帧_首字节11其余为异或后的00()
    {
        var frame = MchoseMouseCodec.BuildA7BatteryQuery();

        Assert.Equal(64, frame.Length);
        Assert.Equal(0x11, frame[0]);          // report ID
        Assert.Equal(0x06 ^ 0xFF, frame[1]);   // 命令 0x06 取反
        Assert.Equal(0xFF, frame[2]);          // 0x00 取反
        Assert.Equal(0xFF, frame[20]);
        Assert.Equal(0x00, frame[21]);         // 补齐段**不**参与异或
    }

    [Fact]
    public void M5_A7应答_整帧异或后取payload9与10()
    {
        var response = new byte[64];
        for (int i = 0; i < response.Length; i++) response[i] = 0xFF;   // 异或后全 0
        response[0] = 0x11;                 // report ID
        response[1] = 0xF9;                 // 命令 0x06 回显
        response[11] = (byte)(55 ^ 0xFF);   // payload[9] = 电量
        response[12] = (byte)(0x00 ^ 0xFF); // payload[10] = 放电

        Assert.True(MchoseMouseCodec.TryParseA7(response, out var reading));
        Assert.Equal(55, reading.Percent);
        Assert.Equal(ChargingState.Discharging, reading.Charging);
    }

    [Fact]
    public void M5b_A7充电帧_真机原始样本()
    {
        // 2026-10-18 真机样本（插着充电线）：异或后为
        //   EE 06 53 52 10 00 04 05 07 00 00 13 01 13 …
        // 即 payload[9]=0x13=19%、payload[10]=0x01=充电中；未插线时 payload[10]=0x00。
        var response = new byte[65];
        var raw = new byte[]
        {
            0xEE, 0x06, 0x53, 0x52, 0x10, 0x00, 0x04, 0x05, 0x07, 0x00, 0x00,
            0x13, 0x01, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        for (int i = 0; i < raw.Length; i++) response[i] = (byte)(raw[i] ^ 0xFF);

        Assert.True(MchoseMouseCodec.TryParseA7(response, out var reading));
        Assert.Equal(19, reading.Percent);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void M5c_A7命令回显不对_判为脏帧()
    {
        // 设备没准备好时会吐脏缓冲，回显不是 0x06 的取反 → 必须拒掉，别读成假电量
        var response = new byte[65];
        response[0] = 0x11;
        response[1] = 0xFB;
        response[11] = (byte)(66 ^ 0xFF);

        Assert.False(MchoseMouseCodec.TryParseA7(response, out _));
    }

    [Fact]
    public void M6_A7全零应答不得被读成255()
    {
        // 设备没应答时 HidD_GetFeature 常返回全 0 缓冲：异或后是 0xFF，必须拒绝
        var response = new byte[64];

        Assert.False(MchoseMouseCodec.TryParseA7(response, out _));
    }

    // ══════════════════════════════════════════════════════════════════
    //  /gmouse 族
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void M7_gmouse查询帧_5530A50B2E010101()
    {
        var frame = MchoseMouseCodec.BuildGmouseBatteryQuery();

        Assert.Equal(65, frame.Length);
        Assert.Equal(
            "005530A50B2E010101".PadRight(130, '0'),
            Convert.ToHexString(frame));
    }

    [Fact]
    public void M8_gmouse应答_帧头在下标0()
    {
        var frame = new byte[65];
        frame[0] = 0xAA;
        frame[1] = 0x30;
        frame[8] = 88;
        frame[9] = 0x00;

        Assert.True(MchoseMouseCodec.TryParseGmouse(frame, out var reading));
        Assert.Equal(88, reading.Percent);
        Assert.Equal(ChargingState.Discharging, reading.Charging);
    }

    [Fact]
    public void M9_gmouse应答_帧头在下标1也认()
    {
        var frame = new byte[65];
        frame[0] = 0x00;   // 某些读接口带 report ID 前缀
        frame[1] = 0xAA;
        frame[2] = 0x30;
        frame[9] = 42;
        frame[10] = 0x01;  // 充电

        Assert.True(MchoseMouseCodec.TryParseGmouse(frame, out var reading));
        Assert.Equal(42, reading.Percent);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void M10_非gmouse帧_不能误判()
    {
        var frame = new byte[65];
        frame[0] = 0x13;   // 键盘 V1 推送
        frame[1] = 0x0A;

        Assert.False(MchoseMouseCodec.TryParseGmouse(frame, out _));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Hub 世代
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void M11_Hub无线应答_ilt布局_隔3字节space()
    {
        var frame = new byte[65];
        frame[4] = 10;    // command
        frame[8] = 5;     // type = 电量（无线：command 后空 3 字节）
        frame[9] = 66;    // 电量
        frame[10] = 0x10; // 高 4 位 charge=1

        Assert.True(MchoseMouseCodec.TryParseHub(frame, out var reading));
        Assert.Equal(66, reading.Percent);
        Assert.Equal(ChargingState.Charging, reading.Charging);
    }

    [Fact]
    public void M12_Hub有线应答_ilt布局_紧邻()
    {
        var frame = new byte[65];
        frame[6] = 10;    // command
        frame[7] = 5;     // type（有线：紧邻）
        frame[8] = 93;
        frame[9] = 0x20;  // 高 4 位 charge=2 → 充满

        Assert.True(MchoseMouseCodec.TryParseHub(frame, out var reading));
        Assert.Equal(93, reading.Percent);
        Assert.Equal(ChargingState.Full, reading.Charging);
    }

    [Fact]
    public void M13_Hub全零应答不得被读成电量()
    {
        Assert.False(MchoseMouseCodec.TryParseHub(new byte[65], out _));
    }

    // ══════════════════════════════════════════════════════════════════
    //  通用校验
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]     // 占位/未就绪
    [InlineData(101)]
    [InlineData(255)]
    public void M14_越界电量一律拒绝(int percent)
    {
        var response = new byte[65];
        response[7] = 0x01;
        response[8] = (byte)percent;

        Assert.False(MchoseMouseCodec.TryParseA5(response, out _));
    }

    [Fact]
    public void M15_1是合法电量_不能当成占位值丢掉()
    {
        // 与键盘 V1（≤1 一律无效）刻意不同：鼠标真能到 1%，拒掉就漏了低电量提醒
        var response = new byte[65];
        response[8] = 1;

        Assert.True(MchoseMouseCodec.TryParseA5(response, out var reading));
        Assert.Equal(1, reading.Percent);
    }

    [Theory]
    [InlineData("mouse-a5")]
    [InlineData("mouse-a7")]
    [InlineData("mouse-gmouse")]
    [InlineData("mouse-hub")]
    public void M16_协议名可往返(string name)
    {
        // 只走字符串进出：MchoseMouseFamily 是 internal，不能出现在 public 测试方法签名里
        Assert.True(MchoseMouseCodec.TryParseProtocolName(name, out var family));
        Assert.Equal(name, MchoseMouseCodec.ProtocolName(family));
    }

    [Theory]
    [InlineData("qhw")]
    [InlineData("qhw-push")]
    [InlineData("keyboard-v1")]
    [InlineData("")]
    [InlineData(null)]
    public void M17_键盘协议名不被误认为鼠标(string? name)
    {
        Assert.False(MchoseMouseCodec.TryParseProtocolName(name, out _));
    }

    // ══════════════════════════════════════════════════════════════════
    //  匹配规则回归（R = regression）
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void R1_键盘老dongle_41E4_2001必须仍判为None()
    {
        // 本机 G87：usage 0xFF02、20/20、feature 8 —— 命中它就会把键盘抢去走鼠标协议
        var keyboard = MouseInfo(0x41E4, 0x2001, usagePage: 0xFF02, usage: 0x0001,
            featLen: 8);

        Assert.Equal(MchoseMouseFamily.None, MchoseMouseCodec.Match(keyboard));
    }

    [Fact]
    public void R2_QHW新dongle_3837_3033_20字节通道仍判为None()
    {
        // 键盘 QHW 通道：20/64 字节 output，feature 为 0 → 不能当成 HubNative
        var qhw = MouseInfo(0x3837, 0x3033, usagePage: 0xFF00, usage: 0x0002,
            featLen: 0);

        Assert.Equal(MchoseMouseFamily.None, MchoseMouseCodec.Match(qhw));
    }

    [Fact]
    public void R3_其他品牌不得被认领()
    {
        Assert.Equal(MchoseMouseFamily.None,
            MchoseMouseCodec.Match(MouseInfo(0x046D, 0xC548, featLen: 65)));   // 罗技 Bolt
        Assert.Equal(MchoseMouseFamily.None,
            MchoseMouseCodec.Match(MouseInfo(0x24AE, 0x1412, featLen: 65)));   // 雷柏
        Assert.Equal(MchoseMouseFamily.None,
            MchoseMouseCodec.Match(MouseInfo(0x1D57, 0xFA65, featLen: 65)));   // 英菲克
    }

    [Fact]
    public void R4_A5需要MI02_接口1不认()
    {
        var wrongInterface = MouseInfo(0x2023, 0xF013,
            path: @"\\?\hid#vid_2023&pid_f013&mi_01&col01#x");
        var rightInterface = MouseInfo(0x2023, 0xF013,
            path: @"\\?\hid#vid_2023&pid_f013&mi_02&col01#x");

        Assert.Equal(MchoseMouseFamily.None, MchoseMouseCodec.Match(wrongInterface));
        Assert.Equal(MchoseMouseFamily.A5Pro, MchoseMouseCodec.Match(rightInterface));
    }

    [Fact]
    public void R5_A5未在PID表内的型号不认()
    {
        var unknownPid = MouseInfo(0x2023, 0x9999,
            path: @"\\?\hid#vid_2023&pid_9999&mi_02&col01#x");

        Assert.Equal(MchoseMouseFamily.None, MchoseMouseCodec.Match(unknownPid));
    }

    [Fact]
    public void R6_gmouse优先于Hub_同为3837时按usage判()
    {
        var gmouse = MouseInfo(0x3837, 0x4011, usagePage: 0xFF01, usage: 0x0010, featLen: 65);
        var hub = MouseInfo(0x3837, 0x4011, usagePage: 0xFF00, usage: 0x0002, featLen: 65);

        Assert.Equal(MchoseMouseFamily.Gmouse, MchoseMouseCodec.Match(gmouse));
        Assert.Equal(MchoseMouseFamily.HubNative, MchoseMouseCodec.Match(hub));
    }

    [Fact]
    public void R7_A7按VIDPID精确匹配()
    {
        Assert.Equal(MchoseMouseFamily.A7Legacy,
            MchoseMouseCodec.Match(MouseInfo(0x5253, 0x1021, usagePage: 0x0001, usage: 0x0002, featLen: 64)));
        Assert.Equal(MchoseMouseFamily.None,
            MchoseMouseCodec.Match(MouseInfo(0x5253, 0x1022, usagePage: 0x0001, usage: 0x0002, featLen: 64)));
    }

    [Fact]
    public void R8_A7插线后的有线PID_0010也要认()
    {
        // 真机：插上充电线后设备以 5253:0010 重新枚举一份接口，不认它充电期间设备就消失了
        Assert.Equal(MchoseMouseFamily.A7Legacy,
            MchoseMouseCodec.Match(MouseInfo(0x5253, 0x0010, usagePage: 0xFF01, usage: 0x0001, featLen: 65)));
    }
}
