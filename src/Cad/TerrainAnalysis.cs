using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 地形分析 —— 三角网坡度着色：按每个三角面的三维法向求坡度角，绿(平)→红(陡)配色。
/// 纯逻辑、可单测。输出按坡度着色的三角边线（我方线管线），无需面填充/文字。
/// </summary>
public static class TerrainAnalysis
{
    /// <summary>三角面坡度角(度)：法向与竖直方向夹角。平面=0°，直立=90°。</summary>
    public static double SlopeDegrees(
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
    {
        double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
        double vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12) return 0;
        double cos = Math.Abs(nz) / len;                     // 与竖直轴夹角的余弦
        return Math.Acos(Math.Clamp(cos, 0, 1)) * 180 / Math.PI;
    }

    /// <summary>坡度(度) → 颜色：绿(平)→黄→红(陡)，0..60° 映射满量程。</summary>
    public static (float r, float g, float b) SlopeColor(double slopeDeg)
    {
        double t = Math.Clamp(slopeDeg / 60.0, 0, 1);
        return ((float)t, (float)(1 - t), 0.15f);
    }

    /// <summary>三角面坡向(度, 0..360 罗盘方位)；平面返回 -1。</summary>
    public static double AspectDegrees(
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
    {
        double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
        double vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz;
        if (nx * nx + ny * ny < 1e-12) return -1;            // 平面无坡向
        double ang = Math.Atan2(ny, nx) * 180 / Math.PI;
        return ang < 0 ? ang + 360 : ang;
    }

    /// <summary>坡向(度) → 颜色：按方位 HSV 配色；平面(-1)灰。</summary>
    public static (float r, float g, float b) AspectColor(double aspectDeg)
        => aspectDeg < 0 ? (0.6f, 0.6f, 0.6f) : HsvToRgb(aspectDeg, 0.7f, 0.9f);

    public static (float r, float g, float b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return ((float)(r + m), (float)(g + m), (float)(b + m));
    }

    /// <summary>三角网 → 按坡度着色的三角边线（每三角自身 3 边着色，不去重以保色）。</summary>
    public static List<SceneEntity> BuildSlopeMap(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris)
        => BuildShaded(pts, tris, (A, B, C) => SlopeColor(SlopeDegrees(A, B, C)));

    /// <summary>高程 → 分带色（低=绿 中=黄 高=棕，hypsometric）。range 为 zmax−zmin。</summary>
    public static (float r, float g, float b) ElevationColor(double z, double zmin, double range)
    {
        double t = Math.Clamp((z - zmin) / (range < 1e-9 ? 1 : range), 0, 1);
        (float r, float g, float b) lo = (0.25f, 0.55f, 0.35f), mid = (0.90f, 0.85f, 0.40f), hi = (0.60f, 0.42f, 0.32f);
        if (t < 0.5)
        {
            float u = (float)(t * 2);
            return (lo.r + (mid.r - lo.r) * u, lo.g + (mid.g - lo.g) * u, lo.b + (mid.b - lo.b) * u);
        }
        float w = (float)((t - 0.5) * 2);
        return (mid.r + (hi.r - mid.r) * w, mid.g + (hi.g - mid.g) * w, mid.b + (hi.b - mid.b) * w);
    }

    /// <summary>三角网 → 按平均高程分带着色的三角边线。</summary>
    public static List<SceneEntity> BuildElevationMap(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris)
    {
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var p in pts) { if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }
        double range = zmax - zmin;
        return BuildShaded(pts, tris, (A, B, C) => ElevationColor((A.z + B.z + C.z) / 3, zmin, range));
    }

    /// <summary>三角网 → 按坡向着色的三角边线。</summary>
    public static List<SceneEntity> BuildAspectMap(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris)
        => BuildShaded(pts, tris, (A, B, C) => AspectColor(AspectDegrees(A, B, C)));

