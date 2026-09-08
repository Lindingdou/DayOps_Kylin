using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 由多段线建三角网（等值线建面）——把线的顶点当作输入点、线段当作约束边。
///
/// 此前「创建三角网」只收点实体，多段线仅被当作裁剪边界；于是最常见的
/// 「选一堆等值线 → 建面」走不通，只会提示"需 ≥3 个点"。
///
/// 约束边很关键：等值线是地形的结构线，若只把顶点当散点做无约束 Delaunay，
/// 相邻等值线之间会被连出跨越山脊/沟谷的三角形，面就"糊"了。
///
/// 纯逻辑、可单测：只做 顶点收集 + 去重 + 约束边索引，不碰 UI 与三角化本身。
/// </summary>
public static class PolylineTin
{
    /// <summary>一条输入线：平面点串 + 每点高程 + 是否闭合。</summary>
    public sealed class Line
    {
        public required IReadOnlyList<(double x, double y)> Points { get; init; }
        /// <summary>逐点高程；给 null 表示整条线用同一高程 <see cref="FlatZ"/>。</summary>
        public IReadOnlyList<double>? Z { get; init; }
        public double FlatZ { get; init; }
        public bool Closed { get; init; }

        public double ZAt(int i) => Z != null && Z.Count == Points.Count ? Z[i] : FlatZ;
    }

    public sealed class Result
    {
        public List<(double x, double y, double z)> Verts = new();
        /// <summary>约束边（顶点索引对）。</summary>
        public List<(int u, int v)> Constraints = new();
        /// <summary>因与已有顶点重合而被合并掉的点数（诊断用）。</summary>
        public int MergedVertices;
    }

    /// <summary>
    /// 从「显示态线框」缓冲收集（交错 P3_C3，每段 2 顶点 × 6 float）。
    /// 导入的 .3dm/OFF 等值线走的是显示通道，不是场景实体、选不中 ——
    /// 没有它这类图纸就"建不出三角网"。端点按同一精度合并，等值线的每段成为一条约束。
    /// </summary>
    public static Result CollectSegments(IReadOnlyList<float> p3c3)
    {
        var res = new Result();
        if (p3c3 == null || p3c3.Count < 12) return res;
        var index = new Dictionary<(long, long), int>();

        int Vertex(int off)
        {
            double x = p3c3[off], y = p3c3[off + 1], z = p3c3[off + 2];
            var k = Key(x, y);
            if (index.TryGetValue(k, out int hit)) { res.MergedVertices++; return hit; }
            index[k] = res.Verts.Count;
            res.Verts.Add((x, y, z));
            return res.Verts.Count - 1;
        }

        for (int i = 0; i + 11 < p3c3.Count; i += 12)
        {
            int a = Vertex(i), b = Vertex(i + 6);
            if (a != b) res.Constraints.Add((a, b));
        }
        return res;
    }

