// 忠实移植自原 PitMine3D Modules/RoadLib/Evolution/RoadEvolutionAnalyzer.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 两期道路中心线演化判定引擎（只识别，不执行）。
///
/// 按几何比，不按 Id —— 两期各自从点云提取，边/节点 Id 不通用。
///
/// ── 口径（RE1–RE8，改口径就改这里，别在调用方打补丁）─────────────────────
///  RE1 判定原子是<b>边上的一段</b>，不是整条边。一条边可以同时出「保持 380m + 延拓 100m」。
///      整条边一个类别那版答不了截短、答不了中段改线、两头都长只报一头（台架 C4/C5/C9）。
///  RE2 一个采样点算不算"共有"：在<b>容差内且走向同向</b>的候选里取最近，
///      不是"取全局最近再看角度"（后者在路口/接缝处必错，见 <see cref="GeometryIndex"/>）。
///  RE2b 端外闸：超出对期折线端点之外的点不算覆盖，否则端头延拓固定少报一个容差带宽。
///  RE3 覆盖按<b>弧长</b>算，不按采样点个数。段边界切在相邻两采样点的弧长中点。
///  RE4 两期<b>用同一套匹配口径、同一个容差</b>各扫一遍：本期侧出 保持/移位/延拓，
///      上期侧出 截短/废除。不再有两个互不互补的覆盖率阈值造出的"两边都不报"灰带。
///  RE5 短于 <see cref="RoadEvolutionOptions.GrowMinLenM"/> 的游程并入相邻段（滤提取抖动）。
///  RE6 段的类别：本期未覆盖=延拓（贴端头=延伸/夹中间=改线新线）；上期未覆盖=截短（同理）。
///      整条边覆盖率低于阈值时，未覆盖段<b>升格</b>为「新建入网」/「已废除」——只升格类别，
///      覆盖段照常按保持/移位出行，否则那截还在的路会既算"消失"又算"共有"。
///  RE7 共有段的<b>带符号横移中位数</b> ≥ <see cref="RoadEvolutionOptions.ShiftThresholdM"/> → 判「移位」。
///      用带符号中位数而不是平均绝对值：提取抖动左右乱窜、中位数≈0，整体平移才顶得上去。
///  RE8 里程账可配平：同一条边的所有段首尾相接、长度之和 = 该边全长，
///      于是 Σ本期侧段 = 本期总里程、Σ上期侧段 = 上期总里程。差一米就是有段被吞了或重复计了。
///      （重采样相对原中线的微小缩水按比例摊回各段，保证这条账是硬的。）
///
/// 输出全是分段 + 证据，交给「更新路网」去落地（RE12：只能并入本期侧的段）。
/// </summary>
public static class RoadEvolutionAnalyzer
{
    /// <param name="prev">上期路网快照。</param>
    /// <param name="curr">本期路网快照。</param>
    public static RoadEvolutionResult Analyze(RoadGraph prev, RoadGraph curr, RoadEvolutionOptions? options = null)
    {
        var opt = options ?? new RoadEvolutionOptions();
        var result = new RoadEvolutionResult();
        if (prev is null || curr is null) return result;

        var prevLines = prev.Edges.Where(e => e.Centerline.Count >= 2).ToList();
        var currLines = curr.Edges.Where(e => e.Centerline.Count >= 2).ToList();

        double tol = Math.Max(1e-6, opt.MatchToleranceM);
        double cosTol = Math.Cos(Math.Clamp(opt.MatchAngleDeg, 0, 89.9) * Math.PI / 180.0);
        double overshoot = Math.Max(opt.SampleStepM / 2, 0.5);

        var prevIdx = GeometryIndex.Build(prevLines.Select(e => (IReadOnlyList<Point3d>)e.Centerline).ToList(), tol);
        var currIdx = GeometryIndex.Build(currLines.Select(e => (IReadOnlyList<Point3d>)e.Centerline).ToList(), tol);

        var led = result.Ledger;

        foreach (var e in currLines)
            ScanEdge(e, EvolutionSide.Curr, prevIdx, prevLines, opt, tol, cosTol, overshoot, result);
        foreach (var e in prevLines)
            ScanEdge(e, EvolutionSide.Prev, currIdx, currLines, opt, tol, cosTol, overshoot, result);

        led.PrevTotalM = prevLines.Sum(e => PolylineMetrics.Length2D(e.Centerline));
        led.CurrTotalM = currLines.Sum(e => PolylineMetrics.Length2D(e.Centerline));
        foreach (var r in result.Routes)
        {
            if (r.Side == EvolutionSide.Curr)
            {
                if (r.Class == RoadEvolutionClass.Extend) led.NewM += r.SegLenM;
                else
                {
                    led.SharedCurrM += r.SegLenM;
                    if (r.Class == RoadEvolutionClass.Shift) led.ShiftedM += r.SegLenM;
                }
            }
            else
            {
                if (r.Class is RoadEvolutionClass.Shorten or RoadEvolutionClass.Abolish) led.GoneM += r.SegLenM;
                else led.SharedPrevM += r.SegLenM;
            }
        }
        return result;
    }

