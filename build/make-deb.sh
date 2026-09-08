#!/usr/bin/env bash
# 打 .deb（麒麟/Debian 系）。与 package-deb.sh 的区别：不依赖 fpm/dpkg-deb/ar，
# 纯 tar + 手工 ar 归档 —— 因此在 Windows(Git Bash) 上也能出包，方便无 Linux 机时直接交付测试。
#
#   ./publish-linux.sh linux-x64            # 先交叉发布(自包含, 目标机无需装 .NET)
#   ./fetch-deps.sh                         # 取 app-local ICU(麒麟上 .NET 最常缺的依赖)
#   ./make-deb.sh linux-x64 0.1.0
#
# 产物: dist/pitmine3d_<VER>_<ARCH>.deb
set -euo pipefail

RID="${1:-linux-x64}"
VER="${2:-0.1.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$HERE/.."
DIST="$ROOT/dist/$RID"

case "$RID" in
    linux-x64)         ARCH=amd64;  ICUDIR="$HERE/deps/icu-x64/runtimes/linux-x64/native" ;;
    linux-arm64)       ARCH=arm64;  ICUDIR="$HERE/deps/icu-arm64/runtimes/linux-arm64/native" ;;
    linux-loongarch64) ARCH=loong64; ICUDIR="" ;;
    *) echo "未知 RID: $RID"; exit 1 ;;
esac

# 先重新发布, 再打包。**不要**依赖 dist/ 里已有的产物 ——
# 之前这里只检查目录存在就直接拷, 结果连出 4 个版本号不同、内容却是同一份旧二进制的包,
# 改的东西一次都没进包, 白白排查了几轮。除非显式 --no-publish, 否则一律重发布。
if [ "${3:-}" = "--no-publish" ]; then
    [ -d "$DIST" ] || { echo "缺 $DIST，去掉 --no-publish 或先跑: ./publish-linux.sh $RID"; exit 1; }
    echo "!! --no-publish: 直接用已有产物 $DIST ($(date -r "$DIST" "+%Y-%m-%d %H:%M" 2>/dev/null || echo "?"))"
else
    echo ">> 重新发布(保证包里是当前源码)"
    rm -rf "$DIST"
    bash "$HERE/publish-linux.sh" "$RID" Release
fi
[ -d "$DIST" ] || { echo "发布失败: 没有 $DIST"; exit 1; }

# 产物必须比源码新, 否则就是拿了旧东西打包
NEWEST_SRC=$(find "$ROOT/src" -name '*.cs' -newer "$DIST/PitMine3D.Kylin.dll" -print -quit 2>/dev/null || true)
if [ -n "$NEWEST_SRC" ]; then
    echo "!! 警告: 源码比发布产物新($NEWEST_SRC), 包里可能不是最新代码"; exit 1
fi

STAGE="$(mktemp -d)"
APPDIR="$STAGE/opt/pitmine3d"
mkdir -p "$APPDIR" "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/scalable/apps" \
         "$STAGE/usr/share/doc/pitmine3d" "$STAGE/DEBIAN"

echo ">> 拷贝应用(自包含 .NET + Avalonia/Skia 原生库)"
cp -r "$DIST"/. "$APPDIR/"

# ── 随包携带的运行时依赖 ───────────────────────────────────────────
# ICU：.NET 的全球化(非 Invariant)依赖，麒麟上版本常不匹配/缺失 —— 带一份, 系统没有时才用。
if [ -n "$ICUDIR" ] && [ -d "$ICUDIR" ]; then
    mkdir -p "$APPDIR/runtime-libs"
    cp "$ICUDIR"/libicu*.so.* "$APPDIR/runtime-libs/"
    echo ">> 已带 ICU: $(ls "$APPDIR/runtime-libs" | tr '\n' ' ')"
else
    echo "!! 未找到 ICU($ICUDIR)，包内不带 ICU；麒麟若缺 libicu 将回退到 Invariant 模式"
fi

