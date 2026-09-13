// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/KpiService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class KpiService : IKpiService
{
    private readonly ISqlService _sql;
    private readonly IRepository<EquipmentKpiMonthly> _repo;

    public KpiService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<EquipmentKpiMonthly>();
    }

    public EquipmentKpiMonthly? Get(string equipmentId, int year, int month)
        => _repo.GetByKey(new { EquipmentId = equipmentId, Year = year, Month = month });

    public IReadOnlyList<EquipmentKpiMonthly> ByEquipment(string equipmentId)
        => _repo.Where("equipment_id = @id ORDER BY year, month", new { id = equipmentId });

    public IReadOnlyList<EquipmentKpiMonthly> ByEquipmentYear(string equipmentId, int year)
        => _repo.Where("equipment_id = @id AND year = @y ORDER BY month",
            new { id = equipmentId, y = year });

    public IReadOnlyList<EquipmentKpiMonthly> ByModelMonthly(string model)
    {
        // 通过 JOIN equipment 表按型号聚合
        var sql = @"SELECT '' AS equipment_id, k.year, k.month,
                       AVG(k.plan_hours) AS plan_hours,
                       AVG(k.work_hours) AS work_hours,
                       AVG(k.fault_hours) AS fault_hours,
                       AVG(k.idle_hours) AS idle_hours,
                       AVG(k.delay_hours) AS delay_hours,
                       AVG(k.availability) AS availability,
                       AVG(k.actual_run_rate) AS actual_run_rate,
                       AVG(k.utilization_rate) AS utilization_rate,
                       AVG(k.internal_fault_rate_pct) AS internal_fault_rate_pct,
                       AVG(k.external_fault_rate_pct) AS external_fault_rate_pct
                    FROM equipment_kpi_monthly k
                    JOIN equipment e ON e.equipment_id = k.equipment_id
                    WHERE e.model = @m
                    GROUP BY k.year, k.month
                    ORDER BY k.year, k.month";
        return new List<EquipmentKpiMonthly>(_sql.Query<EquipmentKpiMonthly>(sql, new { m = model }));
    }

    public IReadOnlyList<EquipmentKpiMonthly> ByModelYearly(string model)
    {
        var sql = @"SELECT '' AS equipment_id, k.year, 0 AS month,
                       SUM(k.plan_hours) AS plan_hours,
                       SUM(k.work_hours) AS work_hours,
                       SUM(k.fault_hours) AS fault_hours,
                       SUM(k.idle_hours) AS idle_hours,
                       SUM(k.delay_hours) AS delay_hours,
                       AVG(k.availability) AS availability,
                       AVG(k.actual_run_rate) AS actual_run_rate,
                       AVG(k.utilization_rate) AS utilization_rate,
                       AVG(k.internal_fault_rate_pct) AS internal_fault_rate_pct,
                       AVG(k.external_fault_rate_pct) AS external_fault_rate_pct
                    FROM equipment_kpi_monthly k
                    JOIN equipment e ON e.equipment_id = k.equipment_id
                    WHERE e.model = @m
                    GROUP BY k.year
                    ORDER BY k.year";
        return new List<EquipmentKpiMonthly>(_sql.Query<EquipmentKpiMonthly>(sql, new { m = model }));
    }

    public void Upsert(EquipmentKpiMonthly entity) => _repo.Upsert(entity);
    public void Delete(string equipmentId, int year, int month)
        => _sql.Execute("DELETE FROM equipment_kpi_monthly WHERE equipment_id = @id AND year = @y AND month = @m",
            new { id = equipmentId, y = year, m = month });
}
