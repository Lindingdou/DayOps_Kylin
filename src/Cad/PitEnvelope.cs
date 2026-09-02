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
