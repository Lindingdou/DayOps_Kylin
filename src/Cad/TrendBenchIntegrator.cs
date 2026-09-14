using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>可控形态扩展结果：各级线（含 level0 = 基线），每级一条。</summary>
public sealed class FormExpandResult
{
    public bool   Success;
    public string Error = "";
    public int    LevelCount;
    public double MaxRetreat;
    public readonly List<List<(double X, double Y, double Z)>> Levels = new();
}

/// <summary>
/// 可控形态扩展（忠实原 <c>BenchFormExpander</c>）：把工作线（趋势形态）放到给定的各真实平盘标高上，
/// 逐级按真实标高差放坡退距（retreat 累积 = Σ(Δz/tanα + W)），λ∈[0,1] 控制展开（0 = 各级收拢于基线，1 = 完整放坡形态）。
/// 形状来自趋势（可控），标高来自现状真值，退距可控（α/W/λ）。放坡退距口径与 <see cref="TemplateDrivingEngine.BenchOffset"/> 同源。
/// </summary>
public static class BenchFormExpander
{
    public static FormExpandResult ExpandOnLevels(WorkLineSamples wl, double[] levelZs, double alphaDeg, double bermWidth, double lambda)
    {
        var r = new FormExpandResult();
        if (wl == null || !wl.Success || wl.Baseline.Count < 2 || wl.Samples.Count < 1) { r.Error = "工作线几何无效"; return r; }
        if (levelZs == null || levelZs.Length == 0) { r.Error = "没有平盘标高(现状台阶为空?)"; return r; }
        if (lambda < 0) lambda = 0;
        double tanA = Math.Tan(Math.Max(1.0, Math.Min(89.0, alphaDeg)) * Math.PI / 180.0);
        int nSeg = Math.Min(wl.Samples.Count, wl.Baseline.Count - 1 + (wl.Closed ? 1 : 0));
        if (nSeg < 1) { r.Error = "工作线段数 < 1"; return r; }
        var dirX = new double[nSeg]; var dirY = new double[nSeg];
        for (int i = 0; i < nSeg; i++)
        {
            double dx = wl.Samples[i].Dx, dy = wl.Samples[i].Dy;
            double dl = Math.Sqrt(dx * dx + dy * dy); if (dl < 1e-9) { dx = 1; dy = 0; dl = 1; }
            dirX[i] = dx / dl; dirY[i] = dy / dl;
        }
        var vDirX = new double[wl.Baseline.Count]; var vDirY = new double[wl.Baseline.Count];
        for (int vi = 0; vi < wl.Baseline.Count; vi++)
        {
            double ax = 0, ay = 0; int cnt = 0;
            if (vi - 1 >= 0 && vi - 1 < nSeg) { ax += dirX[vi - 1]; ay += dirY[vi - 1]; cnt++; }
            if (vi < nSeg) { ax += dirX[vi]; ay += dirY[vi]; cnt++; }
            if (cnt == 0) { ax = dirX[0]; ay = dirY[0]; }
            double al = Math.Sqrt(ax * ax + ay * ay); if (al < 1e-9) al = 1;
            vDirX[vi] = ax / al; vDirY[vi] = ay / al;
        }
        double cumRetreat = 0;
        for (int k = 0; k < levelZs.Length; k++)
        {
            if (k > 0) cumRetreat += Math.Abs(levelZs[k - 1] - levelZs[k]) / tanA + bermWidth;
            double retreat = lambda * cumRetreat;
            var line = new List<(double X, double Y, double Z)>(wl.Baseline.Count);
            for (int vi = 0; vi < wl.Baseline.Count; vi++)
            {
                var p = wl.Baseline[vi];
                line.Add((p.X + retreat * vDirX[vi], p.Y + retreat * vDirY[vi], levelZs[k]));
            }
            r.Levels.Add(line);
        }
        r.LevelCount = r.Levels.Count;
        r.MaxRetreat = lambda * cumRetreat;
        r.Success = true;
        return r;
    }
}

public sealed class IntegratedBench
{
    public double Elevation;
    public double Station;
    public int    SourceCount;
    public readonly List<(double X, double Y, double Z)> Line = new();
}

public sealed class TrendIntegrateResult
{
    public bool   Success;
    public string Error = "";
    public int    CrossingCount;
    public readonly List<IntegratedBench> Benches = new();
}