    // ── 一条边的扫描 ─────────────────────────────────────────────────────
    private static void ScanEdge(
        RoadEdge e, EvolutionSide side, GeometryIndex other, List<RoadEdge> otherEdges,
        RoadEvolutionOptions opt, double tol, double cosTol, double overshoot, RoadEvolutionResult result)
    {
        double rawLen = PolylineMetrics.Length2D(e.Centerline);
        if (rawLen < 1e-6) return;

        var s = PolylineMetrics.Resample(e.Centerline, opt.SampleStepM);
        int n = s.Count;
        if (n < 2)
        {
            EmitWholeEdge(e, side, rawLen, cover: 0, matchDist: double.NaN, shift: 0, otherId: null, result);
            return;
        }

        // 逐采样点：共有？距多远？横移多少？对上了对期哪条线？
        var arc = new double[n];
        for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + s[i].HorizontalDistanceTo(s[i - 1]);
        double sampledLen = arc[n - 1];
        if (sampledLen < 1e-9) return;
        double scale = rawLen / sampledLen;               // RE8：重采样缩水按比例摊回，保证段长之和 = 边全长

        var matched = new bool[n];
        var dist = new double[n];
        var off = new double[n];
        var line = new int[n];
        for (int i = 0; i < n; i++)
        {
            var (tx, ty) = PolylineMetrics.SampleTangent(s, i);
            var hit = other.Match(s[i], tx, ty, tol, cosTol, overshoot);
            matched[i] = hit.Matched;
            dist[i] = hit.DistanceM;
            off[i] = hit.SignedOffsetM;
            line[i] = hit.LineIndex;
        }

        // RE3/RE5：切游程 → 并短游程
        var runs = BuildRuns(matched, arc, sampledLen);
        MergeShortRuns(runs, opt.GrowMinLenM);

        double coveredLen = runs.Where(r => r.Matched).Sum(r => r.EndM - r.StartM);
        double cover = coveredLen / sampledLen;

        // RE6：整条边覆盖率低于阈值 → 这条边的未覆盖段升格为「新建入网」/「已废除」。
        // <b>只升格类别，不吞掉覆盖段</b>：上期边只剩 100 m 被本期覆盖时，那 100 m 仍要按保持出行，
        // 否则上期侧的账会把这 100 m 既算进"消失"又算进"共有"（第一版就是这么错的，台架 C6 抓到）。
        double wholeThresh = side == EvolutionSide.Curr ? opt.NewCoverFrac : opt.GoneCoverFrac;
        bool wholeGone = cover < wholeThresh;
        bool singleRun = runs.Count == 1;

        foreach (var run in runs)
        {
            double segLen = (run.EndM - run.StartM) * scale;
            if (segLen < 1e-9) continue;
            var geom = PolylineMetrics.SubPolyline(s, arc, run.StartM, run.EndM);
            if (geom.Count < 2) continue;

            bool atEnd = run.FirstIndex == 0 || run.LastIndex == n - 1;
            var idxs = Enumerable.Range(run.FirstIndex, run.LastIndex - run.FirstIndex + 1).ToList();
            string? otherId = DominantOtherId(idxs.Select(i => line[i]), idxs.Select(i => matched[i]), otherEdges);

            if (run.Matched)
            {
                double shift = MedianOf(idxs.Where(i => matched[i]).Select(i => off[i]));
                double md = MedianOf(idxs.Where(i => matched[i]).Select(i => dist[i]));
                var cls = Math.Abs(shift) >= opt.ShiftThresholdM ? RoadEvolutionClass.Shift : RoadEvolutionClass.Keep;
                result.Routes.Add(new RouteEvolution
                {
                    Class = cls,
                    Side = side,
                    PrevEdgeId = side == EvolutionSide.Curr ? otherId : e.Id,
                    CurrEdgeId = side == EvolutionSide.Curr ? e.Id : otherId,
                    DisplayCenterline = geom,
                    SegLenM = segLen,
                    EdgeLenM = rawLen,
                    SegStartM = run.StartM * scale,
                    AtEdgeEnd = atEnd,
                    MatchDistanceM = md,
                    ShiftM = shift,
                });
                continue;
            }

            // 未覆盖段：本期侧 = 延拓，上期侧 = 截短
            bool atStart = run.FirstIndex == 0;
            var (gdx, gdy) = atEnd
                ? PolylineMetrics.OutwardTangent(geom, atStart)
                : PolylineMetrics.OutwardTangent(geom, atStart: false);
            Point3d? freeEnd = atEnd ? (atStart ? geom[0] : geom[geom.Count - 1]) : null;
            bool? dirAgree = null;
            if (side == EvolutionSide.Curr && opt.AdvanceDirXY is { } adv && (gdx != 0 || gdy != 0))
                dirAgree = gdx * adv.X + gdy * adv.Y > 0;

            result.Routes.Add(new RouteEvolution
            {
                Class = side == EvolutionSide.Curr
                    ? RoadEvolutionClass.Extend
                    : (wholeGone ? RoadEvolutionClass.Abolish : RoadEvolutionClass.Shorten),
                Side = side,
                IsNewRoad = side == EvolutionSide.Curr && wholeGone,
                IsWholeEdge = wholeGone && singleRun,
                PrevEdgeId = side == EvolutionSide.Curr ? null : e.Id,
                CurrEdgeId = side == EvolutionSide.Curr ? e.Id : null,
                DisplayCenterline = geom,
                SegLenM = segLen,
                EdgeLenM = rawLen,
                SegStartM = run.StartM * scale,
                AtEdgeEnd = atEnd,
                MatchDistanceM = double.NaN,
                GrowthDx = gdx,
                GrowthDy = gdy,
                FreeEnd = freeEnd,
                DirAgreesWithAdvance = dirAgree,
            });
        }
    }