# ── 启动器：按需补依赖, 再起应用 ───────────────────────────────────
cat > "$APPDIR/pitmine3d.sh" <<'LAUNCH'
#!/bin/sh
# PitMine3D 麒麟版启动器：系统缺 ICU 时用随包携带的一份；再缺就退到 Invariant 模式(仅影响区域格式)。
APP_DIR=/opt/pitmine3d
LIBS="$APP_DIR/runtime-libs"

# ldconfig 常在 /sbin, 普通用户 PATH 里可能没有; 都查不到再翻常见库目录
has_lib() {
    for LDC in ldconfig /sbin/ldconfig /usr/sbin/ldconfig; do
        command -v "$LDC" >/dev/null 2>&1 || [ -x "$LDC" ] || continue
        "$LDC" -p 2>/dev/null | grep -q "$1" && return 0
    done
    for D in /usr/lib /usr/lib64 /lib /lib64 /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu; do
        ls "$D"/$1* >/dev/null 2>&1 && return 0
    done
    return 1
}

if ! has_lib "libicuuc.so"; then
    if [ -d "$LIBS" ] && ls "$LIBS"/libicuuc.so.* >/dev/null 2>&1; then
        LD_LIBRARY_PATH="$LIBS${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
        export LD_LIBRARY_PATH
    else
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
        export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
    fi
fi

# 麒麟自带 Mesa 时软件渲染更稳；有独显/驱动就走硬件(不强制)
[ -n "${PITMINE_SOFTWARE_GL:-}" ] && { LIBGL_ALWAYS_SOFTWARE=1; export LIBGL_ALWAYS_SOFTWARE; }

# 应急/诊断开关(都由主程序读取, 这里只是留个说明并确保传下去):
#   PITMINE_TRACE=1      每步往 crash.log 落一条 TRACE 并立即刷盘 —— 原生崩溃时看最后一条就知道崩在哪
#   PITMINE_NO_CURSOR=1  不画十字光标(光标每次移动都要重传一次 GL 缓冲, 驱动有问题时最先崩在这)
#   PITMINE_SOFTWARE_GL=1 强制软件渲染

# 运行日志: 始终留一份到用户目录, 闪退时直接回传这个文件
LOGDIR="${XDG_DATA_HOME:-$HOME/.local/share}/PitMine3D.Kylin"
mkdir -p "$LOGDIR" 2>/dev/null || LOGDIR=/tmp
LOG="$LOGDIR/last-run.log"
echo "=== $(date "+%Y-%m-%d %H:%M:%S") 启动 ===" >> "$LOG"

# ── 图形档位自动降级阶梯 ────────────────────────────────────────────────
# 目标是**尽量留在硬件渲染**: 国产 GPU 驱动往往只有低版本路径被充分打磨, 直接投降到
# 软件渲染在 4019x2317 这种分辨率上会很慢。所以按阶梯逐级降低对驱动的要求, 崩一次降一档:
#
#     auto(GL 4.0 优先) → 3.0 → 2.1(兼容配置, 连 VAO 都不用) → soft(软件渲染)
#
# 判据只能看上次的退出状态: 被信号打死(码 >128)即本档不可靠。**不能**按"有没有画出首帧"判 ——
# 实测该驱动有时画出首帧后第 N 帧才崩, 那样永远不降档, 变成反复闪退。
# 某一档能正常退出就停在那一档, 不再降。用户手动指定则完全听用户的, 不动阶梯。
LEVELFILE="$LOGDIR/.gl-level"
CRASHFLAG="$LOGDIR/.gl-crashed"
USING_SOFTWARE=""
LADDER_ON=""

if [ -n "${LIBGL_ALWAYS_SOFTWARE:-}" ]; then
    USING_SOFTWARE=1                      # 用户显式要软件渲染
elif [ -n "${PITMINE_GL_PROFILE:-}" ]; then
    :                                     # 用户显式指定档位, 阶梯让路
