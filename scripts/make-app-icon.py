#!/usr/bin/env python3
"""生成 VigilWatt 程序图标（纯标准库，无需 Pillow）。

用法：
    python scripts/make-app-icon.py                # 写 src/VigilWatt.App/Assets/VigilWatt.ico
    python scripts/make-app-icon.py --preview out.png

设计：绿色圆角方块 + 白色闪电（watt），解析式绘制 + 超采样抗锯齿。
输出 ICO 含 16/20/24/32/40/48/64/128/256 九种尺寸（>=64 用 PNG 条目，其余用 DIB）。
"""

from __future__ import annotations

import argparse
import os
import struct
import zlib

# ---------- 配色 ----------
TOP = (0x3E, 0xD0, 0x7A)      # 顶部亮绿
BOTTOM = (0x1C, 0x94, 0x4E)   # 底部深绿
GLYPH = (0xFF, 0xFF, 0xFF)    # 闪电白

# ---------- 背景圆角方块 ----------
BG0, BG1, BG_R = 0.02, 0.98, 0.22

# ---------- 闪电多边形（归一化，y 向下） ----------
BOLT = [(0.585, 0.095), (0.275, 0.545), (0.455, 0.545),
        (0.395, 0.905), (0.735, 0.435), (0.535, 0.435)]


def in_round_rect(x, y, x0, y0, x1, y1, r):
    """点是否落在圆角矩形内。"""
    if r <= 0:
        return x0 <= x <= x1 and y0 <= y <= y1
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    dx, dy = x - cx, y - cy
    return dx * dx + dy * dy <= r * r


def in_polygon(x, y, pts):
    """射线法判断点是否在多边形内。"""
    inside = False
    n = len(pts)
    j = n - 1
    for i in range(n):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def sample(x, y):
    """返回 (背景覆盖, 白色覆盖)，x/y 为 0..1 归一化坐标。"""
    if not in_round_rect(x, y, BG0, BG0, BG1, BG1, BG_R):
        return 0.0, 0.0
    return 1.0, 1.0 if in_polygon(x, y, BOLT) else 0.0


def render(size):
    """渲染一个尺寸，返回 RGBA 行列表（自上而下）。"""
    ss = 4 if size <= 64 else 3
    inv = 1.0 / (size * ss)
    n = ss * ss
    rows = []
    for py in range(size):
        row = []
        v = (py + 0.5) / size
        br = int(TOP[0] + (BOTTOM[0] - TOP[0]) * v)
        bg_g = int(TOP[1] + (BOTTOM[1] - TOP[1]) * v)
        bb = int(TOP[2] + (BOTTOM[2] - TOP[2]) * v)
        for px in range(size):
            bg_acc = white_acc = 0.0
            for sy in range(ss):
                y = (py * ss + sy + 0.5) * inv
                for sx in range(ss):
                    b, w = sample((px * ss + sx + 0.5) * inv, y)
                    bg_acc += b
                    white_acc += w
            bg_cov = bg_acc / n
            if bg_cov <= 0:
                row.append((0, 0, 0, 0))
                continue
            a = white_acc / bg_acc if bg_acc > 0 else 0.0
            r = int(br * (1 - a) + GLYPH[0] * a)
            g = int(bg_g * (1 - a) + GLYPH[1] * a)
            b2 = int(bb * (1 - a) + GLYPH[2] * a)
            row.append((r, g, b2, int(round(255 * bg_cov))))
        rows.append(row)
    return rows


# ---------- 编码 ----------

def encode_png(rows):
    h = len(rows)
    w = len(rows[0])
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def encode_dib(rows):
    h = len(rows)
    w = len(rows[0])
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    for row in reversed(rows):                    # DIB 自下而上
        for r, g, b, a in row:
            xor += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4
    and_mask = bytes(mask_row * h)                # 32 位图标：透明由 alpha 决定，AND 掩码全 0
    return header + bytes(xor) + and_mask


def build_ico(sizes):
    images = []
    for size in sizes:
        rows = render(size)
        data = encode_png(rows) if size >= 64 else encode_dib(rows)
        images.append((size, data))

    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        d = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", d, d, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data
    return bytes(out)


def build_preview(sizes, scale=4, pad=8):
    """把各尺寸按 scale 放大后横向拼成一张预览 PNG，便于肉眼检查。"""
    cells = []
    for size in sizes:
        rows = render(size)
        big = [[rows[y // scale][x // scale] for x in range(size * scale)]
               for y in range(size * scale)]
        cells.append(big)

    total_w = sum(len(c[0]) for c in cells) + pad * (len(cells) + 1)
    total_h = max(len(c) for c in cells) + pad * 2
    canvas = [[(0, 0, 0, 0)] * total_w for _ in range(total_h)]
    xoff = pad
    for cell in cells:
        for y, row in enumerate(cell):
            canvas[y + pad][xoff:xoff + len(row)] = row
        xoff += len(cell[0]) + pad
    return encode_png(canvas)


def main():
    ap = argparse.ArgumentParser()
    here = os.path.dirname(os.path.abspath(__file__))
    default_out = os.path.join(here, "..", "src", "VigilWatt.App", "Assets", "VigilWatt.ico")
    ap.add_argument("-o", "--out", default=os.path.normpath(default_out))
    ap.add_argument("--preview", help="额外输出一张放大预览 PNG")
    args = ap.parse_args()

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    ico = build_ico(sizes)
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(ico)
    print(f"已写出 {args.out}（{len(ico)} 字节，{len(sizes)} 种尺寸）")

    if args.preview:
        with open(args.preview, "wb") as f:
            f.write(build_preview(sizes))
        print(f"预览图 {args.preview}")


if __name__ == "__main__":
    main()
