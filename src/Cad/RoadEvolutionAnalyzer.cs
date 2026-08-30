using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>折线段均匀格索引(忠实移植原 RoadLib.Network.SegmentGrid)：格 → (线号, 段起点扁平下标)。只按 XY。</summary>
internal sealed class EvoSegmentGrid
{
    private readonly Dictionary<(int, int), List<(int Line, int Si)>> _cells = new();
    private readonly double _cs;
    private EvoSegmentGrid(double cellSize) => _cs = cellSize;

    public static EvoSegmentGrid Build(IReadOnlyList<double[]> lines, double minCellM)
    {
        double sum = 0; int segs = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var f = lines[i];
            if (f == null || f.Length < 6) continue;
            for (int s = 0; s + 5 < f.Length; s += 3)
            {
                double dx = f[s + 3] - f[s], dy = f[s + 4] - f[s + 1];
                sum += Math.Sqrt(dx * dx + dy * dy); segs++;
            }
        }
        double cs = Math.Max(Math.Max(minCellM, 5.0), segs > 0 ? sum / segs : 25.0);
        var g = new EvoSegmentGrid(cs);
        for (int i = 0; i < lines.Count; i++)
        {
            var f = lines[i];
            if (f == null || f.Length < 6) continue;
            for (int s = 0; s + 5 < f.Length; s += 3)
                g.Register(i, s, f[s], f[s + 1], f[s + 3], f[s + 4]);
        }
        return g;
    }

    private void Register(int line, int si, double ax, double ay, double bx, double by)
    {
        int x0 = Cell(Math.Min(ax, bx)), x1 = Cell(Math.Max(ax, bx));
        int y0 = Cell(Math.Min(ay, by)), y1 = Cell(Math.Max(ay, by));
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                var k = (cx, cy);
                if (!_cells.TryGetValue(k, out var l)) { l = new List<(int, int)>(); _cells[k] = l; }
                l.Add((line, si));
            }
    }

    private int Cell(double v) => (int)Math.Floor(v / _cs);

    public IEnumerable<(int Line, int Si)> Query(double x, double y, double radius)
    {
        int x0 = Cell(x - radius), x1 = Cell(x + radius);
        int y0 = Cell(y - radius), y1 = Cell(y + radius);
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
                if (_cells.TryGetValue((cx, cy), out var l))
                    foreach (var it in l) yield return it;
    }
}

/// <summary>一期中线匹配索引(忠实移植原 RoadLib.Evolution.GeometryIndex)：查采样点在对期有无对应路面。</summary>
internal sealed class EvoGeometryIndex
{
    private readonly List<double[]> _flat;
    private readonly EvoSegmentGrid _grid;
    private EvoGeometryIndex(List<double[]> flat, EvoSegmentGrid grid) { _flat = flat; _grid = grid; }
    public int LineCount => _flat.Count;

    public static EvoGeometryIndex Build(IReadOnlyList<IReadOnlyList<Pt3>> lines, double tolM)
    {
        var flat = new List<double[]>(lines.Count);
        foreach (var pl in lines)
        {
            var f = new double[Math.Max(pl.Count, 0) * 3];
            for (int i = 0; i < pl.Count; i++) { f[3 * i] = pl[i].X; f[3 * i + 1] = pl[i].Y; f[3 * i + 2] = pl[i].Z; }
            flat.Add(f);
        }
        return new EvoGeometryIndex(flat, EvoSegmentGrid.Build(flat, tolM * 2));
    }

    internal readonly record struct Hit(bool Matched, int LineIndex, double DistanceM, double SignedOffsetM);

    public Hit Match(in Pt3 p, double tx, double ty, double tolM, double cosTol, double overshootTolM)
    {
        double best = double.MaxValue; int bestLine = -1; double bestOff = 0;
        foreach (var (li, si) in _grid.Query(p.X, p.Y, tolM))
        {
            var f = _flat[li];
            if (si + 5 >= f.Length) continue;
            double ax = f[si], ay = f[si + 1], bx = f[si + 3], by = f[si + 4];
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12) continue;
            double len = Math.Sqrt(len2);
            double ux = dx / len, uy = dy / len;

            double dot = ux * tx + uy * ty;
            if (Math.Abs(dot) < cosTol) continue;

            double t = ((p.X - ax) * dx + (p.Y - ay) * dy) / len2;
            double tc = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = ax + tc * dx, qy = ay + tc * dy;
            double ex = p.X - qx, ey = p.Y - qy;
            double d = Math.Sqrt(ex * ex + ey * ey);
            if (d > tolM || d >= best) continue;

            if (t < 0 && si == 0)
            {
                double over = -t * len;
                if (over > overshootTolM) continue;
            }
            else if (t > 1 && si + 8 >= f.Length)
            {
                double over = (t - 1) * len;
                if (over > overshootTolM) continue;
            }

            double sx = dot >= 0 ? ux : -ux, sy = dot >= 0 ? uy : -uy;
            best = d; bestLine = li; bestOff = sx * ey - sy * ex;
        }
        return bestLine >= 0 ? new Hit(true, bestLine, best, bestOff) : new Hit(false, -1, double.NaN, 0);
    }
}

