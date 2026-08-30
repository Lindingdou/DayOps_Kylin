using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 栅格形态学 / 连通域工具箱（忠实移植原 <c>PlanLib.ShortTerm.LandformClassifier</c> 的纯算子）——
/// 膨胀/腐蚀/闭/开运算(8-邻域, 迭代 r 次)、填内部孔洞、8-连通域(带最小格数过滤)、Moore 外轮廓描摹、
/// Douglas-Peucker 抽稀。掩膜为行主序 bool[nx*ny]。纯托管、确定性、可单测。
/// </summary>
public static class RasterMorphology
{
    /// <summary>膨胀：8-邻域, 迭代 r 次。</summary>
    public static bool[] Dilate(bool[] m, int nx, int ny, int r)
    {
        var cur = (bool[])m.Clone();
        for (int it = 0; it < r; it++)
        {
            var next = (bool[])cur.Clone();
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    int c = y * nx + x; if (cur[c]) continue;
                    if ((x > 0 && cur[c - 1]) || (x < nx - 1 && cur[c + 1]) ||
                        (y > 0 && cur[c - nx]) || (y < ny - 1 && cur[c + nx]) ||
                        (x > 0 && y > 0 && cur[c - nx - 1]) || (x < nx - 1 && y > 0 && cur[c - nx + 1]) ||
                        (x > 0 && y < ny - 1 && cur[c + nx - 1]) || (x < nx - 1 && y < ny - 1 && cur[c + nx + 1]))
                        next[c] = true;
                }
            cur = next;
        }
        return cur;
    }

    /// <summary>腐蚀：对补集膨胀再取补。</summary>
    public static bool[] Erode(bool[] m, int nx, int ny, int r)
    {
        var inv = new bool[m.Length];
        for (int i = 0; i < m.Length; i++) inv[i] = !m[i];
        var d = Dilate(inv, nx, ny, r);
        var outm = new bool[m.Length];
        for (int i = 0; i < m.Length; i++) outm[i] = !d[i];
        return outm;
    }

    /// <summary>闭运算(先膨胀后腐蚀)：桥接窄于 2r 的缝隙。</summary>
    public static bool[] Close(bool[] m, int nx, int ny, int r) => Erode(Dilate(m, nx, ny, r), nx, ny, r);

    /// <summary>开运算(先腐蚀后膨胀)：抹掉窄于 2r 的细部, 保留宽于 2r 的主体。</summary>
    public static bool[] Open(bool[] m, int nx, int ny, int r) => Dilate(Erode(m, nx, ny, r), nx, ny, r);

    /// <summary>填内部孔洞：从边界 flood 背景, 未到达的背景 = 孔洞 → 置真。</summary>
    public static void FillHoles(bool[] m, int nx, int ny)
    {
        int N = nx * ny; var outside = new bool[N]; var st = new Stack<int>();
        void Push(int c) { if (c >= 0 && c < N && !m[c] && !outside[c]) { outside[c] = true; st.Push(c); } }
        for (int x = 0; x < nx; x++) { Push(x); Push((ny - 1) * nx + x); }
        for (int y = 0; y < ny; y++) { Push(y * nx); Push(y * nx + nx - 1); }
        while (st.Count > 0)
        {
            int c = st.Pop(); int cx = c % nx, cy = c / nx;
            if (cx > 0) Push(c - 1); if (cx < nx - 1) Push(c + 1);
            if (cy > 0) Push(c - nx); if (cy < ny - 1) Push(c + nx);
        }
        for (int c = 0; c < N; c++) if (!m[c] && !outside[c]) m[c] = true;
    }

    /// <summary>8-连通域, 只返回格数 ≥ minCells 的块。</summary>
    public static List<List<int>> Components(bool[] m, int nx, int ny, int minCells)
    {
        int N = nx * ny; var seen = new bool[N]; var outc = new List<List<int>>();
        int[] dxn = { 1, -1, 0, 0, 1, 1, -1, -1 }, dyn = { 0, 0, 1, -1, 1, -1, 1, -1 };
        var st = new Stack<int>();
        for (int s = 0; s < N; s++)
        {
            if (!m[s] || seen[s]) continue;
            var comp = new List<int>(); st.Push(s); seen[s] = true;
            while (st.Count > 0)
            {
                int c = st.Pop(); comp.Add(c); int cx = c % nx, cy = c / nx;
                for (int k = 0; k < 8; k++)
                {
                    int ax = cx + dxn[k], ay = cy + dyn[k];
                    if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                    int a = ay * nx + ax;
                    if (m[a] && !seen[a]) { seen[a] = true; st.Push(a); }
                }
            }
            if (comp.Count >= minCells) outc.Add(comp);
        }
        return outc;
    }

    /// <summary>Moore-邻域外轮廓描摹(顺时针), 返回 cell 坐标序列。</summary>
    public static List<(int x, int y)> TraceBoundary(bool[] m, int nx, int ny)
    {
        int N = nx * ny; int start = -1;
        for (int c = 0; c < N; c++) if (m[c]) { start = c; break; }
        var ring = new List<(int x, int y)>();
        if (start < 0) return ring;
        int sx = start % nx, sy = start / nx;
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        bool In(int x, int y) => x >= 0 && y >= 0 && x < nx && y < ny && m[y * nx + x];
        int px = sx, py = sy, dir = 0; ring.Add((sx, sy));
        int guard = 0, maxGuard = N * 8 + 16;
        while (guard++ < maxGuard)
        {
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int nd = (dir + k) % 8;
                int ax = px + dx[nd], ay = py + dy[nd];
                if (In(ax, ay)) { px = ax; py = ay; ring.Add((px, py)); dir = (nd + 6) % 8; found = true; break; }
            }
            if (!found) break;
            if (px == sx && py == sy) break;
        }
        return ring;
    }

    /// <summary>Douglas-Peucker 简化(容差以 cell 为单位)。</summary>
    public static List<(int x, int y)> Simplify(List<(int x, int y)> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count]; keep[0] = keep[pts.Count - 1] = true;
        var st = new Stack<(int a, int b)>(); st.Push((0, pts.Count - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop();
            double maxd = -1; int idx = -1;
            double ax = pts[a].x, ay = pts[a].y, bx = pts[b].x, by = pts[b].y;
            double dxl = bx - ax, dyl = by - ay; double len = Math.Sqrt(dxl * dxl + dyl * dyl);
            for (int i = a + 1; i < b; i++)
            {
                double d = len < 1e-9
                    ? Math.Sqrt((pts[i].x - ax) * (pts[i].x - ax) + (pts[i].y - ay) * (pts[i].y - ay))
                    : Math.Abs(dxl * (ay - pts[i].y) - (ax - pts[i].x) * dyl) / len;
                if (d > maxd) { maxd = d; idx = i; }
            }
            if (maxd > tol && idx > 0) { keep[idx] = true; st.Push((a, idx)); st.Push((idx, b)); }
        }
        var outp = new List<(int x, int y)>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
        return outp;
    }
}