    /// <summary>
    /// 体素抽稀 —— 忠实原版：顶点超上限时先按体素格抽稀再剖分，而不是把几十万点全剖出来。
    ///
    /// 为什么必须做：地形图一张就有 50 万顶点，全剖出来是 98 万个三角形。剖分本身只要 0.2 秒，
    /// 但接下来要给这近百万三角形做镶嵌、算唯一边线、传 GL 缓冲，界面就在这一步卡死
    /// （用户实测"原版能建出来、Kylin 卡死"，差的就是这一步）。
    ///
    /// 做法：按格子取一个代表点（离格心最近的那个，比取第一个更贴合地形），
    /// 格子边长自适应——从数据范围估起，逐步放大直到点数落到 target 以内。
    /// </summary>
    public static List<(double x, double y, double z)> Downsample(
        IReadOnlyList<(double x, double y, double z)> verts, int target, out double voxelUsed)
    {
        voxelUsed = 0;
        if (verts == null || verts.Count <= target) return verts == null ? new() : new(verts);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var v in verts)
        {
            if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
            if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
        }
        double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);

        // 起点：让格子数约等于目标点数（面积 / 目标数 开方）
        double cell = Math.Max(Math.Sqrt(w * h / Math.Max(target, 1)), 1e-6);
        for (int attempt = 0; attempt < 24; attempt++)
        {
            var keep = new Dictionary<(long, long), int>(target * 2);
            for (int i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                long gx = (long)Math.Floor((v.x - minX) / cell), gy = (long)Math.Floor((v.y - minY) / cell);
                var k = (gx, gy);
                if (!keep.TryGetValue(k, out int cur)) { keep[k] = i; continue; }
                // 取离格心更近的那个：等值线在格内往往连成一串，取格心代表更贴合地形
                double ccx = minX + (gx + 0.5) * cell, ccy = minY + (gy + 0.5) * cell;
                var o = verts[cur];
                if (Sq(v.x - ccx) + Sq(v.y - ccy) < Sq(o.x - ccx) + Sq(o.y - ccy)) keep[k] = i;
            }
            if (keep.Count <= target || attempt == 23)
            {
                voxelUsed = cell;
                var outp = new List<(double x, double y, double z)>(keep.Count);
                foreach (var i in keep.Values) outp.Add(verts[i]);
                return outp;
            }
            cell *= 1.35;   // 还是太多, 放大格子再来
        }
        voxelUsed = cell;
        return new(verts);

        static double Sq(double t) => t * t;
    }

    /// <summary>清洗统计（对应原版 preclean_* 计数，用于回报"剔除了多少处问题约束"）。</summary>
    public sealed class CleanStat
    {
        /// <summary>零长/两端同点。</summary>
        public int Degenerate;
        /// <summary>同一对端点重复出现。</summary>
        public int Duplicate;
        /// <summary>与已保留的约束真交叉（等值线互相穿越，数据本身矛盾）。</summary>
        public int Crossing;
        public int Total => Degenerate + Duplicate + Crossing;
    }

    /// <summary>
    /// 约束边预清洗 —— 忠实原版：三角化前先剔掉退化/重复/交叉的约束段，并统计剔除数回报用户。
    ///
    /// 为什么必须做：等值线数据里线与线相交、同一段被画两遍很常见。带着这种约束去做
    /// 约束三角化，轻则结果乱，重则嵌入过程反复删三角形而卡住。原版对应
    /// preclean_dup_segs / preclean_bad_segs(cross/T/overlap) 那几个计数。
    ///
    /// 交叉的取舍：按输入顺序贪心保留，后来的那条与已保留的真交叉就丢掉 ——
    /// 比两条都丢更保守（少开洞），且结果与输入顺序一一对应、可复现。
    /// 候选对用均匀网格找，不做 m² 的两两比对（三万条约束时那是九亿次）。
    /// </summary>
    public static List<(int u, int v)> Clean(
        IReadOnlyList<(double x, double y, double z)> verts,
        IReadOnlyList<(int u, int v)> constraints,
        out CleanStat stat)
    {
        stat = new CleanStat();
        var kept = new List<(int u, int v)>(constraints?.Count ?? 0);
        if (verts == null || constraints == null || constraints.Count == 0) return kept;

        var seen = new HashSet<(int, int)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var v in verts)
        {
            if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
            if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
        }
        int g = Math.Clamp((int)Math.Sqrt(constraints.Count / 2.0), 1, 512);
        double gw = maxX - minX > 1e-12 ? (maxX - minX) / g : 1;
        double gh = maxY - minY > 1e-12 ? (maxY - minY) / g : 1;
        var cells = new List<int>[g * g];
        int Gx(double x) => Math.Clamp((int)((x - minX) / gw), 0, g - 1);
        int Gy(double y) => Math.Clamp((int)((y - minY) / gh), 0, g - 1);

        foreach (var (u, v) in constraints)
        {
            if (u < 0 || v < 0 || u >= verts.Count || v >= verts.Count || u == v) { stat.Degenerate++; continue; }
            var key = u < v ? (u, v) : (v, u);
            if (!seen.Add(key)) { stat.Duplicate++; continue; }

            var a = verts[u]; var b = verts[v];
            int x0 = Gx(Math.Min(a.x, b.x)), x1 = Gx(Math.Max(a.x, b.x));
            int y0 = Gy(Math.Min(a.y, b.y)), y1 = Gy(Math.Max(a.y, b.y));

            bool crosses = false;
            for (int gy = y0; gy <= y1 && !crosses; gy++)
                for (int gx = x0; gx <= x1 && !crosses; gx++)
                {
                    var bucket = cells[gy * g + gx];
                    if (bucket == null) continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        var (pu, pv) = kept[bucket[i]];
                        if (pu == u || pu == v || pv == u || pv == v) continue;   // 共端点不算交叉
                        if (ProperlyCross(verts[pu], verts[pv], a, b)) { crosses = true; break; }
                    }
                }
            if (crosses) { stat.Crossing++; continue; }

            int id = kept.Count;
            kept.Add((u, v));
            for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                    (cells[gy * g + gx] ??= new List<int>()).Add(id);
        }
        return kept;
    }

    /// <summary>两段是否真交叉（严格相交于各自内部；共端点、共线重叠都不算）。</summary>
    private static bool ProperlyCross((double x, double y, double z) p1, (double x, double y, double z) p2,
                                      (double x, double y, double z) q1, (double x, double y, double z) q2)
    {
        double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2);
        double d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));

        static double Cross((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    /// <summary>平面去重精度（毫米级；矿区坐标 1e7 量级下仍不溢出 long）。</summary>
    private static (long, long) Key(double x, double y) => ((long)Math.Round(x * 1e3), (long)Math.Round(y * 1e3));

    /// <summary>
    /// 收集顶点与约束边。平面位置重合的点合并成一个顶点 ——
    /// 等值线常在端点处首尾相接，不合并会给三角化喂入重合点，剖分直接失败。
    /// </summary>
    public static Result Collect(IReadOnlyList<Line> lines)
    {
        var res = new Result();
        if (lines == null || lines.Count == 0) return res;

        var index = new Dictionary<(long, long), int>();
        foreach (var line in lines)
        {
            if (line.Points.Count < 2) continue;
            var ids = new List<int>(line.Points.Count);
            for (int i = 0; i < line.Points.Count; i++)
            {
                var (x, y) = line.Points[i];
                var k = Key(x, y);
                if (index.TryGetValue(k, out int existing)) { ids.Add(existing); res.MergedVertices++; }
                else
                {
                    index[k] = res.Verts.Count;
                    ids.Add(res.Verts.Count);
                    res.Verts.Add((x, y, line.ZAt(i)));
                }
            }
            for (int i = 0; i + 1 < ids.Count; i++)
                if (ids[i] != ids[i + 1]) res.Constraints.Add((ids[i], ids[i + 1]));
            if (line.Closed && ids.Count > 2 && ids[^1] != ids[0]) res.Constraints.Add((ids[^1], ids[0]));
        }
        return res;
    }
}
