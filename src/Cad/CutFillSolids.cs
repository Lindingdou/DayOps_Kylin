using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 两期三角网算量·填方/挖方封闭体（原 MeshEditLib VolumeSplitDialog 确定后 native 两步拾取 jig 的托管等价）：
/// 两面 XY 重叠范围按「体积格网」采样 dz = 后期 − 前期 → 最小高差去噪 → 形态学开运算(去噪半径) → 4 邻域连通块 →
/// 最小台阶高过滤(区内最大高差不足整块丢弃) → 每连通块在「渲染格网」(≥体积格网时降三角)上生成一个**水密实体**
/// (顶=高面、底=低面、周界竖壁, 角点共用索引)。体积按体积格网细格累加, 精度不受渲染格影响。纯逻辑、可单测。
/// </summary>
public static class CutFillSolids
{
    public sealed class Options
    {
        public double GridCell = 2.0;     // 体积格网(m)
        public double RenderCell = 6.0;   // 渲染格网(m); > GridCell 时解耦
        public double MinDz = 1.0;        // 最小高差(m): 小于此视为未变化
        public int OpenRadius = 1;        // 去噪半径(格), 0=不去噪
        public double MinBenchH = 3.0;    // 最小台阶高(m), 0=不过滤
    }

    public sealed class Body
    {
        public bool IsFill;               // true=填方(后期更高) false=挖方(后期更低)
        public int Index;                 // 同类序号(1 起)
        public int Cells;                 // 体积格数
        public double AreaM2;             // 投影面积
        public double VolumeM3;           // 体积(细格累加)
        public double MaxDz;              // 区内最大 |高差|
        public double MeanDz;
        public List<(double x, double y, double z)> Verts = new();
        public List<(int a, int b, int c)> Tris = new();
        public List<(int i, int j)> CellIdx = new();   // 细格索引(供着色)
    }

    public sealed class Result
    {
        public SurfaceVolume.Result Raw = new();      // 未去噪的原始两期算量
        public List<Body> Bodies = new();
        public double FillM3, CutM3;                 // 去噪/过滤后的填/挖体积
        public double NetM3 => FillM3 - CutM3;
        public int Nx, Ny;
        public double X0, Y0, Cell;
        public double[,]? Dz;                         // 细格 dz(NaN=无效)
        public int[,]? Label;                         // 细格所属 Body 序号(-1 无)
        public int DroppedNoise, DroppedLowBench;     // 被开运算去掉的格数 / 被最小台阶高整块丢弃的连通块数
        public string Error = "";
    }

    public static Result Compute(double[] vA, int[] tA, double[] vB, int[] tB, Options o)
    {
        var res = new Result();
        double cell = o.GridCell > 0 ? o.GridCell : 2.0;
        res.Raw = SurfaceVolume.CutFill(vA, tA, vB, tB, cell);
        if (res.Raw.ValidCells == 0) { res.Error = "两面 XY 无重叠(或都采不到高程)"; return res; }
        cell = res.Raw.CellSize;   // CutFill 可能因格数上限放大格距
        var (ax0, ay0, ax1, ay1) = SurfaceVolume.BoundsXY(vA);
        var (bx0, by0, bx1, by1) = SurfaceVolume.BoundsXY(vB);
        double x0 = Math.Max(ax0, bx0), y0 = Math.Max(ay0, by0), x1 = Math.Min(ax1, bx1), y1 = Math.Min(ay1, by1);
        int nx = (int)Math.Ceiling((x1 - x0) / cell), ny = (int)Math.Ceiling((y1 - y0) / cell);
        if (nx <= 0 || ny <= 0) { res.Error = "重叠范围退化"; return res; }
        res.Nx = nx; res.Ny = ny; res.X0 = x0; res.Y0 = y0; res.Cell = cell;

        var gA = new SurfaceVolume.TriGrid(vA, tA, cell);
        var gB = new SurfaceVolume.TriGrid(vB, tB, cell);
        var zA = new double[nx, ny]; var zB = new double[nx, ny]; var dz = new double[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                double cx = x0 + (i + 0.5) * cell, cy = y0 + (j + 0.5) * cell;
                var a = gA.Sample(cx, cy); var b = gB.Sample(cx, cy);
                if (a == null || b == null) { zA[i, j] = zB[i, j] = dz[i, j] = double.NaN; continue; }
                zA[i, j] = a.Value; zB[i, j] = b.Value; dz[i, j] = b.Value - a.Value;
            }
        res.Dz = dz;

        // 分类: +1 填 / -1 挖 / 0 未变化
        var cls = new sbyte[nx, ny];
        double minDz = Math.Max(0, o.MinDz);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                double d = dz[i, j];
                if (double.IsNaN(d)) continue;
                if (d >= minDz && d > 0) cls[i, j] = 1; else if (-d >= minDz && d < 0) cls[i, j] = -1;
            }

