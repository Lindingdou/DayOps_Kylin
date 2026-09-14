using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网朝向一致化 —— 面邻接 BFS 传播：相邻两面在公共边上须**反向**遍历(一致朝向)；同向则翻转邻面。
/// 完成后按带符号体积规范为**外向**(体积&gt;0)。修 loft+weld 等产出的「水密但朝向不一致」网格，
/// 使散度体积/缠绕数(其前提是朝向一致)可靠。纯逻辑、可单测。
/// 前提：近流形(每边≤2 面)；非流形边按边上所有面成对处理(退化网格结果可能不完美)。
/// </summary>
public static class MeshOrient
{
    /// <summary>返回朝向一致且外向的新三角表(顶点不变；n≤1 或无几何时原样返回)。</summary>
    public static List<(int a, int b, int c)> MakeConsistent(
        IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var T = new List<(int a, int b, int c)>(tris);
        int n = T.Count;
        if (n == 0 || verts == null || verts.Count == 0) return T;

        long Key(int u, int w) { int lo = u < w ? u : w, hi = u < w ? w : u; return ((long)lo << 32) | (uint)hi; }
        var edgeFaces = new Dictionary<long, List<int>>(n * 3, PackedKeyComparer.Instance);
        void AddE(int u, int w, int f) { var k = Key(u, w); if (!edgeFaces.TryGetValue(k, out var l)) { l = new List<int>(2); edgeFaces[k] = l; } l.Add(f); }
        for (int f = 0; f < n; f++) { var (a, b, c) = T[f]; AddE(a, b, f); AddE(b, c, f); AddE(c, a, f); }

        static bool HasDir((int a, int b, int c) t, int u, int w)
            => (t.a == u && t.b == w) || (t.b == u && t.c == w) || (t.c == u && t.a == w);

        var visited = new bool[n];
        var stack = new Stack<int>();
        void ProcessEdge(int f, int u, int w)
        {
            if (!edgeFaces.TryGetValue(Key(u, w), out var faces)) return;
            foreach (int g in faces)
            {
                if (g == f || visited[g]) continue;
                if (HasDir(T[g], u, w)) { var t = T[g]; T[g] = (t.a, t.c, t.b); }   // 同向遍历公共边 → 翻转 g(交换 b,c)
                visited[g] = true; stack.Push(g);
            }
        }
        for (int seed = 0; seed < n; seed++)
        {
            if (visited[seed]) continue;
            visited[seed] = true; stack.Push(seed);
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                var (a, b, c) = T[f];
                ProcessEdge(f, a, b); ProcessEdge(f, b, c); ProcessEdge(f, c, a);
            }
        }

        if (SignedVolume6(verts, T) < 0)                     // 规范外向：体积<0(内向)→全翻转
            for (int f = 0; f < n; f++) { var t = T[f]; T[f] = (t.a, t.c, t.b); }
        return T;
    }

    /// <summary>6× 带符号体积(散度定理 Σ a·(b×c))。朝向一致时符号即内/外向；正=外向。</summary>
    public static double SignedVolume6(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> tris)
    {
        double s = 0;
        foreach (var (a, b, c) in tris)
        {
            var A = v[a]; var B = v[b]; var C = v[c];
            s += A.x * (B.y * C.z - B.z * C.y) - A.y * (B.x * C.z - B.z * C.x) + A.z * (B.x * C.y - B.y * C.x);
        }
        return s;
    }

    /// <summary>朝向是否一致：每条内部边恰被两面**反向**各遍历一次(同向即不一致)。开放边(单面)忽略。</summary>
    public static bool IsConsistent(IReadOnlyList<(int a, int b, int c)> tris)
    {
        var dir = new HashSet<long>(PackedKeyComparer.Instance);
        long DKey(int u, int w) => ((long)u << 32) | (uint)w;
        foreach (var (a, b, c) in tris)
        {
            foreach (var (u, w) in new[] { (a, b), (b, c), (c, a) })
                if (!dir.Add(DKey(u, w))) return false;      // 同一有向边出现两次 → 不一致
        }
        return true;
    }
}
