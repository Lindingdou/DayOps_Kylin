#!/bin/sh
# PitMine3D 麒麟环境体检（纯 shell，无任何依赖，拷过去 chmod +x 就能跑）
#   ./pitmine3d-doctor.sh          屏幕输出 + 报告落 ~/pitmine3d-doctor.log
# 查: 系统/glibc · 必需动态库 · ICU · 显示会话 · OpenGL/GLSL 版本 · 中文字体 · 主程序安装状态
# OpenGL 版本优先用同目录/系统里的 pitmine3d-doctor(自带 GLX 探测)，没有再退 glxinfo。
LOG="${HOME:-/tmp}/pitmine3d-doctor.log"
FAIL=0; WARN=0
: > "$LOG"
say()  { echo "$*"; echo "$*" >> "$LOG"; }
head_() { say ""; say "── $* ────────────────────────────────────────────"; }
ok()   { say "  [ 正常 ] $*"; }
warn() { WARN=$((WARN+1)); say "  [ 注意 ] $*"; }
bad()  { FAIL=$((FAIL+1)); say "  [ 缺失 ] $*"; }

say "PitMine3D 麒麟环境体检  $(date '+%Y-%m-%d %H:%M:%S')"
say "(纯脚本，不依赖主程序；把 $LOG 回传即可定位问题)"

