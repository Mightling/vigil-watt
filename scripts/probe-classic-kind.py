"""探针：枚举本机经典蓝牙（BTHENUM\\）设备节点，找"设备类型"的可靠信号。

用途见 scripts/README.md；结论与判定规则见 docs/dev/runtime-pitfalls.md §3.1。

用法：
    python probe-classic-kind.py            # 只列 BTHENUM 节点的关键属性
    python probe-classic-kind.py --all      # 另 dump 每个节点全部属性
"""
import ctypes
import sys
from ctypes import wintypes as wt

setupapi = ctypes.WinDLL("setupapi", use_last_error=True)

DIGCF_PRESENT = 0x2
DIGCF_ALLCLASSES = 0x4

# DEVPKEY_Device_*
DEVPKEY_FAMILY = "{A45C254E-DF1C-4EFD-8020-67D146A850E0}"
K_CLASSGUID = f"{DEVPKEY_FAMILY},10"    # DEVPKEY_Device_ClassGuid（HIDClass / MEDIA / Bluetooth …）
K_COMPATIDS = f"{DEVPKEY_FAMILY},3"     # DEVPKEY_Device_CompatibleIds
K_DEVICEDESC = f"{DEVPKEY_FAMILY},2"    # DEVPKEY_Device_DeviceDesc
K_FRIENDLYNAME = f"{DEVPKEY_FAMILY},14" # DEVPKEY_Device_FriendlyName
K_ENUMERATOR = f"{DEVPKEY_FAMILY},24"   # DEVPKEY_Device_EnumeratorName（本机恒为 BTHENUM，无区分度）

# DEVPKEY_DeviceDisplay_Category（Windows 设备功能类别，如 Input.Mouse / Audio.Headphone）
K_CATEGORY = "{78C34FC8-104A-4ACA-9EA4-524D52996E57},90"

DEVPROP_TYPE_BYTE = 0x03
DEVPROP_TYPE_UINT16 = 0x05
DEVPROP_TYPE_UINT32 = 0x07
DEVPROP_TYPE_STRING = 0x12
DEVPROP_TYPE_STRING_LIST = 0x2012
DEVPROP_TYPE_GUID = 0x0D

BTHENUM_PREFIX = "BTHENUM\\"

# 经典蓝牙服务 UUID → 设备类型提示
SERVICE_HINTS = {
    "0000111E": "HFP-AG(音频/免手操作网关)",
    "0000111F": "HFP-HF(免手操作)",
    "00001108": "HSP-Headset(耳机)",
    "0000110B": "A2DP-AudioSink(耳机/音箱)",
    "0000110A": "A2DP-AudioSource",
    "0000110C": "AVRCP-Target(媒体控制)",
    "0000110E": "AVRCP-Controller",
    "00001124": "HIDP(HID 键鼠/手柄)",
    "00001812": "HOGP(BLE HID)",
    "00001105": "OBEX-ObjectPush",
    "0000112D": "SIM-Access",
    "00001103": "DUN(拨号)",
    "00001101": "SPP(串口)",
    "00001106": "OBEX-FTP",
    "0000111A": "Handsfree-AudioGateway",
    "00001104": "PAN-User",
}


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class DEVPROPKEY(ctypes.Structure):
    _fields_ = [("fmtid", GUID), ("pid", wt.ULONG)]


class SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wt.DWORD), ("ClassGuid", GUID),
        ("DevInst", wt.DWORD), ("Reserved", ctypes.c_void_p),
    ]


def parse_guid(text):
    t = text.strip().strip("{}")
    parts = t.split("-")
    g = GUID()
    g.Data1 = int(parts[0], 16)
    g.Data2 = int(parts[1], 16)
    g.Data3 = int(parts[2], 16)
    for i, b in enumerate(bytes.fromhex(parts[3] + parts[4])):
        g.Data4[i] = b
    return g


def make_key(text):
    guid_text, pid = text.rsplit(",", 1)
    return DEVPROPKEY(fmtid=parse_guid(guid_text), pid=int(pid))


def guid_str(g):
    return (f"{{{g.Data1:08X}-{g.Data2:04X}-{g.Data3:04X}-"
            f"{bytes(g.Data4[:2]).hex().upper()}-"
            f"{bytes(g.Data4[2:]).hex().upper()}}}")


setupapi.SetupDiGetClassDevsW.restype = wt.HANDLE
setupapi.SetupDiGetClassDevsW.argtypes = [
    ctypes.POINTER(GUID), wt.LPCWSTR, wt.HWND, wt.DWORD]
setupapi.SetupDiEnumDeviceInfo.restype = wt.BOOL
setupapi.SetupDiEnumDeviceInfo.argtypes = [
    wt.HANDLE, wt.DWORD, ctypes.POINTER(SP_DEVINFO_DATA)]
setupapi.SetupDiGetDeviceInstanceIdW.restype = wt.BOOL
setupapi.SetupDiGetDeviceInstanceIdW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), wt.LPWSTR, wt.DWORD,
    ctypes.POINTER(wt.DWORD)]
