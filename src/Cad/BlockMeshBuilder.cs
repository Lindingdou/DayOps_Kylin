using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 块体 → 真六面体网格（体素）。
///
/// 此前 Kylin 把每个块画成一张平面矩形（RectEntity，按 Sx/Sy 足印放在 Z 上），
/// 于是三维里看到的是一摞平板而不是体素 —— 本类改为按块的 Sx/Sy/Sz 生成六面体。
///
/// 控量靠**整块剔除**，口径照原版 SurfaceInstanceBuilder：一个块若六个方向都有同尺寸邻居
/// （即六张面都被别的可见块占着），它整个埋在模型内部，直接不发；只发「暴露」的壳层块，
/// 且暴露块仍画**完整六面**。100×100×30 的实心模型由 30 万块降到约 2.6 万块。
///
/// 注意原版特意**没有**按面剔（写了 GreedyMeshFace 但关掉了）：只留外表面会得到一层空壳，
/// 视点进到模型内部或剖切时会穿模。这里照此办理 —— 剔块不剔面。
/// 尺寸不同的邻居（父块 vs 子块）面不重合，算不上「占着」，于是两边都留，不会漏。
///
/// 纯逻辑、可单测：只出顶点/三角/逐顶点色，不碰 UI 与渲染。
/// </summary>
public static class BlockMeshBuilder
{
    /// <summary>一个待建的块：中心 + 半尺寸 + 颜色(0..1)。</summary>
    public readonly record struct Cell(
        double X, double Y, double Z, double Hx, double Hy, double Hz, float R, float G, float B);

    /// <summary>生成结果：顶点 / 三角 / 逐顶点色 / 剔除掉的内部面数。</summary>
    public sealed class Result
    {
        public List<(double x, double y, double z)> Verts = new();
        public List<(int a, int b, int c)> Tris = new();
        public List<(float r, float g, float b)> Colors = new();
        /// <summary>立方体的棱（顶点索引对，已按位置去重）。不用三角边——那会把每个面的对角线也画出来。</summary>
        public List<(int i, int j)> Edges = new();
        /// <summary>实际画出的块数（壳层块）。</summary>
        public int DrawnCells;
        /// <summary>整块埋在内部而省掉的块数（诊断/回报用）。</summary>
        public int CulledCells;
        /// <summary>达到面数上限而未生成的块数；&gt;0 表示显示不完整。</summary>
        public int TruncatedCells;
        /// <summary>面数（每块 6 张）。</summary>
        public int FaceCount => DrawnCells * 6;
    }

    /// <summary>坐标 → 面键用的定点数（矿区坐标可到 1e7 量级，0.1 mm 精度足够且不溢出 long）。</summary>
    private static long Q(double v) => (long)Math.Round(v * 1e4);

    /// <summary>棱去重用的位置键（同一条棱会被相邻的两张面、相邻的两块各发一次）。</summary>
    private static (long, long, long, long, long, long) EdgeKey((double x, double y, double z) a, (double x, double y, double z) b)
    {
        var ka = (Q(a.x), Q(a.y), Q(a.z));
        var kb = (Q(b.x), Q(b.y), Q(b.z));
        return ka.CompareTo(kb) <= 0
            ? (ka.Item1, ka.Item2, ka.Item3, kb.Item1, kb.Item2, kb.Item3)
            : (kb.Item1, kb.Item2, kb.Item3, ka.Item1, ka.Item2, ka.Item3);
    }

    /// <summary>
    /// 建壳层网格。maxCells 为画出的块数上限（超出即停止并计入 <see cref="Result.TruncatedCells"/>），
    /// 防止极端筛选（棋盘式可见性，人人都暴露）把三角数炸开。
    /// </summary>
    public static Result Build(IReadOnlyList<Cell> cells, int maxCells = 300_000)
    {
        var res = new Result();
        if (cells == null || cells.Count == 0) return res;
        if (cells.Count > maxCells) return BuildDecimated(cells, maxCells);
        var edgeSeen = new HashSet<(long, long, long, long, long, long)>();

        // ① 每张面出现几次：出现两次 = 被两个可见块共用。
        //    面键 = (轴, 所在平面坐标, 面内四至)，故只有「同尺寸且正对」的邻居才配得上对。
        var seen = new Dictionary<(int axis, long plane, long u0, long v0, long u1, long v1), int>(cells.Count * 3);
        foreach (var c in cells)
            for (int f = 0; f < 6; f++)
            {
                var k = FaceKey(c, f);
                seen[k] = seen.TryGetValue(k, out int n) ? n + 1 : 1;
            }

        // ② 六张面都被占 = 整块埋在内部 → 不发；否则发完整六面(同原版: 剔块不剔面)
        foreach (var c in cells)
        {
            bool buried = true;
            for (int f = 0; f < 6 && buried; f++) buried = seen[FaceKey(c, f)] > 1;
            if (buried) { res.CulledCells++; continue; }
            if (res.DrawnCells >= maxCells) { res.TruncatedCells++; continue; }
            for (int f = 0; f < 6; f++) EmitFace(res, c, f, edgeSeen);
            res.DrawnCells++;
        }
        return res;
    }

