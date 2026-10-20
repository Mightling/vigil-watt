namespace VigilWatt.Core.Plugins.Betop;

/// <summary>
/// 功 能： 北通 (BETOP) 手柄状态报告的纯帧编解码——不碰句柄、不写日志，便于单测。
/// 2026/10/06 15:53:36 xliu
/// 帧结构、T9 被动流、占位帧判定与实测依据见 docs/hardware/protocols.md §4。
/// </summary>
internal static class BetopCodec
{
    /// <summary>状态报告 report ID（鲲鹏20 = 0x02；协议家族一致，型号间可能不同）。</summary>
    public const byte ReportId = 0x02;

    public const byte CmdStatus = 0x05;

    public const byte SubStatus = 0x01;

    /// <summary>header 字节 = (subcmd &lt;&lt; 4) | cmd = 0x15。</summary>
    public const byte StatusHeader = (SubStatus << 4) | CmdStatus;

    /// <summary>整帧偏移：电量百分比（0~100）。</summary>
    public const int BatteryOffset = 2;

    /// <summary>整帧偏移：充电标志所在字节（取低 4 位）。</summary>
    public const int ChargeOffset = 4;

    /// <summary>充电标志掩码（低 4 位，非 0 = 充电中）。</summary>
    public const byte ChargeMask = 0x0F;

    /// <summary>无效电量下界：0 视为设备未就绪/占位值，不接受。</summary>
    public const byte InvalidPercentFloor = 0;

    // T9 被动状态流（Col04 持续推流，不响应查询）。识别特征只能取 report ID：
    // byte[1] 是可变状态位（实测见过 0x02/0x04/0x0C），拿它当帧头会让解析全数落空。

    /// <summary>T9 状态流所在 collection 的 report ID（整帧偏移 0；无 ID 前缀的布局在偏移 1）。</summary>
    public const byte T9StreamReportId = 0x04;

    /// <summary>T9 状态流帧最小长度（含 report ID）。</summary>
    public const int T9MinLength = 24;

    /// <summary>T9 电量原始字节所在偏移。</summary>
    public const int T9BatteryOffset = 23;

    /// <summary>T9 电量满值：该字节编码为"4-bit 电量等级(0~15) × 8"，故满值 = 120。</summary>
    public const byte T9BatteryFull = 120;

    /// <summary>
    /// dongle 在"手柄未连接（关机/休眠/未配对）"时上报的占位值（raw 80）——不是电量。
    /// 开机态永远是 0x58，故只认 0x50 是零风险判据。详见 protocols.md §4.4.1。
    /// </summary>
    public const byte T9NotLinkedRaw = 0x50;

    /// <summary>把 T9 原始电量字节（0~120）换算成百分比（0~100，四舍五入）。</summary>
    public static int T9ToPercent(byte raw) => (raw * 100 + T9BatteryFull / 2) / T9BatteryFull;

    /// <summary>T9 被动状态流的解析结果。</summary>
    /// <param name="Percent">电量百分比（0~100）；<paramref name="Linked"/> 为 false 时无意义（是占位值）。</param>
    /// <param name="Charging">是否充电中（T9 流无充电标志字节，恒为 false）。</param>
    /// <param name="Linked">false = dongle 上报"未连接"占位帧，手柄并不在线，调用方应标离线。</param>
    public readonly record struct T9Status(int Percent, bool Charging, bool Linked);

    /// <summary>
    /// 解析 T9 的被动状态流帧；非该帧型或电量越界返回 null。
    /// 识别依据只有 report ID，不要按 byte[1] 判帧头。
    /// </summary>
    public static T9Status? ParseT9Status(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + T9MinLength) continue;
            if (input[off] != T9StreamReportId) continue;

            byte raw = input[off + T9BatteryOffset];
            if (raw <= InvalidPercentFloor || raw > T9BatteryFull) return null;

            // 占位帧：手柄没连上，不是电量。
            if (raw == T9NotLinkedRaw) return new T9Status(0, false, Linked: false);

            return new T9Status(T9ToPercent(raw), false, Linked: true);
        }
        return null;
    }

    /// <summary>
    /// 把 Windows 的 USB product string 收拾成型号名：去厂商前缀与 "For …" 用途后缀。
    /// 例：<c>"BEITONG T9 For Keyboard &amp; Mouse"</c> → <c>"T9"</c>（不去掉的话 UI 会按
    /// 名字里的 "Keyboard" 把它画成键盘图标）。
    /// </summary>
    public static string CleanProductName(string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return "";
        var s = product.Trim();

        foreach (var vendor in new[] { "BEITONG", "BETOP", "北通" })
        {
            if (s.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            {
                s = s[vendor.Length..].TrimStart(' ', '-', '_', ':', '：');
                break;
            }
        }

        int forIdx = s.IndexOf(" For ", StringComparison.OrdinalIgnoreCase);
        if (forIdx > 0) s = s[..forIdx];

        return s.Trim();
    }

    public static byte EncodeHeader(byte cmd, byte subcmd)
        => (byte)(((subcmd & 0x0F) << 4) | (cmd & 0x0F));

    public static (byte Cmd, byte Subcmd) DecodeHeader(byte header)
        => ((byte)(header & 0x0F), (byte)((header >> 4) & 0x0F));

    /// <summary>
    /// 构造一条查询帧（不足部分补零）。北通接收器对写入长度敏感，必须写满接口声明的输出
    /// 报文长度，故长度由调用方按接口给出。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 小于 2。</exception>
    public static byte[] BuildQuery(int length, byte reportId = ReportId, byte header = StatusHeader)
    {
        if (length < 2)
            throw new ArgumentOutOfRangeException(nameof(length), "长度至少 2（report_id + header）");
        var frame = new byte[length];
        frame[0] = reportId;
        frame[1] = header;
        return frame;
    }

    /// <summary>接口未声明 report ID（描述符无 0x85 项）时：header 直接放报文首字节，无 ID 前缀。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 小于 1。</exception>
    public static byte[] BuildQueryWithoutReportId(int length, byte header = StatusHeader)
    {
        if (length < 1)
            throw new ArgumentOutOfRangeException(nameof(length), "长度至少 1（header）");
        var frame = new byte[length];
        frame[0] = header;
        return frame;
    }

    /// <summary>
    /// 解析状态报告帧（header 0x15），返回 (电量%, 是否充电中)；非状态报告或电量无效返回 null。
    /// Windows 的 ReadInput 缓冲带 report ID 前缀（下标 0），这里同时兼容无前缀布局（下标 1）。
    /// </summary>
    public static (int Percent, bool Charging)? ParseStatus(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + ChargeOffset + 1) continue;
            if (input[off] != ReportId || input[off + 1] != StatusHeader) continue;

            byte pct = input[off + BatteryOffset];
            if (pct <= InvalidPercentFloor || pct > 100) return null;
            bool charging = (input[off + ChargeOffset] & ChargeMask) != 0;
            return (pct, charging);
        }
        return null;
    }
}
