#!/usr/bin/env bash
# 把「环境体检器」打成独立 .deb（不含主程序, 36MB 单文件, 装完直接 pitmine3d-doctor）。
#   ./make-doctor-deb.sh linux-x64 0.1.0
set -euo pipefail

RID="${1:-linux-x64}"
VER="${2:-0.1.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$HERE/.."

case "$RID" in
    linux-x64)   ARCH=amd64 ;;
    linux-arm64) ARCH=arm64 ;;
    *) echo "未知 RID: $RID"; exit 1 ;;
esac

echo ">> 发布体检器 ($RID)"
PUB="$ROOT/dist/doctor-$RID"
rm -rf "$PUB"
dotnet publish "$ROOT/tools/PitMine3D.Doctor/PitMine3D.Doctor.csproj" -c Release -r "$RID" -o "$PUB" >/dev/null
BIN="$PUB/pitmine3d-doctor"
[ -f "$BIN" ] || { echo "发布产物里没有 pitmine3d-doctor"; exit 1; }

STAGE="$(mktemp -d)"
mkdir -p "$STAGE/opt/pitmine3d-doctor" "$STAGE/usr/share/doc/pitmine3d-doctor" "$STAGE/DEBIAN"
cp "$BIN" "$STAGE/opt/pitmine3d-doctor/"
cp "$HERE/pitmine3d-doctor.sh" "$STAGE/opt/pitmine3d-doctor/"
chmod +x "$STAGE/opt/pitmine3d-doctor/pitmine3d-doctor" "$STAGE/opt/pitmine3d-doctor/pitmine3d-doctor.sh"

cat > "$STAGE/usr/share/doc/pitmine3d-doctor/README.md" <<'DOC'
# PitMine3D 环境体检器

用途：主程序在麒麟机上启动异常时，先查清「哪一项不满足」。独立运行，不需要装主程序。

    pitmine3d-doctor          # 完整版(自带 GLX 探测, 免装 mesa-utils)
    pitmine3d-doctor.sh       # 纯脚本版(几 KB, 无任何依赖; GL 版本借上面那个或 glxinfo)

两者都会做「依赖全量扫描」: 解析 /opt/pitmine3d 里每个 ELF 的 DT_NEEDED,
把真实需要的动态库逐个判定 包内自带 / 系统已有 / 缺失, 并给出 apt 安装命令。

报告：`~/pitmine3d-doctor.log`（把它回传即可定位问题）

检查项：系统/架构/发行版/glibc · 主程序运行时要加载的动态库 · ICU · 显示会话(X11/Wayland)
· 真实创建 GLX 上下文读出 OpenGL/GLSL 版本与渲染器 · EGL · 中文字体，并按主程序档位给结论。

退出码：0=无致命缺失，1=有 [缺失] 项。
DOC

INSTALLED_KB=$(du -sk "$STAGE/opt" "$STAGE/usr" | awk '{s+=$1} END {print s}')
cat > "$STAGE/DEBIAN/control" <<CTRL
Package: pitmine3d-doctor
Version: $VER
Section: utils
Priority: optional
Architecture: $ARCH
Maintainer: DayOps <noreply@example.com>
Installed-Size: $INSTALLED_KB
Depends: libc6
Description: PitMine3D 麒麟环境体检器
 单文件诊断工具: 检查 glibc / 动态库 / ICU / 显示会话 / OpenGL 与 GLSL 版本 / 中文字体,
 判断本机是否满足 PitMine3D 麒麟版的运行条件, 并把报告写到 ~/pitmine3d-doctor.log。
 不依赖主程序, 可单独安装运行。
CTRL

cat > "$STAGE/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
ln -sf /opt/pitmine3d-doctor/pitmine3d-doctor /usr/bin/pitmine3d-doctor
ln -sf /opt/pitmine3d-doctor/pitmine3d-doctor.sh /usr/bin/pitmine3d-doctor.sh
chmod +x /opt/pitmine3d-doctor/pitmine3d-doctor /opt/pitmine3d-doctor/pitmine3d-doctor.sh 2>/dev/null || true
echo "已安装环境体检器。运行: pitmine3d-doctor   (报告: ~/pitmine3d-doctor.log)"
exit 0
POST

cat > "$STAGE/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
rm -f /usr/bin/pitmine3d-doctor /usr/bin/pitmine3d-doctor.sh
exit 0
PRERM
chmod 755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm"

OUT="$ROOT/dist/pitmine3d-doctor_${VER}_${ARCH}.deb"
bash "$HERE/deb-pack.sh" "$STAGE" "$OUT"
rm -rf "$STAGE" "$PUB"
echo ">> 生成: $OUT  ($(du -h "$OUT" | cut -f1))"
echo "   安装: sudo dpkg -i $(basename "$OUT")   ·  运行: pitmine3d-doctor"
