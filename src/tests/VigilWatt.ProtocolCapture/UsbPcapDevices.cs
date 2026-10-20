using System.Runtime.InteropServices;

namespace VigilWatt.ProtocolCapture;
/// <summary>
/// 功 能： 枚举本机 USBPcap 设备。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public static class UsbPcapDevices
{
    public static IReadOnlyList<string> ListRootHubs()
    {
        var buffer = new char[65536];
        var length = QueryDosDevice(null, buffer, buffer.Length);
        if (length == 0) throw new InvalidOperationException($"无法枚举 Windows 设备路径，Win32 错误 {Marshal.GetLastWin32Error()}。");
        return new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => name.Length > 7 && name.StartsWith("USBPcap", StringComparison.OrdinalIgnoreCase) && name[7..].All(char.IsDigit))
            .Select(name => $@"\\.\{name}")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string? deviceName, [Out] char[] targetPath, int max);
}
