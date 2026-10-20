# -*- coding: utf-8 -*-
"""
弱水时砂 FIRST ANGEL（38FF:0001 USB 直连）HID 被动监听——零写入。
监听 MI_03 接口的 input report 60 秒，记录耳机自发推送的所有帧。
协议背景：官方升级助手 first_angel_upgrade.exe 用 report 0xAB (64B) 通道，
设备响应帧头 47 4D ("GM")。若耳机主动推 0xAB 帧会在这里现身。
"""
import hid, sys, time

VID, PID = 0x38FF, 0x0001
DUR = 60.0

def main():
    paths = [d['path'] for d in hid.enumerate(VID, PID)]
    if not paths:
        print(f"未找到 0x{VID:04X}:0x{PID:04X}，耳机没插 USB 线？"); return
    for p in paths:
        print("interface path:", p)
    h = hid.device()
    h.open_path(paths[0])
    h.set_nonblocking(False)
    print(f"监听 {DUR:.0f}s（期间可操作耳机：播放/暂停/音量/戴上取下/看电量语音）...")
    t0 = time.time()
    n = 0
    while time.time() - t0 < DUR:
        data = h.read(64, timeout_ms=200)
        if data:
            n += 1
            ts = time.strftime('%H:%M:%S')
            hexs = ' '.join(f'{b:02X}' for b in data)
            print(f"[{ts}] ({len(data)}B) {hexs}")
    print(f"共 {n} 帧。无输出 = 该接口只报 Consumer 键（音量/播放），无电量推送。")
    h.close()

if __name__ == '__main__':
    main()
