#!/bin/bash
# 原 PitMine3D Modules/TaskLib → Kylin src/TaskLib 逐文件移植（命名空间/依赖适配 + 补隐式 using）
# 用法: port_tasklib.sh <原相对路径 如 Domain/Dispatch.cs> [目标子目录 默认同名]
SRC=/c/Users/cFore/coder/PitMine3D/Modules/TaskLib
DST=/c/Users/cFore/coder/DayOps_Kylin/src/TaskLib
rel="$1"; sub="${2:-$(dirname "$rel")}"
mkdir -p "$DST/$sub"
out="$DST/$sub/$(basename "$rel")"
{
  echo "// 忠实移植自原 PitMine3D Modules/TaskLib/$rel（逐行对应；仅命名空间/依赖适配）"
  # 补隐式 using（原工程 ImplicitUsings=enable，Kylin 关闭）
  for u in System System.Collections.Generic System.IO System.Linq System.Threading.Tasks; do
    grep -qE "^using $u;" "$SRC/$rel" || echo "using $u;"
  done
  sed -e 's/^\xEF\xBB\xBF//' \
    -e 's/^namespace TaskLib\.\([A-Za-z]*\);/namespace PitMine3D.Kylin.TaskLib.\1;/' \
    -e 's/^namespace TaskLib;/namespace PitMine3D.Kylin.TaskLib;/' \
    -e 's/^using TaskLib\.\([A-Za-z]*\);/using PitMine3D.Kylin.TaskLib.\1;/' \
    -e 's/^using TaskStatus = TaskLib\.Domain\.TaskStatus;/using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;/' \
    -e 's/^using QualityDraft = TaskLib\.Engine\.FaceQualityDraft;/using QualityDraft = PitMine3D.Kylin.TaskLib.Engine.FaceQualityDraft;/' \
    -e 's/^using GeoDataBase\.Domain;/using PitMine3D.Kylin.Data;/' \
    -e 's/^using GeoDataBase\.Public\.Entities;/using PitMine3D.Kylin.Data.Entities;/' \
    -e 's/^using GeoDataBase\.Public\.Services;/using PitMine3D.Kylin.Data.Services;/' \
    -e 's/^using GeoDataBase\.Public;/using PitMine3D.Kylin.Data;/' \
    -e 's/^using RoadLib\.Network;/using PitMine3D.Kylin.Cad.Road;/' \
    -e 's/^using RoadLib\.Routing;/using PitMine3D.Kylin.Cad.Road;/' \
    -e 's/^using \([A-Za-z]*\) = TaskLib\./using \1 = PitMine3D.Kylin.TaskLib./' \
    -e 's/^using BlockModelLib\.Domain;/using PitMine3D.Kylin.UnitLedger;/' \
    -e 's/^using PitMine\.Platform;/using PitMine3D.Kylin.Platform;/' \
    -e 's/^using PitMine\.Platform\.Capabilities;/using PitMine3D.Kylin.Platform.Capabilities;/' \
    -e 's/^using PointCloudLib\.Shading;/using PitMine3D.Kylin.Shading;/' \
    -e 's/GeoDataBase\.Domain\.EquipmentDataContext/PitMine3D.Kylin.Data.EquipmentDataContext/g' \
    -e 's/GeoDataBase\.Public\.Entities\./PitMine3D.Kylin.Data.Entities./g' \
    -e 's/GeoDataBase\.Public\.Services\./PitMine3D.Kylin.Data.Services./g' \
    "$SRC/$rel"
} > "$out"
echo "$out"
# 后处理：正文里的全限定引用
sed -i 's/BlockModelLib\.Domain\./PitMine3D.Kylin.UnitLedger./g; s/PitMine\.Platform\./PitMine3D.Kylin.Platform./g; s/GeoDataBase\.Equipment\.CsvDataStore/PitMine3D.Kylin.Data.Legacy.CsvDataStore/g; s/GeoDataBase\.Equipment\.DispatchRule/PitMine3D.Kylin.Data.Legacy.DispatchRule/g' "$out"
