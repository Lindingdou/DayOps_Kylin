using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 两期三角网算量（原 MeshEditLib「体编辑 → 两期三角网算量」VolumeSplitDialog 的计算核）：
/// 在两面 XY 重叠范围内按格距栅格采样 z，逐格 dz = 后期 − 前期，正为填(填方)、负为挖(挖方)，×格面积累加。
/// 只有两面都采到的格参与；返回 挖/填/净 + 有效格数 + 重叠面积。
/// </summary>
public static class SurfaceVolume
{
    public sealed class Result
    {
        public double CutM3, FillM3;           // 挖方(前期高于后期) / 填方(后期高于前期)
        public double NetM3 => FillM3 - CutM3;
        public int Cells, ValidCells;
        public double CellSize;
        public double OverlapAreaM2 => ValidCells * CellSize * CellSize;
        public double MinDz = double.MaxValue, MaxDz = double.MinValue;
        /// <summary>逐格 (x,y,dz)（有效格），供着色/导出。</summary>
        public List<(double x, double y, double dz)> Diff = new();
    }

    /// <summary>格距 ≤0 时自动取重叠范围短边/150。</summary>
    public static Result CutFill(double[] vA, int[] tA, double[] vB, int[] tB, double cell = 0, int maxCells = 4_000_000)
    {
        var r = new Result();
        var (ax0, ay0, ax1, ay1) = BoundsXY(vA);
        var (bx0, by0, bx1, by1) = BoundsXY(vB);
        double x0 = Math.Max(ax0, bx0), y0 = Math.Max(ay0, by0), x1 = Math.Min(ax1, bx1), y1 = Math.Min(ay1, by1);
        if (x1 <= x0 || y1 <= y0) { r.CellSize = cell; return r; }
        if (cell <= 0) cell = Math.Max(Math.Min(x1 - x0, y1 - y0) / 150.0, 1e-6);
        long nx = (long)Math.Ceiling((x1 - x0) / cell), ny = (long)Math.Ceiling((y1 - y0) / cell);
        while (nx * ny > maxCells) { cell *= 1.5; nx = (long)Math.Ceiling((x1 - x0) / cell); ny = (long)Math.Ceiling((y1 - y0) / cell); }
        r.CellSize = cell;
        double area = cell * cell;
        var gridA = new TriGrid(vA, tA, cell);
        var gridB = new TriGrid(vB, tB, cell);
        for (long j = 0; j < ny; j++)
        {
            double cy = y0 + (j + 0.5) * cell;
            for (long i = 0; i < nx; i++)
            {
                double cx = x0 + (i + 0.5) * cell;
                r.Cells++;
                var za = gridA.Sample(cx, cy); if (za == null) continue;
                var zb = gridB.Sample(cx, cy); if (zb == null) continue;
                double dz = zb.Value - za.Value;
                r.ValidCells++;
                if (dz >= 0) r.FillM3 += dz * area; else r.CutM3 += -dz * area;
                if (dz < r.MinDz) r.MinDz = dz; if (dz > r.MaxDz) r.MaxDz = dz;
                r.Diff.Add((cx, cy, dz));
            }
        }
        if (r.ValidCells == 0) { r.MinDz = r.MaxDz = 0; }
        return r;
    }

    public static (double x0, double y0, double x1, double y1) BoundsXY(double[] v)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int i = 0; i + 2 < v.Length; i += 3)
        {
            if (v[i] < x0) x0 = v[i]; if (v[i] > x1) x1 = v[i];
            if (v[i + 1] < y0) y0 = v[i + 1]; if (v[i + 1] > y1) y1 = v[i + 1];
        }
        if (x0 == double.MaxValue) return (0, 0, 0, 0);
        return (x0, y0, x1, y1);
    }

    /// <summary>三角形 XY 包围盒分桶的快速采样器（大网逐点采样避免 O(T) 扫描）。</summary>
    public sealed class TriGrid
    {
        private readonly double[] _v; private readonly int[] _t;
        private readonly double _x0, _y0, _cs; private readonly int _nx, _ny;
        private readonly List<int>[] _buckets;

        public TriGrid(double[] v, int[] t, double cell)
        {
            _v = v; _t = t;
            var (x0, y0, x1, y1) = BoundsXY(v);
            _x0 = x0; _y0 = y0;
            double span = Math.Max(x1 - x0, y1 - y0);
            _cs = Math.Max(cell * 4, span / 128);
            if (_cs <= 0) _cs = 1;
            _nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / _cs) + 1);
            _ny = Math.Max(1, (int)Math.Ceiling((y1 - y0) / _cs) + 1);
            _buckets = new List<int>[_nx * _ny];
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k] * 3, b = t[k + 1] * 3, c = t[k + 2] * 3;
                if (a < 0 || b < 0 || c < 0 || a + 2 >= v.Length || b + 2 >= v.Length || c + 2 >= v.Length) continue;
                double tx0 = Math.Min(v[a], Math.Min(v[b], v[c])), tx1 = Math.Max(v[a], Math.Max(v[b], v[c]));
                double ty0 = Math.Min(v[a + 1], Math.Min(v[b + 1], v[c + 1])), ty1 = Math.Max(v[a + 1], Math.Max(v[b + 1], v[c + 1]));
                int i0 = Idx(tx0, _x0), i1 = Idx(tx1, _x0), j0 = Idx(ty0, _y0), j1 = Idx(ty1, _y0);
                for (int j = Math.Max(0, j0); j <= Math.Min(_ny - 1, j1); j++)
                    for (int i = Math.Max(0, i0); i <= Math.Min(_nx - 1, i1); i++)
                        (_buckets[j * _nx + i] ??= new List<int>()).Add(k);
            }
        }
        private int Idx(double v, double o) => (int)Math.Floor((v - o) / _cs);

        public double? Sample(double x, double y)
        {
            int i = Idx(x, _x0), j = Idx(y, _y0);
            if (i < 0 || j < 0 || i >= _nx || j >= _ny) return null;
            var bk = _buckets[j * _nx + i];
            if (bk == null) return null;
            foreach (var k in bk)
            {
                int a = _t[k] * 3, b = _t[k + 1] * 3, c = _t[k + 2] * 3;
                double ax = _v[a], ay = _v[a + 1], bx = _v[b], by = _v[b + 1], cx = _v[c], cy = _v[c + 1];
                double d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(d) < 1e-12) continue;
                double w0 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
                double w1 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
                double w2 = 1 - w0 - w1;
                const double eps = -1e-9;
                if (w0 >= eps && w1 >= eps && w2 >= eps) return w0 * _v[a + 2] + w1 * _v[b + 2] + w2 * _v[c + 2];
            }
            return null;
        }
    }
}
