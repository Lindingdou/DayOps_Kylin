// 忠实移植自原 PitMine3D Modules/RoadLib/Routing/HaulTextReport.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 往返对的等宽文本报告（纯函数、零 UI 依赖、可单测）。点对点寻径与等效运距共用同一套排版，
/// 保证同一对源汇在两个入口读到的是同一组数字、同一套列。
///
/// 逐段表为什么要带车道/限载/限速/路面：过滤到底跑没跑，用户得能自己看出来。
/// 只给"边Id/里程/纵坡/状态"四列时，限载绕行、限速影响时间这些事在屏上完全不可见，
/// 等于让人相信一个看不见的东西。
/// </summary>
public static class HaulTextReport
{
    /// <summary>「—」：没有值。运距/成本域里 0 是语义合法但灾难性的值（0 运距 = 免费运输），一律不用 0 顶替。</summary>
    public const string Dash = "—";

    /// <summary>整块报告：口径 → 结论 → 去程逐段 → 回程逐段（含诊断）。</summary>
    public static string PairBlock(RoadGraph graph, HaulPairResult pair, HaulCaliper caliper)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【{pair.SrcId} ⇄ {pair.DstId}】{pair.StatusText}");
        sb.AppendLine();
        sb.Append(caliper.SummaryBlock());
        sb.AppendLine();

        sb.AppendLine("汇总（去程=重车 src→dst，回程=空车 dst→src，两条腿各自独立求解）：");
        sb.AppendLine($"  去程：里程 {Num(pair.Outbound.LengthM, "F0", " m")} · 等效 {Num(pair.Outbound.EquivM, "F0", " m")} · 时间 {Num(pair.Outbound.TimeMin, "F1", " min")}");
        sb.AppendLine($"  回程：里程 {Num(pair.Return.LengthM, "F0", " m")} · 等效 {Num(pair.Return.EquivM, "F0", " m")} · 时间 {Num(pair.Return.TimeMin, "F1", " min")}");
        sb.AppendLine($"  往返等效运距：{Num(pair.RoundTripEquivM, "F0", " m")}");
        sb.AppendLine($"  循环时间：{Num(pair.CycleTimeMin, "F1", " min")}"
                      + $"（重车运 + 空车返 + 装车 {caliper.SpotLoadMin:F1} + 卸车调车 {caliper.ManeuverDumpMin:F1}）");
        sb.AppendLine($"  单趟成本：{(pair.CostPerTripYuan is { } c ? $"{c:F0} 元" : Dash + "（未设运输单价）")}");
        sb.AppendLine();

        if (pair.PrimaryDiagnosis is { } d && d.Cause != HaulBlockCause.None)
        {
            sb.AppendLine($"✗ 走不通的原因：{d.Text}");
            sb.AppendLine();
        }

        AppendLeg(sb, graph, "去程（重车）", pair.Outbound, null);
        AppendLeg(sb, graph, "回程（空车）", pair.Return, pair.Outbound.Path.EdgeIds);
        return sb.ToString();
    }

    private static void AppendLeg(StringBuilder sb, RoadGraph graph, string title, HaulLeg leg,
        IReadOnlyList<string>? compareWith)
    {
        sb.AppendLine($"{title}：{leg.FromId} → {leg.ToId}");
        if (!leg.Feasible)
        {
            sb.AppendLine($"  不可达 —— {leg.Diagnosis?.Text ?? "（未跑诊断）"}");
            sb.AppendLine();
            return;
        }

        // 走没走"补出来的"段，必须在读数之前说 —— 那几段图上没有真路，是算法/人工连出来的。
        // 现场量过：桥接距离 50m 时 79.5% 的路径至少踩一条，不说等于给了一条"看着能走、实际得先修路"的线。
        var synth = leg.Path.EdgeIds.Select(graph.GetEdge).Where(e => e is { IsSynthetic: true }).ToList();
        if (synth.Count > 0)
            sb.AppendLine($"  ⚠ 其中 {synth.Count} 段是补出来的（合计 {synth.Sum(e => e!.LengthM):F0} m）：图上没有对应的真实中线，"
                          + "现场要么本来就通、要么得先修 —— 逐段表「来源」列标出了是哪几段。");

        sb.Append(SegmentTable(graph, leg.Path.NodeIds, leg.Path.EdgeIds, compareWith, indent: "  "));
        sb.AppendLine();
    }

    /// <summary>
    /// 逐段表。<paramref name="compareWith"/> 非空时，不在该边表里的段前面打 <c>*</c>
    /// —— 回程与去程走的不是同一串边时，差在哪一眼能看见（这正是"不能把去程反过来当回程"的现场证据）。
    /// </summary>
    public static string SegmentTable(RoadGraph graph, IReadOnlyList<string> nodeIds, IReadOnlyList<string> edgeIds,
        IReadOnlyList<string>? compareWith = null, string indent = "")
    {
        var sb = new StringBuilder();
        if (nodeIds.Count > 0)
            sb.AppendLine($"{indent}路径：{string.Join(" → ", nodeIds)}（{edgeIds.Count} 段）");

        var other = compareWith is null ? null : new HashSet<string>(compareWith, StringComparer.Ordinal);
        sb.AppendLine($"{indent}{"",-2}{"边Id",-10}{"里程",8}{"平均坡",9}{"最陡段",9}{"车道",5}{"限载",8}{"限速",8}  {"路面",-8}{"状态",-6}来源");
        foreach (var eid in edgeIds)
        {
            var e = graph.GetEdge(eid);
            if (e is null) continue;
            string mark = other is not null && !other.Contains(eid) ? "* " : "  ";
            string load = e.MaxLoadT > 0 ? $"{e.MaxLoadT:F0} t" : Dash;
            string spd = e.SpeedLimitKph > 0 ? $"{e.SpeedLimitKph:F0} km/h" : Dash;
            string pav = string.IsNullOrWhiteSpace(e.Pavement) ? Dash : e.Pavement!;
            // 「来源」空着 = 图上抽的真实中线；有字 = 补出来的 / 坑线落地复用（见 RoadEdge.SourceRef）。
            string src = e.IsSynthetic ? "⚠ " + e.SourceRef : (e.SourceRef ?? "");
            sb.AppendLine($"{indent}{mark}{eid,-10}{e.LengthM,7:F0}m{e.GradePct,8:+0.0;-0.0}%"
                        + $"{e.MaxAbsSegGradePct,8:F1}%{e.LaneCount,5}{load,8}{spd,8}  {pav,-8}{StatusText(e.Status),-6}{src}");
        }
        if (other is not null)
            sb.AppendLine($"{indent}（* = 去程没走的段；全无 * 即原路返回）");
        return sb.ToString();
    }

    /// <summary>边状态中文。</summary>
    public static string StatusText(RoadEdgeStatus s) => s switch
    {
        RoadEdgeStatus.Open => "开放",
        RoadEdgeStatus.Maintenance => "检修",
        _ => "关闭",
    };

    /// <summary>可空数值 → 文本；null 一律 <see cref="Dash"/>，不落 0。</summary>
    public static string Num(double? v, string fmt, string unit = "")
        => v is { } x ? x.ToString(fmt) + unit : Dash;
}
