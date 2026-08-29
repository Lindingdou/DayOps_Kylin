#!/usr/bin/env bash
# 把发布产物打成 .deb（麒麟桌面 Debian 系）。需先跑 publish-linux.sh，且已装 fpm。
#   sudo gem install fpm    # 或 apt install ruby-dev && gem install fpm
#   ./package-deb.sh linux-x64 0.1.0
set -euo pipefail

RID="${1:-linux-x64}"
VER="${2:-0.1.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DIST="$HERE/../dist/$RID"

case "$RID" in
    linux-x64)         ARCH=amd64 ;;
    linux-arm64)       ARCH=arm64 ;;
    linux-loongarch64) ARCH=loong64 ;;
    *) echo "未知 RID: $RID"; exit 1 ;;
esac

[ -d "$DIST" ] || { echo "缺 $DIST，先跑 publish-linux.sh $RID"; exit 1; }

STAGE="$(mktemp -d)"
install -d "$STAGE/opt/pitmine3d" "$STAGE/usr/share/applications" "$STAGE/usr/bin"
cp -r "$DIST"/* "$STAGE/opt/pitmine3d/"
chmod +x "$STAGE/opt/pitmine3d/PitMine3D.Kylin"
ln -sf /opt/pitmine3d/PitMine3D.Kylin "$STAGE/usr/bin/pitmine3d"
cp "$HERE/pitmine3d.desktop" "$STAGE/usr/share/applications/"

fpm -s dir -t deb -n pitmine3d -v "$VER" -a "$ARCH" \
    --description "PitMine3D 露天矿三维平台 · 麒麟版 (Avalonia + OpenGL)" \
    --depends "libfontconfig1" --depends "libice6" --depends "libsm6" \
    -C "$STAGE" -p "$HERE/../dist/pitmine3d_${VER}_${ARCH}.deb" \
    opt usr

rm -rf "$STAGE"
echo ">> 生成: dist/pitmine3d_${VER}_${ARCH}.deb"
echo "   安装: sudo dpkg -i dist/pitmine3d_${VER}_${ARCH}.deb"
