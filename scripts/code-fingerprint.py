#!/usr/bin/env python3
"""代码指纹：剥离注释与空白，只保留代码 token，用于证明「只改注释、逻辑零改动」。

用法：
    python code-fingerprint.py snapshot        <out.json>   # 记录 src/ 全部 .cs 的指纹
    python code-fingerprint.py verify          <in.json>    # 与快照比对，逐文件报告差异
    python code-fingerprint.py snapshot-xaml   <out.json>   # 同上，对象换成全部 .xaml
    python code-fingerprint.py verify-xaml     <in.json>

.cs 指纹 = 代码 token 序列的 sha256。字符串/字符字面量内容按原样保留（大小写、空格敏感），
token 之间统一压成单个空格，因此「删注释、删空行、改缩进」不会导致指纹变化，
而任何真实的代码改动（增删改 token）都会被检出。

.xaml 指纹 = 剥掉 <!-- --> 注释、再把空白压成单空格后的 sha256（XAML 没有更细的 token 概念）。
"""

import hashlib
import json
import os
import re
import sys

IDENT_START = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_")
IDENT_BODY = IDENT_START | set("0123456789")


def tokenize(src: str):
    """产出 C# 代码 token 列表（丢弃注释与空白）。"""
    toks = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]

        # 行注释
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            i += 2
            while i < n and src[i] not in "\r\n":
                i += 1
            continue

        # 块注释
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            i += 2
            while i + 1 < n and not (src[i] == "*" and src[i + 1] == "/"):
                i += 1
            i = min(i + 2, n)
            continue

        # 原始字符串字面量（C# 11）"""..."""
        if src.startswith('"""', i):
            j = i + 3
            # 统计开引号个数（支持 4 个及以上）
            q = 3
            while j < n and src[j] == '"':
                q += 1
                j += 1
            close = '"' * q
            k = src.find(close, j)
            end = n if k < 0 else k + q
            toks.append("STR" + src[i:end])
            i = end
            continue

        # 逐字字符串 @"..."（"" 表示一个引号）
        if c == "@" and i + 1 < n and src[i + 1] == '"':
            j = i + 2
            while j < n:
                if src[j] == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            toks.append("STR" + src[i:j])
            i = j
            continue

        # 普通字符串 / 插值字符串
        if c == '"':
            j = i + 1
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == '"':
                    j += 1
                    break
                if src[j] in "\r\n":
                    break
                j += 1
            toks.append("STR" + src[i:j])
            i = j
            continue

        # 字符字面量
        if c == "'":
            j = i + 1
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == "'":
                    j += 1
                    break
                j += 1
            toks.append("CHR" + src[i:j])
            i = j
            continue

        # 标识符 / 关键字
        if c in IDENT_START:
            j = i
            while j < n and src[j] in IDENT_BODY:
                j += 1
            toks.append(src[i:j])
            i = j
            continue

        # 数字（含十六进制、后缀、小数点）
        if c.isdigit():
            j = i
            while j < n and (src[j].isalnum() or src[j] in "._"):
                # 避免吃掉后续的成员访问（如 1.ToString 里的点）
                if src[j] == "." and (j + 1 >= n or not src[j + 1].isdigit()):
                    break
                j += 1
            toks.append(src[i:j])
            i = j
            continue

        # 空白
        if c in " \t\r\n":
            i += 1
            continue

        # 其它符号：多字符运算符尽量整体保留（对指纹无影响，只为可读）
        two = src[i:i + 2]
        if two in ("=>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=",
                   "*=", "/=", "%=", "??", "?.", "<<", ">>", "->", "::"):
            toks.append(two)
            i += 2
            continue
        toks.append(c)
        i += 1
    return toks


def fingerprint(path: str) -> str:
    with open(path, encoding="utf-8-sig", errors="replace") as fh:
        src = fh.read()
    joined = " ".join(tokenize(src))
    return hashlib.sha256(joined.encode("utf-8")).hexdigest()


def xaml_fingerprint(path: str) -> str:
    with open(path, encoding="utf-8-sig", errors="replace") as fh:
        src = fh.read()
    src = re.sub(r"<!--.*?-->", " ", src, flags=re.S)
    src = re.sub(r"\s+", " ", src).strip()
    return hashlib.sha256(src.encode("utf-8")).hexdigest()


def _collect(root, exts, fn_of):
    out = {}
    for dp, dn, fn in os.walk(root):
        if "obj" in dp or "bin" in dp:
            continue
        for f in fn:
            if any(f.endswith(e) for e in exts):
                p = os.path.join(dp, f)
                out[os.path.relpath(p, root).replace("\\", "/")] = fn_of(p)
    return out


def collect(root="src"):
    return _collect(root, (".cs",), fingerprint)


def collect_xaml(root="src"):
    return _collect(root, (".xaml",), xaml_fingerprint)


def main():
    modes = ("snapshot", "verify", "snapshot-xaml", "verify-xaml")
    if len(sys.argv) != 3 or sys.argv[1] not in modes:
        print(__doc__)
        return 2
    mode, target = sys.argv[1], sys.argv[2]

    is_xaml = mode.endswith("-xaml")
    is_snapshot = mode.startswith("snapshot")
    collector = collect_xaml if is_xaml else collect
    label = ".xaml" if is_xaml else ".cs"

    if is_snapshot:
        data = collector()
        with open(target, "w", encoding="utf-8") as fh:
            json.dump(data, fh, ensure_ascii=False, indent=1, sort_keys=True)
        print(f"已记录 {len(data)} 个 {label} 文件的指纹 -> {target}")
        return 0

    with open(target, encoding="utf-8") as fh:
        old = json.load(fh)
    new = collector()

    changed = [k for k in sorted(set(old) & set(new)) if old[k] != new[k]]
    added = sorted(set(new) - set(old))
    removed = sorted(set(old) - set(new))

    print(f"快照文件数={len(old)}  当前文件数={len(new)}")
    if changed:
        print(f"\n[!] 代码 token 发生变化（{len(changed)} 个）—— 不允许：")
        for k in changed:
            print(f"    {k}")
    if added:
        print(f"\n[+] 新增文件（{len(added)}）：")
        for k in added:
            print(f"    {k}")
    if removed:
        print(f"\n[-] 删除文件（{len(removed)}）：")
        for k in removed:
            print(f"    {k}")

    if not changed and not added and not removed:
        print(f"\n[OK] 全部 {label} 的代码内容完全一致：逻辑零改动。")
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
