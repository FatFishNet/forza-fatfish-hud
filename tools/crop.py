#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""裁剪与缩放 — 从图里切出要看的区域。

比 vision_crop 多两件事：批量裁、按坐标标注。用它替代反复手调裁剪框。

用法
  # 单块裁剪，放大 4 倍看清楚
  python crop.py 图.png --region 114,310,263,332 --scale 4 -o 字标.png

  # 一次切多块（看细节常用）
  python crop.py 图.png --region 114,310,263,332 --region 220,220,310,290 -o 分块.png

  # 以某点为圆心切一个方形窗口
  python crop.py 图.png --around 313.5,319 --size 400 --scale 2 -o 表盘.png
"""
import argparse
import sys

from PIL import Image

from imgload import add_flatten_args, load_rgb


def parse_region(s):
    parts = [int(v) for v in s.split(',')]
    if len(parts) != 4:
        raise SystemExit(f'区域格式应为 x0,y0,x1,y1，收到：{s}')
    return tuple(parts)


def main():
    p = argparse.ArgumentParser(description='裁剪与缩放')
    p.add_argument('image')
    p.add_argument('--region', action='append', default=[],
                   help='裁剪区 x0,y0,x1,y1，可重复给多块')
    p.add_argument('--around', default=None,
                   help='以 x,y 为中心的方形窗口，配合 --size')
    p.add_argument('--size', type=int, default=300, help='--around 时的边长')
    p.add_argument('--scale', type=float, default=1.0, help='缩放倍数（整数倍最清晰）')
    p.add_argument('--resample', default='lanczos',
                   choices=['lanczos', 'nearest', 'bilinear'],
                   help='放大用 lanczos；要看原始像素用 nearest')
    p.add_argument('--gap', type=int, default=8, help='多块拼接时的间距（像素）')
    p.add_argument('--grid', default=None,
                   help='多块按网格拼：列x行，例如 2x2；不给则竖排')
    p.add_argument('--label', action='store_true', help='在多块拼接图上标注区域坐标')
    p.add_argument('-o', '--output', required=True)
    add_flatten_args(p)
    args = p.parse_args()

    # flatten=none 时保留原始 RGB（不合成底色），用于看抗锯齿原始像素
    im = load_rgb(args.image, args.flatten, args.premultiplied)
    W, H = im.size

    regions = []
    if args.around:
        cx, cy = [float(v) for v in args.around.split(',')]
        half = args.size / 2.0
        regions.append((int(cx - half), int(cy - half),
                        int(cx + half), int(cy + half)))
    for r in args.region:
        regions.append(parse_region(r))

    if not regions:
        raise SystemExit('至少给一个 --region 或 --around')

    resample = {
        'lanczos': Image.LANCZOS,
        'nearest': Image.NEAREST,
        'bilinear': Image.BILINEAR,
    }[args.resample]

    crops = []
    for (x0, y0, x1, y1) in regions:
        # 越界钳制，不让 PIL 报错
        cx0, cy0 = max(0, x0), max(0, y0)
        cx1, cy1 = min(W, x1), min(H, y1)
        if cx1 <= cx0 or cy1 <= cy0:
            print(f'跳过越界区域 {(x0, y0, x1, y1)}', file=sys.stderr)
            continue
        c = im.crop((cx0, cy0, cx1, cy1))
        if args.scale != 1.0:
            nw = max(1, int(round(c.width * args.scale)))
            nh = max(1, int(round(c.height * args.scale)))
            c = c.resize((nw, nh), resample)
        crops.append(((x0, y0, x1, y1), c))

    if not crops:
        raise SystemExit('没有有效区域')

    if len(crops) == 1:
        crops[0][1].save(args.output)
        (x0, y0, x1, y1), c = crops[0]
        print(f'已裁剪 {args.output}  ({c.width}x{c.height})  '
              f'源区域 {x0},{y0},{x1},{y1}')
        return

    # 多块拼接
    if args.grid:
        cols, rows = [int(v) for v in args.grid.lower().split('x')]
    else:
        cols, rows = 1, len(crops)

    cw = max(c.width for _, c in crops)
    ch = max(c.height for _, c in crops)
    pad = args.gap
    label_h = 18 if args.label else 0

    sheet_w = cols * cw + (cols + 1) * pad
    sheet_h = rows * (ch + label_h) + (rows + 1) * pad
    sheet = Image.new('RGB', (sheet_w, sheet_h), (24, 24, 28))

    from PIL import ImageDraw
    draw = ImageDraw.Draw(sheet)

    for i, (region, c) in enumerate(crops):
        r, col = divmod(i, cols)
        if r >= rows:
            break
        x = pad + col * (cw + pad)
        y = pad + r * (ch + label_h + pad)
        sheet.paste(c, (x, y))
        if args.label:
            draw.text((x + 2, y + ch + 2),
                      '%d,%d,%d,%d' % region, fill=(200, 200, 200))

    sheet.save(args.output)
    print(f'已拼接 {args.output}  ({sheet_w}x{sheet_h})，{len(crops)} 块')


if __name__ == '__main__':
    main()
