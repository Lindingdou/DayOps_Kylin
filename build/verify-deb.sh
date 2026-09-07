#!/usr/bin/env bash
# 校验 .deb 结构(本机无 dpkg 时用)：ar 成员顺序/名字/长度 + 三个成员各自内容 + 关键文件权限。
#   ./verify-deb.sh dist/pitmine3d_0.1.0_amd64.deb
set -euo pipefail
DEB="${1:?用法: verify-deb.sh <deb 文件>}"
[ -s "$DEB" ] || { echo "FAIL 文件不存在/为空: $DEB"; exit 1; }

fail=0
ok()   { echo "  ok   $*"; }
bad()  { echo "  FAIL $*"; fail=1; }

magic=$(head -c 8 "$DEB")
[ "$magic" = "!<arch>" ] && ok "ar 魔数" || bad "ar 魔数: '$magic'"

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
TOTAL=$(wc -c < "$DEB")
off=8; idx=0
declare -a names
fld() { dd if="$DEB" bs=1 skip="$1" count="$2" 2>/dev/null | tr -d '\0' | sed 's/ *$//'; }
while [ $((off + 60)) -le "$TOTAL" ]; do
    name=$(fld "$off" 16)
    mode=$(fld $((off + 40)) 8)
    size=$(fld $((off + 48)) 10)
    endc=$(dd if="$DEB" bs=1 skip=$((off + 58)) count=1 2>/dev/null)
    [ -n "$name" ] || break
    [ "$endc" = '`' ] && ok "成员 $name 头结束符" || bad "成员 $name 头结束符异常('$endc')"
    names[$idx]="$name"
    dd if="$DEB" bs=1 skip=$((off + 60)) count="$size" of="$WORK/$name" 2>/dev/null
    printf '  成员 %-16s size=%-10s mode=%s\n' "$name" "$size" "$mode"
    off=$((off + 60 + size + (size % 2)))
    idx=$((idx + 1))
done

[ "${names[0]:-}" = "debian-binary" ]  && ok "成员1 debian-binary" || bad "成员1 应为 debian-binary"
[ "${names[1]:-}" = "control.tar.gz" ] && ok "成员2 control.tar.gz" || bad "成员2 应为 control.tar.gz"
[ "${names[2]:-}" = "data.tar.gz" ]    && ok "成员3 data.tar.gz" || bad "成员3 应为 data.tar.gz"
[ "$(cat "$WORK/debian-binary")" = "2.0" ] && ok "debian-binary=2.0" || bad "debian-binary 内容异常"

gzip -t "$WORK/control.tar.gz" 2>/dev/null && ok "control.tar.gz 完整" || bad "control.tar.gz 损坏"
gzip -t "$WORK/data.tar.gz" 2>/dev/null && ok "data.tar.gz 完整" || bad "data.tar.gz 损坏"

echo "--- control ---"
tar tzf "$WORK/control.tar.gz" | sed 's/^/  /'
tar xzf "$WORK/control.tar.gz" -C "$WORK"
grep -q '^Package: ' "$WORK/control" && ok "control 有 Package" || bad "control 缺 Package"
grep -q '^Architecture: ' "$WORK/control" && ok "control 有 Architecture" || bad "control 缺 Architecture"
grep -q '^Version: ' "$WORK/control" && ok "control 有 Version" || bad "control 缺 Version"
pm=$(tar tvzf "$WORK/control.tar.gz" | awk '$NF ~ /postinst$/ {print $1}')
case "$pm" in -rwxr-xr-x*) ok "postinst 可执行 ($pm)";; *) bad "postinst 权限 $pm 应为 755";; esac

echo "--- data 关键项 ---"
list=$(tar tvzf "$WORK/data.tar.gz")
chk() { # $1=路径正则 $2=期望权限前缀 $3=说明
    local line; line=$(echo "$list" | awk -v p="$1" '$NF ~ p {print; exit}')
    [ -n "$line" ] || { bad "$3 缺失"; return; }
    case "$line" in $2*) ok "$3 ($(echo "$line" | awk '{print $1, $2}'))";; *) bad "$3 权限异常: $line";; esac
}
chk '/opt/pitmine3d/PitMine3D.Kylin$'  '-rwxr-xr-x' "主程序 PitMine3D.Kylin"
chk '/opt/pitmine3d/pitmine3d.sh$'     '-rwxr-xr-x' "启动器 pitmine3d.sh"
chk 'libSkiaSharp.so$'                 '-rwxr-xr-x' "SkiaSharp 原生库"
chk 'libicuuc.so'                      '-rwxr-xr-x' "随包 ICU"
chk 'pitmine3d.desktop$'               '-rw-r--r--' "桌面项"
chk 'pitmine3d.svg$'                   '-rw-r--r--' "图标"
owners=$(echo "$list" | awk '{print $2}' | sort -u | tr '\n' ' ')
[ "$owners" = "root/root " ] && ok "属主全为 root/root" || bad "属主异常: $owners"
n=$(echo "$list" | wc -l); echo "  data 条目数: $n"
dotnet_dll=$(echo "$list" | grep -c 'System.Private.CoreLib.dll' || true)
[ "$dotnet_dll" -ge 1 ] && ok "自包含 .NET 运行时在包内" || bad "包内缺 .NET 运行时"

echo
[ $fail -eq 0 ] && echo "== 校验通过: $DEB" || { echo "== 校验失败"; exit 1; }
