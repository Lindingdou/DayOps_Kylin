#!/bin/bash
# 原 PitMine3D Tests/Tests.TaskLib/*.cs → Kylin tests/PitMine3D.Kylin.Tests/TaskLib/（命名空间/依赖适配 + 补隐式 using）
SRC=/c/Users/cFore/coder/PitMine3D/Tests/Tests.TaskLib
DST=/c/Users/cFore/coder/DayOps_Kylin/tests/PitMine3D.Kylin.Tests/TaskLib
mkdir -p "$DST"
f="$1"; out="$DST/$f"
{
  echo "// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/$f（逐行对应；仅命名空间/依赖适配）"
  for u in System System.Collections.Generic System.IO System.Linq System.Threading.Tasks; do
    grep -qE "^using $u;" "$SRC/$f" || echo "using $u;"
  done
  sed -e 's/^\xEF\xBB\xBF//' \
    -e 's/^namespace Tests\.TaskLib;/namespace PitMine3D.Kylin.Tests.TaskLibTests;/' \
    -e 's/^namespace Tests\.TaskLib$/namespace PitMine3D.Kylin.Tests.TaskLibTests/' \
    -e 's/\([^.A-Za-z]\)TaskLib\.\(Domain\|Engine\|Simulation\|Zoning\|Adjust\|ShiftOps\|Gantt\|Scheduling\|Reporting\|Features\|Order\)/\1PitMine3D.Kylin.TaskLib.\2/g' \
    -e 's/^using TaskStatus = TaskLib\./using TaskStatus = PitMine3D.Kylin.TaskLib./' \
    -e 's/^using \([A-Za-z]*\) = TaskLib\./using \1 = PitMine3D.Kylin.TaskLib./' \
    -e 's/^using GeoDataBase\.Domain;/using PitMine3D.Kylin.Data;/' \
    -e 's/^using GeoDataBase\.Public\.Entities;/using PitMine3D.Kylin.Data.Entities;/' \
    -e 's/^using GeoDataBase\.Public\.Services;/using PitMine3D.Kylin.Data.Services;/' \
    -e 's/^using GeoDataBase\.Public;/using PitMine3D.Kylin.Data;/' \
    -e 's/^using RoadLib\.Network;/using PitMine3D.Kylin.Cad.Road;/' \
    -e 's/^using RoadLib\.Routing;/using PitMine3D.Kylin.Cad.Road;/' \
    -e 's/^using BlockModelLib\.Domain;/using PitMine3D.Kylin.UnitLedger;/' \
    -e 's/^using PitMine\.Platform;/using PitMine3D.Kylin.Platform;/' \
    -e 's/^using PitMine\.Platform\.Capabilities;/using PitMine3D.Kylin.Platform.Capabilities;/' \
    -e 's/^using PointCloudLib\.Shading;/using PitMine3D.Kylin.Shading;/' \
    -e 's/^using SqlLib\.Public;/using PitMine3D.Kylin.Data.Sql;/' \
    "$SRC/$f"
} > "$out"
sed -i 's/BlockModelLib\.Domain\./PitMine3D.Kylin.UnitLedger./g; s/PitMine\.Platform\./PitMine3D.Kylin.Platform./g; s/GeoDataBase\.Domain\.EquipmentDataContext/PitMine3D.Kylin.Data.EquipmentDataContext/g; s/GeoDataBase\.Public\.Entities\./PitMine3D.Kylin.Data.Entities./g' "$out"
echo "$out"
