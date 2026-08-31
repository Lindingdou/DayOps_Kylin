using System;

namespace PitMine3D.Kylin.Cad.Tasks;

/// <summary>
/// 穿孔任务的作业量（孔数/延米）——忠实移植 TaskLib.Domain.DrillQuantity。
/// 穿孔无 m³ 口径, 量是孔数与延米; 达成率优先按延米(钻机真工作量)。未录一律 null, 不拿 0 冒充。
/// </summary>
public sealed class DrillQuantity
{
    public int? PlanHoles { get; set; }
    public double? PlanMeters { get; set; }
    public int? ActualHoles { get; set; }
    public double? ActualMeters { get; set; }

    public bool HasPlan => PlanMeters is > 0 || PlanHoles is > 0;

    /// <summary>达成率 %：优先按延米, 无延米按孔数。无计划量返回 null。</summary>
    public double? AttainmentPct
    {
        get
        {
            if (PlanMeters is > 0 && ActualMeters.HasValue) return Math.Round(ActualMeters.Value / PlanMeters.Value * 100, 0);
            if (PlanHoles is > 0 && ActualHoles.HasValue) return Math.Round(ActualHoles.Value * 100.0 / PlanHoles.Value, 0);
            return null;
        }
    }

    public string PlanCaption =>
        (PlanHoles is > 0 ? $"{PlanHoles} 孔" : "")
        + (PlanHoles is > 0 && PlanMeters is > 0 ? " / " : "")
        + (PlanMeters is > 0 ? $"{PlanMeters:0.#} m" : "");
}
