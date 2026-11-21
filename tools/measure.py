#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""图像测量 — 把像素变成数字。

所有输出都是 JSON，可直接写进 spec.json。

子命令
  radial   按半径做圆周统计（找银盘 / 齿环 / 数字环的分界）
  ray      沿单条射线逐像素扫描（定径向分界）
  arc      角度直方图（数齿、量刻度节拍）
  blobs    连通域分析（找小表、数字块、圆点）

核心约定
  · 坐标一律图像像素，原点左上
  · 每个径向结果同时给出 px 与 rel（rel = px / --ref-r），
    这样不会再把「相对银盘」和「相对盘外沿」两套基准搞混
  · 掩膜用 --mask 的迷你 DSL 描述，例：--mask "lum>150,warm>20,sat<100"

用法示例
  python measure.py radial refs/a.png --cx 313 --cy 319 --ref-r 211 --r0 0.9 --r1 1.4
  python measure.py ray    refs/a.png --cx 313 --cy 319 --angle 45 --r0 0.95 --r1 1.15
  python measure.py arc    refs/a.png --cx 313 --cy 319 --r0 1.04 --r1 1.06 --mask "lum>150"
  python measure.py blobs  refs/a.png --mask "lum<70" --min-area 500 --region 100,100,500,500
"""
import argparse
import json
import math
import sys

import numpy as np
from PIL import Image

from imgload import DEFAULT_FLATTEN, add_flatten_args, load_rgb

# ----------------------------------------------------------------------------
# 基础
# ----------------------------------------------------------------------------


def load(path, flatten=DEFAULT_FLATTEN, premultiplied=False):
    im = load_rgb(path, flatten, premultiplied)
    a = np.asarray(im).astype(np.float64)
    return {
        'a': a,
        'lum': a.mean(axis=2),
        'sat': a.max(axis=2) - a.min(axis=2),
        'warm': a[:, :, 0] - a[:, :, 2],
        'h': a.shape[0],
        'w': a.shape[1],
    }


OPS = {
    '>': lambda v, t: v > t,
    '>=': lambda v, t: v >= t,
    '<': lambda v, t: v < t,
    '<=': lambda v, t: v <= t,
}


def build_mask(img, expr):
    """解析 'lum>150,warm>20' 这类表达式，返回 bool 掩膜。"""
    if not expr:
        return np.ones((img['h'], img['w']), bool)
    mask = np.ones((img['h'], img['w']), bool)
    for term in expr.split(','):
        term = term.strip()
        if not term:
            continue
        op = None
        for o in ('>=', '<=', '>', '<'):
            if o in term:
                op = o
                break
        if op is None:
            raise SystemExit(f'掩膜项无法解析：{term}')
        name, val = term.split(op, 1)
        name = name.strip()
        val = float(val)
        if name not in img:
            raise SystemExit(f'未知通道：{name}（可用 lum/sat/warm/r/g/b）')
        src = img[name] if name not in ('r', 'g', 'b') else img['a'][:, :, 'rgb'.index(name)]
        mask &= OPS[op](src, val)
    return mask


def sample_ring(img, cx, cy, r, n=720):
    """在半径 r 上均匀采样 n 个点，返回被采样到的通道值字典。"""
    th = np.radians(np.arange(0, 360, 360.0 / n))
    x = cx + r * np.cos(th)
    y = cy + r * np.sin(th)
    ok = (x >= 0) & (x < img['w']) & (y >= 0) & (y < img['h'])
    xi = x[ok].astype(int)
    yi = y[ok].astype(int)
    return {
        'valid': ok.mean(),
        'lum': img['lum'][yi, xi],
        'sat': img['sat'][yi, xi],
        'warm': img['warm'][yi, xi],
        'ang': np.degrees(th)[ok],
    }


def label(mask):
    """8 邻域连通域标记。返回 (标签图, 连通域数量)。"""
    h, w = mask.shape
    lab = np.zeros((h, w), np.int32)
    cur = 0
    ys, xs = np.nonzero(mask)
    for y0, x0 in zip(ys.tolist(), xs.tolist()):
        if lab[y0, x0]:
            continue
        cur += 1
        lab[y0, x0] = cur
        stack = [(y0, x0)]
        while stack:
            y, x = stack.pop()
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not lab[ny, nx]:
                        lab[ny, nx] = cur
                        stack.append((ny, nx))
    return lab, cur


def rel(px, ref_r):
    return round(px / ref_r, 4) if ref_r else None


# ----------------------------------------------------------------------------
# 子命令
# ----------------------------------------------------------------------------


def cmd_radial(args):
    img = load(args.image, args.flatten, args.premultiplied)
    rows = []
    for r in np.arange(args.r0 * args.ref_r, args.r1 * args.ref_r + 1e-9, args.step):
        s = sample_ring(img, args.cx, args.cy, r, args.samples)
        if s['valid'] < 0.5:
            break
        lum = s['lum']
        rows.append({
            'r_px': round(float(r), 2),
            'r_rel': rel(r, args.ref_r),
            'valid': round(float(s['valid']), 3),
            'lum_median': round(float(np.median(lum)), 1),
            'lum_p10': round(float(np.percentile(lum, 10)), 1),
            'lum_p90': round(float(np.percentile(lum, 90)), 1),
            'bright_frac': round(float((lum > args.bright).mean()), 4),
            'dark_frac': round(float((lum < args.dark).mean()), 4),
            'warm_frac': round(float((s['warm'] > args.warm).mean()), 4),
        })
    out = {
        'image': args.image,
        'center': [args.cx, args.cy],
        'ref_r': args.ref_r,
        'rows': rows,
    }
    emit(out)


def cmd_ray(args):
    img = load(args.image, args.flatten, args.premultiplied)
    th = math.radians(args.angle)
    rows = []
    for r in np.arange(args.r0 * args.ref_r, args.r1 * args.ref_r + 1e-9, args.step):
        x = int(round(args.cx + r * math.cos(th)))
        y = int(round(args.cy + r * math.sin(th)))
        if not (0 <= x < img['w'] and 0 <= y < img['h']):
            break
        c = img['a'][y, x]
        rows.append({
            'r_px': round(float(r), 2),
            'r_rel': rel(r, args.ref_r),
            'x': x, 'y': y,
            'rgb': [int(v) for v in c],
            'lum': round(float(img['lum'][y, x]), 1),
        })
    emit({'image': args.image, 'angle': args.angle, 'rows': rows})


def arc_signal(img, cx, cy, ref_r, r0, r1, samples, avg_step):
    """沿角度取一维亮度信号：每个角度只取一个代表值（沿半径取中位）。

    不能拿像素在角度上的直方图去数齿：像素数是二维的，每个角箱里的像素数
    取决于环带宽度，箱子再被形态学一滤就全空了（这次踩过）。
    一维信号才可以稳定地切亮暗段。
    """
    angles = np.arange(0, 360, 360.0 / samples)
    sig = np.full(samples, np.nan)
    radii = np.arange(r0 * ref_r, r1 * ref_r + 1e-9, avg_step)
    for i, th in enumerate(angles):
        vals = []
        t = math.radians(th)
        for r in radii:
            x = int(round(cx + r * math.cos(t)))
            y = int(round(cy + r * math.sin(t)))
            if 0 <= x < img['w'] and 0 <= y < img['h']:
                vals.append(img['lum'][y, x])
        if vals:
            sig[i] = float(np.median(vals))
    return angles, sig


def segments_from_signal(angles, sig, percentile, min_width):
    """把一维信号切成本段亮区，返回 [(起角, 止角), ...]。"""
    n = len(sig)
    if np.all(np.isnan(sig)):
        return [], None
    thr = float(np.nanpercentile(sig, percentile))
    on = np.nan_to_num(sig, nan=0.0) > thr

    if on.all():
        return [(0.0, 360.0)], thr

    # 从第一个「灭」之后开始扫，避免把跨 0° 的段切成两半
    off_idx = np.nonzero(~on)[0]
    start_idx = int(off_idx[0]) + 1
    order = [(start_idx + k) % n for k in range(n)]
    runs = []
    cur_start = None
    for oi in order:
        if on[oi]:
            if cur_start is None:
                cur_start = angles[oi]
        else:
            if cur_start is not None:
                end = angles[oi]
                if end <= cur_start:
                    end += 360.0
                runs.append((cur_start, end))
                cur_start = None
    if cur_start is not None:
        runs.append((cur_start, angles[order[0]] + 360.0))

    runs = [(a, b) for a, b in runs if (b - a) >= min_width]
    return runs, thr


def cmd_arc(args):
    """沿角度扫一圈数齿 / 量刻度节拍。"""
    img = load(args.image, args.flatten, args.premultiplied)

    angles, sig = arc_signal(img, args.cx, args.cy, args.ref_r,
                             args.r0, args.r1, args.samples, args.avg_step)
    if np.all(np.isnan(sig)):
        emit({'image': args.image, 'runs': [], 'note': '采样全部越界'})
        return

    runs, thr = segments_from_signal(angles, sig, args.percentile, args.min_width)
    widths = [round(b - a, 2) for a, b in runs]

    out = {
        'image': args.image,
        'band_rel': [args.r0, args.r1],
        'band_px': [round(args.r0 * args.ref_r, 2), round(args.r1 * args.ref_r, 2)],
        'auto_threshold': round(thr, 1) if thr is not None else None,
        'count': len(runs),
        'width_median': round(float(np.median(widths)), 3) if widths else None,
        'pitch': round(360.0 / len(runs), 3) if runs else None,
        'width_ratio': round(float(np.median(widths)) / (360.0 / len(runs)), 4) if runs else None,
        'runs': [[round(a, 2), round(b, 2)] for a, b in runs],
    }

    # --sweep：在环带内多个子环上重复测，取众数。
    # 单次测量受抗锯齿和采样相位影响，实测同一圈齿数会在 85~94 之间跳，
    # 多半径投票才稳定到真值。
    if args.sweep > 0:
        votes = {}
        span = (args.r1 - args.r0)
        subs = np.linspace(args.r0, args.r1 - span * 0.1, args.sweep)
        for r0i in subs:
            r1i = r0i + span * 0.1
            a2, s2 = arc_signal(img, args.cx, args.cy, args.ref_r,
                                r0i, r1i, args.samples, args.avg_step)
            if np.all(np.isnan(s2)):
                continue
            rr, _ = segments_from_signal(a2, s2, args.percentile, args.min_width)
            if rr:
                votes[len(rr)] = votes.get(len(rr), 0) + 1
        if votes:
            best = max(votes.items(), key=lambda kv: (kv[1], kv[0]))[0]
            out['sweep'] = {
                'samples': dict(sorted(votes.items())),
                'mode': best,
                'agree': votes[best],
            }
            out['count'] = best
            out['pitch'] = round(360.0 / best, 3) if best else None

    emit(out)


def cmd_blobs(args):
    img = load(args.image, args.flatten, args.premultiplied)
    mask = build_mask(img, args.mask)
    if args.region:
        x0, y0, x1, y1 = [int(v) for v in args.region.split(',')]
        box = np.zeros_like(mask)
        box[y0:y1, x0:x1] = True
        mask &= box
    lab, n = label(mask)
    blobs = []
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        area = len(ys)
        if area < args.min_area:
            continue
        bx0, bx1 = int(xs.min()), int(xs.max())
        by0, by1 = int(ys.min()), int(ys.max())
        bw, bh = bx1 - bx0 + 1, by1 - by0 + 1
        cx, cy = float(xs.mean()), float(ys.mean())
        d = math.hypot(cx - args.cx, cy - args.cy) if args.cx is not None else None
        blobs.append({
            'area': area,
            'bbox': [bx0, by0, bx1, by1],
            'size': [bw, bh],
            'aspect': round(bw / bh, 3),
            'center': [round(cx, 1), round(cy, 1)],
            'r_area': round(math.sqrt(area / math.pi), 2),
            'r_bbox': round((bw + bh) / 4.0, 2),
            'dist_from_center': round(d, 2) if d is not None else None,
            'dist_rel': rel(d, args.ref_r) if d is not None else None,
            'angle': round((math.degrees(math.atan2(cy - args.cy, cx - args.cx)) + 360) % 360, 2)
            if args.cx is not None else None,
        })
    blobs.sort(key=lambda b: -b['area'])
    emit({'image': args.image, 'count': len(blobs), 'blobs': blobs})


# ----------------------------------------------------------------------------


def emit(obj):
    sys.stdout.reconfigure(encoding='utf-8')
    print(json.dumps(obj, ensure_ascii=False, indent=2))


def main():
    p = argparse.ArgumentParser(description='图像测量：把像素变成数字')
    sub = p.add_subparsers(dest='cmd', required=True)

    def common(sp):
        sp.add_argument('image')
        sp.add_argument('--cx', type=float, default=None, help='圆心 x')
        sp.add_argument('--cy', type=float, default=None, help='圆心 y')
        sp.add_argument('--ref-r', type=float, default=None,
                        help='参考半径（例如银盘半径）。给了就输出 rel 字段')
        # HUD 是透明背景，必须显式合成到底色，否则透明区会被当成深色前景
        add_flatten_args(sp)

    sp = sub.add_parser('radial', help='按半径做圆周统计')
    common(sp)
    sp.add_argument('--r0', type=float, required=True, help='起始半径倍数（相对 ref-r）')
    sp.add_argument('--r1', type=float, required=True, help='结束半径倍数')
    sp.add_argument('--step', type=float, default=0.01, help='半径步长（像素）')
    sp.add_argument('--samples', type=int, default=720, help='每圈采样点数')
    sp.add_argument('--bright', type=float, default=150, help='lum 超过此值算亮')
    sp.add_argument('--dark', type=float, default=45, help='lum 低于此值算暗')
    sp.add_argument('--warm', type=float, default=20, help='warm 超过此值算暖色')
    sp.set_defaults(func=cmd_radial)

    sp = sub.add_parser('ray', help='单条射线逐像素扫描')
    common(sp)
    sp.add_argument('--angle', type=float, required=True, help='射线角度（度，0=右）')
    sp.add_argument('--r0', type=float, required=True)
    sp.add_argument('--r1', type=float, required=True)
    sp.add_argument('--step', type=float, default=1.0, help='步长（像素）')
    sp.set_defaults(func=cmd_ray)

    sp = sub.add_parser('arc', help='角度直方图，数齿/量刻度')
    common(sp)
    sp.add_argument('--r0', type=float, required=True, help='环带内沿倍数')
    sp.add_argument('--r1', type=float, required=True, help='环带外沿倍数')
    sp.add_argument('--percentile', type=float, default=50,
                    help='自适应阈值取角向信号的哪一分位（默认 50，亮暗各半）')
    sp.add_argument('--samples', type=int, default=1800, help='角向采样数（0.2°/点）')
    sp.add_argument('--avg-step', type=float, default=1.0,
                    help='沿半径取中位时的步长（像素）')
    sp.add_argument('--min-width', type=float, default=0.4,
                    help='亮段最小角度宽度（度），低于此视为噪声')
    sp.add_argument('--sweep', type=int, default=0,
                    help='环带内多半径重复测并取众数（0=关闭）；'
                         '数齿这类易受抗锯齿影响的量建议开 9')
    sp.set_defaults(func=cmd_arc)

    sp = sub.add_parser('blobs', help='连通域分析')
    common(sp)
    sp.add_argument('--mask', default='', help='掩膜表达式')
    sp.add_argument('--min-area', type=int, default=100)
    sp.add_argument('--region', default=None, help='限定区域 x0,y0,x1,y1')
    sp.set_defaults(func=cmd_blobs)

    args = p.parse_args()
    if args.cx is None and args.cmd in ('radial', 'ray', 'arc'):
        raise SystemExit('radial/ray/arc 需要 --cx 与 --cy')
    if args.ref_r is None:
        raise SystemExit('需要 --ref-r（参考半径，例如银盘半径）')
    args.func(args)


if __name__ == '__main__':
    main()
