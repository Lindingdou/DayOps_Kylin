using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>路面横断面结果。</summary>
public sealed class CrossSectionResult
{
    public double[] WidthM = Array.Empty<double>();            // 每站路面宽(基宽+弯道加宽)
    public double[] SuperelevationPct = Array.Empty<double>(); // 每站超高横坡 %
    public double MaxWideningM;
    public double MaxSuperelevationPct;
    public double WidenedLengthM;                              // 加宽段总长
}

/// <summary>
/// 路面横断面（忠实移植原 <c>MineAssLib.RoadLayout.RoadCrossSection</c>, GBJ22-87 阶段④）：
/// 按中线局部曲率(三点反算外接圆半径)算弯道加宽 + 超高。
///   加宽 ε = 车道数·L²/(2R)(R ≤ 阈值才加宽, L=轴距)；超高 e = clamp(V²/(127R) − μ, 0, e_max)。
/// 纯几何、可单测。
/// </summary>
public static class RoadCrossSection
{
    /// <summary>弯道加宽 m(R ≤ 阈值才加宽)。</summary>
    public static double WideningM(double radiusM, double widenThresholdM, int laneCount, double wheelbaseM)
    {
        if (radiusM <= 1e-6 || double.IsInfinity(radiusM) || radiusM > widenThresholdM) return 0.0;
        return Math.Max(1, laneCount) * (wheelbaseM * wheelbaseM) / (2.0 * radiusM);
    }

    /// <summary>超高横坡 % = clamp(V²/(127R) − μ, 0, e_max)。</summary>
    public static double SuperelevationPct(double radiusM, double designSpeedKmh, double maxSuperPct, double frictionMu = 0.15)
    {
        if (radiusM <= 1e-6 || double.IsInfinity(radiusM)) return 0.0;
        double e = (designSpeedKmh * designSpeedKmh) / (127.0 * radiusM) - frictionMu;
        return Math.Clamp(e * 100.0, 0.0, maxSuperPct);
    }

    /// <summary>按设计车速反算最小平曲线半径 m：R = v²/(127·(μ+e_max))(忠实原 MinCurveRadiusBySpeed, e_max=最大超高)。SuperelevationPct 之逆(超高饱和处)。</summary>
    public static double MinCurveRadiusBySpeed(double designSpeedKmh, double maxSuperPct, double frictionMu = 0.15)
    {
        double denom = 127.0 * (frictionMu + maxSuperPct / 100.0);
        return denom > 1e-6 ? designSpeedKmh * designSpeedKmh / denom : 0.0;
    }

    /// <summary>展线长预览 m：以最大纵坡 maxGradePct 下降 riseM 所需水平展线长 = rise/(grade/100)(忠实原 DevelopmentLengthPreview)。</summary>
    public static double DevelopmentLengthM(double riseM, double maxGradePct)
        => maxGradePct > 1e-6 ? riseM / (maxGradePct / 100.0) : 0.0;

    /// <summary>沿中线逐站算半径(三点曲率) → 路宽 + 超高 + 汇总。</summary>
    public static CrossSectionResult ComputeAlong(
        IReadOnlyList<(double X, double Y, double Z)> pts,
        double baseWidthM, double widenThresholdM, int laneCount, double wheelbaseM,
        double designSpeedKmh, double maxSuperPct)
    {
        int n = pts.Count;
        var res = new CrossSectionResult { WidthM = new double[n], SuperelevationPct = new double[n] };
        if (n == 0) return res;

        double maxWiden = 0, maxSuper = 0;
        for (int k = 0; k < n; k++)
        {
            double R = double.PositiveInfinity;
            if (k >= 1 && k + 1 < n) R = Radius3(pts[k - 1], pts[k], pts[k + 1]);
            double widen = WideningM(R, widenThresholdM, laneCount, wheelbaseM);
            double super = SuperelevationPct(R, designSpeedKmh, maxSuperPct);
            res.WidthM[k] = baseWidthM + widen;
            res.SuperelevationPct[k] = super;
            if (widen > maxWiden) maxWiden = widen;
            if (super > maxSuper) maxSuper = super;
        }

        double widenLen = 0;
        for (int k = 0; k + 1 < n; k++)
            if (res.WidthM[k] > baseWidthM + 1e-6 || res.WidthM[k + 1] > baseWidthM + 1e-6)
                widenLen += Dist(pts[k], pts[k + 1]);

        res.MaxWideningM = maxWiden;
        res.MaxSuperelevationPct = maxSuper;
        res.WidenedLengthM = widenLen;
        return res;
    }

    /// <summary>XY 平面三点外接圆半径(共线 → +∞)。</summary>
    public static double Radius3(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double acx = c.X - a.X, acy = c.Y - a.Y;
        double cross = abx * acy - aby * acx;
        if (Math.Abs(cross) < 1e-9) return double.PositiveInfinity;
        double la = Dist(b, c), lb = Dist(a, c), lc = Dist(a, b);
        return (la * lb * lc) / (2.0 * Math.Abs(cross));
    }

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
