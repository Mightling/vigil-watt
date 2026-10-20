# -*- coding: utf-8 -*-
"""
ROSE FIRST ANGEL dongle（38FF:0001）交互式被动监听——零写入。
监听 5 分钟。耳机连着 2.4G 时，耳机上的任何按键操作（音量/播放/模式键）
都会经 dongle 转成 HID 报文出现在这里——这也是"耳机已连接"的探针。
"""
import hid, time, sys

VID, PID = 0x38FF, 0x0001
DUR = 300.0

paths = [d['path'] for d in hid.enumerate(VID, PID)]
if not paths:
    print("dongle 不在位"); raise SystemExit
h = hid.device(); h.open_path(paths[0]); h.set_nonblocking(True)
print(f"监听 5 分钟 —— 请现在戴上耳机并确认 2.4G 模式：", flush=True)
print("  1) 按几下耳机上的音量键/播放键", flush=True)
print("  2) 播放一段音乐再暂停", flush=True)
print("  3) 有电量语音播报的快捷键就按一下", flush=True)
t0 = time.time(); n = 0
while time.time() - t0 < DUR:
    data = h.read(64)
    if data:
        n += 1
        print(f"[{time.strftime('%H:%M:%S')}] ({len(data)}B) {' '.join(f'{b:02X}' for b in data)}", flush=True)
    time.sleep(0.02)
print(f"共 {n} 帧")
h.close()
