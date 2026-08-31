using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 煤厚分析 —— 等厚线(isopach)。忠实原 GeoDataBase <c>ThicknessSurfaceBuilder</c>「见煤点的(底板标高,煤厚)
/// 做 2.5D 插值」的煤厚面部分：把观测/见煤点 (x, y, 煤厚) 以 IDW 插值到规则网格, 再逐厚度层 Marching Squares
/// 抽等厚线。附煤厚分布统计(min/max/mean/std/分位)。2D 线段架构下呈平面等厚线图(非 3D 定位面)。
/// 复用 <see cref="Contour"/>(IDW 网格 + marching squares + 层表) 与 <see cref="Statistics"/>。纯逻辑、可单测。
/// </summary>
public static class ThicknessSurface
{
    public readonly record struct IsopachSeg(double X0, double Y0, double X1, double Y1, double Level);

    public readonly record struct Result(
        List<IsopachSeg> Lines,      // 等厚线段(带层厚)
        List<double> Levels,         // 实际布线的厚度层
        Statistics.Summary Stats);   // 煤厚分布统计

    /// <summary>
    /// 煤厚等厚线：pts=(x,y,厚度)。interval&gt;0 取整数倍厚度层, 否则 auto 10 层。gridN=插值网格边格数。
    /// 点&lt;3 或厚度无起伏 → 无线(仅统计)。
    /// </summary>
    public static Result Isopach(IReadOnlyList<(double x, double y, double thickness)> pts, int gridN = 64, double interval = 0)
    {
        var lines = new List<IsopachSeg>();
        var levels = new List<double>();
        var tvals = new List<double>(pts?.Count ?? 0);
        if (pts != null) foreach (var p in pts) tvals.Add(p.thickness);
        var stats = Statistics.Describe(tvals);
        if (pts == null || pts.Count < 3 || stats.Max - stats.Min < 1e-9) return new Result(lines, levels, stats);

        var pts3 = new List<(double x, double y, double z)>(pts.Count);
        foreach (var p in pts) pts3.Add((p.x, p.y, p.thickness));
        int n = gridN < 4 ? 4 : gridN;
        var grid = Contour.GridFromPoints(pts3, n, n, out double gx0, out double gy0, out double gdx, out double gdy);
        levels = Contour.Levels(stats.Min, stats.Max, interval);
        foreach (double L in levels)
            foreach (var s in Contour.MarchingSquares(grid, gx0, gy0, gdx, gdy, L))
                lines.Add(new IsopachSeg(s.x0, s.y0, s.x1, s.y1, L));
        return new Result(lines, levels, stats);
    }
}
