using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Mchose;

/// <summary>迈从鼠标协议族。<see cref="None"/> = 不是迈从鼠标。</summary>
internal enum MchoseMouseFamily
{
    None = 0,

    /// <summary>旧 A5 / AX5 Pro：VID 0x2023 + MI_02，65 字节 feature，命令 0x83。</summary>
    A5Pro,

    /// <summary>旧 A7：0x5253:0x1021，feature id 0x11，收发异或 0xFF。</summary>
    A7Legacy,

    /// <summary>/gmouse 网页族（G3 / G3 V2 / A7e / G7）：usagePage 0xFF01 / usage 0x10，output 65 字节。</summary>
    Gmouse,

    /// <summary>新世代 HUB 型号：VID 0x3837，65 字节 feature（06 87 … / 13 4A 01 …）。</summary>
    HubNative,
}

/// <summary>
/// 功 能： 迈从鼠标私有协议族（与键盘 V1 / QHW 是两套完全不同的帧）。
/// 2026/10/18 补丁
///
/// 已收录的四族：
///  · A5Pro     旧 A5 / AX5 Pro        VID 0x2023 + MI_02，65 字节 feature
///  · A7Legacy  旧 A7                  0x5253:0x1021，feature id 0x11，收发异或 0xFF
///  · Gmouse    /gmouse 网页族（G3 / G3 V2 / A7e / G7）usage 0xFF01/0x10，output 65 字节
///  · HubNative 新世代 HUB 型号（K7 / M7 / L7 / A5 Ultra / AX5 V2 …）VID 0x3837，65 字节 feature
///
/// 本类是**纯帧编解码 + 纯匹配规则**：不打开句柄、不写日志、不做 I/O，便于单测。
/// 帧依据见 docs/hardware/protocols.md 附录「迈从鼠标」。
/// </summary>
internal static class MchoseMouseCodec
{
    // ── 帧长（HID 报告的"含 report ID 首字节"总长）────────────────────────
    /// <summary>A5 / Gmouse / Hub 的 feature 或 output 报告长度。</summary>
    public const int Frame65 = 65;

    /// <summary>旧 A7 的 feature 报告长度。</summary>
    public const int Frame64 = 64;

    /// <summary>能被 feature 通道承载的最小长度（更短说明这个接口没有可用的 feature 集合）。</summary>
    public const int MinFeatureFrame = 8;

    // ── VID / PID ──────────────────────────────────────────────────────
    public const ushort VidA5 = 0x2023;
    public const ushort VidA7 = 0x5253;

    /// <summary>A7 Pro 的 2.4G dongle PID（无线模式）。</summary>
    public const ushort PidA7 = 0x1021;

    /// <summary>
    /// A7 Pro 的**有线** PID。实测（2026-10-18，真机）：插上充电线后设备会以
    /// <c>5253:0010</c> 重新枚举一份接口（布局与无线完全相同），断电才回到 <c>1021</c>。
    /// 不认它，充电期间设备就会整个消失。
    /// </summary>
    public const ushort PidA7Wired = 0x0010;
    public const ushort VidGmouseWireless = 0xA8A5;
    public const ushort VidGmouseWired = 0xA8A4;
    public const ushort VidHub = 0x3837;

    // ── report ID ──────────────────────────────────────────────────────
    /// <summary>A5 用无编号（0）的 feature 报告。</summary>
    public const byte A5ReportId = 0x00;

    /// <summary>旧 A7 的 feature report ID。</summary>
    public const byte A7ReportId = 0x11;

    /// <summary>旧 A7 的电量命令（帧里要取反写入，应答里也以取反形式回显）。</summary>
    public const byte A7CmdBattery = 0x06;

    /// <summary>Hub 有线查询的 report ID（也是 06 87 … 的首字节）。</summary>
    public const byte HubWiredReportId = 0x06;

    /// <summary>Hub 无线查询的 report ID（13 4A 01 …）。</summary>
    public const byte HubWirelessReportId = 0x13;

    // ── 命令字节 ──────────────────────────────────────────────────────
    /// <summary>A5 电量命令（DriverCore GetBat）。</summary>
    public const byte A5CmdBattery = 0x83;

    /// <summary>gmouse 应答帧头 Oa-30。</summary>
    public const byte GmouseTag0 = 0xAA;
    public const byte GmouseTag1 = 0x30;

    /// <summary>gmouse 电量请求：55 30 A5 0B 2E 01 01 01。</summary>
    private static readonly byte[] GmouseBatteryHead = { 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 };

