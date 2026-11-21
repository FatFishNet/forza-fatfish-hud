#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""位图描摹 — 把图案变成 SVG path。

从 panel-cluster-altezza 的 LEXUS 字标演进而来，通用化。

流程
  裁区域 → 放大 → 阈值二值化 → marching squares 取轮廓
  → 串成闭环 → RDP 简化 → 归一化坐标 → 输出 SVG / 纯 path

两种输出
  --format svg    完整可打开的 SVG 文件（默认）
  --format path   只给 path 的 d 属性 + 元数据 JSON（塞进现有文件用）

用法
  python trace.py refs/a.png --region 114,310,263,332 --scale 6 --threshold 88
  python trace.py refs/a.png --region 114,310,263,332 --format path -o word.json
"""
import argparse
import json
import sys

import numpy as np
from PIL import Image


# ----------------------------------------------------------------------------
# 轮廓提取
# ----------------------------------------------------------------------------

def marching_squares(mask):
    """对二值掩膜取等值线，返回线段列表 [(p0, p1), ...]，坐标为格点。"""
    H, W = mask.shape
    P = np.zeros((H + 2, W + 2), bool)
    P[1:-1, 1:-1] = mask

    segs = []
    for y in range(H + 1):
        row_a = P[y]
        row_b = P[y + 1]
        for x in range(W + 1):
            a = row_a[x]
            b = row_a[x + 1]
            c = row_b[x + 1]
            d = row_b[x]
            case = (1 if a else 0) * 8 + (1 if b else 0) * 4 + (1 if c else 0) * 2 + (1 if d else 0)
            if case in (0, 15):
                continue
            T = (x + 0.5, y)
            B = (x + 0.5, y + 1.0)
            L = (x + 0.0, y + 0.5)
            R = (x + 1.0, y + 0.5)
            table = {
                1: [(L, B)], 2: [(B, R)], 3: [(L, R)], 4: [(R, T)],
                5: [(L, T), (R, B)], 6: [(B, T)], 7: [(L, T)],
                8: [(T, L)], 9: [(T, B)], 10: [(T, R), (L, B)],
                11: [(T, R)], 12: [(L, R)], 13: [(R, B)], 14: [(L, B)],
            }
            segs.extend(table[case])
    return segs


def chain_loops(segs):
    """把线段串成闭合环。"""
    def key(p):
        return (round(p[0], 3), round(p[1], 3))

    adj = {}
    for i, (s, e) in enumerate(segs):
        adj.setdefault(key(s), []).append(i)
        adj.setdefault(key(e), []).append(i)

    used = set()
    loops = []
    for i in range(len(segs)):
        if i in used:
            continue
        used.add(i)
        s, e = segs[i]
        loop = [s, e]
        cur = key(e)
        while cur != key(loop[0]):
            nxt = None
            for j in adj.get(cur, []):
                if j in used:
                    continue
                a, b = segs[j]
                if key(a) == cur:
                    nxt = (j, b)
                elif key(b) == cur:
                    nxt = (j, a)
                if nxt:
                    break
            if not nxt:
                break
            used.add(nxt[0])
            loop.append(nxt[1])
            cur = key(nxt[1])
        if len(loop) >= 4:
            loops.append(loop)
    return loops


def rdp(points, eps):
    """Ramer–Douglas–Peucker 简化。"""
    if len(points) < 3:
        return points
    a = np.array(points[0], float)
    b = np.array(points[-1], float)
    ab = b - a
    n = np.linalg.norm(ab)
    pts = np.array(points, float)
    if n == 0:
        d = np.linalg.norm(pts - a, axis=1)
    else:
        d = np.abs(np.cross(np.broadcast_to(ab, (len(pts), 2)), pts - a)) / n
    i = int(np.argmax(d))
    if d[i] > eps:
        left = rdp(points[:i + 1], eps)
        right = rdp(points[i:], eps)
        return left[:-1] + right
    return [points[0], points[-1]]


# ----------------------------------------------------------------------------

def fmt(v):
    s = f'{v:.1f}'
    return s[:-2] if s.endswith('.0') else s


def main():
    p = argparse.ArgumentParser(description='位图描摹成 SVG path')
    p.add_argument('image')
    p.add_argument('--region', required=True, help='裁剪区域 x0,y0,x1,y1')
    p.add_argument('--scale', type=int, default=6, help='放大倍数（默认 6）')
    p.add_argument('--threshold', type=float, required=True,
                   help='二值化阈值（lum 低于此值算前景）')
    p.add_argument('--invert', action='store_true', help='反转：lum 高于阈值算前景')
    p.add_argument('--eps', type=float, default=0.9,
                   help='RDP 容差，单位是「放大后的像素」；实际用 eps*scale。'
                        '放大 6x 时默认 0.9 相当于原图 0.9px 的精度')
    p.add_argument('--target-width', type=float, default=1000,
                   help='归一化后的宽度')
    p.add_argument('--min-loop', type=int, default=8,
                   help='轮廓最少点数，低于此丢弃（去噪）')
    p.add_argument('--fill', default='#1B191E', help='SVG 填充色')
    p.add_argument('--format', choices=['svg', 'path'], default='svg')
    p.add_argument('-o', '--output', default=None, help='输出文件；省略则打到 stdout')
    args = p.parse_args()

    im = Image.open(args.image).convert('L')
    x0, y0, x1, y1 = [int(v) for v in args.region.split(',')]
    crop = im.crop((x0, y0, x1, y1))
    if args.scale > 1:
        crop = crop.resize((crop.width * args.scale, crop.height * args.scale), Image.LANCZOS)
    g = np.asarray(crop).astype(float)

    mask = (g > args.threshold) if args.invert else (g < args.threshold)
    if mask.sum() == 0:
        raise SystemExit('阈值没选中任何像素，调整 --threshold')

    segs = marching_squares(mask)
    loops = chain_loops(segs)
    loops = [l for l in loops if len(l) >= args.min_loop]

    paths = []
    eps_abs = args.eps * args.scale     # 容差按放大倍数折算，和原图精度挂钩
    for loop in loops:
        closed = loop[:-1] if (round(loop[0][0], 3) == round(loop[-1][0], 3)
                               and round(loop[0][1], 3) == round(loop[-1][1], 3)) else loop
        simp = rdp(closed + [closed[0]], eps_abs)
        if len(simp) >= 3:
            paths.append(simp)

    if not paths:
        raise SystemExit('没有提取到有效轮廓')

    allx = [pt[0] for l in paths for pt in l]
    ally = [pt[1] for l in paths for pt in l]
    minx, maxx = min(allx), max(allx)
    miny, maxy = min(ally), max(ally)
    w = maxx - minx
    h = maxy - miny
    k = args.target_width / w if w else 1.0

    d_parts = []
    for loop in paths:
        pts = [((pt[0] - minx) * k, (pt[1] - miny) * k) for pt in loop]
        d_parts.append('M' + fmt(pts[0][0]) + ' ' + fmt(pts[0][1])
                       + ''.join('L' + fmt(x) + ' ' + fmt(y) for x, y in pts[1:]) + 'Z')
    d = ''.join(d_parts)

    box_w = round(args.target_width, 1)
    box_h = round(h * k, 1)

    if args.format == 'path':
        out = {
            'box': [box_w, box_h],
            'loops': len(paths),
            'points': sum(len(l) for l in paths),
            'd': d,
        }
        text = json.dumps(out, ensure_ascii=False, indent=2)
    else:
        text = f'''<?xml version="1.0" encoding="UTF-8"?>
<!-- 由 trace.py 描摹生成
     源图：{args.image}
     区域：{args.region}   放大：{args.scale}x   阈值：{args.threshold}
     轮廓：{len(paths)} 个，共 {sum(len(l) for l in paths)} 点
     fill-rule="evenodd" 必须保留，否则内部镂空会被填死 -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {box_w} {box_h}"
     width="{box_w}" height="{box_h}">
  <path fill="{args.fill}" fill-rule="evenodd"
        d="{d}"/>
</svg>
'''

    if args.output:
        with open(args.output, 'w', encoding='utf-8') as f:
            f.write(text)
        print(f'已写入 {args.output}  ({len(text)} 字符, {len(paths)} 轮廓)')
    else:
        sys.stdout.reconfigure(encoding='utf-8')
        print(text)


if __name__ == '__main__':
    main()
