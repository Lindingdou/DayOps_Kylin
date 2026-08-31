using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云逐点表面属性（法向估计 / 逐点坡度·坡向）——每点在 k 近邻上 PCA 拟合局部平面, 最小特征向量为法向,
/// 由法向算坡度(dip=法向与竖直夹角)与坡向(az)。托管重算原内核逐点表面属性(同 粗糙度/曲率 脉络)。
/// XY 网格加速 k 近邻; 复用 <see cref="DepositAutoDetector.JacobiEigen3"/>。纯几何、可单测。
/// </summary>
public static class PointNormals
{
    /// <summary>逐点 (法向, 坡度°, 坡向°)。k=近邻数。点不足返回空。</summary>
    public static List<((double x, double y, double z) n, double slope, double aspect)> Compute(
        IReadOnlyList<(double x, double y, double z)> pts, int k)
    {
        var res = new List<((double, double, double), double, double)>();
        int n = pts?.Count ?? 0;
        if (n < 3) return res;
        if (k < 3) k = 3;

        // XY 网格：格边≈平均间距×2
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts!) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        double area = Math.Max((maxX - minX) * (maxY - minY), 1e-9);
        double spacing = Math.Sqrt(area / n);
        double cell = Math.Max(spacing * 2, 1e-6);
        var grid = new Dictionary<(long, long), List<int>>(n);
        (long, long) Key(double x, double y) => ((long)Math.Floor((x - minX) / cell), (long)Math.Floor((y - minY) / cell));
        for (int i = 0; i < n; i++)
        {
            var key = Key(pts[i].x, pts[i].y);
            if (!grid.TryGetValue(key, out var l)) { l = new List<int>(); grid[key] = l; }
            l.Add(i);
        }

        var cand = new List<(double d2, int idx)>();
        for (int i = 0; i < n; i++)
        {
            var pi = pts[i];
            var (cx, cy) = Key(pi.x, pi.y);
            cand.Clear();
            int ring = 1;
            while (true)   // 逐环扩张直到候选 ≥ k(或覆盖全图)
            {
                cand.Clear();
                for (long gx = cx - ring; gx <= cx + ring; gx++)
                    for (long gy = cy - ring; gy <= cy + ring; gy++)
                        if (grid.TryGetValue((gx, gy), out var bucket))
                            foreach (int j in bucket)
                            {
                                double dx = pts[j].x - pi.x, dy = pts[j].y - pi.y, dz = pts[j].z - pi.z;
                                cand.Add((dx * dx + dy * dy + dz * dz, j));
                            }
                if (cand.Count >= k + 1 || ring > Math.Max((maxX - minX), (maxY - minY)) / cell + 1) break;
                ring++;
            }
            cand.Sort((a, b) => a.d2.CompareTo(b.d2));
            int take = Math.Min(k, cand.Count);
            // 协方差(取最近 take 个, 含自身)
            double mx = 0, my = 0, mz = 0;
            for (int t = 0; t < take; t++) { var q = pts[cand[t].idx]; mx += q.x; my += q.y; mz += q.z; }
            mx /= take; my /= take; mz /= take;
            double sxx = 0, syy = 0, szz = 0, sxy = 0, sxz = 0, syz = 0;
            for (int t = 0; t < take; t++)
            {
                var q = pts[cand[t].idx];
                double ex = q.x - mx, ey = q.y - my, ez = q.z - mz;
                sxx += ex * ex; syy += ey * ey; szz += ez * ez; sxy += ex * ey; sxz += ex * ez; syz += ey * ez;
            }
            var c = new double[3, 3];
            c[0, 0] = sxx / take; c[1, 1] = syy / take; c[2, 2] = szz / take;
            c[0, 1] = c[1, 0] = sxy / take; c[0, 2] = c[2, 0] = sxz / take; c[1, 2] = c[2, 1] = syz / take;
            DepositAutoDetector.JacobiEigen3(c, out var eval, out var evec);
            int s = 0; if (eval[1] < eval[s]) s = 1; if (eval[2] < eval[s]) s = 2;
            double nx = evec[s][0], ny = evec[s][1], nz = evec[s][2];
            double nn = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nn < 1e-12) { res.Add(((0, 0, 1), 0, 0)); continue; }
            if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }   // 法向朝上
            double slope = Math.Acos(Math.Min(1.0, Math.Abs(nz) / nn)) * 180.0 / Math.PI;
            double az = Math.Atan2(nx, -ny) * 180.0 / Math.PI; az %= 360.0; if (az < 0) az += 360.0;
            res.Add(((nx / nn, ny / nn, nz / nn), slope, az));
        }
        return res;
    }
}
