using System.Runtime.InteropServices;

namespace VigilWatt.Core.Power;

/// <summary>本机电源状态的一次快照。</summary>
/// <param name="HasBattery">本机是否装有可用电池（台式机 / 电池被禁用时为 false）。</param>
/// <param name="Percent">0-100；无电池或系统报告未知时为 null。</param>
/// <param name="AcOnline">是否接着交流电（true 也可能同时在放电，如"接电但未充电"）。</param>
public readonly record struct SystemPowerSnapshot(bool HasBattery, int? Percent, bool AcOnline)
{
    /// <summary>什么都没有的快照（读取失败时用）。</summary>
    public static SystemPowerSnapshot Unavailable => new(false, null, false);
}

/// <summary>
/// 功 能： 读本机电源状态的纯 P/Invoke 实现（<c>GetSystemPowerStatus</c>，纯读零写入；不用 WinRT <c>Battery</c>）。
/// 2026/10/08 18:04:41 xliu
/// 详见 docs/project/code-flow.md §3.1。
/// </summary>
public static class SystemPowerReader
{
    /// <summary>BatteryFlag 位：无系统电池。</summary>
    private const byte NoSystemBatteryFlag = 128;

    /// <summary>BatteryFlag 位：状态未知（读不到电池信息）。</summary>
    private const byte UnknownStatusFlag = 255;

    /// <summary>BatteryLifePercent 的未知哨兵值。</summary>
    private const byte UnknownPercent = 255;

    /// <summary>ACLineStatus：0 = 离线（电池供电），1 = 在线（交流电），255 = 未知。</summary>
    private const byte AcOnlineValue = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>读一次电源状态；任何失败退化为 <see cref="SystemPowerSnapshot.Unavailable"/>，绝不抛异常（轮询路径上的一环）。</summary>
    public static SystemPowerSnapshot Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return SystemPowerSnapshot.Unavailable;
            return Interpret(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent);
        }
        catch
        {
            return SystemPowerSnapshot.Unavailable;
        }
    }

    /// <summary>把三个原始字节翻译成快照；独立出来以便单测（真机没法伪造"台式机无电池""接电但 255%"）。</summary>
    internal static SystemPowerSnapshot Interpret(byte acLineStatus, byte batteryFlag, byte batteryLifePercent)
    {
        bool acOnline = acLineStatus == AcOnlineValue;
        bool noBattery = batteryFlag == NoSystemBatteryFlag || batteryFlag == UnknownStatusFlag;
        if (noBattery) return new SystemPowerSnapshot(false, null, acOnline);

        int? percent = batteryLifePercent == UnknownPercent ? null : batteryLifePercent;

        // 有电池但百分比未知：仍然算"有电池"，让界面能显示"电源："这类降级文案，
        // 而不是整行消失（有电池的笔记本不该因为一次读不到百分比就假装自己没有电池）。
        return new SystemPowerSnapshot(true, percent is >= 0 and <= 100 ? percent : null, acOnline);
    }
}