    /// <summary>三角形 XY 投影面积。</summary>
    public static double XyArea((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        => System.Math.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5;

    /// <summary>三角柱相对基准面 baseZ 的带符号体积（均高 × XY 面积）。</summary>
    public static double PrismVolume(
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c, double baseZ)
        => ((a.z - baseZ) + (b.z - baseZ) + (c.z - baseZ)) / 3.0 * XyArea(a, b, c);

    /// <summary>TIN 相对基准面的体积：返回(挖方=基准面上方, 填方=下方(正值), 净值=上−下)。</summary>
    public static (double above, double below, double net) Volume(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris, double baseZ)
    {
        double above = 0, below = 0;
        foreach (var t in tris)
        {
            double v = PrismVolume(pts[t.a], pts[t.b], pts[t.c], baseZ);
            if (v >= 0) above += v; else below += -v;
        }
        return (above, below, above - below);
    }

    /// <summary>两期高程点差值算量：各自 IDW 到同一 n×n 网格，逐格(2−1)按格面积求和。
    /// 返回(挖方=下降量, 填方=上升量, 净=填−挖)。</summary>
    /// <summary>圈范围算量：仅统计质心落在边界多边形内的三角柱体积(相对 baseZ)。返回(上/下/净)。</summary>
    public static (double above, double below, double net) VolumeWithinBoundary(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris, double baseZ,
        IReadOnlyList<(double x, double y)> boundary)
    {
        double above = 0, below = 0;
        foreach (var t in tris)
        {
            double cx = (pts[t.a].x + pts[t.b].x + pts[t.c].x) / 3.0;
            double cy = (pts[t.a].y + pts[t.b].y + pts[t.c].y) / 3.0;
            if (!LineMath.PointInPolygon(cx, cy, boundary)) continue;   // 边界外不计
            double v = PrismVolume(pts[t.a], pts[t.b], pts[t.c], baseZ);
            if (v >= 0) above += v; else below += -v;
        }
        return (above, below, above - below);
    }

    public static (double cut, double fill, double net) TwoEpochVolume(
        IReadOnlyList<(double x, double y, double z)> a, IReadOnlyList<(double x, double y, double z)> b, int n)
    {
        if (a.Count == 0 || b.Count == 0 || n < 2) return (0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Ext(IReadOnlyList<(double x, double y, double z)> pts) { foreach (var p in pts) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; } }
        Ext(a); Ext(b);
        double dx = maxX > minX ? (maxX - minX) / (n - 1) : 1;
        double dy = maxY > minY ? (maxY - minY) / (n - 1) : 1;
        var g1 = Contour.GridInto(a, n, n, minX, minY, dx, dy);
        var g2 = Contour.GridInto(b, n, n, minX, minY, dx, dy);
        double cellArea = dx * dy, cut = 0, fill = 0;
        for (int ix = 0; ix + 1 < n; ix++)
        for (int iy = 0; iy + 1 < n; iy++)
        {
            // 格内 4 角 dz 均值 × 格面积
            double dz = (Dz(g1, g2, ix, iy) + Dz(g1, g2, ix + 1, iy) + Dz(g1, g2, ix + 1, iy + 1) + Dz(g1, g2, ix, iy + 1)) / 4.0;
            double v = dz * cellArea;
            if (v >= 0) fill += v; else cut += -v;
        }
        return (cut, fill, fill - cut);
    }
    private static double Dz(double[,] g1, double[,] g2, int ix, int iy) => g2[ix, iy] - g1[ix, iy];

    public readonly record struct CutFillBand(double ZLow, double ZHigh, double Cut, double Fill);

    /// <summary>
    /// 两期算量分标高带（忠实原 PointCloudLib VolumeReportGenerator「按标高带」聚合）——
    /// 各格变化柱 [min(g1,g2), max(g1,g2)] 按 bandHeight 切到各高程带, 逐带累计挖/填。
    /// 挖/填判据与 <see cref="TwoEpochVolume"/> 一致(m2&gt;m1=填); 各带挖和==整体挖(守恒)。
    /// bandHeight≤0 → 变化区间十等分。纯逻辑、可单测。
    /// </summary>
    public static List<CutFillBand> TwoEpochVolumeByElevation(
        IReadOnlyList<(double x, double y, double z)> a, IReadOnlyList<(double x, double y, double z)> b,
        int n, double bandHeight)
    {
        var res = new List<CutFillBand>();
        if (a.Count == 0 || b.Count == 0 || n < 2) return res;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Ext(IReadOnlyList<(double x, double y, double z)> pts) { foreach (var p in pts) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; } }
        Ext(a); Ext(b);
        double dx = maxX > minX ? (maxX - minX) / (n - 1) : 1;
        double dy = maxY > minY ? (maxY - minY) / (n - 1) : 1;
        var g1 = Contour.GridInto(a, n, n, minX, minY, dx, dy);
        var g2 = Contour.GridInto(b, n, n, minX, minY, dx, dy);
        double cellArea = dx * dy;

        // 各格变化柱 [lo,hi] + 挖/填标志; 同时求全局 z 范围
        double zMin = double.MaxValue, zMax = double.MinValue;
        var cells = new List<(double lo, double hi, bool fill)>();
        for (int ix = 0; ix + 1 < n; ix++)
        for (int iy = 0; iy + 1 < n; iy++)
        {
            double m1 = (g1[ix, iy] + g1[ix + 1, iy] + g1[ix + 1, iy + 1] + g1[ix, iy + 1]) / 4.0;
            double m2 = (g2[ix, iy] + g2[ix + 1, iy] + g2[ix + 1, iy + 1] + g2[ix, iy + 1]) / 4.0;
            double lo = System.Math.Min(m1, m2), hi = System.Math.Max(m1, m2);
            if (hi - lo < 1e-12) continue;                 // 无变化格不计
            cells.Add((lo, hi, m2 > m1));
            if (lo < zMin) zMin = lo; if (hi > zMax) zMax = hi;
        }
        if (cells.Count == 0) return res;
        if (bandHeight <= 1e-9) bandHeight = (zMax - zMin) / 10.0;
        if (bandHeight <= 1e-9) bandHeight = 1.0;
        int nb = System.Math.Max(1, (int)System.Math.Ceiling((zMax - zMin + 1e-9) / bandHeight));
        var cut = new double[nb]; var fill = new double[nb];
        foreach (var c in cells)
            for (int k = 0; k < nb; k++)
            {
                double zb = zMin + k * bandHeight, zt = zb + bandHeight;
                double ov = System.Math.Min(c.hi, zt) - System.Math.Max(c.lo, zb);   // 柱与带重叠高
                if (ov <= 0) continue;
                double v = ov * cellArea;
                if (c.fill) fill[k] += v; else cut[k] += v;
            }
        for (int k = 0; k < nb; k++)
        {
            double zb = zMin + k * bandHeight, zt = System.Math.Min(zMax, zb + bandHeight);
            res.Add(new CutFillBand(zb, zt, cut[k], fill[k]));
        }
        return res;
    }

    /// <summary>两期分标高填挖 → CSV(z_low,z_high,cut,fill,net)。</summary>
    public static string TwoEpochByElevationCsv(IReadOnlyList<CutFillBand> bands)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("z_low,z_high,cut,fill,net\n");
        foreach (var b in bands)
            sb.Append(b.ZLow.ToString("R", inv)).Append(',').Append(b.ZHigh.ToString("R", inv)).Append(',')
              .Append(b.Cut.ToString("R", inv)).Append(',').Append(b.Fill.ToString("R", inv)).Append(',')
              .Append((b.Fill - b.Cut).ToString("R", inv)).Append('\n');
        return sb.ToString();
    }

    private static List<SceneEntity> BuildShaded(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris,
        Func<(double x, double y, double z), (double x, double y, double z), (double x, double y, double z), (float r, float g, float b)> color)
    {
        var list = new List<SceneEntity>();
        foreach (var t in tris)
        {
            var (r, g, b) = color(pts[t.a], pts[t.b], pts[t.c]);
            void E(int u, int v) => list.Add(new LineEntity { X0 = pts[u].x, Y0 = pts[u].y, X1 = pts[v].x, Y1 = pts[v].y, Cr = r, Cg = g, Cb = b });
            E(t.a, t.b); E(t.b, t.c); E(t.c, t.a);
        }
        return list;
    }
}
