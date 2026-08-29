#!/usr/bin/env bash
# 打成 AppImage 单文件（跨发行版分发，规避依赖地狱）。需 appimagetool 在 PATH。
#   ./package-appimage.sh linux-x64
set -euo pipefail

RID="${1:-linux-x64}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DIST="$HERE/../dist/$RID"
APPDIR="$HERE/../dist/PitMine3D.AppDir"

[ -d "$DIST" ] || { echo "缺 $DIST，先跑 publish-linux.sh $RID"; exit 1; }

rm -rf "$APPDIR"
install -d "$APPDIR/usr/bin"
cp -r "$DIST"/* "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/PitMine3D.Kylin"
cp "$HERE/pitmine3d.desktop" "$APPDIR/pitmine3d.desktop"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/PitMine3D.Kylin" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# 占位图标（正式版换成真 png）
touch "$APPDIR/pitmine3d.png"

ARCH_ENV=$(case "$RID" in linux-x64) echo x86_64;; linux-arm64) echo aarch64;; linux-loongarch64) echo loongarch64;; esac)
ARCH="$ARCH_ENV" appimagetool "$APPDIR" "$HERE/../dist/PitMine3D-$RID.AppImage"
echo ">> 生成: dist/PitMine3D-$RID.AppImage"
