#!/usr/bin/env python3
"""进程内存分层采样（纯 ctypes，只读，零写入目标进程）。

用途：判断"内存涨了"到底是**私有提交（真泄漏）**还是**工作集（驻留，天生可回收）**。
输出按 `VirtualQueryEx` 的 Type 分层统计 PRIVATE / MAPPED / IMAGE，另附工作集与句柄数。

用法：
    python probe-process-memory.py VigilWatt.App              # 采样 60 秒，3 秒一拍
    python probe-process-memory.py 14104 600 out.log          # 指定 PID、秒数、输出文件
    python probe-process-memory.py VigilWatt.App 900 out.log 3   # 自定义间隔（秒）

判据（详见 docs/reviews/archive/memory-growth-2026-10-12.md §6）：
    - PRIVATE 列**逐轮上台阶** → 真泄漏，按 review-standard.md §1 升 P0；
    - 只有 WS 列涨、且能被"内存回收"类工具打掉 → **不是泄漏**，涨的是可回收的驻留页。

注意：`VirtualQueryEx` 的"已提交"总量（PRIVATE+MAPPED+IMAGE）**不是**任务管理器的
「提交大小」——IMAGE 与文件后备的 MAPPED 不计入进程提交量，别拿它当内存占用看。
"""
import ctypes
import ctypes.wintypes as w
import os
import sys
import time

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TH32CS_SNAPPROCESS = 0x2
MEM_COMMIT = 0x1000
MEM_PRIVATE, MEM_MAPPED, MEM_IMAGE = 0x20000, 0x40000, 0x1000000
MAX_USER_ADDR = 0x7FFFFFFEFFFF

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class PMC_EX(ctypes.Structure):
    _fields_ = [("cb", w.DWORD), ("PageFaultCount", w.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                ("PrivateUsage", ctypes.c_size_t)]


class MBI64(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", w.DWORD), ("__a1", w.DWORD), ("RegionSize", ctypes.c_ulonglong),
                ("State", w.DWORD), ("Protect", w.DWORD), ("Type", w.DWORD), ("__a2", w.DWORD)]


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", w.DWORD),
                ("cntThreads", w.DWORD), ("th32ParentProcessID", w.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", w.DWORD),
                ("szExeFile", ctypes.c_char * 260)]


def find_pid(name):
    """按进程名（可省 .exe）找 PID；同名多开时返回第一个。"""
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == -1:
        return None
    try:
        entry = PROCESSENTRY32()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
        ok = k32.Process32First(snap, ctypes.byref(entry))
        while ok:
            exe = entry.szExeFile.decode("mbcs", "replace")
            if exe.lower() in (name.lower(), (name + ".exe").lower()):
                return entry.th32ProcessID
            ok = k32.Process32Next(snap, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snap)
    return None


def snapshot_regions(handle):
    """遍历地址空间，按 Type 汇总已提交字节数与区域数。"""
    agg = {MEM_PRIVATE: 0, MEM_MAPPED: 0, MEM_IMAGE: 0, 0: 0}
    cnt = dict.fromkeys(agg, 0)
    mbi = MBI64()
    addr = 0
    while addr < MAX_USER_ADDR:
        if k32.VirtualQueryEx(handle, ctypes.c_void_p(addr), ctypes.byref(mbi),
                              ctypes.sizeof(mbi)) == 0:
            addr += 0x1000
            continue
        if mbi.State == MEM_COMMIT:
            key = mbi.Type if mbi.Type in agg else 0
            agg[key] += mbi.RegionSize
            cnt[key] += 1
        addr = mbi.BaseAddress + mbi.RegionSize if mbi.RegionSize else addr + 0x1000
    return agg, cnt


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    target = sys.argv[1]
    secs = int(sys.argv[2]) if len(sys.argv) > 2 else 60
    out = sys.argv[3] if len(sys.argv) > 3 else ""
    interval = float(sys.argv[4]) if len(sys.argv) > 4 else 3.0

    pid = int(target) if target.isdigit() else find_pid(target)
    if not pid:
        print(f"找不到进程：{target}")
        return 1

    handle = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        print(f"OpenProcess 失败（PID {pid}），err={ctypes.get_last_error()}；可能需要管理员权限")
        return 1

    print(f"PID={pid}  采样 {secs} 秒 / 每 {interval} 秒" + (f"  写入 {out}" if out else ""))
    header = (f"{'time':>8} {'privMB':>7} {'wsMB':>7} {'commitMB':>9} {'handles':>7} | "
              f"{'PRIVATE':>8} {'MAPPED':>8} {'IMAGE':>8}  (regions)")
    print(header)
    fp = open(out, "w", encoding="utf-8") if out else None
    if fp:
        fp.write("time priv_mb ws_mb commit_mb handles | PRIVATE_mb MAPPED_mb IMAGE_mb (regions)\n")

    try:
        start = time.time()
        while time.time() - start < secs:
            pmc = PMC_EX()
            pmc.cb = ctypes.sizeof(PMC_EX)
            k32.K32GetProcessMemoryInfo(handle, ctypes.byref(pmc), pmc.cb)
            handles = w.DWORD(0)
            k32.GetProcessHandleCount(handle, ctypes.byref(handles))
            agg, cnt = snapshot_regions(handle)
            line = (f"{time.strftime('%H:%M:%S'):>8} {pmc.PrivateUsage / 1048576:7.1f} "
                    f"{pmc.WorkingSetSize / 1048576:7.1f} {pmc.PagefileUsage / 1048576:9.1f} "
                    f"{handles.value:7d} | {agg[MEM_PRIVATE] / 1048576:8.1f} "
                    f"{agg[MEM_MAPPED] / 1048576:8.1f} {agg[MEM_IMAGE] / 1048576:8.1f}  "
                    f"({cnt[MEM_PRIVATE]}/{cnt[MEM_MAPPED]}/{cnt[MEM_IMAGE]})")
            print(line)
            if fp:
                fp.write(line.strip() + "\n")
                fp.flush()
            time.sleep(interval)
    except KeyboardInterrupt:
        pass
    finally:
        if fp:
            fp.close()
        k32.CloseHandle(handle)
    return 0


if __name__ == "__main__":
    sys.exit(main())
