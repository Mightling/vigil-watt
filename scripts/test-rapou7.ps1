$src = @"
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public class HidProtoTest
{
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid cls, string dev, uint flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string dev, [Out] char[] buf, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetAttributes(SafeFileHandle h, ref ATTRS a);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetCaps(IntPtr pp, ref CAPS c);

    [StructLayout(LayoutKind.Sequential)]
    public struct ATTRS { public int Size; public ushort Vid; public ushort Pid; public ushort Ver; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CAPS
    {
        public ushort UsagePage; public ushort Usage;
        public ushort InLen; public ushort OutLen; public ushort FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] R1;
        public ushort N1, N2, N3, N4, N5, N6, N7, N8, N9, N10, N11, N12;
    }

    static Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

    public static string FindCol01()
    {
        uint size = 0;
        CM_Get_Device_Interface_List_SizeW(out size, ref HidGuid, "", 0);
        char[] buf = new char[size];
        CM_Get_Device_Interface_ListW(ref HidGuid, "", buf, size, 0);
        string all = new string(buf);
        string[] parts = all.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string p in parts)
        {
            if (p.IndexOf("VID_41E4", StringComparison.OrdinalIgnoreCase) < 0) continue;
            SafeFileHandle h = CreateFileW(p, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            ATTRS a = new ATTRS();
            a.Size = Marshal.SizeOf(typeof(ATTRS));
            if (!HidD_GetAttributes(h, ref a)) { h.Dispose(); continue; }
            ushort feat = 0, usage = 0, page = 0;
            IntPtr pp;
            if (HidD_GetPreparsedData(h, out pp))
            {
                CAPS c = new CAPS();
                if (HidP_GetCaps(pp, ref c) == 0x00110000) { feat = c.FeatLen; usage = c.Usage; page = c.UsagePage; }
                HidD_FreePreparsedData(pp);
            }
            h.Dispose();
            string line = "iface: feat=" + feat + " usage=0x" + usage.ToString("X4") + " page=0x" + page.ToString("X4") + " " + p;
            Console.WriteLine(line);
            if (p.IndexOf("COL01", StringComparison.OrdinalIgnoreCase) >= 0) return p;
        }
        return "";
    }

    public static void TestRapou7Frame(string path)
    {
        SafeFileHandle h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine("open failed: " + Marshal.GetLastWin32Error()); return; }

        // Rapou7 经典协议电量查询帧：report 0x11 + 命令 0x06 经 0xFF 异或 = 0xF9，其余 0xFF
        byte[] req = new byte[20];
        req[0] = 0x11; req[1] = 0xF9;
        for (int i = 2; i < 20; i++) req[i] = 0xFF;

        bool setOk = HidD_SetFeature(h, req, (uint)req.Length);
        Console.WriteLine("SetFeature(0x11, doc frame): ok=" + setOk + " err=" + Marshal.GetLastWin32Error());

        byte[] resp = new byte[20];
        resp[0] = 0x11;
        bool getOk = HidD_GetFeature(h, resp, (uint)resp.Length);
        Console.WriteLine("GetFeature(0x11): ok=" + getOk + " err=" + Marshal.GetLastWin32Error());
        if (getOk) Console.WriteLine("resp: " + BitConverter.ToString(resp));

        byte[] r13 = new byte[20]; r13[0] = 0x13;
        bool g13 = HidD_GetFeature(h, r13, (uint)r13.Length);
        Console.WriteLine("GetFeature(0x13): ok=" + g13 + " err=" + Marshal.GetLastWin32Error());
        if (g13) Console.WriteLine("resp13: " + BitConverter.ToString(r13));

        h.Dispose();
    }
}
"@
Add-Type -TypeDefinition $src

$path = [HidProtoTest]::FindCol01()
if ($path) {
    Write-Host "COL01 = $path"
    [HidProtoTest]::TestRapou7Frame($path)
} else {
    Write-Host "COL01 not found"
}
