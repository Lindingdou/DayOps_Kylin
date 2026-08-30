using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 确定可采区域（忠实移植原 <c>PlanLib.ShortTerm.MineableAreaIdentifier</c>）——
/// 在煤层底板三角网上找采煤台阶(质心处底板标高与自身平盘标高最接近者)、算可采面积,
/// 并对上覆各级台阶算需揭露的后退量(逐级步长 = 平盘宽 + 坡面水平投影 H/tanα)。纯几何、可单测。
/// </summary>
public static class MineableAreaIdentifier
{
    /// <summary>一条台阶线：扁平世界坐标 [x0,y0,z0,...] + 派生量(代表标高=Z均值, XY质心)。</summary>
    public sealed class BenchLine
    {
        public double[] Xyz = Array.Empty<double>();
        public bool Closed;
        public double RepZ;
        public double Cx, Cy;

        public static BenchLine From(double[] xyz, bool closed)
        {
            var b = new BenchLine { Xyz = xyz, Closed = closed };
            int n = xyz.Length / 3;
            if (n > 0)
            {
                double sx = 0, sy = 0, sz = 0;
                for (int i = 0; i < n; i++) { sx += xyz[i * 3]; sy += xyz[i * 3 + 1]; sz += xyz[i * 3 + 2]; }
                b.Cx = sx / n; b.Cy = sy / n; b.RepZ = sz / n;
            }
            return b;
        }
    }

    public sealed class OverburdenReport
    {
        public double Z;
        public double CurrentSetbackM;
        public double TargetSetbackM;
        public double StripBackM;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public BenchLine? CoalBench;
        public double CoalFloorZ;
        public double FloorMatchDz;
        public double MineableAreaM2;
        public List<OverburdenReport> Overburden = new();
    }

    public static Result Identify(
        double[] seamFloorVerts, int[] seamFloorTris,
        IReadOnlyList<BenchLine> benches,
        double wMin, double benchH, double faceAngleDeg, double bermW)
    {
        var res = new Result();
        if (benches == null || benches.Count == 0) { res.Message = "未提供台阶线。"; return res; }
        if (seamFloorVerts == null || seamFloorVerts.Length < 9 || seamFloorTris == null || seamFloorTris.Length < 3)
        { res.Message = "未提供有效煤层底板面。"; return res; }

        // ① 找采煤台阶
        BenchLine? coal = null;
        double bestDz = double.MaxValue, coalZ = 0;
        foreach (var b in benches)
        {
            double? fz = SampleMeshZ(seamFloorVerts, seamFloorTris, b.Cx, b.Cy);
            if (fz is not double z) continue;
            double dz = Math.Abs(b.RepZ - z);
            if (dz < bestDz) { bestDz = dz; coal = b; coalZ = z; }
        }
        if (coal == null) { res.Message = "台阶线 XY 范围内采样不到煤层底板面。"; return res; }

        res.Ok = true;
        res.CoalBench = coal;
        res.CoalFloorZ = coalZ;
        res.FloorMatchDz = bestDz;
        res.MineableAreaM2 = coal.Closed ? Math.Abs(PolygonAreaXY(coal.Xyz)) : 0;

        // ② 上覆各级后退量
        double benchRun = bermW + (faceAngleDeg > 0 && faceAngleDeg < 90
            ? benchH / Math.Tan(faceAngleDeg * Math.PI / 180.0) : 0);
        var over = benches.Where(b => b != coal && b.RepZ > coal.RepZ + 0.5)
                          .OrderBy(b => b.RepZ).ToList();
        for (int k = 0; k < over.Count; k++)
        {
            var b = over[k];
            double cur = MinRingDistanceXY(coal.Xyz, b.Xyz);
            double target = wMin + k * benchRun;
            res.Overburden.Add(new OverburdenReport
            {
                Z = b.RepZ, CurrentSetbackM = cur, TargetSetbackM = target,
                StripBackM = Math.Max(0, target - cur)
            });
        }

        res.Message = $"采煤台阶：Z≈{coalZ:0.0}m(与底板差 {bestDz:0.0}m)"
                    + (coal.Closed ? $", 可采面积≈{res.MineableAreaM2 / 1e4:0.0}ha" : "(采煤台阶线未闭合)")
                    + $"; 上覆 {over.Count} 级待揭露。";
        return res;
    }

    /// <summary>2.5D 网格 Z 采样：XY 投影点落在哪个三角内 → 重心插值 Z。取首个命中。</summary>
    public static double? SampleMeshZ(double[] v, int[] tris, double x, double y)
    {
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t] * 3, b = tris[t + 1] * 3, c = tris[t + 2] * 3;
            if (a < 0 || c + 2 >= v.Length) continue;
            double ax = v[a], ay = v[a + 1], az = v[a + 2];
            double bx = v[b], by = v[b + 1], bz = v[b + 2];
            double cx = v[c], cy = v[c + 1], cz = v[c + 2];
            double d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(d) < 1e-12) continue;
            double w0 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
            double w1 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
            double w2 = 1.0 - w0 - w1;
            const double eps = -1e-6;
            if (w0 >= eps && w1 >= eps && w2 >= eps)
                return w0 * az + w1 * bz + w2 * cz;
        }
        return null;
    }

    /// <summary>两环最近点 XY 距(逐顶点对)。</summary>
    public static double MinRingDistanceXY(double[] a, double[] b)
    {
        double best = double.MaxValue;
        int na = a.Length / 3, nb = b.Length / 3;
        for (int i = 0; i < na; i++)
        {
            double ax = a[i * 3], ay = a[i * 3 + 1];
            for (int j = 0; j < nb; j++)
            {
                double dx = ax - b[j * 3], dy = ay - b[j * 3 + 1];
                double d2 = dx * dx + dy * dy;
                if (d2 < best) best = d2;
            }
        }
        return best < double.MaxValue ? Math.Sqrt(best) : 0;
    }

    /// <summary>闭合环 XY 投影面积(鞋带公式)。</summary>
    public static double PolygonAreaXY(double[] ring)
    {
        int n = ring.Length / 3;
        if (n < 3) return 0;
        double area = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            area += ring[i * 3] * ring[j * 3 + 1] - ring[j * 3] * ring[i * 3 + 1];
        }
        return area * 0.5;
    }
}
