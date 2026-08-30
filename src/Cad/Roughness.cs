namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 地表粗糙度 —— 高程网格每格取 3×3 邻域内高程极差(max−min)作粗糙度。纯逻辑、可单测。
/// </summary>
public static class Roughness
{
    public static double[,] Compute(double[,] grid)
    {
        int nx = grid.GetLength(0), ny = grid.GetLength(1);
        var r = new double[nx, ny];
        for (int ix = 0; ix < nx; ix++)
        for (int iy = 0; iy < ny; iy++)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int di = -1; di <= 1; di++)
            for (int dj = -1; dj <= 1; dj++)
            {
                int jx = ix + di, jy = iy + dj;
                if (jx < 0 || jx >= nx || jy < 0 || jy >= ny) continue;
                double v = grid[jx, jy];
                if (v < min) min = v; if (v > max) max = v;
            }
            r[ix, iy] = max - min;
        }
        return r;
    }
}
