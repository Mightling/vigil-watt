"""Probe Logi Bolt (046D:C548) HID++ long interface with a Root.Ping."""
import sys
import time

try:
    import hid
except ImportError:
    print("pip install hidapi 后再跑")
    sys.exit(1)

VID, PID = 0x046D, 0xC548
found = False
for d in hid.enumerate(VID, PID):
    up, u = d.get("usage_page") or 0, d.get("usage") or 0
    print(f"iface usage={up:04X}/{u:04X}")
    if up != 0xFF00 or u != 2:
        continue
    found = True
    h = hid.device()
    h.open_path(d["path"])
    try:
        for idx in (1, 2, 0xFF):
            mark = 0x5A
            pkt = bytearray(20)
            pkt[0] = 0x11
            pkt[1] = idx & 0xFF
            pkt[2] = 0x00
            pkt[3] = 0x1B
            pkt[4] = 0x00
            pkt[5] = 0x00
            pkt[6] = mark
            print(f"ping idx={idx} write={pkt[:8].hex()}...")
            h.write(pkt)
            t0 = time.time()
            hit = None
            while time.time() - t0 < 2.0:
                data = h.read(20, timeout_ms=200)
                if data:
                    hit = bytes(data)
                    break
            if hit:
                print(f"  reply={hit.hex()} elapsed={time.time()-t0:.3f}s")
            else:
                print("  timeout 2s")
    finally:
        h.close()

if not found:
    print("未找到 usage FF00/0002 长接口")
