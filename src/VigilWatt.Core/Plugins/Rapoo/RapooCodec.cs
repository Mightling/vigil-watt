using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Rapoo;

/// <summary>0x07 状态帧的解析结果类型。</summary>
internal enum RapooFrameKind
{
    /// <summary>不是 0x07 状态帧（其余字段无意义）。</summary>
    NotStatus,
    Status,
    /// <summary>状态帧但电量越界（0 / &gt;100，实测为休眠瞬态）：不能用来更新读数，但链路位仍然可信。</summary>
    StatusInvalidPercent,
}

/// <summary>0x07 状态帧的解析结果。Kind 决定其余字段是否有意义。</summary>
internal readonly record struct RapooStatus(
    RapooFrameKind Kind, int Percent, ChargingState Charging,
    byte ChargeFlag, byte RfByte, string Link)
{
    /// <summary><c>[18].bit7</c> = 0 ⟺ RF 链路断开。与电量无关，越界帧上同样可信。</summary>
    public bool LinkDown => RapooCodec.IsLinkDown(RfByte);
}

/// <summary>
/// 功 能： 雷柏 2.4G 状态帧（report 0x07，19 字节）的**纯帧解析**，不碰句柄、不写日志，便于单测。
/// 2026/10/08 22:25:16 xliu
/// 帧布局见 docs/hardware/protocols.md §3。
/// </summary>
internal static class RapooCodec
{
    public const byte StatusReportId = 0x07;
    public const int StatusReportLength = 19;

    /// <summary><c>[18].bit7</c> = "RF 已连接"（协议定义）。bit7=0 ⟺ dongle 与鼠标的 2.4G 链路断开。</summary>
    public static bool IsLinkDown(byte rfByte) => (rfByte & 0x80) == 0;

    /// <summary>
    /// 解析一帧。<see cref="RapooStatus.Kind"/> 为 <see cref="RapooFrameKind.NotStatus"/> 时说明
    /// 这不是 0x07 状态帧（其余字段无意义）。
    /// </summary>
    public static RapooStatus Parse(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + StatusReportLength) continue;
            if (input[off] != StatusReportId) continue;

            int linkRaw = input[off + 1] & 0x0F;
            byte chargeFlag = input[off + 7];
            byte pct = input[off + 8];
            byte rfByte = input[off + 18];
            string link = linkRaw switch { 0 => "2.4G", 2 => "有线", _ => $"link{linkRaw}" };

            if (pct is 0 or > 100)
                return new RapooStatus(RapooFrameKind.StatusInvalidPercent, pct,
                    ChargingState.Unknown, chargeFlag, rfByte, link);

            // [7] 充电标志实测：0x01=放电、0x02=充电；充满态的值未采样，100% 一律判充满兜底
            var charging = chargeFlag == 0x02 ? ChargingState.Charging
                : pct >= 100 ? ChargingState.Full
                : ChargingState.Discharging;
            return new RapooStatus(RapooFrameKind.Status, pct, charging, chargeFlag, rfByte, link);
        }
        return new RapooStatus(RapooFrameKind.NotStatus, 0, ChargingState.Unknown, 0, 0, "");
    }
}
