// 忠实移植自原 PitMine3D Modules/TaskLib/Domain/TaskQuantity.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Domain;

// ─────────────────────────────────────────────────────────────────────────────
//  单据上的「计划量」—— 按工序取自己那本账（OD 组，2026-08-20）
//
//  ══ 它补的是哪一段 ══
//  任务编制结果现在是**五道工序**（穿孔→爆破→采装→运输→排土），而
//  生产任务书 / 任务下达 / 派车单 三个窗都是在只有采装的年代写的，
//  量列一律取 `TargetVolumeM3`。于是：
//    · 穿孔笔 —— 分解器算出了**控制方量**，映射到任务时整段丢弃 ⇒ 单据上是「—」；
//    · 运输笔 —— 量在 `HaulTonnageT`（承运吨）⇒ 单据上是「—」；
//    · 排土笔 —— 分解器路的量在 `DumpVolumeM3`（排弃占容）⇒ 单据上是「—」，
//                而**装箱路**把占容方写进了 `TargetVolumeM3`（历史口径），
//                任务书拿它再乘一次 Kr（`TargetDumpM3`）⇒ 同一个数被放大一遍；
//    · 爆破笔 —— 按已定口径本就没有方量（它爆的就是穿孔那笔控制方量）。
//  四道工序里三道在正式单据上写着「—」，第四道写的是放大过的数。
//
//  ══ 三条口径 ══
//   OD1 **按工序取自己那本账**：
//        穿孔 = 控制方量 (m³) ＋ 孔数/延米　　爆破 = 无方量口径
//        采装 = 原位实方 (m³) ＋ 吨　　　　　 运输 = 承运量 (t) ＋ 车次
//        排土 = 排弃占容 (m³)
//        **四本账绝不许并成一列求和** —— 求出来的数不对应任何真实量。
//   OD2 **没有出处的量位写「—」，不写 0**。0 说的是"量到了、是零"，
//        「—」说的是"这个数没有出处"。爆破行、载重解不出来的车次都归后者。
//   OD3 **合计分账列示**：抬头指标条给四行，不给一个总数。
//
//  ⚠ 本类是这四本账在**单据侧的唯一定义处**。三个窗都读它，
//    别在窗口里再写一份 `t.TargetVolumeM3 > 0 ? ... : "—"`：
//    写第二份的那天起，任务书与任务下达就会在同一条任务上给出两个数。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条任务的「计划量」在单据上的呈现（按工序取账）。纯计算，永不抛。</summary>
public static class TaskQuantity
{
    /// <summary>这道工序的量口径名（"控制方量"/"原位实方"/"承运量"/"排弃占容"；爆破为空串）。</summary>
    public static string BasisOf(ProcessType p) => p switch
    {
        ProcessType.Drill => "控制方量",
        ProcessType.Load => "原位实方",
        ProcessType.Haul => "承运量",
        ProcessType.Dump => "排弃占容",
        _ => "",                       // 爆破：没有自己的方量口径；检修/空闲：不是量活
    };

    /// <summary>一条任务的量的一行呈现。</summary>
    public readonly struct Line
    {
        /// <summary>主量（按工序取账）。<see cref="HasValue"/> 为假时无意义。</summary>
        public double Value { get; init; }
        /// <summary>主量单位（"m³" / "t"）。</summary>
        public string Unit { get; init; }
        /// <summary>量口径名（见 <see cref="BasisOf"/>）。</summary>
        public string Basis { get; init; }
        /// <summary>有没有主量。<b>假 = 这个数没有出处</b>，一律写「—」，不写 0。</summary>
        public bool HasValue { get; init; }

        /// <summary>次量文案（采装的吨、运输的车次、穿孔的孔数/延米）；没有则空串。</summary>
        public string Second { get; init; }

        /// <summary>为什么没有主量（"爆破无方量口径" / "未记量"）；有主量时空串。</summary>
        public string Why { get; init; }

        /// <summary>主量文案："12,345 m³实方" / "8,900 t" / "—"。</summary>
        public string Caption => HasValue ? $"{Value:N0} {Unit}" : "—";

        /// <summary>主量 + 口径名："12,345 m³（原位实方）"。表头写不下口径时用它。</summary>
        public string CaptionWithBasis => HasValue && Basis.Length > 0
            ? $"{Value:N0} {Unit}（{Basis}）" : Caption;

        /// <summary>两行合成（主量换行次量）；单据表格的那一格。</summary>
        public string Cell => Second.Length > 0 ? Caption + "\n" + Second : Caption;
    }

