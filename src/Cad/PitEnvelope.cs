using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>一帮的最终帮坡角(忠实原 PitScheme.WallAngle 的 SideName+BetaDeg 子集)。</summary>
public sealed record WallAngle(string SideName, double BetaDeg);

/// <summary>
/// 几何圈定: 从顶口(块体足迹)按<b>各帮各自帮坡角 β</b>逐边变距内缩 drop/tanβ_edge 放坡到坑底,
/// 相邻偏移边求交得新顶点; 几何深度封顶取各边最紧者。忠实移植原 PlanLib.BoundaryOptimization.PitEnvelope
/// 的纯几何核(原 ResolveTopOutline 的地表界 native 路径 + WallSegments 逐段绑定不在此, 走块体足迹 + 方位映射帮)。
/// 纯逻辑、可单测。
/// </summary>
public static class PitEnvelope
{
    /// <summary>各帮平均 β(空/非法回退 40°)。</summary>
    public static double AvgBeta(IReadOnlyList<WallAngle> walls)
    {
        double sum = 0; int c = 0;
        foreach (var w in walls)
            if (w.BetaDeg > 1 && w.BetaDeg < 89) { sum += w.BetaDeg; c++; }
        return c > 0 ? sum / c : 40.0;
    }

    /// <summary>边 i 的外法向(单位)。</summary>
    public static void EdgeOutwardNormal(TopOutline top, int i, out double nx, out double ny)
    {
        int n = top.Count, j = (i + 1) % n;
        double ex = top.X[j] - top.X[i], ey = top.Y[j] - top.Y[i];
        double mx = (top.X[i] + top.X[j]) / 2 - top.Cx, my = (top.Y[i] + top.Y[j]) / 2 - top.Cy;
        nx = ey; ny = -ex;
        double len = Math.Sqrt(nx * nx + ny * ny); if (len < 1e-9) len = 1;
        nx /= len; ny /= len;
        if (nx * mx + ny * my < 0) { nx = -nx; ny = -ny; }
    }

    public static double EdgeNormalAzimuth(TopOutline top, int i)
    {
        EdgeOutwardNormal(top, i, out double nx, out double ny);
        double az = Math.Atan2(ny, nx) * 180.0 / Math.PI;
        return az < 0 ? az + 360 : az;
    }

    /// <summary>方位 → 帮 β(SideName 含 东西南北 / EWSN 匹配; 匹配不到回退平均)。</summary>
    public static double BetaForAzimuth(IReadOnlyList<WallAngle> walls, double azDeg)
    {
        double az = azDeg % 360.0; if (az < 0) az += 360.0;
        (string cn, string en) dir =
            (az >= 315 || az < 45) ? ("东", "E") :
            (az < 135) ? ("北", "N") :
            (az < 225) ? ("西", "W") : ("南", "S");
        double sum = 0; int c = 0;
        foreach (var w in walls)
        {
            if (!(w.BetaDeg > 1 && w.BetaDeg < 89)) continue;
            sum += w.BetaDeg; c++;
            var name = w.SideName ?? "";
            if (name.Contains(dir.cn) || name.IndexOf(dir.en, StringComparison.OrdinalIgnoreCase) >= 0)
                return Clamp(w.BetaDeg);
        }
        return c > 0 ? Clamp(sum / c) : 40.0;
    }

    /// <summary>解析每条边的最终帮坡角: 按边外法向方位映射到帮(块体足迹路径, 无 WallSegments 逐段绑定)。</summary>
    public static double[] EdgeBetas(TopOutline top, IReadOnlyList<WallAngle> walls)
    {
        int n = top.Count;
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = BetaForAzimuth(walls, EdgeNormalAzimuth(top, i));
        return b;
    }