/// <summary>演化判定纯几何度量(忠实移植原 RoadLib.Evolution.PolylineMetrics)。全按平面(XY)。</summary>
internal static class EvoPolylineMetrics
{
    public static double Length2D(IReadOnlyList<Pt3> pl)
    {
        double s = 0;
        for (int i = 1; i < pl.Count; i++) s += pl[i].HorizontalDistanceTo(pl[i - 1]);
        return s;
    }

    public static List<Pt3> Resample(IReadOnlyList<Pt3> pl, double step)
    {
        var outPts = new List<Pt3>();
        int cnt = pl.Count;
        if (cnt == 0) return outPts;
        if (cnt < 2 || step <= 0) { for (int i = 0; i < cnt; i++) outPts.Add(pl[i]); return outPts; }

        outPts.Add(pl[0]);
        double walked = 0, next = step;
        for (int i = 1; i < cnt; i++)
        {
            var a = pl[i - 1]; var b = pl[i];
            double segLen = b.HorizontalDistanceTo(a);
            if (segLen < 1e-9) continue;
            double segStart = walked, segEnd = walked + segLen;
            while (next <= segEnd + 1e-9)
            {
                double t = (next - segStart) / segLen;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                outPts.Add(new Pt3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t));
                next += step;
            }
            walked = segEnd;
        }
        var last = pl[cnt - 1];
        if (outPts[outPts.Count - 1].HorizontalDistanceTo(last) > step * 0.25) outPts.Add(last);
        return outPts;
    }

    public static List<Pt3> SubPolyline(IReadOnlyList<Pt3> s, double[] arc, double fromM, double toM)
    {
        var outPts = new List<Pt3>();
        int n = s.Count;
        if (n < 2 || toM <= fromM) return outPts;
        outPts.Add(At(s, arc, fromM));
        for (int i = 0; i < n; i++)
            if (arc[i] > fromM + 1e-9 && arc[i] < toM - 1e-9) outPts.Add(s[i]);
        outPts.Add(At(s, arc, toM));
        return outPts;
    }

    private static Pt3 At(IReadOnlyList<Pt3> s, double[] arc, double m)
    {
        int n = s.Count;
        if (m <= arc[0]) return s[0];
        if (m >= arc[n - 1]) return s[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (arc[mid] <= m) lo = mid; else hi = mid; }
        double seg = arc[hi] - arc[lo];
        double t = seg < 1e-12 ? 0 : (m - arc[lo]) / seg;
        var a = s[lo]; var b = s[hi];
        return new Pt3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    }

    public static (double X, double Y) SampleTangent(IReadOnlyList<Pt3> s, int i)
    {
        int a = Math.Max(0, i - 1), b = Math.Min(s.Count - 1, i + 1);
        if (a == b) return (1, 0);
        double dx = s[b].X - s[a].X, dy = s[b].Y - s[a].Y;
        double l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (1.0, 0.0) : (dx / l, dy / l);
    }

    public static (double X, double Y) OutwardTangent(IReadOnlyList<Pt3> s, bool atStart)
    {
        int n = s.Count;
        if (n < 2) return (1, 0);
        Pt3 a, b;
        if (atStart) { a = s[1]; b = s[0]; } else { a = s[n - 2]; b = s[n - 1]; }
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (1.0, 0.0) : (dx / l, dy / l);
    }
}

