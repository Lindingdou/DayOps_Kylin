using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网边界环提取（忠实移植原 <c>MeshEditLib.Tools.MeshBoundaryLoops.Extract</c>）——
/// 把开放边(只属于 1 个三角的无向边)串成有向闭合环(外轮廓 + 断层缝两帮 + 空洞边缘)。
/// 先按坐标焊接(0.1mm 量化)再定边；有向边取所属三角绕向(外环 CCW / 洞环 CW)；
/// 汇聚顶点(尖灭/夹点等非流形结点)按最靠顺时针出边拆环, 保证每条返回环都是简单环。纯拓扑、可单测。
/// </summary>
public static class MeshBoundaryLoops
{
    /// <summary>提取世界坐标边界环；每个环 = 有序 3D 点表(不重复首点, 首尾隐式相连)。</summary>
    public static List<List<(double x, double y, double z)>> Extract(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var loops = new List<List<(double x, double y, double z)>>();
        if (verts == null || tris == null || verts.Count < 3 || tris.Count < 1) return loops;
        int nv = verts.Count;

        // 1. 按坐标焊接顶点(0.1mm 量化)
        var remap = new int[nv];
        var map = new Dictionary<(long, long, long), int>(nv);
        var px = new List<double>(); var py = new List<double>(); var pz = new List<double>();
        const double q = 1e4;
        for (int i = 0; i < nv; i++)
        {
            double x = verts[i].x, y = verts[i].y, z = verts[i].z;
            var key = ((long)Math.Round(x * q), (long)Math.Round(y * q), (long)Math.Round(z * q));
            if (!map.TryGetValue(key, out int id))
            {
                id = px.Count; map[key] = id;
                px.Add(x); py.Add(y); pz.Add(z);
            }
            remap[i] = id;
        }

        // 2. 无向边计数 + 记一条有向实例(按三角绕向)
        var undir = new Dictionary<long, int>(tris.Count * 3);
        var dir = new Dictionary<long, (int u, int w)>(tris.Count * 3);
        void AddE(int u, int w)
        {
            if (u == w) return;
            int lo = Math.Min(u, w), hi = Math.Max(u, w);
            long k = ((long)lo << 32) | (uint)hi;
            undir[k] = undir.TryGetValue(k, out int n) ? n + 1 : 1;
            if (!dir.ContainsKey(k)) dir[k] = (u, w);
        }
        foreach (var (ta, tb, tc) in tris)
        {
            if (ta < 0 || tb < 0 || tc < 0 || ta >= nv || tb >= nv || tc >= nv) continue;
            int a = remap[ta], b = remap[tb], c = remap[tc];
            AddE(a, b); AddE(b, c); AddE(c, a);
        }

        // 3. 开放有向边邻接表(u → 后继 w 列表)
        var succ = new Dictionary<int, List<int>>();
        foreach (var kv in undir)
        {
            if (kv.Value != 1) continue;
            var (u, w) = dir[kv.Key];
            if (!succ.TryGetValue(u, out var lst)) { lst = new List<int>(1); succ[u] = lst; }
            lst.Add(w);
        }
        if (succ.Count == 0) return loops;

        // 4. 顺着有向边串环:每条开放有向边只用一次
        long DKey(int u, int w) => ((long)u << 32) | (uint)w;
        var used = new HashSet<long>();

        // 汇聚顶点(≥2 开放出边)处取相对入边最靠顺时针的出边, 把汇聚点拆成两条独立简单环;
        // 单出边常规点走 O(1) 快路径。
        int PickNext(int a, int b, List<int> cands)
        {
            if (cands.Count == 1) return used.Contains(DKey(b, cands[0])) ? -1 : cands[0];
            double inAng = Math.Atan2(py[a] - py[b], px[a] - px[b]);
            int best = -1; double bestRel = double.MaxValue;
            foreach (int c in cands)
            {
                if (used.Contains(DKey(b, c))) continue;
                double rel = Math.Atan2(py[c] - py[b], px[c] - px[b]) - inAng;
                while (rel <= 1e-12) rel += 2 * Math.PI;
                while (rel > 2 * Math.PI) rel -= 2 * Math.PI;
                if (rel < bestRel) { bestRel = rel; best = c; }
            }
            return best;
        }

        foreach (var kv in succ)
        {
            foreach (int w0 in kv.Value)
            {
                if (used.Contains(DKey(kv.Key, w0))) continue;
                var loop = new List<int>();
                int a = kv.Key, b = w0;
                int guard = 0, maxIter = px.Count * 4 + 16;
                while (guard++ < maxIter)
                {
                    long e = DKey(a, b);
                    if (used.Contains(e)) break;
                    used.Add(e);
                    loop.Add(a);
                    if (!succ.TryGetValue(b, out var nexts)) { loop.Add(b); break; }
                    int nb = PickNext(a, b, nexts);
                    if (nb < 0) { if (b != kv.Key) loop.Add(b); break; }
                    a = b; b = nb;
                }
                if (loop.Count < 3) continue;
                var pts = new List<(double, double, double)>(loop.Count);
                for (int i = 0; i < loop.Count; i++)
                {
                    int id = loop[i];
                    pts.Add((px[id], py[id], pz[id]));
                }
                loops.Add(pts);
            }
        }
        return loops;
    }
}
