"""探针：枚举本机所有 PnP 设备节点，读取 DEVPKEY_Device_BatteryLevel。

背景：Windows 11 设置页给蓝牙耳机/音箱显示的电量，来自 PnP 设备属性
    DEVPKEY_Device_BatteryLevel = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2
这个属性挂在**设备节点(devnode)**上，只能用 SetupAPI 读，
用 Windows.Devices.Enumeration 的 AEP 属性(System.Devices.AepBattery_Level)是读不到的
——这正是 VigilWatt 当前拿不到经典蓝牙耳机电量的原因。

用法：
    python probe-devnode-battery.py              # 只列有电量的设备
    python probe-devnode-battery.py --all        # 另外 dump 匹配节点的全部属性
    python probe-devnode-battery.py --match W820 # 自定义匹配关键字（默认 W820）
"""
import ctypes
import sys
from ctypes import wintypes as wt

setupapi = ctypes.WinDLL("setupapi", use_last_error=True)

# ---- 常量 ----
DIGCF_PRESENT = 0x2
DIGCF_ALLCLASSES = 0x4
SPDRP_DEVICEDESC = 0x00
SPDRP_FRIENDLYNAME = 0x0C
DEVPROP_TYPE_BYTE = 0x03
DEVPROP_TYPE_UINT16 = 0x05
DEVPROP_TYPE_UINT32 = 0x07

# DEVPKEY_Device_BatteryLevel
BATTERY_KEY = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2"


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", wt.DWORD),
        ("Data2", wt.WORD),
        ("Data3", wt.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class DEVPROPKEY(ctypes.Structure):
    _fields_ = [("fmtid", GUID), ("pid", wt.ULONG)]


class SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wt.DWORD),
        ("ClassGuid", GUID),
        ("DevInst", wt.DWORD),
        ("Reserved", ctypes.c_void_p),
    ]


def parse_guid(text: str) -> GUID:
    t = text.strip().strip("{}")
    parts = t.split("-")
    g = GUID()
    g.Data1 = int(parts[0], 16)
    g.Data2 = int(parts[1], 16)
    g.Data3 = int(parts[2], 16)
    for i, b in enumerate(bytes.fromhex(parts[3] + parts[4])):
        g.Data4[i] = b
    return g


def make_key(text: str) -> DEVPROPKEY:
    guid_text, pid = text.rsplit(",", 1)
    return DEVPROPKEY(fmtid=parse_guid(guid_text), pid=int(pid))


def guid_str(g: GUID) -> str:
    return (
        f"{{{g.Data1:08X}-{g.Data2:04X}-{g.Data3:04X}-"
        f"{bytes(g.Data4[:2]).hex().upper()}-"
        f"{bytes(g.Data4[2:]).hex().upper()}}}"
    )


# ---- 签名 ----
setupapi.SetupDiGetClassDevsW.restype = wt.HANDLE
setupapi.SetupDiGetClassDevsW.argtypes = [
    ctypes.POINTER(GUID), wt.LPCWSTR, wt.HWND, wt.DWORD,
]
setupapi.SetupDiEnumDeviceInfo.restype = wt.BOOL
setupapi.SetupDiEnumDeviceInfo.argtypes = [
    wt.HANDLE, wt.DWORD, ctypes.POINTER(SP_DEVINFO_DATA),
]
setupapi.SetupDiGetDeviceInstanceIdW.restype = wt.BOOL
setupapi.SetupDiGetDeviceInstanceIdW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), wt.LPWSTR, wt.DWORD,
    ctypes.POINTER(wt.DWORD),
]
setupapi.SetupDiGetDeviceRegistryPropertyW.restype = wt.BOOL
setupapi.SetupDiGetDeviceRegistryPropertyW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), wt.DWORD, ctypes.POINTER(wt.DWORD),
    ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD),
]
setupapi.SetupDiGetDevicePropertyW.restype = wt.BOOL
setupapi.SetupDiGetDevicePropertyW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.POINTER(DEVPROPKEY),
    ctypes.POINTER(wt.ULONG), ctypes.c_void_p, wt.DWORD,
    ctypes.POINTER(wt.DWORD), wt.DWORD,
]
setupapi.SetupDiGetDevicePropertyKeys.restype = wt.BOOL
setupapi.SetupDiGetDevicePropertyKeys.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.c_void_p, wt.DWORD,
    ctypes.POINTER(wt.DWORD), wt.DWORD,
]
setupapi.SetupDiDestroyDeviceInfoList.restype = wt.BOOL
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wt.HANDLE]


def read_property(hdev, did, key: DEVPROPKEY):
    ptype = wt.ULONG()
    size = wt.DWORD(0)
    setupapi.SetupDiGetDevicePropertyW(
        hdev, ctypes.byref(did), ctypes.byref(key), ctypes.byref(ptype),
        None, 0, ctypes.byref(size), 0,
    )
    if size.value == 0:
        return None
    buf = ctypes.create_string_buffer(size.value)
    ok = setupapi.SetupDiGetDevicePropertyW(
        hdev, ctypes.byref(did), ctypes.byref(key), ctypes.byref(ptype),
        buf, size.value, ctypes.byref(size), 0,
    )
    if not ok:
        return None
    return ptype.value, buf.raw[: size.value]