    private static void EmitWholeEdge(
        RoadEdge e, EvolutionSide side, double rawLen, double cover,
        double matchDist, double shift, string? otherId, RoadEvolutionResult result)
    {
        bool isCurr = side == EvolutionSide.Curr;
        var s = PolylineMetrics.Resample(e.Centerline, 8.0);
        var (gdx, gdy) = PolylineMetrics.OutwardTangent(s.Count >= 2 ? s : e.Centerline, atStart: false);
        result.Routes.Add(new RouteEvolution
        {
            Class = isCurr ? RoadEvolutionClass.Extend : RoadEvolutionClass.Abolish,
            Side = side,
            IsNewRoad = isCurr,
            IsWholeEdge = true,
            PrevEdgeId = isCurr ? otherId : e.Id,
            CurrEdgeId = isCurr ? e.Id : otherId,
            DisplayCenterline = e.Centerline,
            SegLenM = rawLen,
            EdgeLenM = rawLen,
            SegStartM = 0,
            AtEdgeEnd = true,
            MatchDistanceM = matchDist,
            ShiftM = shift,
            GrowthDx = isCurr ? gdx : 0,
            GrowthDy = isCurr ? gdy : 0,
            FreeEnd = isCurr && e.Centerline.Count >= 1 ? e.Centerline[e.Centerline.Count - 1] : null,
        });
    }