    /// <summary>按工序取账。<paramref name="t"/> 为 null 时返回"无量"。</summary>
    public static Line Of(ProductionTask? t)
    {
        if (t == null) return new Line { Unit = "", Basis = "", Second = "", Why = "无任务" };

        switch (t.Process)
        {
            // ── 穿孔：控制方量 ＋ 孔数/延米 ──
            //  两个都可能缺：控制方量来自逐班分解器（`ShiftTaskRow.VolumeM3`），
            //  孔数/延米来自 `drill_plan` 台账。缺谁写谁的「—」，不互相顶替。
            case ProcessType.Drill:
            {
                string second = t.Drill?.PlanCaption ?? "";
                bool has = t.ControlVolumeM3 > 1e-6;
                return new Line
                {
                    Value = t.ControlVolumeM3, Unit = "m³", Basis = "控制方量", HasValue = has,
                    Second = second,
                    Why = has ? "" : (second.Length > 0 ? "" : "未记控制方量与孔数"),
                };
            }

            // ── 爆破：按已定口径**没有自己的方量**（它爆的就是穿孔那笔控制方量）──
            //  这里返回"无量"是**口径**，不是缺数据。写 0 会让人去找那 0 是怎么算出来的。
            case ProcessType.Blast:
                return new Line { Unit = "", Basis = "", Second = "", Why = "爆破无方量口径（量在穿孔那一笔）" };

            // ── 采装：原位实方 ＋ 吨 ──
            case ProcessType.Load:
            {
                bool has = t.TargetVolumeM3 > 1e-6;
                return new Line
                {
                    Value = t.TargetVolumeM3, Unit = "m³", Basis = "原位实方", HasValue = has,
                    Second = has ? $"{t.TargetTonnageT:N0} t" : "",
                    Why = has ? "" : "未记采装量",
                };
            }

            // ── 运输：承运量（吨）＋ 车次 ──
            //  ★ 吨是三个体积口径之间**唯一守恒**的量，所以运输按吨记。
            //    车次为 0 = 单车载重没解出来（**不猜一个 100t**），此时不写"0 车次"。
            case ProcessType.Haul:
            {
                bool has = t.HaulTonnageT > 1e-6;
                return new Line
                {
                    Value = t.HaulTonnageT, Unit = "t", Basis = "承运量", HasValue = has,
                    Second = t.TripCount > 0 ? $"{t.TripCount} 车次" : (has ? "车次：载重未解出" : ""),
                    Why = has ? "" : "未记承运量",
                };
            }

            // ── 排土：排弃占容方 ──
            //  ⚠ 历史上两条产出路径把它放在**两个字段**里：逐班分解器派生的排土笔走 `DumpVolumeM3`，
            //    而标量装箱那条路（`TaskExploder.ExplodeFace`）把占容方直接写进了 `TargetVolumeM3`。
            //    **2026-08-22 已在源头修掉**：装箱路现在也写 `DumpVolumeM3`（判据 PL1 钉着）。
            //    下面的回退分支**保留**，但它现在只服务一种情况：**从盘上读回来的老快照**
            //    （`TaskPersistence` 存的是修之前那一版的任务）。去掉它，那些老盘子的排土行会全变「—」。
            //    **仍然不许再乘一次 Kr**（`TargetDumpM3` 干的就是这件事）：两个字段里的数都已经是占容方。
            case ProcessType.Dump:
            {
                double v = t.DumpVolumeM3 > 1e-6 ? t.DumpVolumeM3 : t.TargetVolumeM3;
                bool has = v > 1e-6;
                return new Line
                {
                    Value = v, Unit = "m³", Basis = "排弃占容", HasValue = has,
                    Second = "",
                    Why = has ? "" : "未记排弃量",
                };
            }

            default:
                return new Line { Unit = "", Basis = "", Second = "", Why = "检修/空闲不记量" };
        }
    }

    /// <summary>单据表格那一格（主量换行次量），无量时「—」。</summary>
    public static string Cell(ProductionTask? t) => Of(t).Cell;

    // ═════════════════════════════════════════════════════════════════════════
    //  OD3 合计 —— 分账列示，不给总数
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>一批任务的分账合计。<b>四本账各自一行，不合成一个总量。</b></summary>
    public sealed class Totals
    {
        public int TaskCount;