    /// <summary>A5 / AX5 Pro 的 PID 表（旧驱动 xvi_models 导出）。</summary>
    private static readonly HashSet<ushort> A5Pids = new()
    {
        0xF012, // A5 Pro 无线 bootloader
        0xF013, // A5 Pro / Pro Max 无线 dongle
        0xF015, // A5 Pro golden dongle
        0xF016, // A5 Pro 有线 bootloader
        0xF017, // A5 Pro 有线
        0xF018, // A5 Pro Max 有线 bootloader
        0xF019, // A5 Pro Max 有线
        0xF021, // AX5 Pro Max 无线 bootloader
        0xF022, // AX5 Pro Max 无线
        0xF023, // AX5 Pro 无线 bootloader / AX5 Pro Max 有线 bootloader
        0xF024, // AX5 Pro 无线 / AX5 Pro Max 有线
        0xF025, // AX5 Pro 有线 bootloader
        0xF026, // AX5 Pro 有线
    };

    /// <summary>一次解析出的电量。</summary>
    public readonly record struct MouseReading(int Percent, ChargingState Charging);

    /// <summary>
    /// 匹配所需的设备元数据。**刻意不收 <c>HidDeviceInfo</c>**：本类要能被测试工程直接链源文件
    /// （测试项目是 net8.0 纯逻辑、不引 WinRT），多一个 Hid 依赖就链不上了。
    /// 由插件侧 <c>MouseShapeOf</c> 负责从 <c>HidDeviceInfo</c> 转换。
    /// </summary>
    public readonly record struct MouseMatchInput(
        ushort VendorId,
        ushort ProductId,
        ushort UsagePage,
        ushort Usage,
        ushort FeatureReportLength,
        string DevicePath,
        string Product,
        string Manufacturer);

    // ══════════════════════════════════════════════════════════════════
    //  匹配（纯函数：只看扫描元数据，不碰设备）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 判定这条 HID 接口属于哪套鼠标协议。返回 <see cref="MchoseMouseFamily.None"/> = 不是迈从鼠标。
    /// **顺序有意义**：0x3837 既可能是 /gmouse 族也可能走 HubNative，先按 usage 判 gmouse。
    /// </summary>
    public static MchoseMouseFamily Match(MouseMatchInput info)
    {
        // 1) 旧 A5 / AX5 Pro：VID + PID 表 + 厂商接口 MI_02
        if (info.VendorId == VidA5 && A5Pids.Contains(info.ProductId) && IsInterface(info.DevicePath, 2))
            return MchoseMouseFamily.A5Pro;

        // 2) 旧 A7（1021 = 无线 dongle；0010 = 同一只鼠标插线后的有线枚举）
        if (info.VendorId == VidA7 && (info.ProductId == PidA7 || info.ProductId == PidA7Wired))
            return MchoseMouseFamily.A7Legacy;

        // 3) /gmouse 网页族：网页 requestDevice 固定 usagePage 0xFF01 / usage 0x10
        if (info.UsagePage == 0xFF01
            && (info.Usage == 0x0010 || info.Usage == 0x0000)
            && (info.VendorId == VidGmouseWireless || info.VendorId == VidGmouseWired
                || info.VendorId == VidHub || LooksLikeMchose(info)))
            return MchoseMouseFamily.Gmouse;

        // 4) 新世代 HUB：只认 65 字节 feature 的接口。
        //    键盘那套 QHW 通道是 20/64 字节 output，不会命中这里，因此不会互相抢。
        if (info.VendorId == VidHub && info.FeatureReportLength == Frame65)
            return MchoseMouseFamily.HubNative;

        return MchoseMouseFamily.None;
    }

    /// <summary>设备路径里是否含 <c>mi_02</c>（HID 接口路径带 USB 接口号，等价于 hidapi 的 interface_number）。</summary>
    public static bool IsInterface(string devicePath, int interfaceNumber)
        => !string.IsNullOrEmpty(devicePath)
           && devicePath.Contains($"mi_{interfaceNumber:X2}", StringComparison.OrdinalIgnoreCase);

    /// <summary>产品名/厂商名里含迈从标识。</summary>
    private static bool LooksLikeMchose(MouseMatchInput info)
    {
        var text = $"{info.Manufacturer} {info.Product}".ToLowerInvariant();
        return text.Contains("mchose") || text.Contains("maicong") || text.Contains("迈从");
    }

