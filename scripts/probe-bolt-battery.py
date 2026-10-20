"""Probe Bolt device battery via HID++ Unified Battery 0x1004 / Battery Status 0x1000."""
import time
import hid

VID, PID = 0x046D, 0xC548
SW = 0x0B


def open_long():
    for d in hid.enumerate(VID, PID):
        if (d.get("usage_page"), d.get("usage")) == (0xFF00, 2):
            h = hid.device()
            h.open_path(d["path"])
            return h
    raise SystemExit("no long iface")


def request(h, dev, feat_func, args=b"", timeout=2.0):
    """feat_func already includes (feature<<8)|(func<<4); we OR sw id."""
    req = (feat_func & 0xFFF0) | SW
    pkt = bytearray(20)
    pkt[0] = 0x11
    pkt[1] = dev
    pkt[2] = (req >> 8) & 0xFF
    pkt[3] = req & 0xFF
    for i, b in enumerate(args):
        pkt[4 + i] = b
    h.write(pkt)
    want0, want1 = pkt[2], pkt[3]
    t0 = time.time()
    while time.time() - t0 < timeout:
        data = h.read(20, timeout_ms=200)
        if not data:
            continue
        raw = bytes(data)
        if len(raw) < 4:
            continue
        if raw[0] != 0x11 or raw[1] != dev:
            continue
        if raw[2] == want0 and raw[3] == want1:
            return raw
        if raw[2] in (0x8F, 0xFF):
            print("  err", raw.hex())
            return None
    return None


h = open_long()
try:
    # ping
    mark = 0x5A
    r = request(h, 1, 0x0010, bytes([0, 0, mark]))
    print("ping", r.hex() if r else None)

    # GetFeature 0x1004
    r = request(h, 1, 0x0000, bytes([0x10, 0x04]))
    print("getFeature 1004", r.hex() if r else None)
    idx1004 = r[4] if r and len(r) > 4 else 0
    print("  featureIndex", idx1004)

    if idx1004:
        r = request(h, 1, (idx1004 << 8) | 0x00)
        print("unified bat func0", r.hex() if r else None)
        if r and len(r) >= 7:
            print(f"  percent={r[4]} charging={r[6]}")

    # GetFeature 0x1000
    r = request(h, 1, 0x0000, bytes([0x10, 0x00]))
    print("getFeature 1000", r.hex() if r else None)
    idx1000 = r[4] if r and len(r) > 4 else 0
    print("  featureIndex", idx1000)
    if idx1000:
        r = request(h, 1, (idx1000 << 8) | 0x00)
        print("battery status", r.hex() if r else None)
        if r and len(r) >= 7:
            print(f"  percent={r[4]} state={r[6]}")
finally:
    h.close()
