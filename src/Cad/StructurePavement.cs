using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「结构路面」几何核心 —— 忠实移植原 RoadLib.Render.StructurePavement: 把道路中心线按路宽在 XY 平面
/// 等宽外扩成一条闭合的『结构路面带』(ribbon) 多边形。每点用切向(前后差分, 首尾单段)旋 90° 得单位法向,
/// 左侧 = 点 + 半宽·法向、右侧 = 点 − 半宽·法向; 左顺 + 右逆 + 回起点闭合。切向退化点沿用上一法向。
/// 纯几何、可单测。(原保留 Z; Kylin 2D 场景取 XY。)
/// </summary>
public static class StructurePavement
{
    private const double TangentEpsilon = 1e-6;

    /// <summary>单条中线 → 闭合 ribbon 多边形(末点=首点闭合)。不足 2 点/全退化返 null。</summary>
    public static List<(double x, double y)>? BuildRibbon(IReadOnlyList<(double x, double y)> line, double widthM)
    {
        if (line == null || line.Count < 2 || widthM <= 0) return null;
        double h = widthM / 2.0;
        int n = line.Count;
        var nrm = new (double nx, double ny, bool valid)[n];
        for (int i = 0; i < n; i++)
        {
            double tx, ty;
            if (i == 0) { tx = line[1].x - line[0].x; ty = line[1].y - line[0].y; }
            else if (i == n - 1) { tx = line[n - 1].x - line[n - 2].x; ty = line[n - 1].y - line[n - 2].y; }
            else { tx = line[i + 1].x - line[i - 1].x; ty = line[i + 1].y - line[i - 1].y; }
            double len = Math.Sqrt(tx * tx + ty * ty);
            if (len < TangentEpsilon) { nrm[i] = (0, 0, false); continue; }
            double ux = tx / len, uy = ty / len;
            nrm[i] = (-uy, ux, true);   // XY 旋 90°(左侧法向)
        }
        int firstValid = -1;
        for (int i = 0; i < n; i++) if (nrm[i].valid) { firstValid = i; break; }
        if (firstValid < 0) return null;
        for (int i = 0; i < firstValid; i++) nrm[i] = (nrm[firstValid].nx, nrm[firstValid].ny, true);
        double lnx = nrm[firstValid].nx, lny = nrm[firstValid].ny;
        for (int i = firstValid + 1; i < n; i++)
        {
            if (nrm[i].valid) { lnx = nrm[i].nx; lny = nrm[i].ny; }
            else nrm[i] = (lnx, lny, true);
        }
        var poly = new List<(double x, double y)>(2 * n + 1);
        for (int i = 0; i < n; i++) poly.Add((line[i].x + h * nrm[i].nx, line[i].y + h * nrm[i].ny));   // 左顺
        for (int i = n - 1; i >= 0; i--) poly.Add((line[i].x - h * nrm[i].nx, line[i].y - h * nrm[i].ny)); // 右逆
        poly.Add((line[0].x + h * nrm[0].nx, line[0].y + h * nrm[0].ny));   // 闭合
        return poly;
    }

    /// <summary>多条中线 → 各闭合 ribbon。不足 2 点/全退化的中线跳过。</summary>
    public static List<List<(double x, double y)>> BuildRibbons(IReadOnlyList<IReadOnlyList<(double x, double y)>> centerlines, double widthM)
    {
        var result = new List<List<(double x, double y)>>();
        if (centerlines == null || widthM <= 0) return result;
        foreach (var line in centerlines)
        {
            var r = BuildRibbon(line, widthM);
            if (r != null) result.Add(r);
        }
        return result;
    }
}
