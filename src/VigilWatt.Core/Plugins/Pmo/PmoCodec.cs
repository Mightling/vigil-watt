using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Pmo;

/// <summary>
/// 功 能： PMO（PMOKEY）Wave 75 键盘 / 2.4G dongle 的私有协议**纯帧编解码**。
/// 2026/10/18 新增
///
/// 协议来自官方网页驱动 pmohub.cn 的前端 JS（<c>QhwCmdQueueItem</c> 同代的 <c>M()</c> 传输层 +
/// <c>_getBatteryStatusInternal()</c>），并在本机 36B0:3002 dongle 上真机验证。
///
/// 帧（WebHID 数据层 32 字节）：
/// <code>
///   [0]=0xAA  [1]=命令  [2..3]=偏移  [4]=长度(24)  [5..]=数据
/// </code>
/// Windows 的 WriteFile 缓冲首字节是 report ID，所以**缓冲长度 33、数据层整体右移一位**：
/// <code>
///   buf[0]=0x00  buf[1]=0xAA  buf[2]=命令  buf[5]=24
/// </code>
/// 应答同构，数据层从下标 8 开始是 payload：<c>[8]</c>=电量 %、<c>[9]</c>=充电标志。
/// 验证样本：<c>00 AA 30 00 00 18 00 00 55 40 00 …</c> → 64% 放电；
/// 会话帧里 payload[1..2] = <c>B0 36</c> = VID 0x36B0，可自证偏移正确。
/// </summary>
internal static class PmoCodec
{
    /// <summary>帧标记字节。</summary>
    public const byte Tag = 0xAA;

    /// <summary>Windows 写入缓冲首字节的 report ID（该接口用无编号报告）。</summary>
    public const byte ReportId = 0x00;

    /// <summary>开会话（官方驱动在每次操作前后成对使用）。</summary>
    public const byte CmdStartComm = 0x10;

    /// <summary>关会话。</summary>
    public const byte CmdStopComm = 0x11;

    /// <summary>读设备信息（payload[1..2] = VID 小端）。</summary>
    public const byte CmdGetDeviceInfo = 0x12;

    /// <summary>查电量。</summary>
    public const byte CmdGetBattery = 0x30;

    /// <summary>数据层报告长度（不含 report ID）。</summary>
    public const int DataLength = 32;

    /// <summary>Windows 侧收发缓冲长度（含 report ID 首字节），实测 in=33 / out=33。</summary>
    public const int FrameLength = DataLength + 1;

    /// <summary>请求里声明的读取长度。</summary>
    public const byte RequestLength = 24;

    /// <summary>应答数据层中 payload 的起始下标（8 字节固定头）。</summary>
    public const int PayloadOffset = 8;

    /// <summary>构造一条 33 字节的 Windows 侧请求帧。</summary>
    public static byte[] BuildFrame(byte command)
    {
        var frame = new byte[FrameLength];
        frame[0] = ReportId;
        frame[1] = Tag;
        frame[2] = command;
        // [3..4] = 偏移（0），[5] = 长度
        frame[5] = RequestLength;
        return frame;
    }

    /// <summary>
    /// 解析电量应答。
    /// <paramref name="frame"/> 可以是"含 report ID"（33 字节）或"纯数据层"（32 字节）两种形态，
    /// 两种都试一遍——不同读取路径给的缓冲不一样。
    /// </summary>
    public static bool TryParseBattery(byte[] frame, out int percent, out ChargingState charging)
    {
        percent = 0;
        charging = ChargingState.Unknown;
        if (frame == null) return false;

        // 两种起点：0 = 纯数据层；1 = 带 report ID 前缀
        foreach (int start in new[] { 0, 1 })
        {
            if (frame.Length < start + PayloadOffset + 2) continue;
            if (frame[start] != Tag || frame[start + 1] != CmdGetBattery) continue;

            int pct = frame[start + PayloadOffset];
            int chg = frame[start + PayloadOffset + 1];

            // 0 = 未就绪/占位；>100 不可能是百分比
            if (pct is < 1 or > 100) return false;

            percent = pct;
            charging = chg switch
            {
                0 => ChargingState.Discharging,
                1 => ChargingState.Charging,
                2 => ChargingState.Full,
                _ => ChargingState.Unknown,
            };
            // 满电但标志还没更新时的兜底（与官方驱动"充满按 100%"的处理一致）
            if (charging == ChargingState.Charging && pct >= 100) charging = ChargingState.Full;
            return true;
        }
        return false;
    }

    /// <summary>从设备信息应答里取 VID（真实小端），用于校验帧对齐；取不到返回 false。</summary>
    public static bool TryParseVid(byte[] frame, out ushort vid)
    {
        vid = 0;
        foreach (int start in new[] { 0, 1 })
        {
            if (frame.Length < start + PayloadOffset + 3) continue;
            if (frame[start] != Tag || frame[start + 1] != CmdGetDeviceInfo) continue;
            vid = (ushort)(frame[start + PayloadOffset + 1] | (frame[start + PayloadOffset + 2] << 8));
            return vid != 0;
        }
        return false;
    }
}
