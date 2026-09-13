// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IDailyMineService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IDailyMineService
{
    DailyMineSummary? Get(DateTime date);
    IReadOnlyList<DailyMineSummary> InRange(DateTime start, DateTime end);
    IReadOnlyList<DailyMineSummary> All();
    void Upsert(DailyMineSummary entity);
}