else
    LADDER_ON=1
    # 主程序若认出已知问题驱动会写 .gl-crashed；把它并入阶梯(等价于"本档崩过一次")
    LEVEL=$(cat "$LEVELFILE" 2>/dev/null || echo auto)
    if [ -f "$CRASHFLAG" ] && [ "$LEVEL" = "auto" ]; then
        LEVEL=3.0
        echo "$LEVEL" > "$LEVELFILE" 2>/dev/null || true
        rm -f "$CRASHFLAG" 2>/dev/null || true
    fi
    case "$LEVEL" in
        auto) ;;                                                        # 默认协商表(GL 4.0 优先)
        soft) LIBGL_ALWAYS_SOFTWARE=1; export LIBGL_ALWAYS_SOFTWARE; USING_SOFTWARE=1 ;;
        nogl) PITMINE_NO_GL=1; export PITMINE_NO_GL; USING_SOFTWARE=1 ;;   # 停三维视口, 其余功能照常
        *)    PITMINE_GL_PROFILE="$LEVEL"; export PITMINE_GL_PROFILE ;;
    esac
    if [ "$LEVEL" != "auto" ]; then
        echo "图形档位: $LEVEL (此前更高档位崩过; 想从头再试: rm $LEVELFILE)" | tee -a "$LOG" >&2
    fi
fi

# ── 一键诊断模式: PITMINE_DIAG=1 pitmine3d ────────────────────────────────
# 原生崩溃(段错误)不走托管异常钩子, crash.log 里什么都不会有。这里把所有能逼出线索的开关
# 一次性打开: Mesa/GLX verbose、core dump、.NET 崩溃转储；有 gdb 就直接在 gdb 里跑, 崩了
# 自动打印本地/全部线程回溯 —— 那份回溯会明确指出崩在哪个 .so 的哪个函数。
if [ -n "${PITMINE_DIAG:-}" ]; then
    PITMINE_TRACE=1; export PITMINE_TRACE
    LIBGL_DEBUG=verbose; export LIBGL_DEBUG
    MESA_DEBUG=1; export MESA_DEBUG
    EGL_LOG_LEVEL=debug; export EGL_LOG_LEVEL
    # .NET 自带崩溃转储(原生栈也在里面)
    DOTNET_DbgEnableMiniDump=1; export DOTNET_DbgEnableMiniDump
    DOTNET_DbgMiniDumpType=2; export DOTNET_DbgMiniDumpType
    DOTNET_DbgMiniDumpName="$LOGDIR/coredump.%p"; export DOTNET_DbgMiniDumpName
    ulimit -c unlimited 2>/dev/null || true
    echo "=== 诊断模式: TRACE/Mesa 详细日志/崩溃转储 已开启 ===" | tee -a "$LOG"

    if command -v gdb >/dev/null 2>&1; then
        echo "=== 检测到 gdb, 在 gdb 中运行(崩溃时自动打印回溯) ===" | tee -a "$LOG"
        gdb -q -batch \
            -ex "set pagination off" \
            -ex "handle SIGSEGV stop nopass" \
            -ex "run" \
            -ex "echo \n===== 崩溃点回溯 =====\n" \
            -ex "bt full" \
            -ex "echo \n===== 所有线程回溯 =====\n" \
            -ex "thread apply all bt" \
            --args "$APP_DIR/PitMine3D.Kylin" "$@" >> "$LOG" 2>&1
        RC=$?
        echo "gdb 结束(码 $RC)。回溯见: $LOG" >&2
        tail -n 60 "$LOG" >&2 2>/dev/null || true
        exit "$RC"
    fi
    echo "!! 没装 gdb, 拿不到原生回溯。装上再跑一次最有效: sudo apt install gdb" | tee -a "$LOG"
fi

# 不要用 "app | tee" 拿退出码: PIPESTATUS 是 bash 专有的, 麒麟的 /bin/sh 是 dash,
# ${PIPESTATUS:-0} 在 dash 里恒为 0 —— 段错误也被记成"正常退出", 下面的异常提示永远不打印(踩过)。
# 改为直接重定向取 $?, 失败时再把日志尾巴回显到终端。
"$APP_DIR/PitMine3D.Kylin" "$@" >> "$LOG" 2>&1
RC=$?

