using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 等值线（等高线）提取 —— Marching Squares：在规则网格上抽取某高程 level 的等值线段。
/// 网格 grid[ix,iy]，原点 (x0,y0)，步距 (dx,dy)。纯逻辑、可单测。
/// 输出线段列表（每段 4 个数 x0,y0,x1,y1），供渲染为折线/直线。
/// </summary>
public static class Contour
{
    // case → 连接的边对（边号 0=下 1=右 2=上 3=左）
    private static readonly int[][] Table =
    {
        new int[]{},           // 0
        new[]{3,0},            // 1  a
        new[]{0,1},            // 2  b
        new[]{3,1},            // 3  a,b
        new[]{1,2},            // 4  c
        new[]{3,0,1,2},        // 5  a,c (鞍点)
        new[]{0,2},            // 6  b,c
        new[]{3,2},            // 7  a,b,c
        new[]{2,3},            // 8  d
        new[]{2,0},            // 9  a,d
        new[]{0,1,2,3},        // 10 b,d (鞍点)
        new[]{2,1},            // 11 a,b,d
        new[]{1,3},            // 12 c,d
        new[]{1,0},            // 13 a,c,d
        new[]{0,3},            // 14 b,c,d
        new int[]{},           // 15
    };

    public static List<(double x0, double y0, double x1, double y1)> MarchingSquares(
        double[,] grid, double x0, double y0, double dx, double dy, double level)
    {
        var segs = new List<(double, double, double, double)>();
        int nx = grid.GetLength(0), ny = grid.GetLength(1);

        for (int ix = 0; ix + 1 < nx; ix++)
        for (int iy = 0; iy + 1 < ny; iy++)
        {
            double a = grid[ix, iy], b = grid[ix + 1, iy], c = grid[ix + 1, iy + 1], d = grid[ix, iy + 1];
            int code = (a > level ? 1 : 0) | (b > level ? 2 : 0) | (c > level ? 4 : 0) | (d > level ? 8 : 0);
            var edges = Table[code];
            for (int k = 0; k + 1 < edges.Length; k += 2)
            {
                var p0 = EdgePoint(edges[k], ix, iy, a, b, c, d, x0, y0, dx, dy, level);
                var p1 = EdgePoint(edges[k + 1], ix, iy, a, b, c, d, x0, y0, dx, dy, level);
                segs.Add((p0.x, p0.y, p1.x, p1.y));
            }
        }
        return segs;
    }

    private static (double x, double y) EdgePoint(int edge, int ix, int iy,
        double a, double b, double c, double d, double x0, double y0, double dx, double dy, double L)
    {
        double t;
        switch (edge)
        {
            case 0: t = Frac(a, b, L); return (x0 + (ix + t) * dx, y0 + iy * dy);              // 下 a→b
            case 1: t = Frac(b, c, L); return (x0 + (ix + 1) * dx, y0 + (iy + t) * dy);        // 右 b→c
            case 2: t = Frac(c, d, L); return (x0 + (ix + 1 - t) * dx, y0 + (iy + 1) * dy);    // 上 c→d
            default: t = Frac(d, a, L); return (x0 + ix * dx, y0 + (iy + 1 - t) * dy);         // 左 d→a
        }
    }

    private static double Frac(double v0, double v1, double L)
    {
        double den = v1 - v0;
        return System.Math.Abs(den) < 1e-12 ? 0.5 : (L - v0) / den;
    }

    /// <summary>散点 → 规则网格（反距离加权 IDW，power=2）。返回 grid[nx,ny] 及原点/步距。</summary>
    public static double[,] GridFromPoints(
        IReadOnlyList<(double x, double y, double z)> pts, int nx, int ny,
        out double x0, out double y0, out double dx, out double dy)
    {
        x0 = y0 = dx = dy = 0;
        var g = new double[nx < 2 ? 2 : nx, ny < 2 ? 2 : ny];
        if (pts.Count == 0) return g;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        nx = g.GetLength(0); ny = g.GetLength(1);
        x0 = minX; y0 = minY;
        dx = maxX > minX ? (maxX - minX) / (nx - 1) : 1;
        dy = maxY > minY ? (maxY - minY) / (ny - 1) : 1;
        return GridInto(pts, nx, ny, x0, y0, dx, dy);
    }

    /// <summary>把散点 IDW 插值到指定原点/步距的网格（供两期差值等用同一网格）。</summary>
    public static double[,] GridInto(
        IReadOnlyList<(double x, double y, double z)> pts, int nx, int ny, double x0, double y0, double dx, double dy)
    {
        var g = new double[nx, ny];
        for (int ix = 0; ix < nx; ix++)
        for (int iy = 0; iy < ny; iy++)
        {
            double px = x0 + ix * dx, py = y0 + iy * dy;
            double num = 0, den = 0, exact = 0; bool onPoint = false;
            foreach (var p in pts)
            {
                double d2 = (px - p.x) * (px - p.x) + (py - p.y) * (py - p.y);
                if (d2 < 1e-9) { exact = p.z; onPoint = true; break; }
                double w = 1.0 / d2;
                num += w * p.z; den += w;
            }
            g[ix, iy] = onPoint ? exact : (den > 0 ? num / den : 0);
        }
        return g;
    }
}
