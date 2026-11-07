#!/usr/bin/env python3
"""生成插件目录图标 assets/icon.png。

1280x720（16:9，与 manifest.json 的 imageUrl 显示比例一致），
纯字母 wordmark：深色渐变底 + 居中 "ratings"，无任何图形元素。

用法：
    python scripts/make_icon.py            # 写入 assets/icon.png
    python scripts/make_icon.py --preview  # 额外输出插件卡片实际尺寸预览

依赖：Pillow
"""

from __future__ import annotations

import argparse
import os
import sys

from PIL import Image, ImageDraw, ImageFont

WIDTH, HEIGHT = 1280, 720
TEXT = "ratings"

# 排版参数：字重偏细 + 宽字距，读起来像 wordmark 而不是标题
TRACKING_EM = 0.12   # 字距（相对字号）
TEXT_WIDTH = 780     # 文字横向占宽，约 61% —— 四周留白足够
LIFT = 10            # 垂直上移。g 有下伸部，按外接框居中会让视觉重心偏低

BG_TOP = (22, 24, 31)
BG_BOTTOM = (12, 14, 18)
INK = (245, 246, 248)

FONT_CANDIDATES = (
    "C:/Windows/Fonts/segoeuisl.ttf",                      # Segoe UI Semilight
    "C:/Windows/Fonts/seguisb.ttf",                        # 兜底：Semibold
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-ExtraLight.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "/System/Library/Fonts/Supplemental/Avenir Next.ttc",
    "/System/Library/Fonts/Supplemental/Arial.ttf",
)


def pick_font() -> str:
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            return path
    raise SystemExit("找不到可用字体，请修改 FONT_CANDIDATES")


def build_background() -> Image.Image:
    """竖直方向极淡渐变，避免纯色底的死板感。"""
    strip = Image.new("RGB", (1, HEIGHT))
    for y in range(HEIGHT):
        t = y / (HEIGHT - 1)
        strip.putpixel(
            (0, y),
            tuple(round(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(3)),
        )
    return strip.resize((WIDTH, HEIGHT), Image.BILINEAR)


def draw_tracked(text: str, font: ImageFont.FreeTypeFont, tracking: float, color, pad: int) -> Image.Image:
    """PIL 没有原生 letter-spacing，逐字符绘制并累加宽度。"""
    probe = ImageDraw.Draw(Image.new("RGBA", (8, 8)))
    total = sum(probe.textlength(ch, font=font) for ch in text) + tracking * (len(text) - 1)
    ascent, descent = font.getmetrics()

    canvas = Image.new("RGBA", (int(total) + pad * 2, ascent + descent + pad * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    x = pad
    for ch in text:
        draw.text((x, pad), ch, font=font, fill=color)
        x += probe.textlength(ch, font=font) + tracking
    return canvas


def render(out_path: str) -> None:
    font_path = pick_font()

    # 先用大字号绘制再等比缩放，边缘比直接按目标字号渲染更干净
    probe_size = 220
    font = ImageFont.truetype(font_path, probe_size)
    ink = draw_tracked(TEXT, font, probe_size * TRACKING_EM, INK, pad=60)

    ink = ink.crop(ink.getbbox())
    ink = ink.resize(
        (TEXT_WIDTH, max(1, round(ink.height * TEXT_WIDTH / ink.width))),
        Image.LANCZOS,
    )

    canvas = build_background().convert("RGBA")
    canvas.alpha_composite(
        ink,
        ((WIDTH - ink.width) // 2, (HEIGHT - ink.height) // 2 - LIFT),
    )
    canvas.convert("RGB").save(out_path, optimize=True)

    print(f"wrote {out_path} ({WIDTH}x{HEIGHT}, font={os.path.basename(font_path)}, ink={ink.size})")


def write_preview(icon_path: str) -> None:
    """插件目录卡片上的实际显示尺寸，用来检查小图可读性。输出到 dist/，不进仓库。"""
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
    parser.add_argument("--preview", action="store_true", help="额外输出 320x180 预览图")
    args = parser.parse_args()

    render(args.out)
    if args.preview:
        write_preview(args.out)


if __name__ == "__main__":
    sys.exit(main())