# 阶梯推进: 本档被信号打死就降一档; 正常退出说明本档可用, 保持不动。
if [ -n "$LADDER_ON" ] && [ "$RC" -gt 128 ] 2>/dev/null; then
    CUR=$(cat "$LEVELFILE" 2>/dev/null || echo auto)
    case "$CUR" in
        auto) NEXT=3.0 ;;
        3.0)  NEXT=2.1 ;;
        2.1)  NEXT=soft ;;
        soft) NEXT=nogl ;;
        *)    NEXT=nogl ;;
    esac
    echo "$NEXT" > "$LEVELFILE" 2>/dev/null || true
    case "$NEXT" in
        soft) echo "硬件 OpenGL 各档位都崩, 下次启动改用软件渲染(慢但稳)。" >&2 ;;
        nogl) echo "软件渲染也崩, 下次启动将停用三维视口 —— 数据库/表单/命令行等仍可正常使用。" >&2 ;;
        *)    echo "本档图形驱动崩溃, 下次启动降到 OpenGL $NEXT 再试(仍是硬件渲染)。" >&2 ;;
    esac

    # 不直接闪退: 就地按降后的档位自动重启一次, 让用户看到的是"程序还在"而不是窗口消失
    # 最多自动重启 4 次(阶梯共 4 级), 逐级降到能跑为止; 到顶就不再重启, 避免死循环。
    TRIES=${PITMINE_RESTART_N:-0}
    if [ "$TRIES" -lt 4 ] 2>/dev/null; then
        echo "正在按降级档位($NEXT)自动重启…" >&2
        PITMINE_RESTART_N=$((TRIES + 1)); export PITMINE_RESTART_N
        # 必须清掉本轮由阶梯导出的变量: 否则重启后被继承, 阶梯会当成"用户手动指定"而让路,
        # 结果卡在同一档反复崩(实测踩到)。清掉后由重启的实例按 .gl-level 重新决定。
        unset PITMINE_GL_PROFILE LIBGL_ALWAYS_SOFTWARE PITMINE_NO_GL
        exec "$0" "$@"
    fi
fi

if [ "$RC" != "0" ]; then
    # 被信号打死时 shell 返回 128+信号号: 139=SIGSEGV(段错误), 134=SIGABRT, 132=SIGILL, 136=SIGFPE
    WHY=""
    if [ "$RC" -gt 128 ] 2>/dev/null; then
        SIG=$((RC - 128))
        case "$SIG" in
            11) WHY=" —— SIGSEGV 段错误(原生崩溃, 多半出在图形驱动/GL 调用; 托管异常处理器抓不到, 所以 crash.log 里不会有堆栈)" ;;
            6)  WHY=" —— SIGABRT 运行时中止" ;;
            4)  WHY=" —— SIGILL 非法指令(CPU 指令集不匹配)" ;;
            8)  WHY=" —— SIGFPE 算术异常" ;;
            9)  WHY=" —— SIGKILL 被强杀(内存不足?)" ;;
            *)  WHY=" —— 收到信号 $SIG" ;;
        esac
    fi
    # 段错误会留下 core dump —— 直接从里面把原生回溯挖出来, 不必让用户再跑一次 gdb。
    # 这是唯一能指名"崩在哪个 .so 的哪个函数"的东西。
    if [ "$RC" -gt 128 ] 2>/dev/null; then
        CORE="$LOGDIR/last-core"
        rm -f "$CORE" 2>/dev/null || true
        if command -v coredumpctl >/dev/null 2>&1; then
            coredumpctl dump -1 -o "$CORE" >/dev/null 2>&1 || true
        fi
        # 没有 coredumpctl 就按 core_pattern 找当前目录/常见位置的 core 文件
        if [ ! -s "$CORE" ]; then
            for c in ./core ./core.* /var/lib/systemd/coredump/core.PitMine3D* "$LOGDIR"/core*; do
                [ -s "$c" ] && { CORE="$c"; break; }
            done
        fi
        if [ -s "$CORE" ] && command -v gdb >/dev/null 2>&1; then
            {
                echo ""
                echo "===== 原生回溯(取自 core dump: $CORE) ====="
                gdb -q -batch -ex "bt full" -ex "echo 
--- 所有线程 ---
" -ex "thread apply all bt"                     "$APP_DIR/PitMine3D.Kylin" "$CORE" 2>&1
            } >> "$LOG"
            echo "已从 core dump 提取原生回溯, 见日志末尾: $LOG" >&2
        elif [ -s "$CORE" ]; then
            echo "找到 core dump($CORE) 但没装 gdb。装上再崩一次即可自动出回溯: sudo apt install gdb" >&2
        else
            echo "没找到 core dump。开启方法: ulimit -c unlimited, 或装 systemd-coredump" >&2
        fi
    fi
    echo "PitMine3D 异常退出(码 $RC)$WHY" >&2
    echo "运行日志: $LOG" >&2
    echo "崩溃日志: $LOGDIR/crash.log" >&2
    echo "--- 运行日志末尾 40 行 ---" >&2
    tail -n 40 "$LOG" >&2 2>/dev/null || true
    echo "--- 如需逐步定位原生崩溃, 用 PITMINE_TRACE=1 pitmine3d 再跑一次, 看 crash.log 最后一条 TRACE ---" >&2
