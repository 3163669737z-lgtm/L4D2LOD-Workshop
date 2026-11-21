#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
远景工坊 · 应用图标生成器
=========================
同一份几何（24x24 网格）既用于 exe 图标，也与界面里的品牌标记保持一致。
输出：
  src/Assets/app.ico     多尺寸（16/24/32/48/64/128/256）
  docs/screenshots/app_icon_sheet.png  各变体 × 各尺寸一览（人工挑选用）

用法（用 DSH 自带 Python + Pillow）：
  python tools/make_icon.py --preview      # 只出变体总表
  python tools/make_icon.py                # 生成 app.ico（当前选中变体）
"""
import argparse
import os
import sys

from PIL import Image, ImageDraw

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(BASE, "src", "Assets")
SHOTS = os.path.join(BASE, "docs", "screenshots")

INK = (25, 28, 26, 255)          # 近黑（浅色背景上用）
PAPER = (248, 249, 245, 255)
SIGNAL = (181, 70, 10, 255)      # 信号橙 #B5460A

UNIT = 24.0                       # 设计网格
S = 4096                          # 超采样画布像素
K = S / UNIT                      # 1 设计单位 = K 像素


def px(v):
    return v * K


def rect(d, x0, y0, x1, y1, color):
    d.rectangle([px(x0), px(y0), px(x1), px(y1)], fill=color)


def circle(d, cx, cy, r, color):
    d.ellipse([px(cx - r), px(cy - r), px(cx + r), px(cy + r)], fill=color)


def line(d, x0, y0, x1, y1, color, width):
    d.line([px(x0), px(y0), px(x1), px(y1)], fill=color, width=max(1, int(round(width * K))))


def draw_mark(variant, size, fg=INK, accent=SIGNAL, bg=None):
    """在 size×size 的 RGBA 画布上按变体画品牌标记，返回 4x 超采样后缩小的图。"""
    ss = size * 4
    img = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = ss / UNIT

    def P(v):
        return v * k

    def R(x0, y0, x1, y1, c):
        d.rectangle([P(x0), P(y0), P(x1), P(y1)], fill=c)

    def C(cx, cy, r, c):
        d.ellipse([P(cx - r), P(cy - r), P(cx + r), P(cy + r)], fill=c)

    def L(x0, y0, x1, y1, c, w):
        d.line([P(x0), P(y0), P(x1), P(y1)], fill=c, width=max(1, int(round(w * k))))

    if bg:
        d.rectangle([0, 0, ss - 1, ss - 1], fill=bg)

    if variant == "A":
        # 4 根递降竖条 + 顶部横测线 + 远点
        bars = [(2.4, 9.0, 5.4, 20.0), (7.2, 11.6, 10.2, 20.0),
                (12.0, 14.2, 15.0, 20.0), (16.8, 16.4, 19.8, 20.0)]
        for x0, y0, x1, y1 in bars:
            R(x0, y0, x1, y1, fg)
        L(2.0, 6.6, 20.6, 6.6, accent, 1.5)
        C(20.4, 6.6, 1.5, accent)
    elif variant == "B":
        # 竖条 + 基线 + 斜测线
        bars = [(2.6, 8.0, 5.2, 19.4), (7.0, 10.6, 9.6, 19.4),
                (11.4, 13.0, 14.0, 19.4), (15.8, 15.2, 18.4, 19.4)]
        for x0, y0, x1, y1 in bars:
            R(x0, y0, x1, y1, fg)
        R(2.0, 19.4, 21.0, 20.2, fg)
        L(3.2, 15.2, 19.6, 4.4, accent, 1.4)
        C(19.8, 4.2, 1.4, accent)
    elif variant == "C":
        # 方括号框 + 3 根递降竖条
        fw = 1.2
        # 左上/右上/左下/右下角
        L(2.6, 5.4, 9.0, 5.4, fg, fw); L(2.6, 5.4, 2.6, 12.0, fg, fw)
        L(21.4, 5.4, 15.0, 5.4, fg, fw); L(21.4, 5.4, 21.4, 12.0, fg, fw)
        L(2.6, 18.6, 9.0, 18.6, fg, fw); L(2.6, 18.6, 2.6, 12.0, fg, fw)
        L(21.4, 18.6, 15.0, 18.6, fg, fw); L(21.4, 18.6, 21.4, 12.0, fg, fw)
        bars = [(6.6, 9.0, 9.0, 17.4), (10.8, 11.4, 13.2, 17.4), (15.0, 13.8, 17.4, 17.4)]
        for x0, y0, x1, y1 in bars:
            R(x0, y0, x1, y1, fg)
    elif variant == "D":
        # 圆形准星 + 中心横线 + 3 根竖条
        L(12.0, 2.0, 12.0, 6.0, fg, 1.2)
        L(12.0, 18.0, 12.0, 22.0, fg, 1.2)
        L(2.0, 12.0, 6.0, 12.0, fg, 1.2)
        L(18.0, 12.0, 22.0, 12.0, fg, 1.2)
        bars = [(7.4, 9.2, 9.6, 16.0), (11.0, 10.8, 13.2, 16.0), (14.6, 12.4, 16.8, 16.0)]
        for x0, y0, x1, y1 in bars:
            R(x0, y0, x1, y1, fg)
        C(12.0, 12.0, 1.1, accent)
    elif variant == "E":
        # 阶梯（整体填充）+ 远点
        R(2.4, 16.8, 6.6, 20.4, fg)
        R(6.6, 13.2, 10.8, 20.4, fg)
        R(10.8, 9.6, 15.0, 20.4, fg)
        R(15.0, 6.0, 19.2, 20.4, fg)
        C(20.6, 5.4, 1.6, accent)
    elif variant == "F":
        # 阶梯 + 横测线（信号色）+ 方角，最简
        R(2.8, 15.6, 6.8, 19.8, fg)
        R(6.8, 12.2, 10.8, 19.8, fg)
        R(10.8, 8.8, 14.8, 19.8, fg)
        R(14.8, 5.4, 18.8, 19.8, fg)
        L(2.2, 22.0, 21.8, 22.0, fg, 1.0)
        L(2.4, 4.4, 20.8, 4.4, accent, 1.4)
        C(20.8, 4.4, 1.3, accent)
    elif variant == "G":
        # 递降阶梯（近处细节多 -> 远处细节少）+ 顶部测线 + 远点
        R(2.8, 5.4, 6.8, 19.8, fg)
        R(6.8, 8.8, 10.8, 19.8, fg)
        R(10.8, 12.2, 14.8, 19.8, fg)
        R(14.8, 15.6, 18.8, 19.8, fg)
        L(2.2, 22.0, 21.8, 22.0, fg, 1.0)
        L(2.4, 3.4, 21.4, 3.4, accent, 1.4)
        C(21.4, 3.4, 1.3, accent)
    else:
        raise ValueError(variant)

    return img.resize((size, size), Image.LANCZOS)


def make_sheet(variants, path):
    sizes = [16, 20, 24, 32, 48, 64, 128]
    pad = 12
    cell_w = 128 + pad
    row_h = 128 + pad
    sheet_w = 220 + cell_w * len(sizes)
    sheet_h = 60 + row_h * len(variants)
    sheet = Image.new("RGBA", (sheet_w, sheet_h), (236, 238, 233, 255))
    d = ImageDraw.Draw(sheet)
    d.text((12, 18), "variant \\ size", fill=(40, 44, 42, 255))
    for j, s in enumerate(sizes):
        d.text((220 + j * cell_w + 8, 18), str(s), fill=(40, 44, 42, 255))
    for i, v in enumerate(variants):
        y = 60 + i * row_h
        d.text((12, y + 40), v, fill=(40, 44, 42, 255))
        for j, s in enumerate(sizes):
            x = 220 + j * cell_w
            # 浅底 + 暗底两种，看对比
            tile = Image.new("RGBA", (128, 128), (248, 249, 245, 255))
            tile_d = ImageDraw.Draw(tile)
            tile_d.rectangle([0, 64, 128, 128], fill=(14, 17, 16, 255))
            mark_light = draw_mark(v, 64)
            mark_dark = draw_mark(v, 64, fg=(227, 232, 228, 255), accent=(255, 113, 51, 255))
            tile.alpha_composite(mark_light, (32, 0))
            tile.alpha_composite(mark_dark, (32, 64))
            small = draw_mark(v, 16)
            tile.alpha_composite(small, (56, 24))
            sheet.alpha_composite(tile, (x, y))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sheet.convert("RGB").save(path)
    print("sheet ->", path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true")
    ap.add_argument("--variant", default="F")
    ap.add_argument("--out", default=os.path.join(ASSETS, "app.ico"))
    args = ap.parse_args()

    if args.preview:
        make_sheet(["A", "B", "C", "D", "E", "F", "G"], os.path.join(SHOTS, "app_icon_sheet.png"))
        return

    variants = list(args.variant.split(","))
    os.makedirs(ASSETS, exist_ok=True)
    base = draw_mark(variants[0], 256)
    base.save(args.out, format="ICO",
              sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    # 256 PNG 预览（文档/README 用）
    base.save(os.path.join(SHOTS, "app_icon_256.png"))
    print("ico ->", args.out, "variant", variants[0])


if __name__ == "__main__":
    sys.exit(main())