# ── 系统 ──
head_ "系统"
say "  内核      : $(uname -srm)"
[ -r /etc/os-release ] && say "  发行版    : $(. /etc/os-release; echo "$PRETTY_NAME")"
[ -r /etc/kylin-build ] && say "  麒麟构建  : $(head -2 /etc/kylin-build | tr '\n' ' ')"
GLIBC=$(ldd --version 2>/dev/null | head -1 | awk '{print $NF}')
if [ -n "$GLIBC" ]; then
    MAJ=${GLIBC%%.*}; MIN=${GLIBC#*.}; MIN=${MIN%%.*}
    if [ "${MAJ:-0}" -gt 2 ] || { [ "${MAJ:-0}" -eq 2 ] && [ "${MIN:-0}" -ge 23 ]; }
    then ok "glibc $GLIBC (.NET 8 要求 ≥ 2.23)"
    else bad "glibc $GLIBC 低于 .NET 8 要求的 2.23 —— 主程序无法运行"; fi
else warn "glibc 版本读不到(无 ldd)"; fi

# ── 动态库 ──
head_ "必需动态库"
has_so() {   # $1=soname
    for d in /usr/lib /usr/lib64 /lib /lib64 /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu \
             /usr/local/lib /usr/lib/loongarch64-linux-gnu; do
        [ -e "$d/$1" ] && { echo "$d/$1"; return 0; }
    done
    for L in ldconfig /sbin/ldconfig /usr/sbin/ldconfig; do
        p=$("$L" -p 2>/dev/null | awk -v n="$1" '$1==n {print $NF; exit}') && [ -n "$p" ] && { echo "$p"; return 0; }
    done
    return 1
}
chk_so() {   # $1=soname $2=说明 $3=fatal(1/0)
    p=$(has_so "$1") && { ok "$1 — $2"; return 0; }
    if [ "$3" = "1" ]; then bad "$1 — $2（主程序会加载失败）"; else warn "$1 — $2（缺失可能影响个别功能）"; fi
    return 1
}
chk_so libstdc++.so.6    "C++ 运行时(Skia 依赖)"        1
chk_so libX11.so.6       "X11 窗口(Avalonia 显示后端)"  1
chk_so libfontconfig.so.1 "字体查找(文字渲染)"          1
chk_so libfreetype.so.6  "字形光栅化"                   1
chk_so libXext.so.6      "X11 扩展"                     0
chk_so libXi.so.6        "X11 输入"                     0
chk_so libXrandr.so.2    "X11 分辨率/多屏"              0
chk_so libXcursor.so.1   "X11 光标"                     0
chk_so libXrender.so.1   "X11 渲染扩展"                 0
chk_so libICE.so.6       "会话管理"                     0
chk_so libSM.so.6        "会话管理"                     0
HAS_GL=0; HAS_EGL=0
chk_so libGL.so.1        "OpenGL(桌面 GLX 路径)"        0 && HAS_GL=1
chk_so libEGL.so.1       "EGL(GLES 路径)"               0 && HAS_EGL=1
[ "$HAS_GL$HAS_EGL" = "00" ] && bad "libGL 与 libEGL 都没有 —— 三维视口无法初始化"

# ── ICU ──
head_ "ICU (.NET 全球化依赖)"
ICU=""
for v in 76 75 74 73 72 71 70 69 68 67 66 65 63 60 57 55 52; do
    p=$(has_so "libicuuc.so.$v") && { ICU="libicuuc.so.$v ($p)"; break; }
done
[ -z "$ICU" ] && { p=$(has_so libicuuc.so) && ICU="libicuuc.so ($p)"; }
if [ -n "$ICU" ]; then ok "系统 ICU: $ICU"
else
    warn "系统无 libicuuc —— 主程序改用随包 ICU；若也没有则退 Invariant 模式(仅影响区域格式)"
    if ls /opt/pitmine3d/runtime-libs/libicuuc.so.* >/dev/null 2>&1
    then ok "随包 ICU: $(ls /opt/pitmine3d/runtime-libs/libicuuc.so.* | head -1)"
    else say "  (未装主程序或包内无 ICU: /opt/pitmine3d/runtime-libs)"; fi
fi

# ── 显示会话 ──
head_ "显示会话"
say "  DISPLAY=${DISPLAY:-(未设)}  WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-(未设)}  XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-(未设)}"
if [ -z "$DISPLAY" ] && [ -z "$WAYLAND_DISPLAY" ]; then
    bad "DISPLAY 与 WAYLAND_DISPLAY 都为空 —— 图形程序起不来(是否在 ssh/字符终端里跑?)"
elif [ -z "$DISPLAY" ]; then
    warn "只有 Wayland；主程序走 X11 后端，需要 XWayland"
else ok "X11 会话 DISPLAY=$DISPLAY"; fi

# ── OpenGL ──
head_ "OpenGL"
GLVER=""; GLREN=""; GLSL=""
PROBE=""
for c in "$(dirname "$0")/pitmine3d-doctor" /opt/pitmine3d-doctor/pitmine3d-doctor; do
    [ -x "$c" ] && { PROBE="$c"; break; }
done
if [ -n "$PROBE" ]; then
    OUT=$("$PROBE" 2>/dev/null)
    GLVER=$(echo "$OUT" | awk -F': *' '/GL 版本/ {print $2; exit}')
    GLREN=$(echo "$OUT" | awk -F': *' '/渲染器/ {print $2; exit}')
    GLSL=$(echo "$OUT"  | awk -F': *' '/GLSL 版本/ {print $2; exit}')
    [ -n "$GLVER" ] && say "  (由 pitmine3d-doctor 探测)"
fi
if [ -z "$GLVER" ] && command -v glxinfo >/dev/null 2>&1; then
    GLVER=$(glxinfo -B 2>/dev/null | awk -F': *' '/OpenGL version string/ {print $2; exit}')
    GLREN=$(glxinfo -B 2>/dev/null | awk -F': *' '/OpenGL renderer string/ {print $2; exit}')
    GLSL=$(glxinfo -B 2>/dev/null | awk -F': *' '/shading language version string/ {print $2; exit}')
    [ -n "$GLVER" ] && say "  (由 glxinfo 探测)"
fi
if [ -n "$GLVER" ]; then
    say "  渲染器    : ${GLREN:-未知}"
    say "  GL 版本   : $GLVER"
    say "  GLSL 版本 : ${GLSL:-未知}"
else
    warn "读不到 OpenGL 版本(既无 pitmine3d-doctor 也无 glxinfo)。可装 mesa-utils 后重跑，或用体检器可执行版"
fi

# ── 字体 ──
head_ "中文字体"
N=0
for d in /usr/share/fonts /usr/local/share/fonts "$HOME/.fonts"; do
    [ -d "$d" ] && N=$((N + $(find "$d" -type f \( -name '*.tt*' -o -name '*.otf' \) 2>/dev/null | wc -l)))
done
[ "$N" -gt 0 ] && ok "字体文件 $N 个" || warn "系统字体目录里没有字体 —— 中文可能显示为方框"

# ── 主程序安装状态 ──
head_ "主程序"
if [ -x /opt/pitmine3d/PitMine3D.Kylin ]; then
    ok "已安装 /opt/pitmine3d/PitMine3D.Kylin"
    for f in "$HOME/.local/share/PitMine3D.Kylin/crash.log" "$HOME/.local/share/PitMine3D.Kylin/last-run.log"; do
        [ -f "$f" ] && say "  日志: $f  ($(wc -l < "$f") 行, 最后修改 $(date -r "$f" '+%m-%d %H:%M' 2>/dev/null))"
    done
else say "  (未安装或路径不同: /opt/pitmine3d)"; fi

# ── 结论 ──
head_ "结论"
case "$GLVER" in
    "") say "  · OpenGL 版本未知 —— 若主程序仍闪退, 先装 mesa-utils 或用体检器可执行版再测" ;;
    *)  MAJ=${GLVER%%.*}; REST=${GLVER#*.}; MIN=${REST%%[!0-9]*}
        V=$(( ${MAJ:-0} * 10 + ${MIN:-0} ))
        if   [ "$V" -ge 33 ]; then say "  · OpenGL $GLVER —— 满足最佳档(GLSL 330)"
        elif [ "$V" -ge 30 ]; then say "  · OpenGL $GLVER —— 走 GLSL 130 档(0.1.2 起支持; 更早版本会因着色器 330 编不过而闪退)"
        elif [ "$V" -ge 21 ]; then say "  · OpenGL $GLVER —— 走 GLSL 110 兼容档(0.1.2 起支持; 0.1.1 及更早会闪退)"
        else say "  · OpenGL $GLVER —— 低于 2.1, 三维视口无法工作"; fi ;;
esac
case "$GLREN" in *llvmpipe*|*softpipe*|*Software*) say "  · 当前为软件渲染, 能跑但大模型会卡; 装显卡驱动可提速" ;; esac
say "  · 缺失 $FAIL 项, 注意 $WARN 项"
[ "$FAIL" -eq 0 ] && say "  · 未发现致命缺失; 若仍闪退, 连同 ~/.local/share/PitMine3D.Kylin/crash.log 一并回传" \
                  || say "  · 标 [缺失] 的即为不满足项, 先补这些"
say ""
say "报告已保存: $LOG"
[ "$FAIL" -eq 0 ]