fi
exit "$RC"
LAUNCH
chmod +x "$APPDIR/pitmine3d.sh" "$APPDIR/PitMine3D.Kylin" 2>/dev/null || true

cp "$HERE/pitmine3d.desktop" "$STAGE/usr/share/applications/pitmine3d.desktop"
sed -i 's|^Exec=.*|Exec=/opt/pitmine3d/pitmine3d.sh %F|' "$STAGE/usr/share/applications/pitmine3d.desktop"
cp "$HERE/pitmine3d.svg" "$STAGE/usr/share/icons/hicolor/scalable/apps/pitmine3d.svg"

cat > "$STAGE/usr/share/doc/pitmine3d/README.Kylin.md" <<'DOC'
# PitMine3D 麒麟版

- 安装: `sudo dpkg -i pitmine3d_<版本>_<架构>.deb`
- 启动: 开始菜单「露天矿三维平台」，或终端 `pitmine3d`
- 卸载: `sudo dpkg -r pitmine3d`

## 随包携带的依赖
- **.NET 8 运行时**（自包含发布，目标机无需安装 .NET）
- **Avalonia / SkiaSharp / HarfBuzz / SQLite 原生库**（随发布产物）
- **ICU 72**（`/opt/pitmine3d/runtime-libs`）：仅当系统 `ldconfig` 查不到 `libicuuc.so` 时才启用；
  若两者都没有，启动器自动退到 Invariant 全球化模式（只影响区域格式，程序可正常使用）。

## 需要系统提供（麒麟桌面默认都有）
X11 与 OpenGL 栈: `libx11-6 libice6 libsm6 libgl1 libfontconfig1`。
若缺，联网机器执行: `sudo apt-get install -y libx11-6 libice6 libsm6 libgl1 libfontconfig1`

## 显示异常时
- 无 GPU 驱动/花屏: `PITMINE_SOFTWARE_GL=1 pitmine3d`（强制软件渲染）
- 日志(自动留存): `~/.local/share/PitMine3D.Kylin/last-run.log` 与 `crash.log`
- 闪退时把这两个文件回传即可定位; 也可前台直接看: `pitmine3d`
DOC

