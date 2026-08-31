using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks;

/// <summary>工序类型（忠实原 TaskLib.Domain.ProcessType）。</summary>
public enum ProcessType { Drill, Blast, Load, Haul, Dump, Idle }

/// <summary>穿孔计划信息（最小：单据侧量呈现用到的字段）。</summary>
public sealed class DrillInfo
{
    public int PlanHoles { get; init; }
    public double PlanMeters { get; init; }
    /// <summary>孔数/延米文案，如 "12 孔 / 340 m"。</summary>
    public string PlanCaption =>
        (PlanHoles > 0 ? PlanHoles + " 孔" : "") +
        (PlanHoles > 0 && PlanMeters > 1e-6 ? " / " : "") +
        (PlanMeters > 1e-6 ? $"{PlanMeters:N0} m" : "");
}

/// <summary>
/// 一条生产任务（最小域——照 §七 最小 plan 法, 仅含 TaskQuantity 量核算实际读到的字段）。
/// 全域 ProductionTask(474 行)含调度/状态/时序等; 此为 TaskLib 量核算切片的自足最小集。
/// </summary>
public sealed class ProductionTask
{
    public ProcessType Process { get; init; }
    public double ControlVolumeM3 { get; init; }   // 穿孔控制方量
    public double TargetVolumeM3 { get; init; }     // 采装原位实方
    public double TargetTonnageT { get; init; }
    public double HaulTonnageT { get; init; }       // 运输承运吨
    public int TripCount { get; init; }
    public double DumpVolumeM3 { get; init; }        // 排土排弃占容
    public double OreVolumeM3 { get; init; }
    public double WasteVolumeM3 { get; init; }
    public double EffectiveHaulKm { get; init; }
    public DrillInfo? Drill { get; init; }
}

/// <summary>
/// 一条任务的「计划量」在单据上的呈现（按工序取账）——**忠实逐字移植** TaskLib.Domain.TaskQuantity。
/// OD1 按工序取自己那本账(穿孔控制方量/采装原位实方/运输承运吨/排土占容); OD2 无出处写「—」不写 0;
/// OD3 合计分账列示不给总数。纯计算, 永不抛。
/// </summary>
public static class TaskQuantity
{
    /// <summary>这道工序的量口径名。</summary>
    public static string BasisOf(ProcessType p) => p switch
    {
        ProcessType.Drill => "控制方量",
        ProcessType.Load => "原位实方",
        ProcessType.Haul => "承运量",
        ProcessType.Dump => "排弃占容",
        _ => "",
    };

    public readonly struct Line
    {
        public double Value { get; init; }
        public string Unit { get; init; }
        public string Basis { get; init; }
        public bool HasValue { get; init; }
        public string Second { get; init; }
        public string Why { get; init; }
        public string Caption => HasValue ? $"{Value:N0} {Unit}" : "—";
        public string CaptionWithBasis => HasValue && Basis.Length > 0 ? $"{Value:N0} {Unit}（{Basis}）" : Caption;
        public string Cell => Second.Length > 0 ? Caption + "\n" + Second : Caption;
    }

    /// <summary>按工序取账。t 为 null 时返回"无量"。</summary>
    public static Line Of(ProductionTask? t)
    {
        if (t == null) return new Line { Unit = "", Basis = "", Second = "", Why = "无任务" };
        switch (t.Process)
        {
            case ProcessType.Drill:
            {
                string second = t.Drill?.PlanCaption ?? "";
                bool has = t.ControlVolumeM3 > 1e-6;
                return new Line { Value = t.ControlVolumeM3, Unit = "m³", Basis = "控制方量", HasValue = has, Second = second,
                    Why = has ? "" : (second.Length > 0 ? "" : "未记控制方量与孔数") };
            }
            case ProcessType.Blast:
                return new Line { Unit = "", Basis = "", Second = "", Why = "爆破无方量口径（量在穿孔那一笔）" };
            case ProcessType.Load:
            {
                bool has = t.TargetVolumeM3 > 1e-6;
                return new Line { Value = t.TargetVolumeM3, Unit = "m³", Basis = "原位实方", HasValue = has,
                    Second = has ? $"{t.TargetTonnageT:N0} t" : "", Why = has ? "" : "未记采装量" };
            }
            case ProcessType.Haul:
            {
                bool has = t.HaulTonnageT > 1e-6;
                return new Line { Value = t.HaulTonnageT, Unit = "t", Basis = "承运量", HasValue = has,
                    Second = t.TripCount > 0 ? $"{t.TripCount} 车次" : (has ? "车次：载重未解出" : ""), Why = has ? "" : "未记承运量" };
            }
            case ProcessType.Dump:
            {
                double v = t.DumpVolumeM3 > 1e-6 ? t.DumpVolumeM3 : t.TargetVolumeM3;
                bool has = v > 1e-6;
                return new Line { Value = v, Unit = "m³", Basis = "排弃占容", HasValue = has, Second = "", Why = has ? "" : "未记排弃量" };
            }
            default:
                return new Line { Unit = "", Basis = "", Second = "", Why = "检修/空闲不记量" };
        }
    }

