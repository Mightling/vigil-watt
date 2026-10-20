# -*- coding: utf-8 -*-
"""
ROSE FIRST ANGEL 2.4G dongle（38FF:0001，USB 声卡+HID 复合）被动监听——零写入。
监听 MI_03 HID（usage 0x0C consumer，in=3）60 秒，记录全部 input report。
"""
import hid, time

VID, PID = 0x38FF, 0x0001
DUR = 60.0

paths = [d['path'] for d in hid.enumerate(VID, PID)]
if not paths:
    print("dongle 不在位"); raise SystemExit
h = hid.device(); h.open_path(paths[0]); h.set_nonblocking(False)
print(f"监听 38FF:0001 MI_03 {DUR:.0f}s —— 请确保耳机开着、2.4G 已连上 dongle，期间戴/摘/播放/暂停/调音量")
t0 = time.time(); n = 0
while time.time() - t0 < DUR:
    data = h.read(16, timeout_ms=200)
    if data:
        n += 1
        print(f"[{time.strftime('%H:%M:%S')}] ({len(data)}B) {' '.join(f'{b:02X}' for b in data)}")
print(f"共 {n} 帧")
h.close()
