using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「修复拓扑关系」(REPAIR) 的开关版流水线 —— 对应原版参数面板的 6 个开关 + 几何容差。
///
/// 原版面板把 6 个开关列出来了，但 capability 只把 tolerance 透传给内核（面板注释里写着
/// "5 开关用 native 默认值"）；这里按面板语义逐项真正执行，回显也逐项给出，
/// 与原版命令行 "去退化面 / 焊接顶点 / 填充孔洞 / 删除孤立顶点 / 翻转面 / 拆分非流形边" 一一对应。
///
/// 步序不可换：去退化 → 焊接 → 拆非流形 → 定向 → 补洞 → 去孤立点。
/// 焊接必须早于定向/补洞（重合但未共享的顶点会让朝向传播和边界环提取都失效），
/// 去孤立点必须最后（前面每一步都可能让顶点失去引用）。
/// </summary>
public static partial class MeshRepair
{
    /// <summary>修复开关（对应原版 REPAIR 参数面板）。</summary>
    public sealed class Options
    {
        public double Tolerance;                    // 0 = 自动（包围盒对角 × 1e-4）
        public bool RemoveDegenerate = true;        // 删除退化面
        public bool WeldVertices = true;            // 焊接重复顶点
        public bool FillHoles = true;               // 填充小孔洞
        public bool RemoveIsolated = true;          // 删除孤立顶点
        public bool SplitNonManifold = true;        // 拆分非流形边
        public bool FlipInverted;                   // 翻转方向不一致面（原版默认关）
    }

    /// <summary>逐项修复量（供命令行像原版那样逐条回显）。</summary>
    public sealed class DetailResult
    {
        public List<(double x, double y, double z)> Verts = new();
        public List<(int a, int b, int c)> Tris = new();
        public int RemovedDegenerate, WeldedVertices, FilledHoles, FilledFaces,
                   RemovedIsolated, FlippedFaces, SplitNonManifoldEdges;
        public int BoundaryBefore, BoundaryAfter;
        public int TotalChanges => RemovedDegenerate + WeldedVertices + FilledHoles
                                 + RemovedIsolated + FlippedFaces + SplitNonManifoldEdges;
    }

