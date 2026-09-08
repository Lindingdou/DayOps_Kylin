#!/bin/sh
# 批量把一个目录里的 CSV 一次性导入中心库。
#
#   ./import-all.sh ./数据目录
#   ./import-all.sh ./数据目录 --db "Host=10.0.0.9;Port=5432;Database=pitmine;Username=pitmine;Password=***"
#   ./import-all.sh --templates ./模板目录      # 先出一套空模板给业务填
#   ./import-all.sh --spec                      # 打印表格规范
#
# 为什么是薄壳而不是自己写 SQL：导入要解外键(equipment_id → 实体)、查码表、按去重键更新，
# 用 SQL 重写必然与界面里的逻辑走样。这里直接调主程序的导入命令行，界面与脚本结果一致。
#
# 文件名要含类型名（如 煤质化验_2025.csv、设备台账.csv），程序据此认类型；
# 导入顺序由程序按外键依赖排（设备台账先于按设备统计的那几种），与文件名顺序无关。
set -eu

RED=''; GRN=''; RST=''
if [ -t 1 ]; then RED=$(printf '\033[31m'); GRN=$(printf '\033[32m'); RST=$(printf '\033[0m'); fi
die() { echo "${RED}[失败]${RST} $*" >&2; exit 1; }

# 找主程序：装了 deb 用 /opt 下的，否则用同仓库的构建产物
find_exe() {
    for c in /opt/pitmine3d/PitMine3D.Kylin \
             "$(dirname "$0")/../../src/bin/Release/net8.0/PitMine3D.Kylin" \
             "$(dirname "$0")/../../src/bin/Debug/net8.0/PitMine3D.Kylin"; do
        [ -x "$c" ] && { echo "$c"; return 0; }
    done
    command -v pitmine3d >/dev/null 2>&1 && { echo "pitmine3d"; return 0; }
    return 1
}

EXE=$(find_exe) || die "找不到主程序。装了 deb 应在 /opt/pitmine3d/PitMine3D.Kylin，或先构建本仓库。"

case "${1:-}" in
    ""|-h|--help)
        cat <<'USAGE'
用法:
  ./import-all.sh <数据目录|CSV...> [--db <连接串>] [--no-overwrite]
  ./import-all.sh --templates <输出目录>    导出全部空模板(表头+示例行)
  ./import-all.sh --spec                    打印表格规范(每种导入的列/必填/单位/去重键)

说明:
  · 文件名要含类型名，如 煤质化验_2025.csv、设备台账.csv
  · 导入顺序由程序按外键依赖自动排，不用管文件名顺序
  · 默认同键行更新；加 --no-overwrite 则跳过已存在的行
  · 连接串优先级: --db > 环境变量 > /etc/pitmine3d/db.conf > 用户配置
USAGE
        exit 0 ;;
    --spec)
        exec "$EXE" --import-list ;;
    --templates)
        [ $# -ge 2 ] || die "--templates 需要输出目录"
        exec "$EXE" --import-template all "$2" ;;
esac

TARGET="$1"; shift
[ -e "$TARGET" ] || die "找不到: $TARGET"

if [ -d "$TARGET" ]; then
    N=$(find "$TARGET" -maxdepth 1 \( -name '*.csv' -o -name '*.txt' \) | wc -l)
    [ "$N" -gt 0 ] || die "$TARGET 里没有 .csv/.txt 文件"
    echo "${GRN}==>${RST} 从 $TARGET 读入 $N 个文件"
fi

# 主程序会打印逐个文件的 新增/更新/跳过/错误 与合计, 并以退出码表示成败
"$EXE" --import "$TARGET" "$@"
RC=$?
if [ "$RC" = "0" ]; then
    echo "${GRN}==>${RST} 全部导入完成"
else
    echo "${RED}==>${RST} 导入未全部成功(退出码 $RC), 见上面的 [失败]/[跳过] 行" >&2
fi
exit "$RC"
