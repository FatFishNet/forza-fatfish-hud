#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""取色 — 一个区域的主色是哪些，各自的准确色值。

本项目所有配色都从这里来：奶油白 #EBD2B2、金 #D5AD4A、红 #D91A24
都是这类工具量出来的。measure.py 只给亮度，给不了颜色。

两种模式
  调色板：给区域里占主导的颜色 + 各自占比
  候选比：判断某个已知色值到底对不对（比如验证 #EBD2B2 是不是奶油白）

用法
  python colors.py 图.png --region 300,200,400,300
  python colors.py 图.png --region 300,200,400,300 --top 8
  python colors.py 图.png --region 300,200,400,300 --candidates "#EBD2B2,#D5AD4A,#D91A24"
"""
import argparse
import json
import sys

import numpy as np
from PIL import Image

from imgload import add_flatten_args, load_rgb


def hex2rgb(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def rgb2hex(rgb):
    return '#%02X%02X%02X' % tuple(int(round(v)) for v in rgb)


def lab(rgb):
    """粗略 LAB（用于感知距离），只求够用来排序。"""
    r, g, b = [v / 255.0 for v in rgb]
    x = r * 0.4124 + g * 0.3576 + b * 0.1805
    y = r * 0.2126 + g * 0.7152 + b * 0.0722
    z = r * 0.0193 + g * 0.1192 + b * 0.9505

    def f(t):
        return t ** (1 / 3.0) if t > 0.008856 else 7.787 * t + 16 / 116.0

    fx, fy, fz = f(x / 0.9505), f(y / 1.0), f(z / 1.089)
    return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz))


def dist(c1, c2):
    a, b = lab(c1), lab(c2)
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


def main():
    p = argparse.ArgumentParser(description='取色 / 主色分析')
    p.add_argument('image')
    p.add_argument('--region', default=None, help='限定区域 x0,y0,x1,y1')
    p.add_argument('--top', type=int, default=6, help='输出前 N 个主色')
    p.add_argument('--candidates', default=None,
                   help='候选色值，逗号分隔，例如 "#EBD2B2,#D5AD4A"')
    p.add_argument('--quantize', type=int, default=12,
                   help='量化步长（0-255 里每几级合并，默认 12）')
    p.add_argument('--ignore-dark', type=float, default=None,
                   help='忽略亮度低于此值的像素（去掉背景）')
    add_flatten_args(p)
    args = p.parse_args()

    im = load_rgb(args.image, args.flatten, args.premultiplied)
    if args.region:
        x0, y0, x1, y1 = [int(v) for v in args.region.split(',')]
        im = im.crop((x0, y0, x1, y1))

    a = np.asarray(im).astype(np.float64)
    flat = a.reshape(-1, 3)
    if args.ignore_dark is not None:
        keep = flat.mean(axis=1) >= args.ignore_dark
        flat = flat[keep]
        if len(flat) == 0:
            raise SystemExit('过滤后没有像素了，调小 --ignore-dark')

    out = {
        'image': args.image,
        'region': args.region,
        'pixels': int(len(flat)),
    }

    if args.candidates:
        cands = [hex2rgb(c.strip()) for c in args.candidates.split(',')]
        out['candidates'] = []
        for rgb in cands:
            d = np.sqrt(((flat - np.array(rgb)) ** 2).sum(axis=1))
            near = d < 24
            out['candidates'].append({
                'hex': rgb2hex(rgb),
                'exact_match_px': int(near.sum()),
                'exact_match_frac': round(float(near.mean()), 4),
                'mean_dist': round(float(d.mean()), 2),
            })
        # 排序：命中多的排前
        out['candidates'].sort(key=lambda c: -c['exact_match_frac'])

    # 调色板：量化 + 计数
    q = args.quantize
    if q > 1:
        key = (np.round(flat / q) * q).astype(np.int32)
    else:
        key = flat.astype(np.int32)
    # 用整数编码做计数
    code = (key[:, 0] << 16) | (key[:, 1] << 8) | key[:, 2]
    uniq, counts = np.unique(code, return_counts=True)
    order = np.argsort(-counts)[:args.top]

    palette = []
    total = len(flat)
    for i in order:
        c = uniq[i]
        rgb = ((c >> 16) & 255, (c >> 8) & 255, c & 255)
        # 用该量化桶内像素的真实均值，比桶中心更准
        sel = flat[code == c]
        mean_rgb = sel.mean(axis=0)
        palette.append({
            'hex': rgb2hex(mean_rgb),
            'rgb': [int(round(v)) for v in mean_rgb],
            'count': int(counts[i]),
            'frac': round(float(counts[i]) / total, 4),
        })
    out['palette'] = palette

    sys.stdout.reconfigure(encoding='utf-8')
    print(json.dumps(out, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
