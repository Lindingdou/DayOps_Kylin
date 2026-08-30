namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 地表曲率 —— 高程网格拉普拉斯：curv = (上+下+左+右 − 4·中) / cell²。
/// 负=凸(山脊/峰)，正=凹(沟/坑)，0=平/斜面。边界置 0。纯逻辑、可单测。
/// </summary>
public static class Curvature
{
    public static double[,] Compute(double[,] grid, double cell)
    {
        int nx = grid.GetLength(0), ny = grid.GetLength(1);
        var c = new double[nx, ny];
        double inv = cell > 1e-9 ? 1.0 / (cell * cell) : 1.0;
        for (int ix = 1; ix < nx - 1; ix++)
        for (int iy = 1; iy < ny - 1; iy++)
        {
            double lap = grid[ix - 1, iy] + grid[ix + 1, iy] + grid[ix, iy - 1] + grid[ix, iy + 1] - 4 * grid[ix, iy];
            c[ix, iy] = lap * inv;
        }
        return c;
    }
}