setupapi.SetupDiGetDevicePropertyW.restype = wt.BOOL
setupapi.SetupDiGetDevicePropertyW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.POINTER(DEVPROPKEY),
    ctypes.POINTER(wt.ULONG), ctypes.c_void_p, wt.DWORD,
    ctypes.POINTER(wt.DWORD), wt.DWORD]
setupapi.SetupDiGetDevicePropertyKeys.restype = wt.BOOL
setupapi.SetupDiGetDevicePropertyKeys.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA), ctypes.c_void_p, wt.DWORD,
    ctypes.POINTER(wt.DWORD), wt.DWORD]
setupapi.SetupDiDestroyDeviceInfoList.restype = wt.BOOL
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wt.HANDLE]


def read_property(hdev, did, key):
    ptype = wt.ULONG()
    size = wt.DWORD(0)
    setupapi.SetupDiGetDevicePropertyW(
        hdev, ctypes.byref(did), ctypes.byref(key), ctypes.byref(ptype),
        None, 0, ctypes.byref(size), 0)
    if size.value == 0:
        return None
    buf = ctypes.create_string_buffer(size.value)
    ok = setupapi.SetupDiGetDevicePropertyW(
        hdev, ctypes.byref(did), ctypes.byref(key), ctypes.byref(ptype),
        buf, size.value, ctypes.byref(size), 0)
    if not ok:
        return None
    return ptype.value, buf.raw[: size.value]


def decode(ptype, raw):
    if ptype == DEVPROP_TYPE_BYTE:
        return raw[0]
    if ptype == DEVPROP_TYPE_UINT16:
        return int.from_bytes(raw[:2], "little")
    if ptype == DEVPROP_TYPE_UINT32:
        return int.from_bytes(raw[:4], "little")
    if ptype == DEVPROP_TYPE_GUID:
        return guid_str(GUID.from_buffer_copy(raw[:16]))
    if ptype == DEVPROP_TYPE_STRING:
        return raw.decode("utf-16-le", "replace").rstrip("\x00")
    if ptype == DEVPROP_TYPE_STRING_LIST:
        return raw.decode("utf-16-le", "replace").rstrip("\x00").split("\x00")
    return raw.hex()


def dump_all(hdev, did):
    count = wt.DWORD(0)
    setupapi.SetupDiGetDevicePropertyKeys(
        hdev, ctypes.byref(did), None, 0, ctypes.byref(count), 0)
    if count.value == 0:
        return []
    arr = (DEVPROPKEY * count.value)()
    ok = setupapi.SetupDiGetDevicePropertyKeys(
        hdev, ctypes.byref(did), ctypes.byref(arr), count.value,
        ctypes.byref(count), 0)
    if not ok:
        return []
    out = []
    for k in arr:
        got = read_property(hdev, did, k)
        name = f"{guid_str(k.fmtid)},{k.pid}"
        out.append((name, "(unreadable)" if got is None else decode(*got)))
    return out


def service_hint_from_inst(inst):
    """实例 ID 形如 BTHENUM\\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&005D\\..."""
    body = inst[len(BTHENUM_PREFIX):]
    if not body.startswith("{"):
        return None, None
    uuid = body[1: body.index("}")] if "}" in body else None
    if not uuid:
        return None, None
    short = uuid[:8].upper()
    return uuid, SERVICE_HINTS.get(short)


def main():
    args = sys.argv[1:]
    show_all = "--all" in args

    hdev = setupapi.SetupDiGetClassDevsW(
        None, None, None, DIGCF_PRESENT | DIGCF_ALLCLASSES)
    if not hdev or hdev == wt.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())

    nodes = []
    try:
        did = SP_DEVINFO_DATA()
        did.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
        i = 0
        while setupapi.SetupDiEnumDeviceInfo(hdev, i, ctypes.byref(did)):
            i += 1
            buf = ctypes.create_unicode_buffer(1024)
            if not setupapi.SetupDiGetDeviceInstanceIdW(
                    hdev, ctypes.byref(did), buf, len(buf), None):
                continue
            inst = buf.value
            if not inst.upper().startswith(BTHENUM_PREFIX):
                continue

            def rd(key_text):
                got = read_property(hdev, did, make_key(key_text))
                return None if got is None else decode(*got)

            nodes.append({
                "inst": inst,
                "name": rd(K_FRIENDLYNAME) or rd(K_DEVICEDESC),
                "classguid": rd(K_CLASSGUID),
                "compat": rd(K_COMPATIDS),
                "category": rd(K_CATEGORY),
                "all": dump_all(hdev, did) if show_all else None,
            })
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(hdev)

    print(f"BTHENUM 节点数: {len(nodes)}\n")
    for n in nodes:
        uuid, hint = service_hint_from_inst(n["inst"])
        print(f"--- {n['name']}")
        print(f"    inst      = {n['inst']}")
        print(f"    服务UUID  = {uuid}   {hint or ''}")
        print(f"    设备类别  = {n['category']}")
        print(f"    ClassGuid = {n['classguid']}")
        print(f"    CompatIds = {n['compat']}")
        if show_all:
            for k, v in (n["all"] or []):
                print(f"      {k} = {v}")
        print()


if __name__ == "__main__":
    main()
