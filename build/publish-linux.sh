#!/usr/bin/env bash
# 交叉发布麒麟(Linux)自包含包。可在 Windows/Linux 上跑（dotnet 交叉发布）。
#   ./publish-linux.sh linux-x64        # 兆芯 / 海光
#   ./publish-linux.sh linux-arm64      # 飞腾 / 鲲鹏
#   ./publish-linux.sh linux-loongarch64  # 龙芯（需社区/openEuler 的 .NET SDK，官方不带）
set -euo pipefail

RID="${1:-linux-x64}"
CFG="${2:-Release}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../dist/$RID"

echo ">> 发布 $RID ($CFG) → $OUT"
dotnet publish "$HERE/../src/PitMine3D.Kylin.csproj" \
    -c "$CFG" -r "$RID" --self-contained true \
    -p:PublishSingleFile=false \
    -o "$OUT"

chmod +x "$OUT/PitMine3D.Kylin" 2>/dev/null || true
echo ">> 完成。麒麟机上直接运行: $OUT/PitMine3D.Kylin"
echo "   （自包含，目标机无需安装 .NET；x64/arm64 官方支持，loongarch64 需社区运行时）"