    /// <summary>协议名（写进 <c>TrackedDevice.Protocol</c> 与日志，也用于二次探测时分派）。</summary>
    public static string ProtocolName(MchoseMouseFamily family) => family switch
    {
        MchoseMouseFamily.A5Pro => "mouse-a5",
        MchoseMouseFamily.A7Legacy => "mouse-a7",
        MchoseMouseFamily.Gmouse => "mouse-gmouse",
        MchoseMouseFamily.HubNative => "mouse-hub",
        _ => "",
    };

    /// <summary>协议名 → 协议族。不是鼠标协议返回 false。</summary>
    public static bool TryParseProtocolName(string? protocol, out MchoseMouseFamily family)
    {
        switch (protocol)
        {
            case "mouse-a5": family = MchoseMouseFamily.A5Pro; return true;
            case "mouse-a7": family = MchoseMouseFamily.A7Legacy; return true;
            case "mouse-gmouse": family = MchoseMouseFamily.Gmouse; return true;
            case "mouse-hub": family = MchoseMouseFamily.HubNative; return true;
            default: family = MchoseMouseFamily.None; return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  A5 / AX5 Pro
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构造 A5 电量查询帧（65 字节 feature，无编号报告）。
    /// 原驱动 feature_xfer 固定 [3]=0x02，GetBat 再置 [4]=0x02、[6]=0x83。
    /// </summary>
    public static byte[] BuildA5BatteryQuery()
    {
        var frame = new byte[Frame65];
        frame[0] = A5ReportId;
        frame[3] = 0x02;   // 驱动固定值
        frame[4] = 0x02;   // 命令类别
        frame[6] = A5CmdBattery;
        return frame;
    }

    /// <summary>
    /// 解析 A5 应答：UI 的 GetBat(ref charge, ref percent) → [7]=充电态、[8]=电量。
    /// charge：0 放电 / 1 充电 / 2 充满。
    /// </summary>
    public static bool TryParseA5(byte[] response, out MouseReading reading)
        => TryParseChargePercent(response, chargeIndex: 7, percentIndex: 8, out reading);

    // ══════════════════════════════════════════════════════════════════
    //  旧 A7
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构造旧 A7 查询帧：dataPart = [0x06, 0x00×19]，整段异或 0xFF，前面加 report ID 0x11。
    /// 补齐到 64 字节（补的是 0x00，不是异或值——原实现只发 21 字节，HID 驱动要求写满声明长度）。
    /// </summary>
    public static byte[] BuildA7BatteryQuery()
    {
        var frame = new byte[Frame64];
        frame[0] = A7ReportId;
        frame[1] = (byte)(A7CmdBattery ^ 0xFF);
        for (int i = 2; i < 21; i++) frame[i] = 0xFF;   // 0x00 ^ 0xFF
        return frame;
    }

    /// <summary>解析旧 A7 应答：整帧异或 0xFF，跳过前 2 字节，payload[9]=电量、[10]=充电态。</summary>
    public static bool TryParseA7(byte[] response, out MouseReading reading)
    {
        reading = default;
        if (response.Length < 13) return false;

        // 结构校验（真机踩过）：帧首必须是 report ID，第二字节是命令 0x06 取反后的 0xF9。
        // 设备"还没准备好"时会返回脏缓冲——实测见过尾部是产品名 UTF-16（"P r o"）的帧，
        // 不校验结构的话这种垃圾有可能凑出 1..100 的假电量。
        if (response[0] != A7ReportId || (response[1] ^ 0xFF) != A7CmdBattery) return false;

        var raw = new byte[response.Length];
        for (int i = 0; i < response.Length; i++) raw[i] = (byte)(response[i] ^ 0xFF);

        // payload = raw[2..]；payload[9] = raw[11]、payload[10] = raw[12]
        return TryParseChargePercent(raw, chargeIndex: 12, percentIndex: 11, out reading);
    }

    // ══════════════════════════════════════════════════════════════════
    //  /gmouse 网页族（G3 / G3 V2 / A7e / G7）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>构造 gmouse 电量请求（output 报告 65 字节，report ID 0）。</summary>
    public static byte[] BuildGmouseBatteryQuery()
    {
        var frame = new byte[Frame65];
        frame[0] = 0x00;
        Array.Copy(GmouseBatteryHead, 0, frame, 1, GmouseBatteryHead.Length);
        return frame;
    }

    /// <summary>
    /// 解析 gmouse 应答：帧头 AA 30 可能在下标 0 或 1（部分读接口带 report ID 前缀），
    /// 电量在帧头 +8、充电标志在 +9（0 = 未充电，非 0 = 充电中）。
    /// </summary>
    public static bool TryParseGmouse(byte[] frame, out MouseReading reading)
    {
        reading = default;
        int offset = -1;
        if (frame.Length >= 3 && frame[0] == GmouseTag0 && frame[1] == GmouseTag1) offset = 0;
        else if (frame.Length >= 4 && frame[1] == GmouseTag0 && frame[2] == GmouseTag1) offset = 1;
        if (offset < 0) return false;

        int pctIndex = offset + 8;
        int chgIndex = offset + 9;
        if (chgIndex >= frame.Length) return false;

        int percent = frame[pctIndex];
        if (!IsPlausiblePercent(percent)) return false;

        byte flag = frame[chgIndex];
        var charging = flag == 0
            ? ChargingState.Discharging
            : percent >= 100 ? ChargingState.Full : ChargingState.Charging;

        reading = new MouseReading(percent, charging);
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  新世代 HUB（VID 0x3837）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>构造 HUB 有线查询（feature）：06 87 00 00 01 00 02 00。</summary>
    public static byte[] BuildHubWiredQuery()
    {
        var frame = new byte[Frame65];
        frame[0] = HubWiredReportId;
        frame[1] = 0x87;
        frame[4] = 0x01;
        frame[6] = 0x02;
        return frame;
    }

    /// <summary>构造 HUB 无线查询（feature/output）：13 4A 01。</summary>
    public static byte[] BuildHubWirelessQuery()
    {
        var frame = new byte[Frame65];
        frame[0] = HubWirelessReportId;
        frame[1] = 0x4A;
        frame[2] = 0x01;
        return frame;
    }

    /// <summary>
    /// 解析 HUB 应答（网页 <c>ilt(wired)</c> 解析器）：在帧内找 <c>command = 10</c>，
    /// 有线时紧随 <c>type = 5</c>，无线时中间隔 3 字节 space；之后 <c>[0]</c>=电量、<c>[1]</c>=打包状态
    /// （低 4 位 fullStatus、高 4 位 chargeStatus）。
    /// 两种 space 都试一遍，命中即返回。
    /// </summary>
    public static bool TryParseHub(byte[] frame, out MouseReading reading)
        => TryParseHubIlt(frame, wired: true, out reading)
           || TryParseHubIlt(frame, wired: false, out reading);

    private static bool TryParseHubIlt(byte[] frame, bool wired, out MouseReading reading)
    {
        reading = default;
        int space = wired ? 0 : 3;
        for (int start = 0; start + 3 + space < frame.Length; start++)
        {
            if (frame[start] != 10) continue;                       // command = 10
            int typeIndex = start + 1 + space;
            if (frame[typeIndex] != 5) continue;                    // type = 5（电量）
            int pctIndex = typeIndex + 1;
            int packedIndex = typeIndex + 2;
            if (packedIndex >= frame.Length) continue;

            int percent = frame[pctIndex];
            if (!IsPlausiblePercent(percent)) continue;

            byte packed = frame[packedIndex];
            byte fullStatus = (byte)(packed & 0x0F);
            byte chargeRaw = (byte)(packed >> 4);

            var charging = fullStatus != 0 && percent >= 95
                ? ChargingState.Full
                : chargeRaw switch
                {
                    0 => ChargingState.Discharging,
                    1 => ChargingState.Charging,
                    2 => ChargingState.Full,
                    _ => ChargingState.Unknown,
                };

            reading = new MouseReading(percent, charging);
            return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════
    //  公共小件
    // ══════════════════════════════════════════════════════════════════

    /// <summary>[7]/[8] 型布局（A5 与 A7 异或后共用）：charge 下标在前、percent 下标在后。</summary>
    private static bool TryParseChargePercent(byte[] frame, int chargeIndex, int percentIndex, out MouseReading reading)
    {
        reading = default;
        if (percentIndex >= frame.Length || chargeIndex >= frame.Length) return false;

        int percent = frame[percentIndex];
        if (!IsPlausiblePercent(percent)) return false;

        var charging = frame[chargeIndex] switch
        {
            0 => ChargingState.Discharging,
            1 => ChargingState.Charging,
            2 => ChargingState.Full,
            _ => ChargingState.Unknown,
        };
        reading = new MouseReading(percent, charging);
        return true;
    }

    /// <summary>
    /// 0 是"未就绪/占位"（dongle 没连上设备时的常见值），**不能**当 1% 报出去；
    /// 1..100 一律接受——鼠标真能到 1%，拒掉它就会漏掉低电量提醒。
    /// </summary>
    private static bool IsPlausiblePercent(int percent) => percent is >= 1 and <= 100;
}
