#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抠图 — 把图案提取成透明 PNG。

两种做法
  color   按背景色抠：给定背景色，把接近它的像素变透明（适合深色/纯色底）
  flood   按连通域抠：只保留跟种子点相连的前景（适合图案和背景分开的）

出来的 PNG 可当素材直接嵌网页，也可以再喂给 trace.py 描成 SVG。

用法
  # 背景是浅灰，抠出深色字标
  python extract.py 图.png --region 114,310,263,332 --mode color --bg "#807C77" -o 字标.png

  # 自动取区域四角的颜色当背景色
  python extract.py 图.png --region 114,310,263,332 --mode color --auto-bg --tolerance 40

  # 只保留最大的连通块（去掉零碎噪点）
  python extract.py 图.png --region 0,0,600,600 --mode color --auto-bg --largest
"""
import argparse
import sys
from collections import deque

import numpy as np
from PIL import Image


def hex2rgb(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def auto_bg(a):
    """取四条边的中位色当背景色。"""
    h, w, _ = a.shape
    edges = np.concatenate([
        a[0, :, :], a[h - 1, :, :], a[:, 0, :], a[:, w - 1, :]
    ], axis=0)
    return np.median(edges, axis=0)


def largest_component(mask):
    h, w = mask.shape
    lab = np.zeros((h, w), np.int32)
    best_id, best_n = 0, 0
    cur = 0
    ys, xs = np.nonzero(mask)
    for y0, x0 in zip(ys.tolist(), xs.tolist()):
        if lab[y0, x0]:
            continue
        cur += 1
        lab[y0, x0] = cur
        stack = [(y0, x0)]
        n = 0
        while stack:
            y, x = stack.pop()
            n += 1
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not lab[ny, nx]:
                        lab[ny, nx] = cur
                        stack.append((ny, nx))
        if n > best_n:
            best_n, best_id = n, cur
    return lab == best_id


def main():
    p = argparse.ArgumentParser(description='抠图为透明 PNG')
    p.add_argument('image')
    p.add_argument('--region', required=True, help='处理区域 x0,y0,x1,y1')
    p.add_argument('--mode', choices=['color', 'flood'], default='color')
    p.add_argument('--bg', default=None, help='背景色（#RRGGBB）；不给则用 --auto-bg')
    p.add_argument('--auto-bg', action='store_true', help='自动取边缘中位色当背景')
    p.add_argument('--tolerance', type=float, default=32,
                   help='与背景色的距离阈值，小于它算背景')
    p.add_argument('--invert', action='store_true',
                   help='反过来：保留接近背景色的部分')
    p.add_argument('--largest', action='store_true', help='只保留最大连通块')
    p.add_argument('--feather', type=int, default=1,
                   help='边缘羽化半径（像素），0=硬边')
    p.add_argument('--scale', type=float, default=1.0, help='缩放倍数')
    p.add_argument('-o', '--output', required=True)
    args = p.parse_args()

    im = Image.open(args.image).convert('RGB')
    x0, y0, x1, y1 = [int(v) for v in args.region.split(',')]
    crop = im.crop((x0, y0, x1, y1))
    a = np.asarray(crop).astype(np.float64)
    h, w, _ = a.shape

    if args.bg:
        bg = np.array(hex2rgb(args.bg), float)
    elif args.auto_bg:
        bg = auto_bg(a)
    else:
        raise SystemExit('需要 --bg 或 --auto-bg')

    d = np.sqrt(((a - bg) ** 2).sum(axis=2))
    fg = d >= args.tolerance
    if args.invert:
        fg = ~fg

    if args.largest:
        fg = largest_component(fg)

    if fg.sum() == 0:
        raise SystemExit('没抠出任何前景，调小 --tolerance 或检查 --bg')

    # alpha：前景不透明，背景按距离渐变（羽化），避免硬锯齿
    if args.feather > 0:
        alpha = np.clip((d - args.tolerance * 0.5) / (args.tolerance * 0.5), 0, 1)
        alpha = np.where(fg, np.maximum(alpha, 0.85), 0.0)
    else:
        alpha = fg.astype(float)

    out = np.dstack([a, alpha * 255]).astype(np.uint8)
    img = Image.fromarray(out, 'RGBA')

    if args.scale != 1.0:
        nw = max(1, int(round(img.width * args.scale)))
        nh = max(1, int(round(img.height * args.scale)))
        img = img.resize((nw, nh), Image.LANCZOS)

    img.save(args.output)

    ys, xs = np.nonzero(fg)
    print(f'已抠图 {args.output}  ({img.width}x{img.height})')
    print(f'  背景色 {tuple(int(v) for v in bg)}  容差 {args.tolerance}')
    print(f'  前景 {int(fg.sum())} 像素 / {h*w} ({100*fg.mean():.1f}%)')
    print(f'  前景 bbox x{xs.min()}..{xs.max()}  y{ys.min()}..{ys.max()}')


if __name__ == '__main__':
    main()
