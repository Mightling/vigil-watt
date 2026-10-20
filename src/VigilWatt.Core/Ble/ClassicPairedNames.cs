using System.Text;
using Microsoft.Win32;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 功 能： 从经典蓝牙配对记录（注册表 <c>BTHPORT\...\Devices\&lt;mac12&gt;\Name</c>，REG_BINARY UTF-8）读友好名——WinRT AEP 只返回设备 ID 时的可靠兜底。
/// 2026/10/06 15:53:36 xliu
/// 详见 docs/project/handoff.md「经典蓝牙设备显示名修成原始 ID」。
/// </summary>
internal static class ClassicPairedNames
{
    private const string DevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static Dictionary<string, string> _cache = new();
    private static DateTime _cacheAt = DateTime.MinValue;

    /// <summary>按 MAC（12 位十六进制，大小写/分隔符不敏感）取配对名；没有则返回 null。</summary>
    public static string? TryGet(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var key = mac.Replace(":", "").Replace("-", "");
        if (key.Length != 12) return null;

        return GetTable().TryGetValue(key, out var n) ? n : null;
    }

    private static Dictionary<string, string> GetTable()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;

            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(DevicesKey);
                if (root != null)
                {
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 12) continue;
                        using var dev = root.OpenSubKey(sub);
                        if (dev?.GetValue("Name") is byte[] raw && raw.Length > 0)
                        {
                            var s = Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();
                            if (s.Length > 0) table[sub] = s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Log.Warn($"读经典蓝牙配对名失败（退化为只显示 MAC）: {ex.Message}");
            }

            _cache = table;
            _cacheAt = DateTime.UtcNow;
            return table;
        }
    }
}
