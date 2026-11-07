#!/usr/bin/env python3
"""生成插件目录图标 assets/icon.png。

1280x720（16:9，与 manifest.json 的 imageUrl 显示比例一致）。
视觉参考 Jellyfin 官方插件横幅：近黑底 + 极淡同心圆纹理 + 纯白图形符号与粗体标题。

用法：
    python scripts/make_icon.py              # 星标 + Ratings
    python scripts/make_icon.py --no-icon    # 只有粗体 Ratings
    python scripts/make_icon.py --preview    # 额外输出插件卡片实际尺寸预览

依赖：Pillow
"""

from __future__ import annotations

import argparse
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFont

WIDTH, HEIGHT = 1280, 720
TEXT = "Ratings"

BG_TOP = (20, 21, 25)
BG_BOTTOM = (9, 10, 13)
INK = (247, 248, 250)

FONT_SIZE = 168
STAR_RATIO = 1.68   # 星标直径 / 文字 cap 高。参考官方横幅，图标要明显高于文字
GAP = 38            # 星标与文字间距
RING_ALPHA = 5      # 背景同心圆透明度，只做极淡的质感
RING_STEP = 128

FONT_CANDIDATES = (
    "C:/Windows/Fonts/seguibl.ttf",                              # Segoe UI Black
    "C:/Windows/Fonts/ariblk.ttf",                               # Arial Black
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/System/Library/Fonts/Supplemental/Arial Black.ttf",
)


def pick_font() -> str:
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            return path
    raise SystemExit("找不到可用字体，请修改 FONT_CANDIDATES")


def build_background() -> Image.Image:
    """竖向渐变 + 同心圆描线，模仿官方横幅那种若隐若现的纹理。"""
    strip = Image.new("RGB", (1, HEIGHT))
    for y in range(HEIGHT):
        t = y / (HEIGHT - 1)
        strip.putpixel(
            (0, y),
            tuple(round(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(3)),
        )
    canvas = strip.resize((WIDTH, HEIGHT), Image.BILINEAR).convert("RGBA")

    rings = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    draw = ImageDraw.Draw(rings)
    cx, cy = WIDTH // 2, HEIGHT // 2
    for radius in range(220, 1500, RING_STEP):
        draw.ellipse(
            [cx - radius, cy - radius, cx + radius, cy + radius],
            outline=(255, 255, 255, RING_ALPHA),
            width=2,
        )
    return Image.alpha_composite(canvas, rings)


def star_sprite(size: int, color) -> Image.Image:
    """正立五角星，外接圆直径 = size。4 倍超采样后缩小，边缘更干净。"""
    ss = 4
    canvas = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    center = size * ss / 2
    outer = center * 0.99
    inner = outer * 0.44

    points = []
    for i in range(10):
        radius = outer if i % 2 == 0 else inner
        angle = -math.pi / 2 + i * math.pi / 5
        points.append((center + radius * math.cos(angle), center + radius * math.sin(angle)))

    draw.polygon(points, fill=color)
    return canvas.resize((size, size), Image.LANCZOS)


def render(out_path: str, with_icon: bool) -> None:
    font = ImageFont.truetype(pick_font(), FONT_SIZE)

    # 在放大的透明画布上按绝对坐标排版，最后整体裁切居中
    stage = Image.new("RGBA", (2400, 900), (0, 0, 0, 0))
    draw = ImageDraw.Draw(stage)
    text_x, text_y = 800, 300
    draw.text((text_x, text_y), TEXT, font=font, fill=INK)

    text_bbox = stage.getbbox()
    cap = font.getbbox("R")
    cap_height = cap[3] - cap[1]
    cap_center_y = text_y + cap[1] + cap_height / 2

    if with_icon:
        star_size = int(cap_height * STAR_RATIO)
        star = star_sprite(star_size, INK)
        # 星标中心对齐文字的大写高度中心，而不是含下伸部的外接框中心
        star_center_x = text_bbox[0] - GAP - star_size / 2
        stage.alpha_composite(
            star,
            (int(star_center_x - star_size / 2), int(cap_center_y - star_size / 2)),
        )

    ink = stage.crop(stage.getbbox())

    canvas = build_background()
    canvas.alpha_composite(ink, ((WIDTH - ink.width) // 2, (HEIGHT - ink.height) // 2))
    canvas.convert("RGB").save(out_path, optimize=True)

    print(f"wrote {out_path} ({WIDTH}x{HEIGHT}, ink={ink.size}, icon={with_icon})")


def write_preview(icon_path: str) -> None:
    """插件卡片上的实际显示尺寸，用来检查小图可读性。输出到 dist/，不进仓库。"""
    dist_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "dist")
    os.makedirs(dist_dir, exist_ok=True)
    preview_path = os.path.join(dist_dir, "icon_preview.png")
    Image.open(icon_path).resize((320, 180), Image.LANCZOS).save(preview_path, optimize=True)
    print(f"wrote {preview_path} (320x180)")


def main() -> None:
    parser = argparse.ArgumentParser(description="生成插件目录图标")
    parser.add_argument(
        "--out",
        default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "icon.png"),
        help="输出路径，默认 assets/icon.png",
    )
    parser.add_argument("--no-icon", action="store_true", help="不画星标，只保留粗体标题")
    parser.add_argument("--preview", action="store_true", help="额外输出 320x180 预览图")
    args = parser.parse_args()

    render(args.out, with_icon=not args.no_icon)
    if args.preview:
        write_preview(args.out)


if __name__ == "__main__":
    sys.exit(main())
