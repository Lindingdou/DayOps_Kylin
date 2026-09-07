#!/usr/bin/env bash
# 把一个已备好的 deb 目录树(含 DEBIAN/control 等)打成 .deb。
# 有 dpkg-deb 就用它；没有(Windows/Git Bash)就 tar + 手工 ar 组装。
#   ./deb-pack.sh <stage-dir> <out.deb>
# 约定: stage 下 opt/ 内全部 755(含可执行), usr/ 内 644; DEBIAN/ 里 control 644、脚本 755。
set -euo pipefail

STAGE="${1:?用法: deb-pack.sh <stage-dir> <out.deb>}"
OUT="${2:?用法: deb-pack.sh <stage-dir> <out.deb>}"
[ -f "$STAGE/DEBIAN/control" ] || { echo "缺 $STAGE/DEBIAN/control"; exit 1; }
mkdir -p "$(dirname "$OUT")"
rm -f "$OUT"

if command -v dpkg-deb >/dev/null 2>&1; then
    dpkg-deb --build --root-owner-group "$STAGE" "$OUT"
    exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

first=1
add_tree() {   # $1=子树(opt/usr) $2=权限
    [ -d "$STAGE/$1" ] || return 0
    if [ $first -eq 1 ]; then
        ( cd "$STAGE" && tar --format=gnu --owner=root:0 --group=root:0 --mode="$2" -cf "$WORK/data.tar" "./$1" )
        first=0
    else
        ( cd "$STAGE" && tar --format=gnu --owner=root:0 --group=root:0 --mode="$2" -rf "$WORK/data.tar" "./$1" )
    fi
}
add_tree opt 755
add_tree usr 644
gzip -9n "$WORK/data.tar"

( cd "$STAGE/DEBIAN" && tar --format=gnu --owner=root:0 --group=root:0 --mode=644 -cf "$WORK/control.tar" ./control )
for s in postinst prerm postrm preinst; do
    [ -f "$STAGE/DEBIAN/$s" ] || continue
    ( cd "$STAGE/DEBIAN" && tar --format=gnu --owner=root:0 --group=root:0 --mode=755 -rf "$WORK/control.tar" "./$s" )
done
gzip -9n "$WORK/control.tar"
printf '2.0\n' > "$WORK/debian-binary"

# ar 归档：成员顺序固定 debian-binary → control.tar.gz → data.tar.gz；头 60 字节定长字段
ar_member() {   # $1=归档 $2=成员名 $3=源文件
    local out="$1" name="$2" src="$3" size
    size=$(wc -c < "$src")
    printf '%-16s%-12u%-6u%-6u%-8s%-10u\140\n' "$name" "$(date +%s)" 0 0 "100644" "$size" >> "$out"
    cat "$src" >> "$out"
    [ $((size % 2)) -eq 1 ] && printf '\n' >> "$out"
    return 0
}
printf '!<arch>\n' > "$OUT"
ar_member "$OUT" "debian-binary"  "$WORK/debian-binary"
ar_member "$OUT" "control.tar.gz" "$WORK/control.tar.gz"
ar_member "$OUT" "data.tar.gz"    "$WORK/data.tar.gz"
