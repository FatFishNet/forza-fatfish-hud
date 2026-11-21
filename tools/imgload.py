#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""统一的图片加载：处理 HUD 的透明背景。

## 为什么需要这个模块

ForzaOverlay 的 HUD 是「透明背景 + Premultiplied Alpha」渲染出来的。
直接用 ``Image.open(...).convert('RGB')`` 会**丢掉 alpha 通道**，后果是：

- 透明区的 RGB 是近 0 值 → 被当成「深色像素」
- ``measure.py`` 的 ``lum<70`` 之类掩膜会把整片背景判成前景
- ``colors.py`` 的调色板会混进边缘半透明像素
- ``diff.py`` 两张图透明度不同时差异率虚高

设计稿的 HTML 截图是黑底不透明的，所以隔壁项目没这个问题；
本项目必须显式把透明像素合成到底色上，再交给分析函数。

## 用法

所有脚本都暴露同一个参数：

    --flatten '#000000'    透明像素合成到该底色（默认黑）
    --flatten none         不合成，仅去掉 alpha（图本来就不透明时用）

``--premultiplied`` 只在遇到**预乘格式**的图时才需要。
标准 PNG 存的是直通（straight）alpha，本项目快照工具也按直通存，
所以正常情况下不用给这个开关 —— 不要靠猜，猜错了颜色会偏暗。
"""
from __future__ import annotations

import argparse

import numpy as np
from PIL import Image

DEFAULT_FLATTEN = '#000000'


def parse_color(text: str) -> tuple[int, int, int]:
    """接受 '#RGB' / '#RRGGBB' / 'r,g,b' 三种写法。"""
    s = text.strip()
    if s.startswith('#'):
        s = s[1:]
    if ',' in s:
        parts = [int(v) for v in s.split(',')]
        if len(parts) != 3:
            raise SystemExit(f'颜色要有三个分量，收到：{text}')
        return tuple(parts)  # type: ignore[return-value]
    if len(s) == 3:
        return tuple(int(c * 2, 16) for c in s)  # type: ignore[return-value]
    if len(s) == 6:
        return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16))
    raise SystemExit(f'无法识别的颜色：{text}')


def add_flatten_args(p: argparse.ArgumentParser, default: str = DEFAULT_FLATTEN) -> None:
    p.add_argument(
        '--flatten',
        default=default,
        help=f'透明像素合成到的底色，默认 {default}；给 none 则只去掉 alpha',
    )
    p.add_argument(
        '--premultiplied',
        action='store_true',
        help='输入是预乘 alpha（快照工具默认输出直通，一般不需要）',
    )


def open_rgba(path: str, premultiplied: bool = False) -> tuple[np.ndarray, np.ndarray]:
    """读出 (rgb, alpha)，两者都是 float64；alpha 归一化到 0–1。"""
    im = Image.open(path)
    if im.mode not in ('RGBA', 'LA', 'RGB', 'L'):
        im = im.convert('RGBA')

    if im.mode in ('RGBA', 'LA'):
        rgba = np.asarray(im.convert('RGBA')).astype(np.float64)
        a = rgba[:, :, 3:4] / 255.0
        rgb = rgba[:, :, :3]
        if premultiplied:
            with np.errstate(divide='ignore', invalid='ignore'):
                straight = np.where(a > 1e-6, rgb / np.maximum(a, 1e-6), 0.0)
            rgb = np.clip(straight, 0, 255)
        return rgb, a

    rgb = np.asarray(im.convert('RGB')).astype(np.float64)
    return rgb, np.ones((rgb.shape[0], rgb.shape[1], 1), dtype=np.float64)


def load_rgb(path: str, flatten: str | None = DEFAULT_FLATTEN, premultiplied: bool = False) -> Image.Image:
    """供各脚本使用：返回已合成的 RGB 图像。"""
    rgb, a = open_rgba(path, premultiplied)

    if flatten is None or str(flatten).lower() == 'none':
        return Image.fromarray(rgb.astype(np.uint8), 'RGB')

    bg = np.array(parse_color(str(flatten)), dtype=np.float64).reshape(1, 1, 3)
    out = rgb * a + bg * (1.0 - a)
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), 'RGB')


def load_rgb_array(path: str, flatten: str | None = DEFAULT_FLATTEN, premultiplied: bool = False) -> np.ndarray:
    return np.asarray(load_rgb(path, flatten, premultiplied)).astype(np.float64)
