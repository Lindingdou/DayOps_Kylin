// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IWorkforceService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IWorkforceService
{
    WorkforceMonthly? Get(int year, int month);
    IReadOnlyList<WorkforceMonthly> ByYear(int year);
    IReadOnlyList<WorkforceMonthly> All();
    void Upsert(WorkforceMonthly entity);
}
