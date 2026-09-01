using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「更新煤层面/现状面」核心：用观测点(现状见煤点/补勘顶底板)局部更新目标三角网的顶点 Z。
/// 自动选区 = 观测点影响半径 R 内的顶点(区外不动)，按到最近观测点的距离 smoothstep 羽化(内 1→边界 0)；
/// 区内目标值 = 观测点 IDW 估值；newZ = vz + w·(est − vz)。附影响片区(影响圈搭接=距离&lt;2R 并查集归片)
/// + 位移/面积/净体积统计。忠实原 MeshEditLib.SurfaceUpdateEngine.Evaluate 的默认(IDW)路径。
/// （原支持 NN/MA/IDW/OK/SK/UK 多算法, 此移默认 IDW——与 Kylin Estimation 同为 IDW 基; 克里金变体待后续。
/// 原三维分级色带 overlay 走 native PMBI, 2D 场景受阻, 未移；此出更新后 Z + 数值统计。）纯逻辑、可单测。
/// </summary>
public static class SurfaceUpdate
{
    public sealed class Options
    {
        public double InfluenceRadius = 80.0;   // 影响半径(m)：自动选区 + 羽化尺度
        public int MaxSamples = 16;
        public double IdwPower = 2.0;
    }

    /// <summary>影响片区(影响圈搭接=距离&lt;2R 的观测点并为一片)。</summary>
    public sealed class ClusterStat
    {
        public double Cx, Cy;    // 观测点质心
        public int ObsCount;
        public int VertCount;
        public double MaxAbs;    // 最大|位移|
    }

    public sealed class Result
    {
        public double[] NewZ = Array.Empty<double>();    // 每顶点新 Z(未受影响=原 Z)
        public double[] Disp = Array.Empty<double>();    // 位移(newZ-oldZ)
        public bool[] Affected = Array.Empty<bool>();
        public int AffectedVertices;
        public double MaxAbsDisp, MeanAbsDisp;
        public double MaxDisp, MinDisp;                  // 最大抬升(+)/最大下沉(-)
        public double NetVolume;                         // 净体积 Σ位移×面积(m³,+=抬升)
        public double AffectedArea, TotalArea;
        public List<ClusterStat> Clusters = new();
        public string Message = "";
        public bool Any => AffectedVertices > 0;
    }

