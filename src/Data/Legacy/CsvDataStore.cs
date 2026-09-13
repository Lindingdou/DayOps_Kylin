// 忠实移植自原 PitMine3D Modules/GeoDataBase/Equipment/CsvDataStore.cs 中 TaskLib（FleetMatcher）消费的那一截：
// GetDispatchRules / GetInventoryByModel / GetAverageEfficiency / GetKpiRecords + 富 POCO DispatchRule / KpiRecord
//（逐行对应；仅命名空间/依赖适配）。名字沿用原版"CsvDataStore"——它早已不读 CSV，走的是 EquipmentDataContext 仓储。
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using DbKpi = PitMine3D.Kylin.Data.Entities.EquipmentKpiMonthly;

namespace PitMine3D.Kylin.Data.Legacy;

public sealed class CsvDataStore
{
    // ─── KPI ───
    public List<KpiRecord> GetKpiRecords()
    {
        var ctx = EquipmentDataContext.Current;
        var allKpi = new List<DbKpi>();
        foreach (var e in ctx.Equipment.All())
            allKpi.AddRange(ctx.Kpi.ByEquipment(e.EquipmentId));
        var modelMap = ctx.Equipment.All().ToDictionary(
            e => e.EquipmentId, e => e.Model ?? "", StringComparer.OrdinalIgnoreCase);
        return allKpi.Select(k => new KpiRecord
        {
            EquipmentId = k.EquipmentId,
            Model = modelMap.GetValueOrDefault(k.EquipmentId, ""),
            Year = k.Year,
            Month = k.Month,
            PlanHours = k.PlanHours,
            WorkHours = k.WorkHours,
            FaultHours = k.FaultHours,
            IdleHours = k.IdleHours,
            Availability = k.Availability,
            ActualRunRate = k.ActualRunRate,
            UtilizationRate = k.UtilizationRate
        }).ToList();
    }

    // ─── 在籍台数（按型号） ───
    public Dictionary<string, int> GetInventoryByModel()
    {
        var ctx = EquipmentDataContext.Current;
        return ctx.Equipment.All()
            .Where(e => !string.IsNullOrWhiteSpace(e.Model))
            .GroupBy(e => e.Model!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    // ─── 平均作业效率 η（KPI 可用率×利用率均值，喂编组优化；无数据回落 0.8） ───
    public double GetAverageEfficiency()
    {
        var oee = GetKpiRecords()
            .Where(k => k.Availability > 0 && k.UtilizationRate > 0)
            .Select(k => k.Availability * k.UtilizationRate)
            .ToList();
        return oee.Count > 0 ? Math.Clamp(oee.Average(), 0.4, 0.95) : 0.8;
    }

    // ─── 编组规则 ───
    public List<DispatchRule> GetDispatchRules()
    {
        var rows = EquipmentDataContext.Dispatch.All();
        var modelDict = EquipmentDataContext.Models.All()
            .ToDictionary(m => m.Model, StringComparer.OrdinalIgnoreCase);

        return rows.Select(r =>
        {
            modelDict.TryGetValue(r.ShovelModel, out var sm);
            modelDict.TryGetValue(r.TruckModel, out var tm);
            return new DispatchRule
            {
                ShovelModel = r.ShovelModel,
                ShovelBucketM3 = sm?.BucketM3 ?? 0,
                ShovelPayloadT = sm?.LoadT ?? 0,
                TruckModel = r.TruckModel,
                TruckPayloadT = tm?.LoadT ?? 0,
                BucketLoadsPerTruck = r.BucketLoadsPerTruck,
                RecommendedTruckCount = r.RecommendedTruckCount,
                CycleTimeMin = r.CycleTimeMin,
                EfficiencyScore = r.EfficiencyScore
            };
        }).ToList();
    }
}

public sealed class KpiRecord
{
    public string EquipmentId { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }
    public double PlanHours { get; set; }
    public double WorkHours { get; set; }
    public double FaultHours { get; set; }
    public double IdleHours { get; set; }
    public double Availability { get; set; }
    public double ActualRunRate { get; set; }
    public double UtilizationRate { get; set; }
    public double Oee => Availability * ActualRunRate * UtilizationRate;
    public string YearMonth => $"{Year}-{Month:D2}";
}

/// <summary>已 join equipment_model 的富编组规则（裸的 Entities.DispatchRule 没有斗容/载重）。</summary>
public sealed class DispatchRule
{
    public string ShovelModel { get; set; } = "";
    public double ShovelBucketM3 { get; set; }
    public double ShovelPayloadT { get; set; }
    public string TruckModel { get; set; } = "";
    public double TruckPayloadT { get; set; }
    public double BucketLoadsPerTruck { get; set; }
    public int RecommendedTruckCount { get; set; }
    public double CycleTimeMin { get; set; }
    public int EfficiencyScore { get; set; }
}
