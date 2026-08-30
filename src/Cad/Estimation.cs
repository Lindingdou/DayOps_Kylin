using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 快速估值 —— 品位样本 IDW 插值到网格后，把每个网格单元渲染为品位配色方块（估值面可视化）。
/// 复用 <see cref="Contour.GridInto"/>(IDW) + <see cref="BlockModel.GradeColor"/>。纯逻辑、可单测。
/// </summary>
public static class Estimation
{
    /// <summary>网格 → 品位配色方块（每单元一个 RectEntity）。</summary>
    public static List<SceneEntity> BuildCells(double[,] grid, double x0, double y0, double dx, double dy, double min, double max)
    {
        var list = new List<SceneEntity>();
        int nx = grid.GetLength(0), ny = grid.GetLength(1);
        for (int ix = 0; ix < nx; ix++)
        for (int iy = 0; iy < ny; iy++)
        {
            var (r, g, b) = BlockModel.GradeColor(grid[ix, iy], min, max);
            double cx = x0 + ix * dx, cy = y0 + iy * dy;
            list.Add(new RectEntity { X0 = cx - dx / 2, Y0 = cy - dy / 2, X1 = cx + dx / 2, Y1 = cy + dy / 2, Cr = r, Cg = g, Cb = b });
        }
        return list;
    }

    /// <summary>网格值域(min,max)。</summary>
    public static (double min, double max) Range(double[,] grid)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in grid) { if (v < min) min = v; if (v > max) max = v; }
        return (min, max);
    }
}
