using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>矿床几何特征（对煤单元中心 PCA 得出，纯几何）。移植自 BlockModelLib.Domain。</summary>
public readonly struct DepositSignature
{
    public double DipDeg { get; init; }            // 平均倾角（0=水平，90=直立）
    public double StrikeAzimuthDeg { get; init; }  // 走向方位（0..180）
    public int SeamCount { get; init; }            // 估计煤层数（Z 层游程，启发式）
    public long CoalCellCount { get; init; }       // 参与统计的煤单元数
}

/// <summary>
/// 矿床类型自动识别的**纯几何核**——移植自 BlockModelLib.Domain.DepositAutoDetector。
/// 对煤单元中心做 PCA：最小特征向量=层面法向 → 倾角/走向；按 Z 层煤量游程估煤层数。
/// 原类吃 BlockModel(需内核)；此处抽出纯函数吃煤单元中心点集 + Z 层厚，算法核一致。
/// </summary>
public static class DepositAutoDetector
{
    /// <summary>对煤单元中心做 PCA，返回倾角/走向/煤层数。煤单元 &lt;8 或退化返回 null。zLayer=Z 分层厚(估煤层数用)。</summary>
    public static DepositSignature? Detect(IReadOnlyList<(double X, double Y, double Z)>? coalCells, double zLayer)
    {
        if (coalCells == null || coalCells.Count < 8) return null;
        long cnt = coalCells.Count;

        double Sx = 0, Sy = 0, Sz = 0, Sxx = 0, Syy = 0, Szz = 0, Sxy = 0, Sxz = 0, Syz = 0;
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var (cx, cy, cz) in coalCells)
        {
            Sx += cx; Sy += cy; Sz += cz;
            Sxx += cx * cx; Syy += cy * cy; Szz += cz * cz;
            Sxy += cx * cy; Sxz += cx * cz; Syz += cy * cz;
            if (cz < zmin) zmin = cz; if (cz > zmax) zmax = cz;
        }

        double mx = Sx / cnt, my = Sy / cnt, mz = Sz / cnt;
        var c = new double[3, 3];
        c[0, 0] = Sxx / cnt - mx * mx; c[1, 1] = Syy / cnt - my * my; c[2, 2] = Szz / cnt - mz * mz;
        c[0, 1] = c[1, 0] = Sxy / cnt - mx * my;
        c[0, 2] = c[2, 0] = Sxz / cnt - mx * mz;
        c[1, 2] = c[2, 1] = Syz / cnt - my * mz;

        JacobiEigen3(c, out var eval, out var evec);
        int sIdx = 0;
        if (eval[1] < eval[sIdx]) sIdx = 1;
        if (eval[2] < eval[sIdx]) sIdx = 2;
        double nX = evec[sIdx][0], nY = evec[sIdx][1], nZ = evec[sIdx][2];
        double nn = Math.Sqrt(nX * nX + nY * nY + nZ * nZ);
        if (nn < 1e-12) return null;

        double dip = Math.Acos(Math.Min(1.0, Math.Abs(nZ) / nn)) * 180.0 / Math.PI;
        double az = Math.Atan2(nX, -nY) * 180.0 / Math.PI;
        az %= 180.0; if (az < 0) az += 180.0;

        // 煤层数：按 Z 层厚分箱，占用阈值=峰值 5% 做游程
        int seams = 1;
        if (zLayer > 1e-9 && zmax > zmin)
        {
            int nb = (int)Math.Round((zmax - zmin) / zLayer) + 1;
            if (nb > 0 && nb < 100000)
            {
                var perK = new long[nb];
                foreach (var (_, _, cz) in coalCells)
                {
                    int k = (int)Math.Round((cz - zmin) / zLayer);
                    if (k < 0) k = 0; else if (k >= nb) k = nb - 1;
                    perK[k]++;
                }
                long maxK = 0;
                foreach (var v in perK) if (v > maxK) maxK = v;
                long thr = Math.Max(1, (long)(maxK * 0.05));
                seams = 0; bool inRun = false;
                foreach (var v in perK)
                {
                    bool occ = v >= thr;
                    if (occ && !inRun) { seams++; inRun = true; }
                    else if (!occ) inRun = false;
                }
                if (seams < 1) seams = 1;
            }
        }

        return new DepositSignature { DipDeg = dip, StrikeAzimuthDeg = az, SeamCount = seams, CoalCellCount = cnt };
    }

    /// <summary>对称 3×3 矩阵 Jacobi 特征分解（逐字移植）。eval[i] 对应 evec[i]。</summary>
    public static void JacobiEigen3(double[,] a, out double[] eval, out double[][] evec)
    {
        var m = (double[,])a.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
            if (off < 1e-18) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300) continue;
                    double phi = 0.5 * Math.Atan2(2 * m[p, q], m[q, q] - m[p, p]);
                    double cs = Math.Cos(phi), sn = Math.Sin(phi);
                    for (int k = 0; k < 3; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = cs * mkp - sn * mkq;
                        m[k, q] = sn * mkp + cs * mkq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = cs * mpk - sn * mqk;
                        m[q, k] = sn * mpk + cs * mqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = cs * vkp - sn * vkq;
                        v[k, q] = sn * vkp + cs * vkq;
                    }
                }
        }

        eval = new[] { m[0, 0], m[1, 1], m[2, 2] };
        evec = new[]
        {
            new[] { v[0, 0], v[1, 0], v[2, 0] },
            new[] { v[0, 1], v[1, 1], v[2, 1] },
            new[] { v[0, 2], v[1, 2], v[2, 2] },
        };
    }
}
