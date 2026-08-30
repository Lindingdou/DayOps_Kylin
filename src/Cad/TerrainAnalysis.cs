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

    /// <summary>三角网 → 按坡度着色的三角边线（每三角自身 3 边着色，不去重以保色）。</summary>
    public static List<SceneEntity> BuildSlopeMap(
        IReadOnlyList<(double x, double y, double z)> pts, List<(int a, int b, int c)> tris)
    {
        var list = new List<SceneEntity>();
        foreach (var t in tris)
        {
            var (r, g, b) = SlopeColor(SlopeDegrees(pts[t.a], pts[t.b], pts[t.c]));
            void E(int u, int v) => list.Add(new LineEntity { X0 = pts[u].x, Y0 = pts[u].y, X1 = pts[v].x, Y1 = pts[v].y, Cr = r, Cg = g, Cb = b });
            E(t.a, t.b); E(t.b, t.c); E(t.c, t.a);
        }
        return list;
    }
}
