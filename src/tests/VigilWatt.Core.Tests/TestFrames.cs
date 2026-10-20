namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 真机抓到的原始帧（迈从 G87 键盘，2026-10-06 采集），供解析用例做回归。
/// 2026/10/06 15:53:36 xliu
/// 全部是 20 字节、report 0x13 的 V1 帧，帧尾校验和 = 前 19 字节之和。
/// </summary>
internal static class TestFrames
{
    internal static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// <summary>键盘休眠瞬间的状态通知帧（旧代码把它的 payload 0x01 当成"电量 1%"）。</summary>
    internal static readonly byte[] SleepNotify =
        Hex("13 0A 01 00 04 02 01 00 00 00 00 00 00 00 00 00 00 00 00 25");

    /// <summary>键盘 66% 时的电量推送帧（类型位 0x05，[6]=电量 0x42=66，[7]=充电标志）。</summary>
    internal static readonly byte[] Push66 =
        Hex("13 0A 01 00 04 05 42 01 00 00 00 00 00 00 00 00 00 00 00 6A");

    /// <summary>66% 的 4A 查询响应。</summary>
    internal static readonly byte[] Response66 =
        Hex("13 4A 01 00 02 42 01 00 00 00 00 00 00 00 00 00 00 00 00 A3");

    /// <summary>开会话 ACK。</summary>
    internal static readonly byte[] SessionAck =
        Hex("13 05 01 00 0A 03 00 00 00 02 1C 01 00 00 00 00 00 00 00 45");
}
