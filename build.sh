#!/usr/bin/env bash
# 一键构建双版本插件包：
#   dist/ratings-jf10.11.zip   -> Jellyfin 10.11.x (net9.0)
#   dist/ratings-jf12.zip      -> Jellyfin 12.x    (net10.0)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/src/Jellyfin.Plugin.Ratings/Jellyfin.Plugin.Ratings.csproj"
OUT="$ROOT/dist"
VERSION="${VERSION:-1.0.0}"
GUID="7f1e5b2a-9c3d-4e8b-a1f6-2d4c6b8e9a10"

NAME="Ratings"
OVERVIEW="外部评分聚合：用 MDBList / TMDb / 豆瓣 / AniList 等来源替换 Jellyfin 内置评分"
DESCRIPTION="服务端聚合多来源影视评分，缓存后由前端脚本渲染；支持单集评分与烂番茄/Metacritic 认证角标。"

rm -rf "$OUT"
mkdir -p "$OUT"

package() {
  local framework="$1" abi="$2" slug="$3" work="$OUT/work-$3"

  echo "==> 构建 $framework (targetAbi $abi)"
  rm -rf "$work" && mkdir -p "$work"
  dotnet publish "$PROJ" -c Release -f "$framework" -o "$work" --nologo -v q

  # 插件包只需要程序集与 meta.json，deps.json 会干扰宿主加载
  rm -f "$work"/*.deps.json

  cat > "$work/meta.json" <<EOF
{
  "category": "General",
  "guid": "$GUID",
  "name": "$NAME",
  "description": "$DESCRIPTION",
  "overview": "$OVERVIEW",
  "owner": "",
  "targetAbi": "$abi",
  "version": "$VERSION.0",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF

  python - "$work" "$OUT/ratings-$slug.zip" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED) as zf:
    for name in sorted(os.listdir(src)):
        path = os.path.join(src, name)
        if os.path.isfile(path):
            zf.write(path, name)
print(f"    packed {dst} ({os.path.getsize(dst)} bytes)")
PY

  rm -rf "$work"
}

package "net9.0"  "10.11.0.0" "jf10.11"
package "net10.0" "12.0.0.0"  "jf12"

echo
echo "完成："
ls -lh "$OUT"/*.zip | awk '{print "  " $9 "  " $5}'

# 生成/刷新 Jellyfin 插件仓库清单（docs/manifest.json + docs/packages/*.zip）
if command -v python >/dev/null 2>&1; then
  echo
  echo "==> 生成插件仓库清单"
  python "$ROOT/tools/make-manifest.py"
fi
