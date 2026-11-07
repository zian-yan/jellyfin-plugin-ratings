#!/usr/bin/env python3
"""生成 Jellyfin 插件仓库清单。

输入：dist/ratings-jf10.11.zip、dist/ratings-jf12.zip（由 build.sh 产出）
输出：docs/packages/*.zip（入库，供 Jellyfin 下载）+ docs/manifest.json（仓库清单）

用法：
    python tools/make-manifest.py
    REPO_BASE_URL=https://zian-yan.github.io/jellyfin-plugin-ratings python tools/make-manifest.py

关于版本排序（重要）：
    Jellyfin 选版逻辑见 Emby.Server.Implementations/Updates/InstallationManager.cs
      1) 过滤 TargetAbi <= 当前服务器版本
      2) availableVersions.OrderByDescending(VersionNumber)，取第一条
    OrderBy 是稳定排序，两个 ABI 的版本号相同时，manifest 中的先后顺序决定结果。
    所以 12.x 的条目必须排在 10.11 之前：10.11 服务器会把 12 条目过滤掉，
    12 服务器则两条都通过，此时先命中的 12 条目生效。
"""

import hashlib
import json
import os
import re
import shutil
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
DOCS = os.path.join(ROOT, "docs")
PACKAGES = os.path.join(DOCS, "packages")

GUID = "7f1e5b2a-9c3d-4e8b-a1f6-2d4c6b8e9a10"
NAME = "Ratings"
CATEGORY = "General"
OWNER = "zian-yan"
DESCRIPTION = "服务端聚合多来源影视评分（MDBList / TMDb / 豆瓣 / AniList 等），由前端脚本渲染，支持单集评分与认证角标。"
OVERVIEW = "用 IMDb、烂番茄、Metacritic、Trakt、Letterboxd、豆瓣、AniList 等来源的评分替换 Jellyfin 内置评分。"
CHANGELOG = "1.0.0：首个插件版本，兼容 Jellyfin 10.11 与 12。"

DEFAULT_BASE = "https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-ratings/main/docs"

# zip 文件名 -> (targetAbi, 排序权重)
TARGETS = {
    "ratings-jf10.11.zip": ("10.11.0.0", 1011, "jf10.11"),
    "ratings-jf12.zip": ("12.0.0.0", 1200, "jf12"),
}


def read_version() -> str:
    """从 Directory.Build.props 读取 <Version>，保持与程序集版本一致。"""
    props = os.path.join(ROOT, "Directory.Build.props")
    try:
        with open(props, encoding="utf-8") as fh:
            match = re.search(r"<Version>([^<]+)</Version>", fh.read())
        if match:
            return match.group(1).strip()
    except OSError:
        pass
    return "1.0.0"


def md5_of(path: str) -> str:
    digest = hashlib.md5()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    version = os.environ.get("VERSION") or read_version()
    full_version = version if version.count(".") == 3 else version + ".0"
    base = (os.environ.get("REPO_BASE_URL") or DEFAULT_BASE).rstrip("/")

    if not os.path.isdir(DIST):
        print(f"找不到 {DIST}，请先运行 bash build.sh", file=sys.stderr)
        return 1

    os.makedirs(PACKAGES, exist_ok=True)

    entries = []
    for zip_name, (target_abi, weight, slug) in TARGETS.items():
        src = os.path.join(DIST, zip_name)
        if not os.path.isfile(src):
            print(f"缺少插件包 {src}", file=sys.stderr)
            return 1

        out_name = f"ratings-{full_version}-{slug}.zip"
        dst = os.path.join(PACKAGES, out_name)
        shutil.copyfile(src, dst)

        entries.append(
            {
                "version": full_version,
                "changelog": CHANGELOG,
                "targetAbi": target_abi,
                "sourceUrl": f"{base}/packages/{out_name}",
                "checksum": md5_of(dst),
                "timestamp": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
                "_weight": weight,
            }
        )
        print(f"  {out_name}  {os.path.getsize(dst) // 1024} KB  md5={entries[-1]['checksum']}")

    # 高 ABI 排前面（见文件头的说明）
    entries.sort(key=lambda item: item["_weight"], reverse=True)
    for entry in entries:
        entry.pop("_weight")

    manifest = [
        {
            "guid": GUID,
            "name": NAME,
            "description": DESCRIPTION,
            "overview": OVERVIEW,
            "owner": OWNER,
            "category": CATEGORY,
            "imageUrl": f"{base}/icon.png",
            "versions": entries,
        }
    ]

    manifest_path = os.path.join(DOCS, "manifest.json")
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=2)
        fh.write("\n")

    print()
    print(f"清单已写入 {os.path.relpath(manifest_path, ROOT)}")
    print("在 Jellyfin 控制台 → 插件 → 仓库 中添加：")
    print(f"  {base}/manifest.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