    /// <summary>按开关逐步修复。</summary>
    public static DetailResult Repair(
        IReadOnlyList<(double x, double y, double z)> verts,
        IReadOnlyList<(int a, int b, int c)> tris, Options opt)
    {
        var r = new DetailResult();
        var v = new List<(double x, double y, double z)>(verts ?? new List<(double x, double y, double z)>());
        var t = new List<(int a, int b, int c)>(tris ?? new List<(int a, int b, int c)>());
        r.BoundaryBefore = MeshDiagnose.Analyze(v, t).BoundaryEdges;
        if (v.Count < 3 || t.Count < 1) { r.Verts = v; r.Tris = t; r.BoundaryAfter = r.BoundaryBefore; return r; }

        var mm = MeshMetrics.Compute(v, t);
        double diag = Math.Sqrt((mm.MaxX - mm.MinX) * (mm.MaxX - mm.MinX) +
                                (mm.MaxY - mm.MinY) * (mm.MaxY - mm.MinY) +
                                (mm.MaxZ - mm.MinZ) * (mm.MaxZ - mm.MinZ));
        double tol = opt.Tolerance > 0 ? opt.Tolerance : (diag > 0 ? diag * 1e-4 : 1e-6);

        // ① 去退化面：索引重复 / 面积近零
        if (opt.RemoveDegenerate)
        {
            double areaEps = diag > 0 ? diag * diag * 1e-12 : 1e-18;
            var keep = new List<(int a, int b, int c)>(t.Count);
            foreach (var (a, b, c) in t)
            {
                if (a == b || b == c || a == c || a < 0 || b < 0 || c < 0 ||
                    a >= v.Count || b >= v.Count || c >= v.Count) { r.RemovedDegenerate++; continue; }
                var p = v[a]; var q = v[b]; var s = v[c];
                double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z;
                double wx = s.x - p.x, wy = s.y - p.y, wz = s.z - p.z;
                double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                if (cx * cx + cy * cy + cz * cz <= areaEps * areaEps) { r.RemovedDegenerate++; continue; }
                keep.Add((a, b, c));
            }
            t = keep;
        }

        // ② 焊接重复顶点（顺带丢掉焊后塌陷 / 完全重合的三角）
        if (opt.WeldVertices)
        {
            var w = MeshWeld.Weld(v, t, tol, dropDuplicateTris: true);
            r.WeldedVertices = Math.Max(0, w.InputVerts - w.OutputVerts);
            r.RemovedDegenerate += w.DroppedDegenerate + w.DuplicateTris;
            v = w.Verts; t = w.Tris;
        }

        // ③ 拆分非流形边
        if (opt.SplitNonManifold)
        {
            t = SplitNonManifold(v, t, out int splitEdges);
            r.SplitNonManifoldEdges = splitEdges;
        }

        // ④ 定向：补洞需要一致绕向；开关关且不补洞时不动
        if (opt.FlipInverted || opt.FillHoles)
        {
            var oriented = MeshOrient.MakeConsistent(v, t);
            int flipped = 0;
            for (int i = 0; i < t.Count && i < oriented.Count; i++)
                if (oriented[i].a != t[i].a || oriented[i].b != t[i].b || oriented[i].c != t[i].c) flipped++;
            r.FlippedFaces = flipped;
            t = oriented;
        }

        // ⑤ 补洞
        if (opt.FillHoles)
        {
            int before = t.Count;
            var (fv, ft, holes) = MeshHoleFill.Fill(v, t);
            r.FilledHoles = holes; r.FilledFaces = Math.Max(0, ft.Count - before);
            v = fv; t = ft;
        }

        // ⑥ 删除孤立顶点（未被任何三角引用）
        if (opt.RemoveIsolated)
        {
            var used = new bool[v.Count];
            foreach (var (a, b, c) in t)
            {
                if ((uint)a < used.Length) used[a] = true;
                if ((uint)b < used.Length) used[b] = true;
                if ((uint)c < used.Length) used[c] = true;
            }
            int live = 0; var map = new int[v.Count];
            var nv = new List<(double x, double y, double z)>(v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                if (used[i]) { map[i] = live++; nv.Add(v[i]); }
                else { map[i] = -1; r.RemovedIsolated++; }
            }
            if (r.RemovedIsolated > 0)
            {
                var nt = new List<(int a, int b, int c)>(t.Count);
                foreach (var (a, b, c) in t)
                    if ((uint)a < map.Length && (uint)b < map.Length && (uint)c < map.Length &&
                        map[a] >= 0 && map[b] >= 0 && map[c] >= 0)
                        nt.Add((map[a], map[b], map[c]));
                v = nv; t = nt;
            }
        }

        r.Verts = v; r.Tris = t;
        r.BoundaryAfter = MeshDiagnose.Analyze(v, t).BoundaryEdges;
        return r;
    }

    /// <summary>
    /// 非流形边拆分：一条边挂 &gt;2 个三角时，第 3 个起的三角把该边端点各复制一份，
    /// 使多余的片在这条边上断开（面数不变、顶点增加），后续焊接/补洞才不会把不同片粘死。
    /// </summary>
    private static List<(int a, int b, int c)> SplitNonManifold(
        List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris, out int splitEdges)
    {
        splitEdges = 0;
        var count = new Dictionary<(int, int), int>();
        void Bump(int a, int b) { var k = a < b ? (a, b) : (b, a); count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var (a, b, c) in tris) { Bump(a, b); Bump(b, c); Bump(c, a); }
        var bad = new HashSet<(int, int)>();
        foreach (var kv in count) if (kv.Value > 2) bad.Add(kv.Key);
        if (bad.Count == 0) return tris;
        splitEdges = bad.Count;

        var seen = new Dictionary<(int, int), int>();
        var dup = new Dictionary<(int vertex, int owner), int>();
        int Dup(int vi, int owner)
        {
            if (dup.TryGetValue((vi, owner), out var ni)) return ni;
            verts.Add(verts[vi]); ni = verts.Count - 1; dup[(vi, owner)] = ni; return ni;
        }
        var outTris = new List<(int a, int b, int c)>(tris.Count);
        for (int ti = 0; ti < tris.Count; ti++)
        {
            var (a, b, c) = tris[ti];
            var edges = new[] { (a, b), (b, c), (c, a) };
            foreach (var (u, w) in edges)
            {
                var k = u < w ? (u, w) : (w, u);
                if (!bad.Contains(k)) continue;
                int used = seen.TryGetValue(k, out var n) ? n : 0;
                seen[k] = used + 1;
                if (used < 2) continue;                       // 前两个三角构成流形的一对，保持原样
                if (a == u || a == w) a = Dup(a, ti);
                if (b == u || b == w) b = Dup(b, ti);
                if (c == u || c == w) c = Dup(c, ti);
            }
            outTris.Add((a, b, c));
        }
        return outTris;
    }
}
