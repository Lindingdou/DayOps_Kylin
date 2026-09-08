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
