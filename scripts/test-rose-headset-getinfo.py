# -*- coding: utf-8 -*-
"""
FIRST ANGEL（38FF:0001）官方 GET_INFO 查询——帧格式来自官方 first_angel_upgrade.exe 反编译：
- 全查: report 0xAB, [0x92, seq, TLV...]，TLV=[type,len]，1=版本 3=TWS 4=状态 6=通道
- 响应: report 0xAB, [0x47('G'),0x4D('M'), cmd, len, payload...]
纯只读查询，不发 OTA 标识帧。只发 1 条，最多重试 1 次。
"""
import hid, sys, time

VID, PID = 0x38FF, 0x0001
REPORT_ID = 0xAB

def build_getallinfo(seq=1):
    frame = bytearray([0x92, seq & 0xFF])
    for t in (1, 3, 4, 6):          # Version / TWS / Status / Channel
        frame += bytes([t, 0x00])   # TLV: type, len=0
    return bytes(frame)

def parse_resp(data):
    # data[0] = report id (hidapi read 含 ID 视角) 或不含，做两种容忍
    body = data[1:] if data and data[0] == REPORT_ID else data
    if len(body) >= 4 and body[0] == 0x47 and body[1] == 0x4D:
        cmd, ln = body[2], body[3]
        payload = body[4:4+ln]
        return cmd, payload
    return None, None

def main():
    paths = [d['path'] for d in hid.enumerate(VID, PID)]
    if not paths:
        print("设备不在位"); return
    h = hid.device(); h.open_path(paths[0])
    try:
        h.set_nonblocking(False)
    except Exception:
        pass
    frame = build_getallinfo(1)
    out = [REPORT_ID] + list(frame) + [0] * (64 - len(frame))
    hexs = ' '.join(f'{b:02X}' for b in out)
    print(f"TX ({len(out)}B): {hexs}")
    try:
        n = h.write(out)
    except Exception as ex:
        print("write 异常:", ex); n = -1
    if n <= 0:
        # 退回 64 字节窗口重试一次
        out2 = out[:64]
        print(f"write 返回 {n}，改 64B 窗口重试")
        try: n = h.write(out2)
        except Exception as ex: print("重试异常:", ex)
    if n and n > 0:
        print("RX 监听 4s ...")
        t0 = time.time()
        while time.time() - t0 < 4:
            data = h.read(65, timeout_ms=200)
            if data:
                ts = time.strftime('%H:%M:%S')
                hexr = ' '.join(f'{b:02X}' for b in data)
                cmd, payload = parse_resp(data)
                tag = f"cmd=0x{cmd:02X}" if cmd is not None else "非GM帧"
                print(f"[{ts}] ({len(data)}B) {hexr}  <{tag} payload={payload.hex() if payload else '-'}>")
    h.close()

if __name__ == '__main__':
    main()
