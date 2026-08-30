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
