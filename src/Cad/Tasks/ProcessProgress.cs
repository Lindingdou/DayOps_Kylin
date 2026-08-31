using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks;

/// <summary>
/// 工序进度跟踪核算：按工序(穿孔/采装/运输/排土)聚合计划 vs 实绩达成率。
/// 穿孔按 DrillQuantity(延米优先)口径, 其余按 实绩量/计划量。纯计算, 可单测。
/// (原「工序进度跟踪」跟踪已排产任务 + 实绩回灌; 此以 计划/实绩 CSV 喂, 不依赖调度引擎。)
/// </summary>
public static class ProcessProgress
{
    /// <summary>一条任务的进度输入。drill 非空则用其口径(穿孔笔)。</summary>
    public sealed record Row(ProcessType Process, double PlanQty, double ActualQty, DrillQuantity? Drill = null);

    /// <summary>一个工序的进度汇总。</summary>
    public sealed record ProcSummary(ProcessType Process, int Count, int WithAttainment, double AvgAttainmentPct, int DoneCount);

    /// <summary>单条达成率 %：穿孔用 DrillQuantity, 其余 实绩/计划×100; 无计划量返回 null。</summary>
    public static double? AttainmentOf(Row r)
    {
        if (r.Process == ProcessType.Drill && r.Drill != null) return r.Drill.AttainmentPct;
        if (r.PlanQty > 1e-9) return System.Math.Round(r.ActualQty / r.PlanQty * 100, 0);
        return null;
    }

    /// <summary>按工序聚合：条数 / 有达成率条数 / 平均达成率 / 达标(≥100%)条数。</summary>
    public static List<ProcSummary> Summarize(IEnumerable<Row> rows)
    {
        var list = new List<ProcSummary>();
        foreach (var g in (rows ?? Enumerable.Empty<Row>()).GroupBy(r => r.Process).OrderBy(g => (int)g.Key))
        {
            var atts = g.Select(AttainmentOf).Where(a => a.HasValue).Select(a => a!.Value).ToList();
            double avg = atts.Count > 0 ? atts.Average() : 0;
            int done = atts.Count(a => a >= 100 - 1e-9);
            list.Add(new ProcSummary(g.Key, g.Count(), atts.Count, avg, done));
        }
        return list;
    }

    public static string Label(ProcessType p) => p switch
    {
        ProcessType.Drill => "穿孔", ProcessType.Blast => "爆破", ProcessType.Load => "采装",
        ProcessType.Haul => "运输", ProcessType.Dump => "排土", _ => "其它",
    };
}
