#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""渲染 HTML → 截图 / SVG 源码。

⚠️ 需要完全权限（danger-full-access）。
   浏览器要开命名管道，在 workspace-write 下会失败：
   platform_channel.cc:187 Check failed: 拒绝访问

三种产出
  --shot     整页截图 PNG
  --svg      从页面里抽出 <svg> 的源码（元素 id 按 --svg-id）
  --dom      渲染后的完整 DOM（调试用）

用法
  python render.py page.html --shot out.png --width 1440 --height 720
  python render.py page.html --svg out.svg --svg-id cluster --wait 2500
  python render.py page.html --dom out.html
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

EDGE_CANDIDATES = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Google\Chrome\Application\chrome.exe',
    r'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe',
]


def find_browser():
    for p in EDGE_CANDIDATES:
        if os.path.exists(p):
            return p
    raise SystemExit('找不到 Edge / Chrome')


def run(browser, profile, args, url, timeout):
    cmd = [
        browser,
        '--headless=new', '--disable-gpu', '--no-first-run',
        '--disable-sync', '--disable-extensions', '--mute-audio',
        '--no-default-browser-check', '--hide-scrollbars',
        f'--user-data-dir={profile}',
    ] + args + [url]
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout)


def main():
    p = argparse.ArgumentParser(description='渲染 HTML（需要完全权限）')
    p.add_argument('page')
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument('--shot', metavar='PNG', help='输出整页截图')
    g.add_argument('--svg', metavar='SVG', help='输出页面内 SVG 源码')
    g.add_argument('--dom', metavar='HTML', help='输出渲染后的 DOM')
    p.add_argument('--svg-id', default='cluster', help='要抽取的 SVG 元素 id')
    p.add_argument('--width', type=int, default=1440)
    p.add_argument('--height', type=int, default=720)
    p.add_argument('--scale', type=float, default=1.0, help='设备像素比')
    p.add_argument('--wait', type=int, default=2500,
                   help='等待毫秒数，让 JS 渲染和动画稳定')
    p.add_argument('--timeout', type=int, default=120, help='子进程超时（秒）')
    args = p.parse_args()

    browser = find_browser()
    url = 'file:///' + os.path.abspath(args.page).replace('\\', '/')

    # 临时 profile 放在系统临时目录，用完即删（AGENTS.md 的浏览器约定）
    profile = tempfile.mkdtemp(prefix='dsh-render-')
    try:
        if args.shot:
            flags = [
                f'--window-size={args.width},{args.height}',
                f'--screenshot={os.path.abspath(args.shot)}',
                f'--force-device-scale-factor={args.scale}',
                f'--virtual-time-budget={args.wait}',
            ]
            r = run(browser, profile, flags, url, args.timeout)
            if not os.path.exists(args.shot):
                print(r.stderr[:2000], file=sys.stderr)
                raise SystemExit('截图失败')
            print(f'已截图 {args.shot}  ({os.path.getsize(args.shot)} bytes)')

        elif args.dom:
            flags = [f'--virtual-time-budget={args.wait}']
            r = run(browser, profile, flags + ['--dump-dom'], url, args.timeout)
            if not r.stdout.strip():
                print(r.stderr[:2000], file=sys.stderr)
                raise SystemExit('DOM 导出为空')
            with open(args.dom, 'w', encoding='utf-8') as f:
                f.write(r.stdout)
            print(f'已导出 DOM {args.dom}  ({len(r.stdout)} 字符)')

        else:
            # SVG 抽取靠注入一段脚本不够稳，改用「渲染后 DOM + 正则取 svg」两步走
            flags = [f'--virtual-time-budget={args.wait}']
            r = run(browser, profile, flags + ['--dump-dom'], url, args.timeout)
            if not r.stdout.strip():
                print(r.stderr[:2000], file=sys.stderr)
                raise SystemExit('渲染失败，拿不到 DOM')

            import re
            m = re.search(r'<svg[^>]*(?:id="%s"[^>]*)?>.*?</svg>' % re.escape(args.svg_id),
                          r.stdout, re.S)
            if not m:
                raise SystemExit(f'DOM 里找不到 id="{args.svg_id}" 的 svg')
            svg = m.group(0)
            svg = svg.replace('<svg', '<svg xmlns="http://www.w3.org/2000/svg"', 1)
            doc = '<?xml version="1.0" encoding="UTF-8"?>\n' + svg + '\n'
            with open(args.svg, 'w', encoding='utf-8') as f:
                f.write(doc)
            print(f'已导出 SVG {args.svg}  ({len(doc)} 字符)')

    finally:
        shutil.rmtree(profile, ignore_errors=True)


if __name__ == '__main__':
    main()
