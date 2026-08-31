using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网补洞（填充边界洞）——原 PitMine3D 走 C++ 内核(MeshData), 但**填洞算法是标准几何**:
/// 提开边(仅被 1 个三角形用的边)→按三角形绕向串成有向边界环→每环加质心顶点扇形三角化封闭。
/// 复用同套 (verts, tris) 托管网格表示; 纯逻辑、可单测。中小洞的扇形填充; 大洞/非平面洞质量有限(记录)。
/// </summary>
public static class MeshHoleFill
{
    /// <summary>填充所有边界洞。返回新网格(原顶点 + 每洞一个质心顶点 + 扇形三角)与补洞数。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris, int holes) Fill(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var outV = new List<(double x, double y, double z)>(verts);
        var outT = new List<(int a, int b, int c)>(tris);
        if (verts == null || tris == null || verts.Count < 3 || tris.Count < 1) return (outV, outT, 0);

        // ① 统计无向边被多少三角形使用。
        var count = new Dictionary<(int, int), int>(tris.Count * 3);
        void Bump(int u, int w) { var k = u < w ? (u, w) : (w, u); count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var (a, b, c) in tris) { Bump(a, b); Bump(b, c); Bump(c, a); }

        // ② 收集有向边界边(无向计数==1), 保留三角形绕向。
        var succ = new Dictionary<int, int>();
        void Edge(int u, int w) { var k = u < w ? (u, w) : (w, u); if (count.TryGetValue(k, out var n) && n == 1 && !succ.ContainsKey(u)) succ[u] = w; }
        foreach (var (a, b, c) in tris) { Edge(a, b); Edge(b, c); Edge(c, a); }
        if (succ.Count == 0) return (outV, outT, 0);   // 无洞(封闭网格)

        // ③ 串成环并填充。
        int holes = 0;
        var visited = new HashSet<int>();
        foreach (var start in new List<int>(succ.Keys))
        {
            if (visited.Contains(start)) continue;
            var loop = new List<int>();
            int cur = start; int guard = 0;
            while (succ.TryGetValue(cur, out var nxt) && !visited.Contains(cur) && guard++ < succ.Count + 2)
            {
                visited.Add(cur);
                loop.Add(cur);
                cur = nxt;
                if (cur == start) break;
            }
            if (loop.Count < 3) continue;

            // 质心顶点 + 扇形三角(沿有向边界边 u→w 生成 (u,w,质心), 绕向与边界一致→法向与邻接三角协调)。
            double cx = 0, cy = 0, cz = 0;
            foreach (int vi in loop) { cx += outV[vi].x; cy += outV[vi].y; cz += outV[vi].z; }
            cx /= loop.Count; cy /= loop.Count; cz /= loop.Count;
            int cIdx = outV.Count;
            outV.Add((cx, cy, cz));
            for (int i = 0; i < loop.Count; i++)
            {
                int u = loop[i], w = loop[(i + 1) % loop.Count];
                outT.Add((u, w, cIdx));
            }
            holes++;
        }
        return (outV, outT, holes);
    }
}
