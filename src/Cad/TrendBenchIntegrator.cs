using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 趋势整合现状台阶 —— 忠实移植原 MineAssLib.Driving.TrendBenchIntegrator。
/// 用一条趋势线, 沿其方向把现状台阶线(坡顶/坡底)串起来整合: 趋势线 ∩ 各台阶线(2D 求交)→
/// 交点带真实标高(取整条台阶线代表标高=平盘水平)→ 按标高聚级(基准 z_k)→ 每级把源台阶线段
/// 压平到 z_k、断头接平成一条规整线。高程与分布都来自现状台阶本身(真值), 不放坡造假。
/// 纯几何、确定性(带宽是唯一旋钮)、可单测。趋势线只取 XY 求交(Z 不用)。
/// </summary>
public sealed class IntegratedBench
{
    public double Elevation;      // 基准水平 z_k
    public double Station;        // 沿趋势的站号(排序用)
    public int SourceCount;       // 整合了几条源台阶段
    public readonly List<(double X, double Y, double Z)> Line = new();   // 规整后的台阶线(Z=z_k)
}

public sealed class TrendIntegrateResult
{
    public bool Success;
    public string Error = "";
    public int CrossingCount;
    public readonly List<IntegratedBench> Benches = new();   // 按 Station 排序
}

public static class TrendBenchIntegrator
{
    private struct Crossing { public double Station; public double Z; public int BenchIdx; }

    /// <summary>从现状台阶线提取平盘标高序列(按代表标高聚级, 降序, 顶在前)。全视图聚级, 不看趋势。</summary>
    public static double[] ExtractPlatformLevels(IReadOnlyList<double[]> benchLines, double bandwidth)
        => ClusterDescending(CollectRepZ(benchLines, null), bandwidth);

    /// <summary>沿趋势找平盘: 只用趋势线穿过的台阶线定平盘标高, 降序。</summary>
    public static double[] ExtractPlatformLevelsAlongTrend(
        IReadOnlyList<(double X, double Y, double Z)> trend,
        IReadOnlyList<double[]> benchLines, double bandwidth)
        => ClusterDescending(CollectRepZ(benchLines, trend), bandwidth);

    private static List<double> CollectRepZ(
        IReadOnlyList<double[]> benchLines, IReadOnlyList<(double X, double Y, double Z)>? trend)
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
            zs.Add(v[v.Count / 2]);            // 整条代表标高 = 平盘水平
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
        levels.Sort((a, b) => b.CompareTo(a));   // 降序: 顶平盘在前
        return levels.ToArray();
    }

    private static bool CrossesTrend(IReadOnlyList<(double X, double Y, double Z)> trend, double[] b)
    {
        int bpts = b.Length / 3;
        for (int ti = 0; ti + 1 < trend.Count; ti++)
        {
            double ax = trend[ti].X, ay = trend[ti].Y, bx = trend[ti + 1].X, by = trend[ti + 1].Y;
            for (int s = 0; s + 1 < bpts; s++)
            {
                double cx = b[s * 3], cy = b[s * 3 + 1];
                double dx = b[(s + 1) * 3], dy = b[(s + 1) * 3 + 1];
                if (SegInt(ax, ay, bx, by, cx, cy, dx, dy, out _, out _)) return true;
            }
        }
        return false;
    }

    /// <param name="trend">趋势线(取 XY 求交, Z 不用)。</param>
    /// <param name="benchLines">现状台阶线, 每条 = 扁平 [x0,y0,z0,x1,y1,z1,…]。</param>
    /// <param name="bandwidth">高程聚级带宽(≈台阶高/2, 相邻台阶不并级)。</param>
    public static TrendIntegrateResult Integrate(
        IReadOnlyList<(double X, double Y, double Z)> trend,
        IReadOnlyList<double[]> benchLines,
        double bandwidth)
    {
        var r = new TrendIntegrateResult();
        if (trend == null || trend.Count < 2) { r.Error = "趋势线无效(<2 点)"; return r; }
        if (benchLines == null || benchLines.Count == 0) { r.Error = "没有现状台阶线(图层为空?)"; return r; }
        if (bandwidth <= 0) bandwidth = 1;

        int tn = trend.Count;
        var station = new double[tn];
        for (int i = 1; i < tn; i++)
        {
            double dx = trend[i].X - trend[i - 1].X, dy = trend[i].Y - trend[i - 1].Y;
            station[i] = station[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        // 每条台阶线代表标高(中位数)= 平盘水平(台阶线≈等高线, 整条代表比交点一点更稳)。
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
                {
                    double cx = b[s * 3], cy = b[s * 3 + 1];
                    double dx = b[(s + 1) * 3], dy = b[(s + 1) * 3 + 1];
                    if (SegInt(ax, ay, bx, by, cx, cy, dx, dy, out double t, out _))
                        crossings.Add(new Crossing { Station = station[ti] + t * segLen, Z = benchZ[bi], BenchIdx = bi });
                }
            }
        }
        r.CrossingCount = crossings.Count;
        if (crossings.Count == 0) { r.Error = "趋势线没穿过任何台阶线(方向/位置不对?)"; return r; }

        // 按标高聚级(1D 凝聚: Z 升序, 间隔 > 带宽即新级)
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
            double zk = zs[zs.Count / 2];                          // 基准水平 = 中位数
            double st = g.Select(c => c.Station).OrderBy(s => s).ToList()[g.Count / 2];
            var benchIdx = g.Select(c => c.BenchIdx).Distinct().ToList();

            var pieces = new List<List<(double X, double Y, double Z)>>();
            foreach (int bi in benchIdx)
            {
                var b = benchLines[bi];
                var piece = new List<(double X, double Y, double Z)>(b.Length / 3);
                for (int p = 0; p + 2 < b.Length; p += 3) piece.Add((b[p], b[p + 1], zk));   // 压平到基准水平
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

    // 2D 线段求交: 返回 t(在 AB 上)、u(在 CD 上), 均在 [0,1] 才算相交。
    private static bool SegInt(double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy, out double t, out double u)
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
                double d0 = Dist2(end, p[0]);
                double d1 = Dist2(end, p[^1]);
                if (d0 < bestD) { bestD = d0; best = i; flip = false; }
                if (d1 < bestD) { bestD = d1; best = i; flip = true; }
            }
            var piece = remaining[best]; remaining.RemoveAt(best);
            if (flip) piece.Reverse();
            result.AddRange(piece);
        }
        return result;
    }

    private static double Dist2((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
}
