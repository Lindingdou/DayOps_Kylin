using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>端帮衔接结果：按模板边界裁剪后的端帮台阶线保留段 + 高程衔接段。</summary>
public sealed class EndWallJoinResult
{
    public bool   Success;
    public string Error = "";
    public readonly List<List<(double X, double Y, double Z)>> Clipped = new();
    public readonly List<List<(double X, double Y, double Z)>> Connectors = new();
    public int SourceCount;
    public int ClippedLineCount;
    public int JoinCount;
}

/// <summary>
/// 端帮衔接（忠实原 <c>EndWallJoiner</c>；Kylin 端帮线来源改为调用方传入的多段线几何，排除集由调用方过滤）：
///  ① 端帮线来源 = 视图多段线（排除本功能自身输出与对话框已拾取实体）；
///  ② 模板位置边界裁剪：点在模板带内 ⟺ 能投影到某工作线且 a0 ∈ [前界 advance, 该线台阶爬升最远 a0]（再被控制线/边界线裁一刀）；
///     跨边界的线在穿越处二分切断只留模板外侧段；整条在带内 = 被取代；整条在外 = 不相干；
///  ③ 高程衔接：每个裁剪端点在模板台阶线端点里找高差 ≤ zTol 且平距 ≤ joinRadius 的最近者，补一段衔接线。
/// </summary>
public static class EndWallJoiner
{
    public static EndWallJoinResult Run(
        IReadOnlyList<(double[] Xyz, bool Closed)> sources,
        IReadOnlyList<WorkLineSamples> wls, double advance, BenchTemplateResult benches,
        double zTol, double joinRadius, IReadOnlyList<(double[] Xyz, int Line)>? boundaries = null)
    {
        var r = new EndWallJoinResult();
        if (sources == null || wls == null || wls.Count == 0 || benches == null || benches.Benches.Count == 0)
        { r.Error = "无端帮线来源/工作线/模板台阶"; return r; }

        var proj = WorkLineProjector.Build(wls, 5.0);
        if (!proj.HasAny) { r.Error = "工作线投影器为空"; return r; }

        ComputeCentroids(wls, out var lineCx, out var lineCy, out double refCx, out double refCy);
        var bList = new List<(double[] Xyz, int Line, int Side)>();
        if (boundaries != null)
            foreach (var b in boundaries)
            {
                if (b.Xyz == null || b.Xyz.Length < 6) continue;
                double rx = (b.Line >= 0 && b.Line < lineCx.Length) ? lineCx[b.Line] : refCx;
                double ry = (b.Line >= 0 && b.Line < lineCy.Length) ? lineCy[b.Line] : refCy;
                int s0 = SideOf(b.Xyz, rx, ry);
                if (s0 == 0) continue;
                bList.Add((b.Xyz, b.Line, s0));
            }

        var walkMax = new double[wls.Count];
        for (int l = 0; l < walkMax.Length; l++) walkMax[l] = advance;
        foreach (var bl in benches.Benches)
            foreach (var line in new[] { bl.Toe, bl.Crest })
                foreach (var pt in line)
                    if (proj.TryProject(pt.X, pt.Y, out double a0, out _, out int ln) && ln >= 0 && ln < walkMax.Length && a0 > walkMax[ln]) walkMax[ln] = a0;

        const double MARGIN = 5.0;
        bool Inside(double x, double y)
        {
            if (!(proj.TryProject(x, y, out double a0, out _, out int ln) && ln >= 0 && ln < walkMax.Length
                  && a0 >= advance - MARGIN && a0 <= walkMax[ln] + MARGIN)) return false;
            foreach (var b in bList)
            {
                if (b.Line >= 0 && b.Line != ln) continue;
                if (SideOf(b.Xyz, x, y) != b.Side) return false;
            }
            return true;
        }

        var benchEnds = new List<(double X, double Y, double Z)>();
        foreach (var bl in benches.Benches)
            foreach (var line in new[] { bl.Toe, bl.Crest })
            { if (line.Count < 2) continue; benchEnds.Add(line[0]); benchEnds.Add(line[line.Count - 1]); }

        void TryJoin((double X, double Y, double Z) cut)
        {
            double best = double.MaxValue; int bi = -1;
            for (int i = 0; i < benchEnds.Count; i++)
            {
                var e = benchEnds[i];
                double dz = Math.Abs(e.Z - cut.Z);
                if (dz > zTol) continue;
                double dd = Math.Sqrt((e.X - cut.X) * (e.X - cut.X) + (e.Y - cut.Y) * (e.Y - cut.Y));
                if (dd > joinRadius) continue;
                double score = dd + 10 * dz;
                if (score < best) { best = score; bi = i; }
            }
            if (bi < 0) return;
            r.Connectors.Add(new List<(double, double, double)> { cut, benchEnds[bi] });
            r.JoinCount++;
        }

        foreach (var (xyz, _) in sources)
        {
            if (xyz == null || xyz.Length < 6) continue;
            int n = xyz.Length / 3;
            r.SourceCount++;
            var ins = new bool[n]; bool anyIn = false, anyOut = false;
            for (int i = 0; i < n; i++) { ins[i] = Inside(xyz[i * 3], xyz[i * 3 + 1]); anyIn |= ins[i]; anyOut |= !ins[i]; }
            if (!anyIn) continue;
            r.ClippedLineCount++;
            if (!anyOut) continue;

            (double X, double Y, double Z) P(int i) => (xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);
            (double X, double Y, double Z) Cross(int i0, int i1)
            {
                var a = P(i0); var b = P(i1);
                bool ia = ins[i0];
                for (int it = 0; it < 14; it++)
                {
                    var m = ((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
                    if (Inside(m.Item1, m.Item2) == ia) a = m; else b = m;
                }
                return ((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            }

            var run = new List<(double X, double Y, double Z)>();
            bool startCut = false;
            void Flush(bool endCut)
            {
                if (run.Count >= 2)
                {
                    r.Clipped.Add(new List<(double, double, double)>(run));
                    if (startCut) TryJoin(run[0]);
                    if (endCut) TryJoin(run[run.Count - 1]);
                }
                run.Clear(); startCut = false;
            }
            for (int i = 0; i < n; i++)
            {
                if (!ins[i])
                {
                    if (run.Count == 0 && i > 0 && ins[i - 1]) { run.Add(Cross(i - 1, i)); startCut = true; }
                    run.Add(P(i));
                }
                else if (run.Count > 0) { run.Add(Cross(i - 1, i)); Flush(endCut: true); }
            }
            Flush(endCut: false);
        }

        r.Success = true;
        return r;
    }

    /// <summary>把新生成的模板台阶线严格按控制线/边界线裁剪：只保留边界的模板侧（最长连续段），穿越处二分切在边界线上。</summary>
    public static void ClipBenchesByBoundaries(BenchTemplateResult benches, IReadOnlyList<(double[] Xyz, int Line)> boundaries, IReadOnlyList<WorkLineSamples> wls)
    {
        if (benches == null || boundaries == null || boundaries.Count == 0 || wls == null) return;
        ComputeCentroids(wls, out var lineCx, out var lineCy, out double refCx, out double refCy);
        foreach (var bl in benches.Benches)
        {
            foreach (var b in boundaries)
            {
                if (b.Xyz == null || b.Xyz.Length < 6) continue;
                if (b.Line >= 0 && b.Line != bl.LineIndex) continue;
                double rx = (bl.LineIndex >= 0 && bl.LineIndex < lineCx.Length) ? lineCx[bl.LineIndex] : refCx;
                double ry = (bl.LineIndex >= 0 && bl.LineIndex < lineCy.Length) ? lineCy[bl.LineIndex] : refCy;
                int side = SideOf(b.Xyz, rx, ry);
                if (side == 0) continue;
                ClipKeepSide(bl.Toe, b.Xyz, side);
                ClipKeepSide(bl.Crest, b.Xyz, side);
            }
        }
        benches.Benches.RemoveAll(bl => bl.Toe.Count < 2 || bl.Crest.Count < 2);
        benches.CoalCount = 0; benches.RockCount = 0;
        foreach (var bl in benches.Benches) { if (bl.IsCoal) benches.CoalCount++; else benches.RockCount++; }
    }

    private static void ClipKeepSide(List<(double X, double Y, double Z)> pts, double[] bxyz, int side)
    {
        if (pts.Count < 2) return;
        var ins = new bool[pts.Count];
        bool anyOut = false;
        for (int i = 0; i < pts.Count; i++) { ins[i] = SideOf(bxyz, pts[i].X, pts[i].Y) == side; anyOut |= !ins[i]; }
        if (!anyOut) return;

        (double X, double Y, double Z) Cross(int i0, int i1)
        {
            var a = pts[i0]; var b = pts[i1]; bool ia = ins[i0];
            for (int it = 0; it < 14; it++)
            {
                (double X, double Y, double Z) m = ((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
                if ((SideOf(bxyz, m.X, m.Y) == side) == ia) a = m; else b = m;
            }
            return ((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
        }

        var runs = new List<List<(double X, double Y, double Z)>>();
        var cur = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (ins[i])
            {
                if (cur.Count == 0 && i > 0 && !ins[i - 1]) cur.Add(Cross(i - 1, i));
                cur.Add(pts[i]);
            }
            else if (cur.Count > 0) { cur.Add(Cross(i - 1, i)); runs.Add(cur); cur = new List<(double X, double Y, double Z)>(); }
        }
        if (cur.Count > 0) runs.Add(cur);
        pts.Clear();
        if (runs.Count == 0) return;
        var best = runs[0];
        foreach (var r2 in runs) if (r2.Count > best.Count) best = r2;
        pts.AddRange(best);
    }

    private static void ComputeCentroids(IReadOnlyList<WorkLineSamples> wls, out double[] lineCx, out double[] lineCy, out double refCx, out double refCy)
    {
        int L = wls.Count;
        lineCx = new double[L]; lineCy = new double[L];
        double gx = 0, gy = 0; int gn = 0;
        for (int li = 0; li < L; li++)
        {
            var wl = wls[li];
            double sx = 0, sy = 0; int c = 0;
            if (wl?.Baseline != null) foreach (var b0 in wl.Baseline) { sx += b0.X; sy += b0.Y; c++; gx += b0.X; gy += b0.Y; gn++; }
            if (c > 0) { lineCx[li] = sx / c; lineCy[li] = sy / c; }
        }
        refCx = gn > 0 ? gx / gn : 0; refCy = gn > 0 ? gy / gn : 0;
    }

    /// <summary>点在多段线（平面投影）哪一侧：取最近段的叉积符号（+1/−1；0=在线上/退化）。</summary>
    public static int SideOf(double[] xyz, double x, double y)
    {
        int n = xyz.Length / 3;
        double best = double.MaxValue, cr = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            double ax = xyz[i * 3], ay = xyz[i * 3 + 1];
            double bx = xyz[(i + 1) * 3], by = xyz[(i + 1) * 3 + 1];
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy; if (len2 < 1e-12) continue;
            double t = ((x - ax) * dx + (y - ay) * dy) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double px = ax + t * dx, py = ay + t * dy;
            double d2 = (x - px) * (x - px) + (y - py) * (y - py);
            if (d2 < best) { best = d2; cr = dx * (y - ay) - dy * (x - ax); }
        }
        return cr > 1e-9 ? 1 : (cr < -1e-9 ? -1 : 0);
    }
}