def decode(ptype: int, raw: bytes):
    if ptype == DEVPROP_TYPE_BYTE:
        return raw[0]
    if ptype == DEVPROP_TYPE_UINT16:
        return int.from_bytes(raw[:2], "little")
    if ptype == DEVPROP_TYPE_UINT32:
        return int.from_bytes(raw[:4], "little")
    return raw.hex()


def dump_all(hdev, did):
    count = wt.DWORD(0)
    setupapi.SetupDiGetDevicePropertyKeys(
        hdev, ctypes.byref(did), None, 0, ctypes.byref(count), 0
    )
    if count.value == 0:
        return []
    arr = (DEVPROPKEY * count.value)()
    ok = setupapi.SetupDiGetDevicePropertyKeys(
        hdev, ctypes.byref(did), ctypes.byref(arr), count.value,
        ctypes.byref(count), 0,
    )
    if not ok:
        return []
    out = []
    for k in arr:
        got = read_property(hdev, did, k)
        name = f"{guid_str(k.fmtid)},{k.pid}"
        out.append((name, "(unreadable)" if got is None else decode(*got)))
    return out


def main():
    args = sys.argv[1:]
    show_all = "--all" in args
    match = args[args.index("--match") + 1] if "--match" in args else "W820"

    battery_key = make_key(BATTERY_KEY)
    hdev = setupapi.SetupDiGetClassDevsW(
        None, None, None, DIGCF_PRESENT | DIGCF_ALLCLASSES
    )
    if not hdev or hdev == wt.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())

    total = 0
    hits = []
    matched = []
    try:
        did = SP_DEVINFO_DATA()
        did.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
        i = 0
        while setupapi.SetupDiEnumDeviceInfo(hdev, i, ctypes.byref(did)):
            i += 1
            total += 1
            buf = ctypes.create_unicode_buffer(1024)
            if not setupapi.SetupDiGetDeviceInstanceIdW(
                hdev, ctypes.byref(did), buf, len(buf), None
            ):
                continue
            inst = buf.value

            nb = ctypes.create_unicode_buffer(1024)
            if not setupapi.SetupDiGetDeviceRegistryPropertyW(
                hdev, ctypes.byref(did), SPDRP_FRIENDLYNAME, None,
                nb, ctypes.sizeof(nb), None,
            ):
                setupapi.SetupDiGetDeviceRegistryPropertyW(
                    hdev, ctypes.byref(did), SPDRP_DEVICEDESC, None,
                    nb, ctypes.sizeof(nb), None,
                )
            name = nb.value

            got = read_property(hdev, did, battery_key)
            if got is not None:
                hits.append((inst, name, decode(*got)))
            if match.lower() in inst.lower() or match.lower() in name.lower():
                matched.append((inst, name))
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(hdev)

    print(f"设备节点总数: {total}")
    print(f"\n=== 带 DEVPKEY_Device_BatteryLevel 的设备 ({len(hits)}) ===")
    for inst, name, val in hits:
        print(f"  {val!s:>4} %  {name}\n           {inst}")

    if show_all:
        print(f"\n=== 匹配 '{match}' 的节点全属性 ({len(matched)}) ===")
        hdev = setupapi.SetupDiGetClassDevsW(
            None, None, None, DIGCF_PRESENT | DIGCF_ALLCLASSES
        )
        try:
            did = SP_DEVINFO_DATA()
            did.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
            i = 0
            while setupapi.SetupDiEnumDeviceInfo(hdev, i, ctypes.byref(did)):
                i += 1
                buf = ctypes.create_unicode_buffer(1024)
                if not setupapi.SetupDiGetDeviceInstanceIdW(
                    hdev, ctypes.byref(did), buf, len(buf), None
                ):
                    continue
                inst = buf.value
                nb = ctypes.create_unicode_buffer(1024)
                if not setupapi.SetupDiGetDeviceRegistryPropertyW(
                    hdev, ctypes.byref(did), SPDRP_FRIENDLYNAME, None,
                    nb, ctypes.sizeof(nb), None,
                ):
                    setupapi.SetupDiGetDeviceRegistryPropertyW(
                        hdev, ctypes.byref(did), SPDRP_DEVICEDESC, None,
                        nb, ctypes.sizeof(nb), None,
                    )
                name = nb.value
                if match.lower() in inst.lower() or match.lower() in name.lower():
                    print(f"\n--- {name}\n    {inst}")
                    for k, v in dump_all(hdev, did):
                        print(f"    {k} = {v}")
        finally:
            setupapi.SetupDiDestroyDeviceInfoList(hdev)


if __name__ == "__main__":
    main()