    // ── 游程 ─────────────────────────────────────────────────────────────
    private sealed class Run
    {
        public bool Matched;
        public int FirstIndex, LastIndex;
        public double StartM, EndM;
    }

    /// <summary>按 0/1 序列切游程；段边界取相邻两采样点的弧长中点（RE3）。</summary>
    private static List<Run> BuildRuns(bool[] matched, double[] arc, double total)
    {
        var runs = new List<Run>();
        int n = matched.Length;
        int a = 0;
        for (int i = 1; i <= n; i++)
        {
            if (i < n && matched[i] == matched[a]) continue;
            int b = i - 1;
            runs.Add(new Run
            {
                Matched = matched[a],
                FirstIndex = a,
                LastIndex = b,
                StartM = a == 0 ? 0 : (arc[a - 1] + arc[a]) / 2,
                EndM = b == n - 1 ? total : (arc[b] + arc[b + 1]) / 2,
            });
            a = i;
        }
        return runs;
    }

    /// <summary>RE5：短于 <paramref name="minLenM"/> 的游程并入相邻段（并向更长的那边）。</summary>
    private static void MergeShortRuns(List<Run> runs, double minLenM)
    {
        if (minLenM <= 0) return;
        bool changed = true;
        while (changed && runs.Count > 1)
        {
            changed = false;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].EndM - runs[i].StartM >= minLenM) continue;
                int target;
                if (i == 0) target = 1;
                else if (i == runs.Count - 1) target = runs.Count - 2;
                else target = (runs[i - 1].EndM - runs[i - 1].StartM) >= (runs[i + 1].EndM - runs[i + 1].StartM) ? i - 1 : i + 1;

                var keep = runs[target];
                var drop = runs[i];
                keep.StartM = Math.Min(keep.StartM, drop.StartM);
                keep.EndM = Math.Max(keep.EndM, drop.EndM);
                keep.FirstIndex = Math.Min(keep.FirstIndex, drop.FirstIndex);
                keep.LastIndex = Math.Max(keep.LastIndex, drop.LastIndex);
                runs.RemoveAt(i);
                changed = true;
                break;
            }
            // 并完可能出现相邻同类，合掉
            for (int i = runs.Count - 1; i > 0; i--)
                if (runs[i].Matched == runs[i - 1].Matched)
                {
                    runs[i - 1].EndM = Math.Max(runs[i - 1].EndM, runs[i].EndM);
                    runs[i - 1].LastIndex = Math.Max(runs[i - 1].LastIndex, runs[i].LastIndex);
                    runs.RemoveAt(i);
                    changed = true;
                }
        }
    }

    /// <summary>这一段主要对上了对期哪条边（按命中数取优）。</summary>
    private static string? DominantOtherId(IEnumerable<int> lines, IEnumerable<bool> matched, List<RoadEdge> otherEdges)
    {
        var counts = new Dictionary<int, int>();
        using var li = lines.GetEnumerator();
        using var mi = matched.GetEnumerator();
        while (li.MoveNext() && mi.MoveNext())
        {
            if (!mi.Current || li.Current < 0) continue;
            counts[li.Current] = counts.GetValueOrDefault(li.Current) + 1;
        }
        if (counts.Count == 0) return null;
        int best = -1, bestN = 0;
        foreach (var kv in counts) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
        return best >= 0 && best < otherEdges.Count ? otherEdges[best].Id : null;
    }

    private static double MedianOf(IEnumerable<double> src)
    {
        var v = src.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count == 0) return double.NaN;
        v.Sort();
        int m = v.Count / 2;
        return v.Count % 2 == 1 ? v[m] : (v[m - 1] + v[m]) / 2.0;
    }
}
