using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>顶口轮廓（地表界限制线，或块体平面足迹兜底）。（原 <c>PlanLib.BoundaryOptimization.TopOutline</c>）</summary>
public sealed class PitTopOutline
{
    public List<double> X = new();
    public List<double> Y = new();
    public double Zsurface;        // 顶口标高
    public double Cx, Cy;          // 形心
    public bool FromSurfaceLimit;  // true=来自地表界；false=块体足迹兜底
    public int Count => X.Count;

    /// <summary>转成 Kylin 既有几何核用的 <see cref="TopOutline"/>（同字段）。</summary>
    public TopOutline ToCore()
    {
        var o = new TopOutline { Zsurface = Zsurface, Cx = Cx, Cy = Cy };
        o.X.AddRange(X); o.Y.AddRange(Y);
        return o;
    }
}

/// <summary>
/// 几何圈定辅助（原 <c>PlanLib.BoundaryOptimization.PitEnvelope</c>，方案感知的那一半；纯几何核复用 <see cref="Cad.PitEnvelope"/>）：
/// 从顶口（地表界 / 块体足迹）按**各帮各自帮坡角 β**向内放坡到最小底宽圈定。
/// β 来源优先级：① 用户按境界线段绑定的 WallSegments（逐直线段）；② 回退按方位映射到帮(BetaForAzimuth)。
/// </summary>
public static class PitSchemeEnvelope
{
    public static double AvgBeta(PitScheme s)
    {
        double sum = 0; int c = 0;
        foreach (var w in s.Walls)
            if (w.BetaDeg > 1 && w.BetaDeg < 89) { sum += w.BetaDeg; c++; }
        return c > 0 ? sum / c : 40.0;
    }

    /// <summary>顶口：地表界多段线优先（≥3 顶点），否则块体足迹（包围盒矩形）兜底；都没有返回 null。</summary>
    public static PitTopOutline? ResolveTopOutline(IPlanEntityHost? host, long surfaceLimitHandle, BlockModelMeta? block)
    {
        if (host != null && surfaceLimitHandle != 0
            && host.TryGetPolylineWorldVertices(surfaceLimitHandle, out var xyz, out _)
            && xyz != null && xyz.Length >= 9)
        {
            int n = xyz.Length / 3;
            var o = new PitTopOutline { FromSurfaceLimit = true };
            double sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < n; i++)
            {
                double x = xyz[i * 3], y = xyz[i * 3 + 1], z = xyz[i * 3 + 2];
                o.X.Add(x); o.Y.Add(y); sx += x; sy += y; sz += z;
            }
            o.Cx = sx / n; o.Cy = sy / n; o.Zsurface = sz / n;
            return o;
        }
        if (block != null)
        {
            var b = block.Bounds;
            var o = new PitTopOutline { FromSurfaceLimit = false, Zsurface = b.maxZ, Cx = (b.minX + b.maxX) / 2, Cy = (b.minY + b.maxY) / 2 };
            o.X.AddRange(new[] { b.minX, b.maxX, b.maxX, b.minX });
            o.Y.AddRange(new[] { b.minY, b.minY, b.maxY, b.maxY });
            return o;
        }
        return null;
    }

    public static double EdgeNormalAzimuth(PitTopOutline top, int i) => Cad.PitEnvelope.EdgeNormalAzimuth(top.ToCore(), i);

    /// <summary>方位 → 帮 β（SideName 含 东西南北 / EWSN 匹配；匹配不到回退平均）。</summary>
    public static double BetaForAzimuth(IReadOnlyList<WallAngle> walls, double azDeg)
        => Cad.PitEnvelope.BetaForAzimuth(walls.Select(w => new Cad.WallAngle(w.SideName, w.BetaDeg)).ToList(), azDeg);

    /// <summary>解析每条边的最终帮坡角：用户按段绑定(WallSegments)优先，否则按方位映射到帮。</summary>
    public static double[] ResolveEdgeBetas(PitTopOutline top, PitScheme s)
    {
        int n = top.Count;
        var b = new double[n];
        var segs = s.Geometry.WallSegments;
        bool useSeg = top.FromSurfaceLimit && segs.Count == n;
        var core = top.ToCore();
        for (int i = 0; i < n; i++)
            b[i] = useSeg ? Clamp(segs[i].BetaDeg) : BetaForAzimuth(s.Walls, Cad.PitEnvelope.EdgeNormalAzimuth(core, i));
        return b;
    }

    /// <summary>每条边的额外向内收缩量 (m) = 整体 ContractionM + 段绑定 InsetExtraM（对齐时）。</summary>
    public static double[] ResolveEdgeContraction(PitTopOutline top, PitScheme s)
    {
        int n = top.Count;
        var c = new double[n];
        var segs = s.Geometry.WallSegments;
        bool useSeg = top.FromSurfaceLimit && segs.Count == n;
        double overall = Math.Max(0, s.ContractionM);
        for (int i = 0; i < n; i++)
            c[i] = overall + (useSeg ? Math.Max(0, segs[i].InsetExtraM) : 0);
        return c;
    }

    public static double GeometricDepthCapPerWall(PitTopOutline top, double minBottomWidthM, double[] edgeBeta)
        => Cad.PitEnvelope.GeometricDepthCapPerWall(top.ToCore(), minBottomWidthM, edgeBeta);

    public static (double[] x, double[] y) OffsetPolygon(double[] px, double[] py, double[] edgeInset)
        => Cad.PitEnvelope.OffsetPolygon(px, py, edgeInset);

    private static double Clamp(double betaDeg) => Math.Max(1.0, Math.Min(89.0, betaDeg));
}
