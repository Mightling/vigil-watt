#!/usr/bin/env python3
"""Windows 全内存 dump（minidump）内存布局解剖（纯标准库，只读）。

用途：一份 .DMP 动辄几百 MB，但**文件大小不等于进程占用**。本脚本直接解析 minidump 的
stream，把内存按 `VirtualQueryEx` 的 Type 分层算清楚，并给出进程时长、句柄、模块等旁证。

用法：
    python parse-minidump.py C:\\path\\VigilWatt.App.DMP

输出：
    ① 进程启动时间与运行时长（与 dump 文件时间对比——**先确认 dump 抓在什么时刻**，
       抓到"刚启动"的 dump 只能当基线，证明不了泄漏）；
    ② 句柄数 / 模块数（查重复 LoadLibrary）/ 线程数；
    ③ Memory64List：dump 实际抓到的字节数；
    ④ MemoryInfoList：已提交字节数按 PRIVATE / MAPPED / IMAGE 分层 + 最大区域 + 私有块尺寸直方图。

关键口径（别搞错，详见 docs/reviews/archive/memory-growth-2026-10-12.md §6）：
    IMAGE 与**文件后备**的 MAPPED **不计入进程提交量**。所以"已提交总量"远大于任务管理器的
    「提交大小」是正常的；判断泄漏只看 **PRIVATE** 那一层。

托管侧（托管堆 / 对象计数）本脚本不解析，用 dotnet-dump：
    dotnet-dump analyze <dump> -c "dumpheap -stat" -c "exit"
"""
import collections
import datetime
import os
import struct
import sys

MEM_COMMIT = 0x1000
MEM_PRIVATE, MEM_MAPPED, MEM_IMAGE = 0x20000, 0x40000, 0x1000000
TYPE_NAME = {MEM_PRIVATE: "PRIVATE", MEM_MAPPED: "MAPPED", MEM_IMAGE: "IMAGE"}
MB = 1048576


class Dump:
    def __init__(self, path):
        self.f = open(path, "rb")
        self.path = path
        sig, self.version, nstreams, dir_rva = struct.unpack_from("<IIII", self.f.read(32), 0)
        if sig != 0x504D444D:
            raise ValueError("不是 minidump（缺 MDMP 签名）")
        self.streams = {}
        self.f.seek(dir_rva)
        for _ in range(nstreams):
            st, size, rva = struct.unpack("<III", self.f.read(12))
            self.streams.setdefault(st, []).append((size, rva))
        self._load_ranges()
        self._load_modules()

    def read(self, rva, n):
        self.f.seek(rva)
        return self.f.read(n)

    def stream(self, kind):
        return self.streams.get(kind, [(0, 0)])[0]

    # ---- 地址 -> 文件偏移（Memory64List）----
    def _load_ranges(self):
        self.ranges = []
        size, rva = self.stream(9)
        if not size:
            return
        data = self.read(rva, size)
        n = struct.unpack_from("<Q", data, 0)[0]
        base_rva = struct.unpack_from("<Q", data, 8)[0]
        off = 16
        for _ in range(n):
            start, dsize = struct.unpack_from("<QQ", data, off)
            self.ranges.append((start, dsize, base_rva))
            base_rva += dsize
            off += 16
        self.ranges.sort()

    def read_mem(self, addr, length):
        lo, hi = 0, len(self.ranges) - 1
        while lo <= hi:
            mid = (lo + hi) // 2
            start, dsize, foff = self.ranges[mid]
            if start <= addr < start + dsize:
                take = min(length, start + dsize - addr)
                self.f.seek(foff + (addr - start))
                return self.f.read(take)
            if addr < start:
                hi = mid - 1
            else:
                lo = mid + 1
        return b""

    # ---- 模块 ----
    def _load_modules(self):
        self.modules = []
        size, rva = self.stream(4)
        if not size:
            return
        data = self.read(rva, size)
        n = struct.unpack_from("<I", data, 0)[0]
        off = 4
        raw = []
        for _ in range(n):
            base, isize, _csum, _tds, name_rva = struct.unpack_from("<QIIII", data, off)
            raw.append((base, isize, name_rva))
            off += 108
        for base, isize, name_rva in raw:
            length = struct.unpack_from("<I", self.read(name_rva, 4), 0)[0]
            name = self.read(name_rva + 4, length).decode("utf-16-le", "replace")
            self.modules.append((base, isize, name))

    def regions(self):
        """(base, region_size, state, protect, type) 列表（MemoryInfoList）。"""
        out = []
        size, rva = self.stream(16)
        if not size:
            return out
        data = self.read(rva, size)
        header, entry_size, n = struct.unpack_from("<IIQ", data, 0)
        off = header
        for _ in range(n):
            base, _abase, _aprot, _a1, rsize, state, prot, mtype, _a2 = \
                struct.unpack_from("<QQIIQIIII", data, off)
            out.append((base, rsize, state, prot, mtype))
            off += entry_size
        return out


