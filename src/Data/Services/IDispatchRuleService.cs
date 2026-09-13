// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IDispatchRuleService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IDispatchRuleService
{
    DispatchRule? GetById(long id);
    IReadOnlyList<DispatchRule> All(bool activeOnly = true);
    IReadOnlyList<DispatchRule> ByShovelModel(string shovelModel);
    IReadOnlyList<DispatchRule> ByTruckModel(string truckModel);
    void Insert(DispatchRule entity);
    void Update(DispatchRule entity);
    void Delete(long id);
}