    /// <summary>
    /// 块数远超上限时的快路：**跳过内部面剔除，按等间隔抽稀取 maxCells 块**。
    ///
    /// 为什么跳过剔除：剔除靠"同尺寸且正对的邻居把六张面都占住"，要先给每块的 6 张面建一张
    /// 面表。311 万块就是 1870 万次字典操作、字典本身预留 936 万槽 —— 实测 5.9 秒、内存冲到
    /// 4.1 GB，而八叉树模型里子块尺寸参差，面根本配不上对，**实测剔掉 0 块**。既然最后横竖
    /// 只画 maxCells 块，这趟表纯属白建。
    ///
    /// 为什么抽稀而不是取前 N 块：取前缀会让画出来的块偏向文件里靠前的那部分；等间隔抽稀
    /// 至少在整个模型上均匀铺开。两者都只是"看个大概"，真要逐块看得靠筛选/剖切把块数降下来。
    /// </summary>
    private static Result BuildDecimated(IReadOnlyList<Cell> cells, int maxCells)
    {
        var res = new Result();
        var edgeSeen = new HashSet<(long, long, long, long, long, long)>();
        double stride = (double)cells.Count / maxCells;      // >1
        double acc = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            if (i < acc) { res.TruncatedCells++; continue; }
            acc += stride;
            if (res.DrawnCells >= maxCells) { res.TruncatedCells++; continue; }
            for (int f = 0; f < 6; f++) EmitFace(res, cells[i], f, edgeSeen);
            res.DrawnCells++;
        }
        return res;
    }

    /// <summary>把结果装成一个三角网实体（逐顶点色）。空结果返回 null。</summary>
    public static MeshEntity? ToMesh(Result r, string name, (float r, float g, float b) baseColor)
    {
        if (r.Tris.Count == 0) return null;
        var m = new MeshEntity(name, r.Verts, r.Tris)
        {
            Cr = baseColor.r, Cg = baseColor.g, Cb = baseColor.b,
            VertColors = r.Colors,
            EdgeOverride = r.Edges,     // 只画立方体的棱，不画三角剖分的对角线
        };
        m.Invalidate();
        return m;
    }

    // 面序号：0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z
    private static (int axis, long plane, long u0, long v0, long u1, long v1) FaceKey(in Cell c, int f)
    {
        switch (f)
        {
            case 0: return (0, Q(c.X - c.Hx), Q(c.Y - c.Hy), Q(c.Z - c.Hz), Q(c.Y + c.Hy), Q(c.Z + c.Hz));
            case 1: return (0, Q(c.X + c.Hx), Q(c.Y - c.Hy), Q(c.Z - c.Hz), Q(c.Y + c.Hy), Q(c.Z + c.Hz));
            case 2: return (1, Q(c.Y - c.Hy), Q(c.X - c.Hx), Q(c.Z - c.Hz), Q(c.X + c.Hx), Q(c.Z + c.Hz));
            case 3: return (1, Q(c.Y + c.Hy), Q(c.X - c.Hx), Q(c.Z - c.Hz), Q(c.X + c.Hx), Q(c.Z + c.Hz));
            case 4: return (2, Q(c.Z - c.Hz), Q(c.X - c.Hx), Q(c.Y - c.Hy), Q(c.X + c.Hx), Q(c.Y + c.Hy));
            default: return (2, Q(c.Z + c.Hz), Q(c.X - c.Hx), Q(c.Y - c.Hy), Q(c.X + c.Hx), Q(c.Y + c.Hy));
        }
    }

    /// <summary>发一张面（4 顶点 2 三角，法线朝外）；同时登记这张面的 4 条边界棱（去重）。</summary>
    private static void EmitFace(Result res, in Cell c, int f, HashSet<(long, long, long, long, long, long)> edgeSeen)
    {
        double x0 = c.X - c.Hx, x1 = c.X + c.Hx;
        double y0 = c.Y - c.Hy, y1 = c.Y + c.Hy;
        double z0 = c.Z - c.Hz, z1 = c.Z + c.Hz;
        (double x, double y, double z) p0, p1, p2, p3;
        switch (f)
        {
            case 0: p0 = (x0, y1, z0); p1 = (x0, y0, z0); p2 = (x0, y0, z1); p3 = (x0, y1, z1); break;   // -X
            case 1: p0 = (x1, y0, z0); p1 = (x1, y1, z0); p2 = (x1, y1, z1); p3 = (x1, y0, z1); break;   // +X
            case 2: p0 = (x0, y0, z0); p1 = (x1, y0, z0); p2 = (x1, y0, z1); p3 = (x0, y0, z1); break;   // -Y
            case 3: p0 = (x1, y1, z0); p1 = (x0, y1, z0); p2 = (x0, y1, z1); p3 = (x1, y1, z1); break;   // +Y
            case 4: p0 = (x0, y1, z0); p1 = (x1, y1, z0); p2 = (x1, y0, z0); p3 = (x0, y0, z0); break;   // -Z
            default: p0 = (x0, y0, z1); p1 = (x1, y0, z1); p2 = (x1, y1, z1); p3 = (x0, y1, z1); break;  // +Z
        }
        int b = res.Verts.Count;
        res.Verts.Add(p0); res.Verts.Add(p1); res.Verts.Add(p2); res.Verts.Add(p3);
        var col = (c.R, c.G, c.B);
        res.Colors.Add(col); res.Colors.Add(col); res.Colors.Add(col); res.Colors.Add(col);
        res.Tris.Add((b, b + 1, b + 2));
        res.Tris.Add((b, b + 2, b + 3));
        // 面的四条边界棱（对角线不发）；同一条棱会被两张面、两块各发一次，按位置去重
        var quad = new[] { p0, p1, p2, p3 };
        for (int k = 0; k < 4; k++)
        {
            int k2 = (k + 1) & 3;
            if (edgeSeen.Add(EdgeKey(quad[k], quad[k2]))) res.Edges.Add((b + k, b + k2));
        }
    }
}
