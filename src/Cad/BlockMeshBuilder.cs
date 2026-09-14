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
/// 控量靠**整块剔除**，口径照原版 SurfaceInstanceBuilder：一个块若六个方向都被别的可见块占着，
/// 它整个埋在模型内部，直接不发；只发「暴露」的壳层块。100×100×30 的实心模型由 30 万块降到约 2.6 万块，
/// 平朔那份 311 万八叉树叶块降到 7.1 万块。
///
/// 暴露块**只发没被邻居挡住的那几张面**。这一条与原版不同，原因是渲染管线不同：原版走 native
/// 体素实例、按体渲染；托管端是一张两面受光的三角网、且不做背面剔除，于是「本块朝内的面」与
/// 「邻块朝外的面」严丝合缝重合、深度完全相等，谁先画谁赢 —— 逐块次序一变，侧面就糊成一片
/// 白噪点（模型侧壁是一摞 0.5 m 薄片，这个现象尤其明显）。挡住的面本来也看不见，剔掉即可；
/// 剔完侧面干净，三角数还少一大截。
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
        /// <summary>实际发出的面数（= 三角数 / 2；被邻居挡住的面不发）。</summary>
        public int FaceCount => Tris.Count / 2;
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
    /// edgeLimit 为「还值得建棱表」的画出块数上限：块一多调用方本来就不描边（见
    /// <see cref="BlockModelMeta.WireframeCellLimit"/>），那张几百万条的去重表纯属白建（30 万块约
    /// 350 万条棱、光去重集就 200 MB）。缺省 int.MaxValue = 照旧一律建。
    /// </summary>
    public static Result Build(IReadOnlyList<Cell> cells, int maxCells = 300_000, int edgeLimit = int.MaxValue)
    {
        var res = new Result();
        if (cells == null || cells.Count == 0) return res;

        // ① 逐块算「六张面各自露不露」。首选细格占用位图（对变尺寸块也成立）；块不落格时回落面表配对。
        //    掩码 bit f = 第 f 张面没被邻居挡住；掩码为 0 = 整块埋在内部。
        var faces = ExposeByOccupancy(cells, res) ?? ExposeByFaceKeys(cells, res, maxCells);
        if (faces == null) return BuildDecimated(cells, maxCells, edgeLimit);

        // ② 按上限截断（暴露块仍可能超预算）
        int willDraw = 0, faceCount = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            if (faces[i] == 0) continue;
            if (willDraw >= maxCells) { faces[i] = 0; res.TruncatedCells++; continue; }
            willDraw++; faceCount += System.Numerics.BitOperations.PopCount(faces[i]);
        }
        if (willDraw == 0) return res;

        // ③ 发面：只发露出来的那几张
        var edgeSeen = willDraw <= edgeLimit ? new HashSet<(long, long, long, long, long, long)>() : null;
        Reserve(res, faceCount);
        for (int i = 0; i < cells.Count; i++)
        {
            uint m = faces[i];
            if (m == 0) continue;
            for (int f = 0; f < 6; f++) if ((m & (1u << f)) != 0) EmitFace(res, cells[i], f, edgeSeen);
            res.DrawnCells++;
        }
        return res;
    }

    /// <summary>细格占用位图封顶（32 MB）。超了就不建，回落别的判法。</summary>
    private const long MaxGridCells = 268_435_456;

    /// <summary>
    /// 「整块埋没」的判法之一：把所有块按【最细格】铺进一张占用位图，再看每块六个方向紧贴的
    /// 那层细格是否全被占住。返回逐块「要不要画」；块不落在统一细格上（尺寸不成整数倍/位置不对齐）
    /// 或格数超上限则返回 null。
    ///
    /// 非它不可：面表配对那套只认「同尺寸且正对」的邻居，而 .blk 是八叉树，叶块尺寸参差
    /// （311 万叶块里 76 万是粗块），面根本配不上对 —— 实测剔掉 0 块，于是 311 万块超预算、
    /// 只能等间隔抽稀画 30 万块，模型满屏窟窿。改按细格占用判，同一份数据剔掉 305 万块、
    /// 只剩 7.1 万块壳层，模型完整且远在预算内（位图 7.6 MB、耗时 0.3 秒）。
    /// </summary>
    private static uint[]? ExposeByOccupancy(IReadOnlyList<Cell> cells, Result res)
    {
        double hx = double.MaxValue, hy = double.MaxValue, hz = double.MaxValue;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var c in cells)
        {
            if (!(c.Hx > 0) || !(c.Hy > 0) || !(c.Hz > 0)) return null;
            if (c.Hx < hx) hx = c.Hx; if (c.Hy < hy) hy = c.Hy; if (c.Hz < hz) hz = c.Hz;
            if (c.X - c.Hx < minX) minX = c.X - c.Hx; if (c.X + c.Hx > maxX) maxX = c.X + c.Hx;
            if (c.Y - c.Hy < minY) minY = c.Y - c.Hy; if (c.Y + c.Hy > maxY) maxY = c.Y + c.Hy;
            if (c.Z - c.Hz < minZ) minZ = c.Z - c.Hz; if (c.Z + c.Hz > maxZ) maxZ = c.Z + c.Hz;
        }
        double gx = hx * 2, gy = hy * 2, gz = hz * 2;              // 细格边长 = 最小块的边长
        long nx = Dim(maxX - minX, gx), ny = Dim(maxY - minY, gy), nz = Dim(maxZ - minZ, gz);
        if (nx <= 0 || ny <= 0 || nz <= 0) return null;
        if (nx > MaxGridCells / ny || nx * ny > MaxGridCells / nz) return null;
        long total = nx * ny * nz;

        var bits = new ulong[(total + 63) >> 6];
        // 铺占用：块 i 覆盖 [i0, i0+si) × … 的细格
        foreach (var c in cells)
        {
            if (!Lattice(c, minX, minY, minZ, gx, gy, gz, nx, ny, nz, out long i0, out long j0, out long k0,
                         out long si, out long sj, out long sk)) return null;
            for (long k = k0; k < k0 + sk; k++)
                for (long j = j0; j < j0 + sj; j++)
                {
                    long b = (k * ny + j) * nx;
                    for (long i = i0; i < i0 + si; i++) { long q = b + i; bits[q >> 6] |= 1UL << (int)(q & 63); }
                }
        }
        bool Occ(long i, long j, long k)
        {
            if (i < 0 || j < 0 || k < 0 || i >= nx || j >= ny || k >= nz) return false;
            long q = (k * ny + j) * nx + i;
            return (bits[q >> 6] & (1UL << (int)(q & 63))) != 0;
        }

        // 一张面「被挡住」= 紧贴它的那层细格全被占。六张面各判各的(面序: 0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z)
        var faces = new uint[cells.Count];
        for (int idx = 0; idx < cells.Count; idx++)
        {
            Lattice(cells[idx], minX, minY, minZ, gx, gy, gz, nx, ny, nz, out long i0, out long j0, out long k0,
                    out long si, out long sj, out long sk);
            bool covLo = true, covHi = true;
            for (long j = j0; j < j0 + sj && (covLo || covHi); j++)
                for (long k = k0; k < k0 + sk && (covLo || covHi); k++)
                { if (!Occ(i0 - 1, j, k)) covLo = false; if (!Occ(i0 + si, j, k)) covHi = false; }
            uint m = 0;
            if (!covLo) m |= 1u << 0; if (!covHi) m |= 1u << 1;

            covLo = covHi = true;
            for (long i = i0; i < i0 + si && (covLo || covHi); i++)
                for (long k = k0; k < k0 + sk && (covLo || covHi); k++)
                { if (!Occ(i, j0 - 1, k)) covLo = false; if (!Occ(i, j0 + sj, k)) covHi = false; }
            if (!covLo) m |= 1u << 2; if (!covHi) m |= 1u << 3;

            covLo = covHi = true;
            for (long i = i0; i < i0 + si && (covLo || covHi); i++)
                for (long j = j0; j < j0 + sj && (covLo || covHi); j++)
                { if (!Occ(i, j, k0 - 1)) covLo = false; if (!Occ(i, j, k0 + sk)) covHi = false; }
            if (!covLo) m |= 1u << 4; if (!covHi) m |= 1u << 5;

            faces[idx] = m;
            if (m == 0) res.CulledCells++;
        }
        return faces;
    }

    private static long Dim(double extent, double g)
    {
        double n = extent / g;
        if (!(n > 0) || n > MaxGridCells) return 0;
        return (long)Math.Round(n);
    }

    /// <summary>块 → 细格下标区间；不落格（尺寸非整数倍 / 位置不对齐 / 越界）返回 false。</summary>
    private static bool Lattice(in Cell c, double minX, double minY, double minZ, double gx, double gy, double gz,
                                long nx, long ny, long nz,
                                out long i0, out long j0, out long k0, out long si, out long sj, out long sk)
    {
        i0 = j0 = k0 = 0; si = sj = sk = 0;
        if (!Axis(c.X - c.Hx, minX, gx, c.Hx * 2, nx, out i0, out si)) return false;
        if (!Axis(c.Y - c.Hy, minY, gy, c.Hy * 2, ny, out j0, out sj)) return false;
        if (!Axis(c.Z - c.Hz, minZ, gz, c.Hz * 2, nz, out k0, out sk)) return false;
        return true;
    }

    private static bool Axis(double lo, double min, double g, double len, long n, out long i0, out long s)
    {
        double fi = (lo - min) / g, fs = len / g;
        i0 = (long)Math.Round(fi); s = (long)Math.Round(fs);
        const double Tol = 1e-6;                       // 允许浮点噪声, 不允许"半格"错位
        if (Math.Abs(fi - i0) > Tol || Math.Abs(fs - s) > Tol) return false;
        return s >= 1 && i0 >= 0 && i0 + s <= n;
    }

    /// <summary>
    /// 「整块埋没」的判法之二（回落）：按面配对 —— 一张面出现两次 = 被两个可见块共用。
    /// 面键含面内四至，故只有「同尺寸且正对」的邻居才配得上对；尺寸参差的八叉树上配不成，
    /// 故优先走 <see cref="ExposeByOccupancy"/>。块数超预算时不建这张表（几百万次字典操作、
    /// 内存冲到 4 GB），返回 null 让调用方走抽稀。
    /// </summary>
    private static uint[]? ExposeByFaceKeys(IReadOnlyList<Cell> cells, Result res, int maxCells)
    {
        if (cells.Count > maxCells) return null;
        var seen = new Dictionary<(int axis, long plane, long u0, long v0, long u1, long v1), int>(cells.Count * 3);
        foreach (var c in cells)
            for (int f = 0; f < 6; f++)
            {
                var k = FaceKey(c, f);
                seen[k] = seen.TryGetValue(k, out int n) ? n + 1 : 1;
            }
        var faces = new uint[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            uint m = 0;
            for (int f = 0; f < 6; f++) if (seen[FaceKey(c, f)] <= 1) m |= 1u << f;   // 出现两次 = 被邻居占着
            faces[i] = m;
            if (m == 0) res.CulledCells++;
        }
        return faces;
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
    private static Result BuildDecimated(IReadOnlyList<Cell> cells, int maxCells, int edgeLimit = int.MaxValue)
    {
        var res = new Result();
        var edgeSeen = Math.Min(cells.Count, maxCells) <= edgeLimit ? new HashSet<(long, long, long, long, long, long)>() : null;
        Reserve(res, Math.Min(cells.Count, maxCells) * 6);   // 抽稀路径不判遮挡, 每块六面全发
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

    /// <summary>按将要画出的**面数**一次预留顶点/三角/色表容量：几十万张面靠 List 自增得翻倍搬十几次。</summary>
    private static void Reserve(Result res, int drawFaces)
    {
        if (drawFaces <= 0) return;
        res.Verts = new List<(double x, double y, double z)>(drawFaces * 4);
        res.Colors = new List<(float r, float g, float b)>(drawFaces * 4);
        res.Tris = new List<(int a, int b, int c)>(drawFaces * 2);
    }

    /// <summary>
    /// 把结果装成一个三角网实体（逐顶点色）。空结果返回 null。
    /// 顶点/三角表直接交给实体（不复制）—— 720 万顶点复制一遍要多占 200 多 MB，而 <paramref name="r"/>
    /// 建完就只用来报「画了几块/剔了几块」，两边都不会再改这几张表。
    /// </summary>
    public static MeshEntity? ToMesh(Result r, string name, (float r, float g, float b) baseColor)
    {
        if (r.Tris.Count == 0) return null;
        var m = new MeshEntity
        {
            Name = name,
            Verts = r.Verts, Tris = r.Tris,
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

    /// <summary>发一张面（4 顶点 2 三角，法线朝外）；edgeSeen 非 null 时顺带登记这张面的 4 条边界棱（去重）。</summary>
    private static void EmitFace(Result res, in Cell c, int f, HashSet<(long, long, long, long, long, long)>? edgeSeen)
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
        if (edgeSeen == null) return;      // 块太多、横竖不描边 → 不建这张几百万条的表
        var quad = new[] { p0, p1, p2, p3 };
        for (int k = 0; k < 4; k++)
        {
            int k2 = (k + 1) & 3;
            if (edgeSeen.Add(EdgeKey(quad[k], quad[k2]))) res.Edges.Add((b + k, b + k2));
        }
    }
}