def filetime_to_local(value):
    """FILETIME(1601 起 100ns) -> 本地时间。**别直接当 Unix 时间加偏移，会差一个时区。**"""
    return datetime.datetime.fromtimestamp(value / 1e7 - 11644473600)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    path = sys.argv[1]
    if not os.path.isfile(path):
        print(f"文件不存在：{path}")
        return 1

    d = Dump(path)
    print(f"dump      : {path}")
    print(f"文件大小  : {os.path.getsize(path) / MB:.1f} MB   写入时间 {datetime.datetime.fromtimestamp(os.path.getmtime(path))}")
    print(f"签名版本  : {d.version:#x}   流数 {len(d.streams)}")

    # 进程时长（ThreadInfoListStream）
    size, rva = d.stream(17)
    if size:
        data = d.read(rva, size)
        header, entry_size, n = struct.unpack_from("<III", data, 0)
        starts = []
        for i in range(n):
            off = header + i * entry_size
            starts.append(struct.unpack_from("<Q", data, off + 16)[0])
        if starts:
            start = filetime_to_local(min(starts))
            print(f"线程数    : {n}")
            print(f"进程启动  : {start}   运行时长 ≈ {datetime.datetime.fromtimestamp(os.path.getmtime(path)) - start}")

    # 句柄数（HandleDataStream）
    size, rva = d.stream(12)
    if size:
        _hdr, _desc, count, _res = struct.unpack_from("<4I", d.read(rva, 16), 0)
        print(f"句柄数    : {count}")

    # 模块
    if d.modules:
        total = sum(m[1] for m in d.modules)
        names = collections.Counter(os.path.basename(m[2]) for m in d.modules)
        dup = {k: v for k, v in names.items() if v > 1}
        print(f"模块      : {len(d.modules)} 个，映像合计 {total / MB:.1f} MB"
              f"   重名: {dup if dup else '无'}")
        print("  最大的 10 个：")
        for base, isize, name in sorted(d.modules, key=lambda m: -m[1])[:10]:
            print(f"    {isize / MB:7.1f} MB  {base:#014x}  {name}")

    # 抓到的内存量
    if d.ranges:
        print(f"dump 实抓 : {sum(r[1] for r in d.ranges) / MB:.1f} MB（{len(d.ranges)} 个区间）")

    # 分层
    committed = [r for r in d.regions() if r[2] == MEM_COMMIT]
    if not committed:
        return 0
    by_type = collections.Counter()
    cnt = collections.Counter()
    for _base, rsize, _state, _prot, mtype in committed:
        by_type[mtype] += rsize
        cnt[mtype] += 1
    print("已提交虚拟内存按类型（注意：IMAGE 与文件后备 MAPPED 不计入进程提交量）")
    for mtype, nbytes in by_type.most_common():
        print(f"   {TYPE_NAME.get(mtype, hex(mtype)):8s} {nbytes / MB:9.1f} MB   区域 {cnt[mtype]}")
    print(f"   {'合计':8s} {sum(by_type.values()) / MB:9.1f} MB")

    print("最大的 10 个已提交区域")
    for base, rsize, _state, prot, mtype in sorted(committed, key=lambda r: -r[1])[:10]:
        print(f"   {rsize / MB:8.2f} MB  {base:#014x}  {TYPE_NAME.get(mtype, hex(mtype)):8s} prot={prot:#x}")

    private = [r for r in committed if r[4] == MEM_PRIVATE]
    print(f"私有内存  : {sum(r[1] for r in private) / MB:.1f} MB / {len(private)} 个区域"
          "   （逐轮上台阶才是泄漏；尺寸分布里出现海量同尺寸小块也是信号）")
    hist = collections.Counter(r[1] for r in private)
    for rsize, c in hist.most_common(5):
        print(f"   {rsize / 1024:9.1f} KB × {c}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