        // 穿孔
        public int DrillTasks;
        public double DrillControlM3;
        public int DrillHoles;
        public double DrillMeters;

        // 爆破（只有条数：它没有方量口径）
        public int BlastTasks;

        // 采装
        public int LoadTasks;
        public double LoadInSituM3;
        public double LoadTonnageT;
        /// <summary>计入采出量的部分（矿/煤）m³ 实方。</summary>
        public double OreM3;
        /// <summary>计入剥离量的部分 m³ 实方。</summary>
        public double WasteM3;

        // 运输
        public int HaulTasks;
        public double HaulTonnageT;
        public int Trips;
        /// <summary>运输功 t·km —— <b>逐笔运输</b>累加（承运吨 × 该笔自己的运距）。</summary>
        public double WorkTKm;
        /// <summary>有运距的那部分承运吨（加权平均运距的分母）。</summary>
        public double HauledWithKmT;
        /// <summary>吨量加权平均运距 km；没有带运距的运输笔时为 0。</summary>
        public double AvgHaulKm => HauledWithKmT > 1e-6 ? WorkTKm / HauledWithKmT : 0;
        /// <summary>没有运距的运输笔数（运输功因此偏小，必须写出来）。</summary>
        public int HaulNoKm;

        // 排土
        public int DumpTasks;
        public double DumpM3;

        /// <summary>
        /// 抬头指标条：**四本账各一段**。空段不写（本班没有这道工序就不占位置）。
        /// <para>这里刻意不给"合计 N 万m³"——那个数会把控制方量、实方、占容方加在一起。</para>
        /// </summary>
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
                if (BlastTasks > 0)
                    parts.Add($"爆破 {BlastTasks} 炮（不派设备 · 无方量口径）");
                if (LoadTasks > 0)
                    parts.Add($"采装 {LoadTasks} 项 · {LoadInSituM3 / 1e4:0.00} 万m³实方 / {LoadTonnageT / 1e4:0.00} 万t");
                if (HaulTasks > 0)
                    parts.Add($"运输 {HaulTasks} 项 · 承运 {HaulTonnageT / 1e4:0.00} 万t"
                        + (Trips > 0 ? $" / {Trips} 车次" : " / 车次未解出")
                        + (WorkTKm > 1e-6 ? $" · 运输功 {WorkTKm / 1e4:0.00} 万t·km · 加权运距 {AvgHaulKm:0.00} km" : " · 运输功 —")
                        + (HaulNoKm > 0 ? $"（{HaulNoKm} 项无运距，运输功偏小）" : ""));
                if (DumpTasks > 0)
                    parts.Add($"排土 {DumpTasks} 项 · 排弃占容 {DumpM3 / 1e4:0.00} 万m³");
                return parts.Count == 0 ? "本班无任务。" : string.Join("　｜　", parts);
            }
        }
    }

    /// <summary>分账汇总。<paramref name="tasks"/> 里的检修/空闲不计。</summary>
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
                    s.DrillTasks++;
                    s.DrillControlM3 += t.ControlVolumeM3;
                    s.DrillHoles += t.Drill?.PlanHoles ?? 0;
                    s.DrillMeters += t.Drill?.PlanMeters ?? 0;
                    break;
                case ProcessType.Blast:
                    s.BlastTasks++;
                    break;
                case ProcessType.Load:
                    s.LoadTasks++;
                    s.LoadInSituM3 += t.TargetVolumeM3;
                    s.LoadTonnageT += t.TargetTonnageT;
                    s.OreM3 += t.OreVolumeM3;
                    s.WasteM3 += t.WasteVolumeM3;
                    break;
                case ProcessType.Haul:
                    s.HaulTasks++;
                    s.HaulTonnageT += t.HaulTonnageT;
                    s.Trips += t.TripCount;
                    // 运输功逐笔算：混采面派生出的煤趟与岩趟运距不同，
                    // 拿主去向的运距乘全部吨量，抬头那个数就是编的。
                    if (t.EffectiveHaulKm > 1e-6)
                    {
                        s.WorkTKm += t.HaulTonnageT * t.EffectiveHaulKm;
                        s.HauledWithKmT += t.HaulTonnageT;
                    }
                    else if (t.HaulTonnageT > 1e-6) s.HaulNoKm++;
                    break;
                case ProcessType.Dump:
                    s.DumpTasks++;
                    s.DumpM3 += Of(t).Value;
                    break;
            }
        }
        return s;
    }
}
