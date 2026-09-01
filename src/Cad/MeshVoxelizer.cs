using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 封闭三角网体素化 —— 忠实原 BlockModelLib「离散化模型」(把采矿模型体素化成块体)。
/// 在网格包围盒内按 <paramref name="cellSize"/> 布规则格, 格心落在**闭合网内**(广义缠绕数 GWN)的保留成块体。
/// 复用 <see cref="WindingNumberTester.IsInsideClosed"/>(已移基元)。纯几何、确定性、可单测。
/// 需封闭(水密)三角网体; 开放面 GWN 不可靠(调用方应先诊断闭合性)。
/// </summary>
public static class MeshVoxelizer
{
    /// <summary>体素化结果: 块体中心列表 + 用的格尺寸 + 格网维度 + 是否因过大被拒。</summary>
    public sealed class Result
    {
        public List<(double x, double y, double z)> Centers = new();
        public double CellSize;
        public int Nx, Ny, Nz;
        public bool TooLarge;         // 格数超上限, 未体素化(建议加大格尺寸)
        public long TotalCells => (long)Nx * Ny * Nz;
    }

    /// <summary>把封闭三角网(扁平 verts=[x,y,z,…], tris=[a,b,c,…]) 体素化。cellSize 块边长; maxCells 格数上限(超则拒)。</summary>
    public static Result Voxelize(double[] verts, int[] tris, double cellSize, long maxCells = 3_000_000)
    {
        var res = new Result { CellSize = cellSize };
        if (verts == null || tris == null || verts.Length < 9 || tris.Length < 3 || cellSize <= 0) return res;

        double xn = double.MaxValue, yn = double.MaxValue, zn = double.MaxValue;
        double xx = double.MinValue, yx = double.MinValue, zx = double.MinValue;
        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            double x = verts[i], y = verts[i + 1], z = verts[i + 2];
            if (x < xn) xn = x; if (y < yn) yn = y; if (z < zn) zn = z;
            if (x > xx) xx = x; if (y > yx) yx = y; if (z > zx) zx = z;
        }
        res.Nx = Math.Max(1, (int)Math.Ceiling((xx - xn) / cellSize));
        res.Ny = Math.Max(1, (int)Math.Ceiling((yx - yn) / cellSize));
        res.Nz = Math.Max(1, (int)Math.Ceiling((zx - zn) / cellSize));
        if (res.TotalCells > maxCells) { res.TooLarge = true; return res; }

        var w = new WindingNumberTester(verts, tris);
        for (int k = 0; k < res.Nz; k++)
        {
            double cz = zn + (k + 0.5) * cellSize;
            for (int j = 0; j < res.Ny; j++)
            {
                double cy = yn + (j + 0.5) * cellSize;
                for (int i = 0; i < res.Nx; i++)
                {
                    double cx = xn + (i + 0.5) * cellSize;
                    if (w.IsInsideClosed(cx, cy, cz)) res.Centers.Add((cx, cy, cz));
                }
            }
        }
        return res;
    }
}