/// <summary>
/// 两期路网演化判定（忠实移植原 <c>RoadLib.Evolution.RoadEvolutionAnalyzer</c>, RE1-8 逐段游程匹配）——
/// 逐采样点问「对期有无对应路面」(EvoGeometryIndex 走向闸+端外闸)→切游程→并短游程→按覆盖率分类
/// (保持/移位/延拓/截短/已废除), 里程账 RE8 不重不漏。作用于两期中线 EvoLine 列表。纯几何、可单测。
/// </summary>
public static class RoadEvolutionAnalyzer
{
    public static RoadEvolutionResult Analyze(IReadOnlyList<EvoLine> prev, IReadOnlyList<EvoLine> curr, RoadEvolutionOptions? options = null)
    {
        var opt = options ?? new RoadEvolutionOptions();
        var result = new RoadEvolutionResult();
        if (prev is null || curr is null) return result;

        var prevLines = prev.Where(e => e.Centerline.Count >= 2).ToList();
        var currLines = curr.Where(e => e.Centerline.Count >= 2).ToList();

        double tol = Math.Max(1e-6, opt.MatchToleranceM);
        double cosTol = Math.Cos(Math.Clamp(opt.MatchAngleDeg, 0, 89.9) * Math.PI / 180.0);
        double overshoot = Math.Max(opt.SampleStepM / 2, 0.5);

        var prevIdx = EvoGeometryIndex.Build(prevLines.Select(e => (IReadOnlyList<Pt3>)e.Centerline).ToList(), tol);
        var currIdx = EvoGeometryIndex.Build(currLines.Select(e => (IReadOnlyList<Pt3>)e.Centerline).ToList(), tol);

        var led = result.Ledger;
        foreach (var e in currLines)
            ScanEdge(e, EvolutionSide.Curr, prevIdx, prevLines, opt, tol, cosTol, overshoot, result);
        foreach (var e in prevLines)
            ScanEdge(e, EvolutionSide.Prev, currIdx, currLines, opt, tol, cosTol, overshoot, result);

        led.PrevTotalM = prevLines.Sum(e => EvoPolylineMetrics.Length2D(e.Centerline));
        led.CurrTotalM = currLines.Sum(e => EvoPolylineMetrics.Length2D(e.Centerline));
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

    private static void ScanEdge(
        EvoLine e, EvolutionSide side, EvoGeometryIndex other, List<EvoLine> otherEdges,
        RoadEvolutionOptions opt, double tol, double cosTol, double overshoot, RoadEvolutionResult result)
    {
        double rawLen = EvoPolylineMetrics.Length2D(e.Centerline);
        if (rawLen < 1e-6) return;

        var s = EvoPolylineMetrics.Resample(e.Centerline, opt.SampleStepM);
        int n = s.Count;
        if (n < 2) { EmitWholeEdge(e, side, rawLen, matchDist: double.NaN, shift: 0, otherId: null, result); return; }

        var arc = new double[n];
        for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + s[i].HorizontalDistanceTo(s[i - 1]);
        double sampledLen = arc[n - 1];
        if (sampledLen < 1e-9) return;
        double scale = rawLen / sampledLen;

        var matched = new bool[n];
        var dist = new double[n];
        var off = new double[n];
        var line = new int[n];
        for (int i = 0; i < n; i++)
        {
            var (tx, ty) = EvoPolylineMetrics.SampleTangent(s, i);
            var hit = other.Match(s[i], tx, ty, tol, cosTol, overshoot);
            matched[i] = hit.Matched; dist[i] = hit.DistanceM; off[i] = hit.SignedOffsetM; line[i] = hit.LineIndex;
        }

        var runs = BuildRuns(matched, arc, sampledLen);
        MergeShortRuns(runs, opt.GrowMinLenM);

        double coveredLen = runs.Where(r => r.Matched).Sum(r => r.EndM - r.StartM);
        double cover = coveredLen / sampledLen;

        double wholeThresh = side == EvolutionSide.Curr ? opt.NewCoverFrac : opt.GoneCoverFrac;
        bool wholeGone = cover < wholeThresh;
        bool singleRun = runs.Count == 1;

        foreach (var run in runs)
        {
            double segLen = (run.EndM - run.StartM) * scale;
            if (segLen < 1e-9) continue;
            var geom = EvoPolylineMetrics.SubPolyline(s, arc, run.StartM, run.EndM);
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

            bool atStart = run.FirstIndex == 0;
            var (gdx, gdy) = atEnd
                ? EvoPolylineMetrics.OutwardTangent(geom, atStart)
                : EvoPolylineMetrics.OutwardTangent(geom, atStart: false);
            Pt3? freeEnd = atEnd ? (atStart ? geom[0] : geom[geom.Count - 1]) : (Pt3?)null;
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

    private static void EmitWholeEdge(EvoLine e, EvolutionSide side, double rawLen,
        double matchDist, double shift, string? otherId, RoadEvolutionResult result)
    {
        bool isCurr = side == EvolutionSide.Curr;
        var s = EvoPolylineMetrics.Resample(e.Centerline, 8.0);
        var (gdx, gdy) = EvoPolylineMetrics.OutwardTangent(s.Count >= 2 ? s : e.Centerline, atStart: false);
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
            FreeEnd = isCurr && e.Centerline.Count >= 1 ? e.Centerline[e.Centerline.Count - 1] : (Pt3?)null,
        });
    }

    private sealed class Run { public bool Matched; public int FirstIndex, LastIndex; public double StartM, EndM; }

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

    private static string? DominantOtherId(IEnumerable<int> lines, IEnumerable<bool> matched, List<EvoLine> otherEdges)
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