    /// <summary>几何允许最大深度: 各边沿其外法向放坡到最小底宽, 取最紧(最浅)者。</summary>
    public static double GeometricDepthCapPerWall(TopOutline top, double minBottomWidthM, double[] edgeBeta)
    {
        int n = top.Count;
        double best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            EdgeOutwardNormal(top, i, out double nx, out double ny);
            double mn = double.MaxValue, mx = double.MinValue;
            for (int v = 0; v < n; v++)
            {
                double p = top.X[v] * nx + top.Y[v] * ny;
                mn = Math.Min(mn, p); mx = Math.Max(mx, p);
            }
            double half = (mx - mn) / 2.0;
            double run = Math.Max(0.0, half - minBottomWidthM / 2.0);
            double d = run * Tan(edgeBeta[i]);
            best = Math.Min(best, d);
        }
        return best == double.MaxValue ? 0 : best;
    }

    /// <summary>逐帮变距内缩: 下降 drop 米时每边按其 β 内缩 drop/tanβ, 相邻偏移边求交得新顶点。</summary>
    public static (double[] x, double[] y) InsetPerWall(TopOutline top, double drop, double[] edgeBeta)
    {
        int n = top.Count;
        var inset = new double[n];
        for (int i = 0; i < n; i++) inset[i] = drop / Tan(edgeBeta[i]);
        return OffsetPolygon(top.X.ToArray(), top.Y.ToArray(), inset);
    }

    /// <summary>
    /// 按每条边各自的内缩距离 edgeInset[i] 向内偏移闭合多边形, 取相邻偏移边交点为新顶点。
    /// 顶点数保持不变(供逐级生成 crest/toe 环)。对凸形精确、凹形近似。
    /// </summary>
    public static (double[] x, double[] y) OffsetPolygon(double[] px, double[] py, double[] edgeInset)
    {
        int n = px.Length;
        var rx = new double[n]; var ry = new double[n];
        if (n < 3) { Array.Copy(px, rx, n); Array.Copy(py, ry, n); return (rx, ry); }

        double cx = 0, cy = 0;
        for (int i = 0; i < n; i++) { cx += px[i]; cy += py[i]; }
        cx /= n; cy /= n;

        var ax = new double[n]; var ay = new double[n];
        var dxa = new double[n]; var dya = new double[n];
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double ex = px[j] - px[i], ey = py[j] - py[i];
            dxa[i] = ex; dya[i] = ey;
            double nx = ey, ny = -ex;                        // 候选法向
            double mx = (px[i] + px[j]) / 2 - cx, my = (py[i] + py[j]) / 2 - cy;
            if (nx * mx + ny * my < 0) { nx = -nx; ny = -ny; }  // 取外向
            double len = Math.Sqrt(nx * nx + ny * ny); if (len < 1e-9) len = 1;
            nx /= len; ny /= len;
            ax[i] = px[i] - nx * edgeInset[i]; ay[i] = py[i] - ny * edgeInset[i];
        }
        for (int i = 0; i < n; i++)
        {
            int p = (i - 1 + n) % n;
            if (!LineIntersect(ax[p], ay[p], dxa[p], dya[p], ax[i], ay[i], dxa[i], dya[i], out double vx, out double vy))
            { vx = ax[i]; vy = ay[i]; }
            rx[i] = vx; ry[i] = vy;
        }
        return (rx, ry);
    }

    /// <summary>闭合多边形有向面积的绝对值(XY)。</summary>
    public static double PolygonArea(double[] px, double[] py)
    {
        int n = px.Length; if (n < 3) return 0;
        double a = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; a += px[i] * py[j] - px[j] * py[i]; }
        return Math.Abs(a) / 2.0;
    }

    /// <summary>一条台阶线环: 闭合多边形顶点(XY) + 标高 Z + 是否坡顶(crest, 否则坡底 toe)。</summary>
    public sealed record BenchRing(double[] X, double[] Y, double Z, bool Crest);

    /// <summary>
    /// 段③ 三维境界落地: 自顶向下逐台阶生成 crest/toe 环(忠实原 PitMaterializer)。
    /// crest_0(顶口) → 坡面内缩 H/tanα ↘ toe_0 → 平盘内缩 W=H/tanβ−H/tanα → crest_1 → …;
    /// 各帮 β 不同 → 各帮 W 不同(逐边)。环退化(外接框短边 &lt; 5m)即到坑底止。台阶数 n=round(depth/H)。
    /// </summary>
    public static List<BenchRing> MaterializeRings(TopOutline top, double[] edgeBeta,
        double benchH, double faceAngleDeg, double depth, double[]? contraction = null)
    {
        int m = top.Count;
        double slopeRun = benchH / Tan(faceAngleDeg);            // 坡面水平投影(各帮同)
        var slopeInset = new double[m];
        var bermInset = new double[m];
        for (int i = 0; i < m; i++)
        {
            slopeInset[i] = slopeRun;
            double perBenchRun = benchH / Tan(edgeBeta[i]);      // H/tanβ 该帮单台阶总进尺
            bermInset[i] = Math.Max(0.0, perBenchRun - slopeRun);  // 平盘宽 W(该帮)
        }
        int n = Math.Max(1, (int)Math.Round(depth / Math.Max(1e-6, benchH)));
        var rings = new List<BenchRing>();
        double[] curX, curY;
        if (contraction != null && contraction.Length == m && contraction.Any(d => d > 1e-6))
            (curX, curY) = OffsetPolygon(top.X.ToArray(), top.Y.ToArray(), contraction);
        else { curX = top.X.ToArray(); curY = top.Y.ToArray(); }
        double z = top.Zsurface;
        rings.Add(new BenchRing(curX, curY, z, true));
        for (int k = 1; k <= n; k++)
        {
            (curX, curY) = OffsetPolygon(curX, curY, slopeInset); // 坡面下降
            z -= benchH;
            rings.Add(new BenchRing(curX, curY, z, false));       // 坡底(toe)
            if (Degenerate(curX, curY)) break;
            if (k < n)
            {
                (curX, curY) = OffsetPolygon(curX, curY, bermInset); // 平盘内移
                rings.Add(new BenchRing(curX, curY, z, true));       // 下一坡顶(crest)
                if (Degenerate(curX, curY)) break;
            }
        }
        return rings;
    }

    /// <summary>相邻环放样三角化成三维台阶面(各环顶点数 = m, 按 j↔j 连成条带; 忠实原 BuildLoftMesh)。供导 OFF。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) LoftMesh(List<BenchRing> rings)
    {
        var verts = new List<(double, double, double)>();
        var tris = new List<(int, int, int)>();
        if (rings.Count < 2) return (verts, tris);
        int m = rings[0].X.Length;
        foreach (var ring in rings)
            for (int j = 0; j < m; j++) verts.Add((ring.X[j], ring.Y[j], ring.Z));
        for (int r = 0; r < rings.Count - 1; r++)
        {
            if (rings[r].X.Length != m || rings[r + 1].X.Length != m) break;
            int aBase = r * m, bBase = (r + 1) * m;
            for (int j = 0; j < m; j++)
            {
                int j2 = (j + 1) % m;
                int a0 = aBase + j, a1 = aBase + j2, b0 = bBase + j, b1 = bBase + j2;
                tris.Add((a0, b0, b1));
                tris.Add((a0, b1, a1));
            }
        }
        return (verts, tris);
    }

    /// <summary>环退化(外接框短边 &lt; 5m)即到坑底(忠实原 Degenerate)。</summary>
    private static bool Degenerate(double[] x, double[] y)
    {
        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        for (int i = 0; i < x.Length; i++)
        {
            xmin = Math.Min(xmin, x[i]); xmax = Math.Max(xmax, x[i]);
            ymin = Math.Min(ymin, y[i]); ymax = Math.Max(ymax, y[i]);
        }
        return Math.Min(xmax - xmin, ymax - ymin) < 5.0;
    }

    private static bool LineIntersect(double px, double py, double rx, double ry,
                                      double qx, double qy, double sx, double sy,
                                      out double ix, out double iy)
    {
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-9) { ix = 0; iy = 0; return false; }
        double t = ((qx - px) * sy - (qy - py) * sx) / denom;
        ix = px + t * rx; iy = py + t * ry;
        return true;
    }

    private static double Clamp(double betaDeg) => Math.Max(1.0, Math.Min(89.0, betaDeg));
    private static double Tan(double betaDeg) => Math.Tan(Clamp(betaDeg) * Math.PI / 180.0);
}