/// <summary>
/// 趋势整合现状台阶（忠实原 <c>TrendBenchIntegrator</c>）：用一条趋势线，沿其方向把现状台阶线串起来整合。
/// 趋势线 ∩ 各台阶线（2D 求交）→ 交点带真实标高（整条台阶线的代表标高 = 平盘水平）→ 按标高聚级 → 每级把源台阶线段断头接平、压平到 z_k。
/// 高程与分布都来自现状台阶本身（真值），不放坡造假。带宽是唯一旋钮。
/// </summary>
public static class TrendBenchIntegrator
{
    private struct Crossing { public double Station; public double Z; public int BenchIdx; }

    /// <summary>从现状台阶线提取平盘标高序列（按代表标高聚级，降序）。全视图聚级，不看趋势。</summary>
    public static double[] ExtractPlatformLevels(IReadOnlyList<double[]> benchLines, double bandwidth) => ClusterDescending(CollectRepZ(benchLines, null), bandwidth);

    /// <summary>沿趋势找平盘：只用趋势线穿过的台阶线定平盘标高，降序。</summary>
    public static double[] ExtractPlatformLevelsAlongTrend(IReadOnlyList<(double X, double Y, double Z)> trend, IReadOnlyList<double[]> benchLines, double bandwidth)
        => ClusterDescending(CollectRepZ(benchLines, trend), bandwidth);

    private static List<double> CollectRepZ(IReadOnlyList<double[]> benchLines, IReadOnlyList<(double X, double Y, double Z)>? trend)
    {
        var zs = new List<double>();
        if (benchLines == null) return zs;
        foreach (var b in benchLines)
        {
            if (b == null || b.Length < 6) continue;
            if (trend != null && !CrossesTrend(trend, b)) continue;
            var v = new List<double>(b.Length / 3);
            for (int p = 2; p < b.Length; p += 3) v.Add(b[p]);
            v.Sort();
            zs.Add(v[v.Count / 2]);
        }
        return zs;
    }

    private static double[] ClusterDescending(List<double> zs, double bandwidth)
    {
        if (zs == null || zs.Count == 0) return Array.Empty<double>();
        if (bandwidth <= 0) bandwidth = 1;
        zs.Sort();
        var levels = new List<double>();
        var cur = new List<double> { zs[0] };
        for (int i = 1; i < zs.Count; i++)
        {
            if (zs[i] - cur[^1] > bandwidth) { levels.Add(cur[cur.Count / 2]); cur = new List<double>(); }
            cur.Add(zs[i]);
        }
        levels.Add(cur[cur.Count / 2]);
        levels.Sort((a, b) => b.CompareTo(a));
        return levels.ToArray();
    }

    private static bool CrossesTrend(IReadOnlyList<(double X, double Y, double Z)> trend, double[] b)
    {
        int bpts = b.Length / 3;
        for (int ti = 0; ti + 1 < trend.Count; ti++)
        {
            double ax = trend[ti].X, ay = trend[ti].Y, bx = trend[ti + 1].X, by = trend[ti + 1].Y;
            for (int s = 0; s + 1 < bpts; s++)
                if (SegInt(ax, ay, bx, by, b[s * 3], b[s * 3 + 1], b[(s + 1) * 3], b[(s + 1) * 3 + 1], out _, out _)) return true;
        }
        return false;
    }

