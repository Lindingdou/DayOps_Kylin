using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 渐进形态学滤波(PMF, Zhang 2003)——点级分地面/非地面(车辆/设备/植被/临时堆料)。
/// 栅格取每格最低点为初始面, 用【窗口渐增】的形态学开运算(先腐蚀 min 后膨胀 max)逐尺度削去物体:
/// 高程差 > 阈 dh_k = min(dhMax, dh0 + slope·Δwindow·cell) 的格降到开运算面(=去掉该尺度物体);
/// 阈随窗口增长, 故大地形坡面(缓变)保留、大而平的物体(突变)被削。末了逐点判: z − 地面Z > dhMax = 非地面。
/// 比 <see cref="GroundFilter"/>(每格最低点)稳健且【另出非地面点云】。标准算法(原走 native, 此托管重实现,
/// 与 native 具体实现可能不同)。纯逻辑、可单测。
/// </summary>
public static class ProgressiveMorphFilter
{
    public sealed class Result
    {
        public List<(double x, double y, double z)> Ground = new();
        public List<(double x, double y, double z)> NonGround = new();
        public int GroundCount => Ground.Count;
        public int NonGroundCount => NonGround.Count;
    }

    /// <param name="cell">栅格边长 m。</param>
    /// <param name="slope">地形坡度容差(升/跑)：阈随窗口按此增长, 缓于此的地形不被当物体。</param>
    /// <param name="dh0">初始高程差阈 m。</param>
    /// <param name="dhMax">高程差阈上限 m, 亦即最终判点的容差(高出地面 &gt; 此 = 非地面)。</param>
    /// <param name="maxWindowM">最大开运算窗口 m(≈可削去的最大物体尺度)。</param>
    public static Result Filter(IReadOnlyList<(double x, double y, double z)> pts,
        double cell, double slope = 0.3, double dh0 = 0.3, double dhMax = 3.0, double maxWindowM = 20.0)
    {
        var res = new Result();
        int n = pts?.Count ?? 0;
        if (n == 0) return res;
        cell = Math.Max(cell, 1e-6);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts!) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        int nx = Math.Max(1, (int)Math.Floor((maxX - minX) / cell) + 1);
        int ny = Math.Max(1, (int)Math.Floor((maxY - minY) / cell) + 1);
        if ((long)nx * ny > 8_000_000)   // 栅格过大 → 降级全判地面(不误删), 记调用方可增大 cell
        {
            foreach (var p in pts) res.Ground.Add(p);
            return res;
        }

        int Gi(double x) => Math.Min(nx - 1, Math.Max(0, (int)Math.Floor((x - minX) / cell)));
        int Gj(double y) => Math.Min(ny - 1, Math.Max(0, (int)Math.Floor((y - minY) / cell)));

        var minZ = new double[nx * ny];
        var occ = new bool[nx * ny];
        for (int i = 0; i < minZ.Length; i++) minZ[i] = double.MaxValue;
        var cellOf = new int[n];
        for (int p = 0; p < n; p++)
        {
            int idx = Gj(pts[p].y) * nx + Gi(pts[p].x);
            cellOf[p] = idx;
            if (pts[p].z < minZ[idx]) minZ[idx] = pts[p].z;
            occ[idx] = true;
        }

        // ── 渐进开运算：窗口 1,3,7,15,…(≤maxWindow), 每尺度削去高程突变的格 ──
        var ground = (double[])minZ.Clone();
        int maxWCells = Math.Max(1, (int)(maxWindowM / cell));
        int prevW = 0;
        for (int w = 1; w <= maxWCells; w = w * 2 + 1)
        {
            int half = w / 2;
            if (half >= 1)
            {
                var opened = Open(ground, occ, nx, ny, half);
                double dh = Math.Min(dhMax, dh0 + slope * (w - prevW) * cell);
                for (int idx = 0; idx < ground.Length; idx++)
                    if (occ[idx] && ground[idx] - opened[idx] > dh)
                        ground[idx] = opened[idx];        // 该尺度物体 → 降到裸地面
            }
            prevW = w;
        }

        // ── 逐点判：高出裸地面 > dhMax → 非地面 ──
        for (int p = 0; p < n; p++)
        {
            double g = ground[cellOf[p]];
            if (g < double.MaxValue && pts[p].z - g > dhMax) res.NonGround.Add(pts[p]);
            else res.Ground.Add(pts[p]);
        }
        return res;
    }

    /// <summary>灰度开运算 = 先腐蚀(窗内 min)后膨胀(窗内 max)；仅在占据格上取值。</summary>
    private static double[] Open(double[] s, bool[] occ, int nx, int ny, int half)
    {
        var er = new double[nx * ny];
        for (int gj = 0; gj < ny; gj++)
            for (int gi = 0; gi < nx; gi++)
            {
                int idx = gj * nx + gi;
                if (!occ[idx]) { er[idx] = s[idx]; continue; }
                double m = double.MaxValue;
                for (int dj = -half; dj <= half; dj++)
                    for (int di = -half; di <= half; di++)
                    {
                        int nj = gj + dj, ni = gi + di;
                        if (ni < 0 || ni >= nx || nj < 0 || nj >= ny) continue;
                        int k = nj * nx + ni;
                        if (occ[k] && s[k] < m) m = s[k];
                    }
                er[idx] = m;
            }
        var di2 = new double[nx * ny];
        for (int gj = 0; gj < ny; gj++)
            for (int gi = 0; gi < nx; gi++)
            {
                int idx = gj * nx + gi;
                if (!occ[idx]) { di2[idx] = er[idx]; continue; }
                double m = double.MinValue;
                for (int dj = -half; dj <= half; dj++)
                    for (int dk = -half; dk <= half; dk++)
                    {
                        int nj = gj + dj, ni = gi + dk;
                        if (ni < 0 || ni >= nx || nj < 0 || nj >= ny) continue;
                        int k = nj * nx + ni;
                        if (occ[k] && er[k] > m) m = er[k];
                    }
                di2[idx] = (m == double.MinValue) ? s[idx] : m;
            }
        return di2;
    }
}