INSTALLED_KB=$(du -sk "$STAGE/opt" "$STAGE/usr" | awk '{s+=$1} END {print s}')
cat > "$STAGE/DEBIAN/control" <<CTRL
Package: pitmine3d
Version: $VER
Section: science
Priority: optional
Architecture: $ARCH
Maintainer: DayOps <noreply@example.com>
Installed-Size: $INSTALLED_KB
Depends: libc6
Recommends: libx11-6, libice6, libsm6, libgl1, libfontconfig1
Description: PitMine3D 露天矿三维平台 (麒麟版)
 露天煤矿二三维一体化生产计划决策支撑系统的麒麟/Linux 版本。
 基于 Avalonia + OpenGL, 自包含 .NET 8 运行时, 随包携带 ICU, 目标机无需联网装依赖。
CTRL

cat > "$STAGE/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
# 修复 0.1.0/0.1.2 那两版打包留下的损伤：当时把 ./usr ./usr/share 等目录打成了 drw-r--r--,
# dpkg 会把目标机对应目录一并改成无执行位, 非 root 从此进不去 /usr, /usr/bin 里的命令全"找不到"。
for d in /usr /usr/share /usr/share/doc /usr/share/applications /usr/share/icons /usr/share/icons/hicolor /usr/bin; do
    [ -d "$d" ] || continue
    case "$(ls -ld "$d" | cut -c1-10)" in
        d?????????) [ -x "$d" ] || { chmod 755 "$d"; echo "已修复目录权限: $d (此前无执行位)"; } ;;
    esac
done
ln -sf /opt/pitmine3d/pitmine3d.sh /usr/bin/pitmine3d
chmod +x /opt/pitmine3d/PitMine3D.Kylin /opt/pitmine3d/pitmine3d.sh 2>/dev/null || true
# ICU soname 软链(.NET 按 libicuuc.so.<主版本> 查找)
if [ -d /opt/pitmine3d/runtime-libs ]; then
    for f in /opt/pitmine3d/runtime-libs/libicu*.so.*; do
        [ -e "$f" ] || continue
        base=$(basename "$f"); stem=${base%%.so.*}; ver=${base#*.so.}; major=${ver%%.*}
        ln -sf "$base" "/opt/pitmine3d/runtime-libs/$stem.so.$major"
        ln -sf "$base" "/opt/pitmine3d/runtime-libs/$stem.so"
    done
fi
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
# 清掉旧版留下的图形降档状态：0.1.9 曾把 Glenfly 误判为坏驱动直接降级，
# 而真正的崩因是 fcitx 输入法那条 DBus 通路(本版已关)。升级后让硬件渲染重新参与。
for f in "$HOME"/.local/share/PitMine3D.Kylin/.gl-level "$HOME"/.local/share/PitMine3D.Kylin/.gl-crashed; do
    [ -e "$f" ] && rm -f "$f" && echo "已清除旧的图形降档状态: $f"
done
for d in /home/*/.local/share/PitMine3D.Kylin /root/.local/share/PitMine3D.Kylin; do
    [ -d "$d" ] || continue
    rm -f "$d/.gl-level" "$d/.gl-crashed" 2>/dev/null || true
done

if command -v pitmine3d >/dev/null 2>&1; then
    echo "已安装。启动: pitmine3d"
else
    echo "已安装, 但 /usr/bin 软链不可用。请直接运行: /opt/pitmine3d/pitmine3d.sh"
fi
echo "启动异常时先跑体检器: pitmine3d-doctor  (报告 ~/pitmine3d-doctor.log)"
exit 0
POST

cat > "$STAGE/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
rm -f /usr/bin/pitmine3d
exit 0
PRERM
chmod 755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm"

OUT="$ROOT/dist/pitmine3d_${VER}_${ARCH}.deb"
mkdir -p "$ROOT/dist"

bash "$HERE/deb-pack.sh" "$STAGE" "$OUT"

rm -rf "$STAGE"
SZ=$(du -h "$OUT" | cut -f1)
echo ">> 生成: $OUT  ($SZ)"
echo "   安装: sudo dpkg -i $(basename "$OUT")   ·  启动: pitmine3d"
