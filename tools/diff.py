#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""图像差异 — 找出两张图哪里不一样，输出可定位的差异框。

不是"追像素差到 0"，而是回答两个问题：
  1. 差在哪（框的坐标，能贴到图上继续看）
  2. 差多少（各自区域的均值、主要差在哪个通道）

差异率本身在「故意改了内容」的项目里没有参考价值 ——
本项目 MPG→BOOST、160→220 都是有意改的，差异率恒高。
所以这里的重点是**定位**，不是评分。

用法
  python diff.py 参考.png 渲染.png
  python diff.py a.png b.png --grid 8 --top 10 --tolerance 20
  python diff.py a.png b.png --heatmap out.png
"""
import argparse
import json
import sys

import numpy as np
from PIL import Image

from imgload import add_flatten_args, load_rgb


def main():
    p = argparse.ArgumentParser(description='图像差异定位')
    p.add_argument('a', help='参考图')
    p.add_argument('b', help='待比对图')
    p.add_argument('--tolerance', type=float, default=16,
                   help='单像素差异超过此值才算不同（0-255，默认 16）')
    p.add_argument('--grid', type=int, default=8, help='网格划分数量（每边）')
    p.add_argument('--top', type=int, default=10, help='输出差异最大的前 N 个格子')
    p.add_argument('--min-region', type=int, default=200,
                   help='差异连通域最小像素数')
    p.add_argument('--heatmap', default=None, help='输出热力图路径')
    # 两张图分别合成到同一底色，否则透明度不同会让差异率虚高
    add_flatten_args(p)
    p.add_argument('--mask', default=None,
                   help='只看这个区域 x0,y0,x1,y1')
    args = p.parse_args()

    ia = load_rgb(args.a, args.flatten, args.premultiplied)
    ib = load_rgb(args.b, args.flatten, args.premultiplied)
    if ia.size != ib.size:
        print(f'尺寸不同：{ia.size} vs {ib.size}，将 b 缩放到 a 的尺寸', file=sys.stderr)
        ib = ib.resize(ia.size, Image.LANCZOS)

    A = np.asarray(ia).astype(np.float64)
    B = np.asarray(ib).astype(np.float64)
    H, W, _ = A.shape

    diff = np.abs(A - B).max(axis=2)
    mask = np.ones((H, W), bool)
    if args.mask:
        x0, y0, x1, y1 = [int(v) for v in args.mask.split(',')]
        mask[:] = False
        mask[y0:y1, x0:x1] = True

    changed = (diff > args.tolerance) & mask
    total = int(mask.sum())
    changed_n = int(changed.sum())

    # 网格统计
    gh, gw = args.grid, args.grid
    cells = []
    for gy in range(gh):
        for gx in range(gw):
            y0 = gy * H // gh
            y1 = (gy + 1) * H // gh
            x0 = gx * W // gw
            x1 = (gx + 1) * W // gw
            sub = changed[y0:y1, x0:x1]
            n = int(sub.sum())
            if n == 0:
                continue
            cells.append({
                'cell': [gx, gy],
                'bbox': [x0, y0, x1 - 1, y1 - 1],
                'changed_px': n,
                'changed_frac': round(n / max(1, sub.size), 4),
                'mean_diff_a': round(float(A[y0:y1, x0:x1][sub].mean(axis=0).mean()), 1),
                'mean_diff_b': round(float(B[y0:y1, x0:x1][sub].mean(axis=0).mean()), 1),
            })
    cells.sort(key=lambda c: -c['changed_px'])

    # 热力图
    if args.heatmap:
        h = np.zeros((H, W, 3), np.uint8)
        h[:, :, 0] = np.clip(diff * 4, 0, 255)
        h[:, :, 1] = np.clip(diff, 0, 255)
        base = (A * 0.25).astype(np.uint8)
        out = np.clip(base.astype(np.int32) + h.astype(np.int32), 0, 255).astype(np.uint8)
        Image.fromarray(out).save(args.heatmap)

    result = {
        'a': args.a,
        'b': args.b,
        'size': [W, H],
        'tolerance': args.tolerance,
        'changed_px': changed_n,
        'changed_frac': round(changed_n / max(1, total), 4),
        'note': '差异率在本项目没有参考价值：内容本就刻意改过，看定位不看分数',
        'top_cells': cells[:args.top],
        'cell_count': len(cells),
    }
    if args.heatmap:
        result['heatmap'] = args.heatmap

    sys.stdout.reconfigure(encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