    public static TrendIntegrateResult Integrate(IReadOnlyList<(double X, double Y, double Z)> trend, IReadOnlyList<double[]> benchLines, double bandwidth)
    {
        var r = new TrendIntegrateResult();
        if (trend == null || trend.Count < 2) { r.Error = "趋势线无效（<2 点）"; return r; }
        if (benchLines == null || benchLines.Count == 0) { r.Error = "没有现状台阶线（图层为空？）"; return r; }
        if (bandwidth <= 0) bandwidth = 1;
        int tn = trend.Count;
        var station = new double[tn];
        for (int i = 1; i < tn; i++) { double dx = trend[i].X - trend[i - 1].X, dy = trend[i].Y - trend[i - 1].Y; station[i] = station[i - 1] + Math.Sqrt(dx * dx + dy * dy); }
        var benchZ = new double[benchLines.Count];
        for (int bi = 0; bi < benchLines.Count; bi++)
        {
            var b = benchLines[bi];
            if (b == null || b.Length < 6) { benchZ[bi] = double.NaN; continue; }
            var zs = new List<double>(b.Length / 3);
            for (int p = 2; p < b.Length; p += 3) zs.Add(b[p]);
            zs.Sort();
            benchZ[bi] = zs[zs.Count / 2];
        }
        var crossings = new List<Crossing>();
        for (int bi = 0; bi < benchLines.Count; bi++)
        {
            var b = benchLines[bi];
            if (b == null || b.Length < 6) continue;
            int bpts = b.Length / 3;
            for (int ti = 0; ti + 1 < tn; ti++)
            {
                double ax = trend[ti].X, ay = trend[ti].Y, bx = trend[ti + 1].X, by = trend[ti + 1].Y;
                double segLen = station[ti + 1] - station[ti];
                for (int s = 0; s + 1 < bpts; s++)
                    if (SegInt(ax, ay, bx, by, b[s * 3], b[s * 3 + 1], b[(s + 1) * 3], b[(s + 1) * 3 + 1], out double t, out _))
                        crossings.Add(new Crossing { Station = station[ti] + t * segLen, Z = benchZ[bi], BenchIdx = bi });
            }
        }
        r.CrossingCount = crossings.Count;
        if (crossings.Count == 0) { r.Error = "趋势线没穿过任何台阶线（方向/位置不对？）"; return r; }
        crossings.Sort((p, q) => p.Z.CompareTo(q.Z));
        var groups = new List<List<Crossing>>();
        var cur = new List<Crossing> { crossings[0] };
        for (int i = 1; i < crossings.Count; i++)
        {
            if (crossings[i].Z - cur[^1].Z > bandwidth) { groups.Add(cur); cur = new List<Crossing>(); }
            cur.Add(crossings[i]);
        }
        groups.Add(cur);
        foreach (var g in groups)
        {
            var zs = g.Select(c => c.Z).OrderBy(z => z).ToList();
            double zk = zs[zs.Count / 2];
            double st = g.Select(c => c.Station).OrderBy(s => s).ToList()[g.Count / 2];
            var benchIdx = g.Select(c => c.BenchIdx).Distinct().ToList();
            var pieces = new List<List<(double X, double Y, double Z)>>();
            foreach (int bi in benchIdx)
            {
                var b = benchLines[bi];
                var piece = new List<(double X, double Y, double Z)>(b.Length / 3);
                for (int p = 0; p + 2 < b.Length; p += 3) piece.Add((b[p], b[p + 1], zk));
                if (piece.Count >= 2) pieces.Add(piece);
            }
            var line = ChainPieces(pieces);
            if (line.Count < 2) continue;
            var ib = new IntegratedBench { Elevation = zk, Station = st, SourceCount = benchIdx.Count };
            ib.Line.AddRange(line);
            r.Benches.Add(ib);
        }
        r.Benches.Sort((a, b) => a.Station.CompareTo(b.Station));
        r.Success = r.Benches.Count > 0;
        if (!r.Success) r.Error = "聚级后无有效台阶线";
        return r;
    }

    private static bool SegInt(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, out double t, out double u)
    {
        t = u = 0;
        double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-9) return false;
        double qpx = cx - ax, qpy = cy - ay;
        t = (qpx * sy - qpy * sx) / denom;
        u = (qpx * ry - qpy * rx) / denom;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    private static List<(double X, double Y, double Z)> ChainPieces(List<List<(double X, double Y, double Z)>> pieces)
    {
        var result = new List<(double X, double Y, double Z)>();
        if (pieces.Count == 0) return result;
        var remaining = new List<List<(double X, double Y, double Z)>>(pieces);
        result.AddRange(remaining[0]); remaining.RemoveAt(0);
        while (remaining.Count > 0)
        {
            var end = result[^1];
            int best = -1; bool flip = false; double bestD = double.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                var p = remaining[i];
                double d0 = Dist2(end, p[0]), d1 = Dist2(end, p[^1]);
                if (d0 < bestD) { bestD = d0; best = i; flip = false; }
                if (d1 < bestD) { bestD = d1; best = i; flip = true; }
            }
            var piece = remaining[best]; remaining.RemoveAt(best);
            if (flip) piece.Reverse();
            result.AddRange(piece);
        }
        return result;
    }

    private static double Dist2((double X, double Y, double Z) a, (double X, double Y, double Z) b) { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
}
