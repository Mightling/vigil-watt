using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VigilWatt.Core.Ble;

/// <summary>一台经典蓝牙设备在 PnP 设备树里的信息（按设备 MAC 归并多个 <c>BTHENUM\*</c> 节点）。</summary>
internal sealed class ClassicDevNodeInfo
{
    /// <summary>HFP 网关节点上的电量（0-100）；无音频节点时为 null。</summary>
    public int? BatteryPercent { get; set; }

    /// <summary>Windows 设备功能类别（<c>DEVPKEY_DeviceDisplay_Category</c>），如 <c>Input.Mouse</c>；缺失时为空。</summary>
    public string[] Categories { get; set; } = [];

    /// <summary>该设备暴露的经典蓝牙服务 UUID（8 位短式小写），如 <c>00001124</c>=HIDP。</summary>
    public HashSet<string> ServiceUuids { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 功 能： 枚举 <c>BTHENUM\*</c> 设备节点，按设备 MAC 归并出电量、Windows 设备类别与服务 UUID。
/// 2026/10/09 17:22:33 xliu
/// 详见 docs/dev/runtime-pitfalls.md §3 / §3.1。
/// </summary>
internal static class ClassicDevNodeReader
{
    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint DEVPROP_TYPE_BYTE = 0x03;
    private const uint DEVPROP_TYPE_UINT16 = 0x05;
    private const uint DEVPROP_TYPE_UINT32 = 0x07;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>经典蓝牙（BR/EDR）设备节点前缀，用来把 BLE 节点排除在外。</summary>
    private const string BthEnumPrefix = @"BTHENUM\";

    /// <summary>电量属性在 5 秒内复用，避免 watcher 事件密集时反复枚举整棵设备树。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly object CacheLock = new();
    private static Dictionary<string, ClassicDevNodeInfo>? _cache;
    private static DateTimeOffset _cacheAt;

    private static readonly Regex AepMacRegex = new(
        @"(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    /// <summary>实例 ID 里的设备 MAC。⚠️ 不能用"全局找 12 位十六进制"——服务 UUID 里的 <c>00805F9B34FB</c> 会先被匹配。</summary>
    private static readonly Regex DevNodeMacRegex = new(
        @"&(?:BLUETOOTHDEVICE_)?([0-9A-Fa-f]{12})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>子节点实例 ID 前缀里的服务 UUID：<c>BTHENUM\{00001124-…}_VID&amp;…</c>。</summary>
    private static readonly Regex ServiceUuidRegex = new(
        @"^BTHENUM\\\{([0-9A-Fa-f]{8})-", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>DEVPKEY_Device_BatteryLevel = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2</summary>
    private static readonly DevPropKey BatteryKey = new()
    {
        FmtId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        Pid = 2,
    };

    /// <summary>DEVPKEY_DeviceDisplay_Category = {78C34FC8-104A-4ACA-9EA4-524D52996E57}, 90（STRING_LIST）。</summary>
    private static readonly DevPropKey CategoryKey = new()
    {
        FmtId = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"),
        Pid = 90,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>读本机所有经典蓝牙设备节点的信息，返回 归一化MAC(12位大写) → 信息。</summary>
    public static IReadOnlyDictionary<string, ClassicDevNodeInfo> ReadByMac()
    {
        lock (CacheLock)
        {
            if (_cache != null && DateTimeOffset.Now - _cacheAt < CacheTtl) return _cache;
            _cache = ReadUncached();
            _cacheAt = DateTimeOffset.Now;
            return _cache;
        }
    }

    private static Dictionary<string, ClassicDevNodeInfo> ReadUncached()
    {
        var map = new Dictionary<string, ClassicDevNodeInfo>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == InvalidHandle) return map;

        try
        {
            var did = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var sb = new StringBuilder(512);

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                sb.Clear();
                if (!SetupDiGetDeviceInstanceIdW(set, ref did, sb, (uint)sb.Capacity, out _)) continue;

                string instanceId = sb.ToString();
                if (!instanceId.StartsWith(BthEnumPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string? mac = MacFromInstanceId(instanceId);
                if (mac == null) continue;

                if (!map.TryGetValue(mac, out var info))
                    map[mac] = info = new ClassicDevNodeInfo();

                // 服务 UUID 只在子节点上（父节点是 BTHENUM\DEV_<MAC>，没有 {UUID} 段）
                if (ServiceUuidFromInstanceId(instanceId) is { } svc)
                    info.ServiceUuids.Add(svc);

                // 设备类别只在父节点上
                var categories = ReadCategories(set, ref did);
                if (categories.Length > 0) info.Categories = categories;

                int? percent = ReadBatteryPercent(set, ref did);
                if (percent is > 0 and <= 100) info.BatteryPercent ??= percent;
            }
        }
        catch
        {
            // 枚举失败按"没读到"处理，绝不影响其他通道
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return map;
    }

    /// <summary>读一个设备属性；属性不存在时返回 null。</summary>
    private static byte[]? ReadRawProperty(IntPtr set, ref SP_DEVINFO_DATA did, DevPropKey key, out uint type)
    {
        type = 0;
        // 第一次调用拿长度（返回 ERROR_INSUFFICIENT_BUFFER 属正常）
        SetupDiGetDevicePropertyW(set, ref did, ref key, out _, null, 0, out uint required, 0);
        if (required == 0) return null;

        var buf = new byte[required];
        if (!SetupDiGetDevicePropertyW(set, ref did, ref key, out type, buf, required, out _, 0)) return null;
        return buf;
    }

    private static int? ReadBatteryPercent(IntPtr set, ref SP_DEVINFO_DATA did)
    {
        var buf = ReadRawProperty(set, ref did, BatteryKey, out uint type);
        if (buf == null) return null;

        return type switch
        {
            DEVPROP_TYPE_BYTE => buf[0],
            DEVPROP_TYPE_UINT16 => BitConverter.ToUInt16(buf, 0),
            DEVPROP_TYPE_UINT32 => (int)BitConverter.ToUInt32(buf, 0),
            _ => null,
        };
    }

    /// <summary>读 Windows 设备功能类别（UTF-16 多字符串，<c>\0</c> 分隔）。</summary>
    private static string[] ReadCategories(IntPtr set, ref SP_DEVINFO_DATA did)
    {
        var buf = ReadRawProperty(set, ref did, CategoryKey, out uint type);
        if (buf == null || type != DEVPROP_TYPE_STRING_LIST) return [];

        var text = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return text.Length == 0 ? [] : text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>从实例 ID 里取设备 MAC（末段 <c>&amp;&lt;12位&gt;</c>）。</summary>
    private static string? MacFromInstanceId(string instanceId)
    {
        string? last = null;
        foreach (Match m in DevNodeMacRegex.Matches(instanceId)) last = m.Groups[1].Value;
        return last?.ToUpperInvariant();
    }

    /// <summary>从子节点实例 ID 取服务 UUID 短式（8 位小写）；父节点返回 null。</summary>
    private static string? ServiceUuidFromInstanceId(string instanceId)
    {
        var m = ServiceUuidRegex.Match(instanceId);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>从 AEP 设备 ID 取设备 MAC（形如 <c>Bluetooth#Bluetooth&lt;适配器&gt;-&lt;设备&gt;</c>，取最后一段）。</summary>
    public static string? MacFromAepId(string aepId)
    {
        string? last = null;
        foreach (Match m in AepMacRegex.Matches(aepId)) last = m.Value;
        return last?.Replace(":", "").Replace("-", "").ToUpperInvariant();
    }
}
