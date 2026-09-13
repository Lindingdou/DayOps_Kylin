// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IKpiService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IKpiService
{
    EquipmentKpiMonthly? Get(string equipmentId, int year, int month);

    /// <summary>某设备的全部月度 KPI,按年月升序。</summary>
    IReadOnlyList<EquipmentKpiMonthly> ByEquipment(string equipmentId);

    /// <summary>某设备某年所有月份。</summary>
    IReadOnlyList<EquipmentKpiMonthly> ByEquipmentYear(string equipmentId, int year);

    /// <summary>按型号聚合的月度均值(走 SQL VIEW)。</summary>
    IReadOnlyList<EquipmentKpiMonthly> ByModelMonthly(string model);

    /// <summary>按型号-年聚合(走 SQL VIEW)。</summary>
    IReadOnlyList<EquipmentKpiMonthly> ByModelYearly(string model);

    void Upsert(EquipmentKpiMonthly entity);
    void Delete(string equipmentId, int year, int month);
}