    public static string Cell(ProductionTask? t) => Of(t).Cell;

    /// <summary>一批任务的分账合计。四本账各自一行, 不合成一个总量。</summary>
    public sealed class Totals
    {
        public int TaskCount;
        public int DrillTasks; public double DrillControlM3; public int DrillHoles; public double DrillMeters;
        public int BlastTasks;
        public int LoadTasks; public double LoadInSituM3; public double LoadTonnageT; public double OreM3; public double WasteM3;
        public int HaulTasks; public double HaulTonnageT; public int Trips; public double WorkTKm; public double HauledWithKmT; public int HaulNoKm;
        public double AvgHaulKm => HauledWithKmT > 1e-6 ? WorkTKm / HauledWithKmT : 0;
        public int DumpTasks; public double DumpM3;

        public string Caption
        {
            get
            {
                var parts = new List<string>();
                if (DrillTasks > 0)
                    parts.Add($"穿孔 {DrillTasks} 项"
                        + (DrillControlM3 > 1e-6 ? $" · 控制方量 {DrillControlM3 / 1e4:0.00} 万m³" : " · 控制方量 —")
                        + (DrillHoles > 0 || DrillMeters > 1e-6
                            ? $"（{(DrillHoles > 0 ? DrillHoles + " 孔" : "")}"
                              + (DrillHoles > 0 && DrillMeters > 1e-6 ? " / " : "")
                              + (DrillMeters > 1e-6 ? $"{DrillMeters:N0} m" : "") + "）"
                            : ""));
                if (BlastTasks > 0) parts.Add($"爆破 {BlastTasks} 炮（不派设备 · 无方量口径）");
                if (LoadTasks > 0) parts.Add($"采装 {LoadTasks} 项 · {LoadInSituM3 / 1e4:0.00} 万m³实方 / {LoadTonnageT / 1e4:0.00} 万t");
                if (HaulTasks > 0)
                    parts.Add($"运输 {HaulTasks} 项 · 承运 {HaulTonnageT / 1e4:0.00} 万t"
                        + (Trips > 0 ? $" / {Trips} 车次" : " / 车次未解出")
                        + (WorkTKm > 1e-6 ? $" · 运输功 {WorkTKm / 1e4:0.00} 万t·km · 加权运距 {AvgHaulKm:0.00} km" : " · 运输功 —")
                        + (HaulNoKm > 0 ? $"（{HaulNoKm} 项无运距，运输功偏小）" : ""));
                if (DumpTasks > 0) parts.Add($"排土 {DumpTasks} 项 · 排弃占容 {DumpM3 / 1e4:0.00} 万m³");
                return parts.Count == 0 ? "本班无任务。" : string.Join("　｜　", parts);
            }
        }
    }

    /// <summary>分账汇总。检修/空闲不计。</summary>
    public static Totals Sum(IEnumerable<ProductionTask>? tasks)
    {
        var s = new Totals();
        foreach (var t in tasks ?? Enumerable.Empty<ProductionTask>())
        {
            if (t == null || t.Process == ProcessType.Idle) continue;
            s.TaskCount++;
            switch (t.Process)
            {
                case ProcessType.Drill:
                    s.DrillTasks++; s.DrillControlM3 += t.ControlVolumeM3;
                    s.DrillHoles += t.Drill?.PlanHoles ?? 0; s.DrillMeters += t.Drill?.PlanMeters ?? 0; break;
                case ProcessType.Blast: s.BlastTasks++; break;
                case ProcessType.Load:
                    s.LoadTasks++; s.LoadInSituM3 += t.TargetVolumeM3; s.LoadTonnageT += t.TargetTonnageT;
                    s.OreM3 += t.OreVolumeM3; s.WasteM3 += t.WasteVolumeM3; break;
                case ProcessType.Haul:
                    s.HaulTasks++; s.HaulTonnageT += t.HaulTonnageT; s.Trips += t.TripCount;
                    if (t.EffectiveHaulKm > 1e-6) { s.WorkTKm += t.HaulTonnageT * t.EffectiveHaulKm; s.HauledWithKmT += t.HaulTonnageT; }
                    else if (t.HaulTonnageT > 1e-6) s.HaulNoKm++;
                    break;
                case ProcessType.Dump: s.DumpTasks++; s.DumpM3 += Of(t).Value; break;
            }
        }
        return s;
    }
}
