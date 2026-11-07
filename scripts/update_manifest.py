#!/usr/bin/env python3
"""把刚发布的版本写入仓库根 manifest.json。

发布链路（.github/workflows/release.yml）：
    tag v1.0.1  →  构建 zip  →  上传 GitHub Release  →  运行本脚本  →  提交 manifest.json

输入
    meta.json                 插件元数据（name / description / overview / owner / category）
    Directory.Build.props      版本号唯一来源（<Version>）
    RELEASE_NOTES.md           本次发布的更新说明，写入 manifest 的 changelog
    dist/ratings-jf12.zip      即将上传的插件包，用于计算 MD5

环境变量
    RELEASE_TAG   必填，形如 v1.0.1
    REPO_SLUG     可选，默认 zian-yan/jellyfin-plugin-ratings
    IMAGE_URL     可选，默认取仓库 assets/icon.png 的 raw 地址

用法
    RELEASE_TAG=v1.0.1 python3 scripts/update_manifest.py [--dry-run]
"""

import argparse
import hashlib
import json
import os
import re
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
MANIFEST = os.path.join(ROOT, "manifest.json")
META = os.path.join(ROOT, "meta.json")
PROPS = os.path.join(ROOT, "Directory.Build.props")
NOTES = os.path.join(ROOT, "RELEASE_NOTES.md")

REPO_SLUG = os.environ.get("REPO_SLUG", "zian-yan/jellyfin-plugin-ratings")

# 唯一支持的 Jellyfin ABI：12.x（net10.0）
TARGET_ABI = "12.0.0.0"
SLUG = "jf12"
ZIP_NAME = "ratings-jf12.zip"


def read_version() -> str:
    """从 Directory.Build.props 读取 <Version>，保证与程序集版本一致。"""
    with open(PROPS, encoding="utf-8") as fh:
        match = re.search(r"<Version>([^<]+)</Version>", fh.read())
    if not match:
        raise SystemExit("Directory.Build.props 里找不到 <Version>")
    return match.group(1).strip()


def full_version(version: str) -> str:
    """1.0.1 -> 1.0.1.0；1.0.1.0 原样返回。"""
    return version if version.count(".") == 3 else version + ".0"


def version_key(text: str) -> list:
    parts = []
    for piece in text.split("."):
        try:
            parts.append(int(piece))
        except ValueError:
            parts.append(0)
    return parts


def md5_of(path: str) -> str:
    digest = hashlib.md5()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_existing() -> list:
    if not os.path.isfile(MANIFEST):
        return []
    with open(MANIFEST, encoding="utf-8") as fh:
        data = json.load(fh)
    return data if isinstance(data, list) else []


def build_plugin(slug_tag: str, version: str, changelog: str) -> dict:
    with open(META, encoding="utf-8") as fh:
        meta = json.load(fh)

    src = os.path.join(DIST, ZIP_NAME)
    if not os.path.isfile(src):
        raise SystemExit(f"缺少插件包 {src}，请先运行 bash build.sh")

    checksum = md5_of(src)
    print(f"  ratings-{version}-{SLUG}.zip  md5={checksum}")

    image_url = os.environ.get("IMAGE_URL") or (
        f"https://raw.githubusercontent.com/{REPO_SLUG}/main/assets/icon.png"
    )

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": TARGET_ABI,
        "sourceUrl": (
            f"https://github.com/{REPO_SLUG}/releases/download/"
            f"{slug_tag}/ratings-{version}-{SLUG}.zip"
        ),
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc)
        .replace(microsecond=0)
        .strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    return {
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": meta.get("owner") or REPO_SLUG.split("/")[0],
        "category": meta.get("category", "General"),
        "imageUrl": image_url,
        "versions": [entry],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true", help="只打印结果，不写文件")
    args = parser.parse_args()

    raw_tag = os.environ.get("RELEASE_TAG", "").strip()
    if not raw_tag:
        raise SystemExit("缺少环境变量 RELEASE_TAG（形如 v1.0.1）")
    tag = raw_tag if raw_tag.startswith("v") else "v" + raw_tag

    version = full_version(read_version())

    changelog = ""
    if os.path.isfile(NOTES):
        with open(NOTES, encoding="utf-8") as fh:
            changelog = fh.read().strip()
    if not changelog:
        changelog = f"v{version}"

    fresh = build_plugin(tag, version, changelog)

    existing = load_existing()
    plugin = existing[0] if existing else {}
    fresh_targets = {entry["targetAbi"] for entry in fresh["versions"]}
    kept = [
        item
        for item in plugin.get("versions", [])
        # 只保留当前仍支持的 ABI；同版本同 ABI 的旧条目会被新条目覆盖
        if item.get("targetAbi") in fresh_targets
        and (item.get("version"), item.get("targetAbi"))
        not in {(entry["version"], entry["targetAbi"]) for entry in fresh["versions"]}
    ]

    merged = fresh["versions"] + kept
    # 版本降序（同一 ABI 只会有一个版本，无需再按 ABI 排序）
    merged.sort(key=lambda item: version_key(item.get("version", "0")), reverse=True)

    fresh["versions"] = merged

    text = json.dumps([fresh], ensure_ascii=False, indent=2) + "\n"
    if args.dry_run:
        print(text)
        return 0

    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)

    print(f"manifest.json 已更新：{version} / {tag}，共 {len(merged)} 条版本记录")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