    public static Result Evaluate(double[] worldVerts, int[] tris,
        IReadOnlyList<(double x, double y, double z)> obs, Options opt)
    {
        int V = worldVerts.Length / 3;
        var res = new Result { NewZ = new double[V], Disp = new double[V], Affected = new bool[V] };
        for (int i = 0; i < V; i++) res.NewZ[i] = worldVerts[i * 3 + 2];
        if (obs == null || obs.Count == 0) { res.Message = "无观测点"; return res; }

        double R = Math.Max(1e-3, opt.InfluenceRadius);
        double R2 = R * R;

        for (int i = 0; i < V; i++)
        {
            double vx = worldVerts[i * 3], vy = worldVerts[i * 3 + 1], vz = worldVerts[i * 3 + 2];
            // 半径内最近的 MaxSamples 个观测点
            var near = new List<(double d, double z)>();
            foreach (var o in obs)
            {
                double dx = o.x - vx, dy = o.y - vy, d2 = dx * dx + dy * dy;
                if (d2 <= R2) near.Add((Math.Sqrt(d2), o.z));
            }
            if (near.Count == 0) continue;                       // 影响半径外 → 不动
            near.Sort((a, b) => a.d.CompareTo(b.d));
            if (near.Count > opt.MaxSamples) near = near.GetRange(0, opt.MaxSamples);

            double est = Idw(near, opt.IdwPower);
            double dNear = near[0].d;
            double t = Math.Clamp(1.0 - dNear / R, 0, 1);
            double w = t * t * (3 - 2 * t);                      // smoothstep 羽化
            if (w <= 1e-6) continue;

            double newZ = vz + w * (est - vz);
            res.NewZ[i] = newZ; res.Disp[i] = newZ - vz; res.Affected[i] = true;
        }

        // ── 统计 ──
        double sumAbs = 0, maxAbs = 0, maxUp = 0, maxDown = 0; int nAff = 0;
        for (int i = 0; i < V; i++)
        {
            if (!res.Affected[i]) continue;
            double dsp = res.Disp[i], a = Math.Abs(dsp);
            sumAbs += a; if (a > maxAbs) maxAbs = a;
            if (dsp > maxUp) maxUp = dsp; if (dsp < maxDown) maxDown = dsp;
            nAff++;
        }

        // ── 观测点分片区：影响圈搭接(距离<2R)并查集 ──
        int nObs = obs.Count;
        var parent = new int[nObs];
        for (int i = 0; i < nObs; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        for (int i = 0; i < nObs; i++)
            for (int j = i + 1; j < nObs; j++)
            {
                double ddx = obs[i].x - obs[j].x, ddy = obs[i].y - obs[j].y;
                if (ddx * ddx + ddy * ddy < 4 * R2) parent[Find(i)] = Find(j);
            }
        var rootToCluster = new Dictionary<int, int>();
        var obsCluster = new int[nObs];
        for (int i = 0; i < nObs; i++)
        {
            int r = Find(i);
            if (!rootToCluster.TryGetValue(r, out int c))
            { c = res.Clusters.Count; rootToCluster[r] = c; res.Clusters.Add(new ClusterStat()); }
            obsCluster[i] = c;
            var cs = res.Clusters[c];
            cs.ObsCount++; cs.Cx += obs[i].x; cs.Cy += obs[i].y;
        }
        foreach (var cs in res.Clusters) { cs.Cx /= cs.ObsCount; cs.Cy /= cs.ObsCount; }

        // 受影响顶点 → 最近观测点所在片区
        for (int i = 0; i < V; i++)
        {
            if (!res.Affected[i]) continue;
            double vx = worldVerts[i * 3], vy = worldVerts[i * 3 + 1];
            int best = -1; double bestD2 = double.MaxValue;
            for (int o = 0; o < nObs; o++)
            {
                double dx = obs[o].x - vx, dy = obs[o].y - vy, d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = o; }
            }
            if (best < 0) continue;
            var cs = res.Clusters[obsCluster[best]];
            cs.VertCount++;
            double a = Math.Abs(res.Disp[i]); if (a > cs.MaxAbs) cs.MaxAbs = a;
        }

        // ── 面积/净体积：受影响三角(任一角受影响)按三角平均位移(未受影响角=0)累加 ──
        double area = 0, totalArea = 0, netVol = 0;
        int F = tris.Length / 3;
        for (int f = 0; f < F; f++)
        {
            int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
            if (a < 0 || b < 0 || c < 0 || a >= V || b >= V || c >= V) continue;
            double triArea = TriAreaXY(worldVerts, a, b, c);
            totalArea += triArea;
            if (!res.Affected[a] && !res.Affected[b] && !res.Affected[c]) continue;
            area += triArea;
            netVol += triArea * (res.Disp[a] + res.Disp[b] + res.Disp[c]) / 3.0;
        }

        res.Clusters.RemoveAll(cs => cs.VertCount == 0);
        res.AffectedVertices = nAff;
        res.MaxAbsDisp = maxAbs;
        res.MeanAbsDisp = nAff > 0 ? sumAbs / nAff : 0;
        res.MaxDisp = maxUp; res.MinDisp = maxDown;
        res.AffectedArea = area; res.TotalArea = totalArea; res.NetVolume = netVol;
        res.Message = nAff > 0
            ? $"自动选区：受影响 {nAff} 顶点 / 面积 {area:F0} m²；位移 最大 {maxAbs:F2}m、平均 {res.MeanAbsDisp:F2}m"
            : "影响半径内无有效更新(观测点太远或不足)，可调大影响半径";
        return res;
    }

    /// <summary>反距离加权：Σ(z/dᵖ)/Σ(1/dᵖ)；命中观测点(d≈0)直接取其 z。</summary>
    private static double Idw(List<(double d, double z)> near, double power)
    {
        double num = 0, den = 0;
        foreach (var (d, z) in near)
        {
            if (d < 1e-9) return z;                    // 落在观测点上
            double wgt = 1.0 / Math.Pow(d, power);
            num += wgt * z; den += wgt;
        }
        return den > 1e-12 ? num / den : near[0].z;
    }

    private static double TriAreaXY(double[] v, int a, int b, int c)
    {
        double ax = v[a * 3], ay = v[a * 3 + 1], bx = v[b * 3], by = v[b * 3 + 1], cx = v[c * 3], cy = v[c * 3 + 1];
        return Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) * 0.5;
    }
}