        // 形态学开运算(逐类): 腐蚀再膨胀, 方形结构元 (2r+1)²
        if (o.OpenRadius > 0)
        {
            int before = CountNonZero(cls);
            var opened = new sbyte[nx, ny];
            foreach (sbyte k in new sbyte[] { 1, -1 })
            {
                var mask = new bool[nx, ny];
                for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) mask[i, j] = cls[i, j] == k;
                var er = Erode(mask, nx, ny, o.OpenRadius);
                var di = Dilate(er, nx, ny, o.OpenRadius);
                for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) if (di[i, j] && cls[i, j] == k) opened[i, j] = k;
            }
            cls = opened;
            res.DroppedNoise = before - CountNonZero(cls);
        }

        // 连通块(4 邻域, 同类)
        var label = new int[nx, ny];
        for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) label[i, j] = -1;
        var comps = new List<(sbyte k, List<(int i, int j)> cells)>();
        var stack = new Stack<(int, int)>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (cls[i, j] == 0 || label[i, j] >= 0) continue;
                sbyte k = cls[i, j];
                var cellsL = new List<(int, int)>();
                int id = comps.Count;
                label[i, j] = id; stack.Push((i, j));
                while (stack.Count > 0)
                {
                    var (ci, cj) = stack.Pop();
                    cellsL.Add((ci, cj));
                    Visit(ci + 1, cj); Visit(ci - 1, cj); Visit(ci, cj + 1); Visit(ci, cj - 1);
                    void Visit(int ni, int nj)
                    {
                        if (ni < 0 || nj < 0 || ni >= nx || nj >= ny) return;
                        if (cls[ni, nj] != k || label[ni, nj] >= 0) return;
                        label[ni, nj] = id; stack.Push((ni, nj));
                    }
                }
                comps.Add((k, cellsL));
            }

        // 过滤最小台阶高 + 统计 + 建体
        double area = cell * cell;
        int stride = o.RenderCell > cell ? Math.Max(1, (int)Math.Round(o.RenderCell / cell)) : 1;
        int fillIdx = 0, cutIdx = 0;
        var keepLabel = new int[nx, ny];
        for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) keepLabel[i, j] = -1;
        foreach (var (k, cellsL) in comps)
        {
            double maxDz = 0, sum = 0, vol = 0;
            foreach (var (i, j) in cellsL)
            {
                double d = Math.Abs(dz[i, j]);
                if (d > maxDz) maxDz = d;
                sum += d; vol += d * area;
            }
            if (o.MinBenchH > 0 && maxDz < o.MinBenchH) { res.DroppedLowBench++; continue; }
            var body = new Body
            {
                IsFill = k > 0, Index = k > 0 ? ++fillIdx : ++cutIdx, Cells = cellsL.Count,
                AreaM2 = cellsL.Count * area, VolumeM3 = vol, MaxDz = maxDz, MeanDz = cellsL.Count > 0 ? sum / cellsL.Count : 0,
            };
            body.CellIdx.AddRange(cellsL);
            int bid = res.Bodies.Count;
            foreach (var (i, j) in cellsL) keepLabel[i, j] = bid;
            BuildSolid(body, cellsL, nx, ny, x0, y0, cell, stride, zA, zB, k > 0);
            if (k > 0) res.FillM3 += vol; else res.CutM3 += vol;
            res.Bodies.Add(body);
        }
        res.Label = keepLabel;
        return res;
    }

    private static int CountNonZero(sbyte[,] a) { int n = 0; foreach (var v in a) if (v != 0) n++; return n; }

    private static bool[,] Erode(bool[,] m, int nx, int ny, int r)
    {
        var o = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (!m[i, j]) continue;
                bool all = true;
                for (int di = -r; di <= r && all; di++)
                    for (int dj = -r; dj <= r; dj++)
                    {
                        int a = i + di, b = j + dj;
                        if (a < 0 || b < 0 || a >= nx || b >= ny || !m[a, b]) { all = false; break; }
                    }
                o[i, j] = all;
            }
        return o;
    }

    private static bool[,] Dilate(bool[,] m, int nx, int ny, int r)
    {
        var o = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (!m[i, j]) continue;
                for (int di = -r; di <= r; di++)
                    for (int dj = -r; dj <= r; dj++)
                    {
                        int a = i + di, b = j + dj;
                        if (a >= 0 && b >= 0 && a < nx && b < ny) o[a, b] = true;
                    }
            }
        return o;
    }

    /// <summary>
    /// 连通块 → 水密实体: 在粗格(stride×细格)上取并集掩膜, 角点 z = 相邻在体粗格中心 z 的均值(顶=高面/底=低面),
    /// 每粗格 顶 2 三角(朝上) + 底 2 三角(朝下) + 无邻格的边竖壁 2 三角; 角点共用索引 → 由构造水密。
    /// </summary>
    private static void BuildSolid(Body body, List<(int i, int j)> cells, int nx, int ny, double x0, double y0, double cell, int stride,
        double[,] zA, double[,] zB, bool isFill)
    {
        int cnx = (nx + stride - 1) / stride, cny = (ny + stride - 1) / stride;
        var mask = new bool[cnx, cny];
        var topSum = new double[cnx, cny]; var botSum = new double[cnx, cny]; var cnt = new int[cnx, cny];
        foreach (var (i, j) in cells)
        {
            int ci = i / stride, cj = j / stride;
            mask[ci, cj] = true;
            double hi = Math.Max(zA[i, j], zB[i, j]), lo = Math.Min(zA[i, j], zB[i, j]);
            topSum[ci, cj] += hi; botSum[ci, cj] += lo; cnt[ci, cj]++;
        }
        double ccell = cell * stride;
        var cornerIdx = new Dictionary<(int, int, bool), int>();
        int Corner(int ci, int cj, bool top)
        {
            var key = (ci, cj, top);
            if (cornerIdx.TryGetValue(key, out int id)) return id;
            double s = 0; int n = 0;
            for (int di = -1; di <= 0; di++)
                for (int dj = -1; dj <= 0; dj++)
                {
                    int a = ci + di, b = cj + dj;
                    if (a < 0 || b < 0 || a >= cnx || b >= cny || !mask[a, b] || cnt[a, b] == 0) continue;
                    s += (top ? topSum[a, b] : botSum[a, b]) / cnt[a, b]; n++;
                }
            double z = n > 0 ? s / n : 0;
            id = body.Verts.Count;
            body.Verts.Add((x0 + ci * ccell, y0 + cj * ccell, z));
            cornerIdx[key] = id;
            return id;
        }
        bool In(int ci, int cj) => ci >= 0 && cj >= 0 && ci < cnx && cj < cny && mask[ci, cj];
        for (int ci = 0; ci < cnx; ci++)
            for (int cj = 0; cj < cny; cj++)
            {
                if (!mask[ci, cj]) continue;
                int t00 = Corner(ci, cj, true), t10 = Corner(ci + 1, cj, true), t11 = Corner(ci + 1, cj + 1, true), t01 = Corner(ci, cj + 1, true);
                int b00 = Corner(ci, cj, false), b10 = Corner(ci + 1, cj, false), b11 = Corner(ci + 1, cj + 1, false), b01 = Corner(ci, cj + 1, false);
                body.Tris.Add((t00, t10, t11)); body.Tris.Add((t00, t11, t01));      // 顶(朝上 CCW)
                body.Tris.Add((b00, b11, b10)); body.Tris.Add((b00, b01, b11));      // 底(朝下)
                if (!In(ci, cj - 1)) { body.Tris.Add((t00, b00, b10)); body.Tris.Add((t00, b10, t10)); }   // 南壁(外法向 -y)
                if (!In(ci + 1, cj)) { body.Tris.Add((t10, b10, b11)); body.Tris.Add((t10, b11, t11)); }   // 东壁
                if (!In(ci, cj + 1)) { body.Tris.Add((t11, b11, b01)); body.Tris.Add((t11, b01, t01)); }   // 北壁
                if (!In(ci - 1, cj)) { body.Tris.Add((t01, b01, b00)); body.Tris.Add((t01, b00, t00)); }   // 西壁
            }
        _ = isFill;
    }
}
