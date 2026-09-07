#!/usr/bin/env bash
# 取"随包携带"的运行时依赖 —— 目前是 app-local ICU（.NET 在麒麟上最常缺/版本不匹配的依赖）。
# 需联网, 只跑一次; 产物落在 build/deps/, 由 make-deb.sh 打进包里。
#   ./fetch-deps.sh linux-x64
set -euo pipefail

RID="${1:-linux-x64}"
ICU_VER="${2:-72.1.0.3}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DEPS="$HERE/deps"
mkdir -p "$DEPS"

case "$RID" in
    linux-x64)   PKG="Microsoft.ICU.ICU4C.Runtime.linux-x64";   OUTDIR="$DEPS/icu-x64" ;;
    linux-arm64) PKG="Microsoft.ICU.ICU4C.Runtime.linux-arm64"; OUTDIR="$DEPS/icu-arm64" ;;
    *) echo "未支持的 RID: $RID"; exit 1 ;;
esac

ZIP="$DEPS/$(echo "$PKG" | tr 'A-Z.' 'a-z-').zip"
if [ ! -s "$ZIP" ]; then
    echo ">> 下载 $PKG $ICU_VER"
    curl -fL --retry 3 -o "$ZIP" "https://www.nuget.org/api/v2/package/$PKG/$ICU_VER"
fi
rm -rf "$OUTDIR"
unzip -q -o "$ZIP" -d "$OUTDIR"
echo ">> ICU 就绪:"
find "$OUTDIR" -name 'libicu*.so.*' -exec ls -lh {} \; | awk '{print "   ", $5, $9}'
