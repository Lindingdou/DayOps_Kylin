// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneRaster.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  占地并集 —— 一组采掘单元的平面占地 → 一条（或几条）外轮廓环。
//
//  ── 为什么是栅格，不是多边形布尔 ──
//  本期一个作业面下挂着几个采掘带，它们的占地互相搭边、拐角处还可能自交
//  （凹弯处后界轨会折回，UnitPrism 的 folded 就是数这个）。多边形并集在这种输入上
//  要处理自交、共线、退化边，一条都写错就是几何雪崩。而这里要的只是**范围**：
//  栅格化 → 连通块 → Moore 描边 → DP 抽稀，任意凹形/自交都稳，且误差可量化、可报出。
//  与 PlanLib 的 RegionGeometry 是同一条路子（那边做的是区域互斥的差集），
//  两个模块之间没有引用关系，所以各留一份实现；口径（Moore + DP）刻意保持一致。
//
//  ── 三条纪律 ──
//  ① **格距报出来**：轮廓精度就是格距，藏起来的话人会以为这是一条实测边界。
//  ② **不连通不合并**：两块不挨着的占地不去求它们的外包轮廓 —— 那会把中间那片
//     根本不作业的地一起圈进来。分成几块就返回几块，由调用方决定怎么用。
//  ③ **描边环面积与栅格面积要对得上**：细颈/一格宽的地方 Moore 描边会走出去又走回来，
//     环的鞋带面积会塌掉。两个面积差得多就说明描边不可信，<see cref="Piece.Trustworthy"/>
//     把这件事摆出来，而不是让调用方拿一个静默错的面积去算工程量。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>占地并集的栅格件。纯几何、无 IO、可离线判据。</summary>
internal static class ZoneRaster
{
    /// <summary>栅格总格数上限（超了自动放粗格距）。1000×1000 的图在 2m 格上正好到量级。</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>并集出来的一块（一个连通块 = 一条闭合环）。</summary>
    public sealed class Piece
    {
        /// <summary>外轮廓（世界坐标，首尾不重复，隐式闭合）。</summary>
        public List<(double X, double Y)> Ring = new();
        /// <summary>本块占的格数 × 格面积 —— 这是**真实占地面积**（不受描边失真影响）。</summary>
        public double CellAreaM2;
        /// <summary>描边环的鞋带面积。</summary>
        public double RingAreaM2;
        public int CellCount;

        /// <summary>
        /// 描边可信：环面积与栅格面积相差 &lt; 15%。
        /// <para>假时说明本块有一格宽的细颈或孔洞，Moore 描边走出去又走回来，环把自己抵消掉了。
        /// 此时轮廓只能看态势，面积一律用 <see cref="CellAreaM2"/>。</para>
        /// </summary>
        public bool Trustworthy =>
            CellAreaM2 > 0 && Math.Abs(RingAreaM2 - CellAreaM2) <= 0.15 * CellAreaM2;
    }

    public sealed class UnionResult
    {
        public List<Piece> Pieces = new();
        /// <summary>格距（m）—— 轮廓精度就是它。</summary>
        public double CellM;
        /// <summary>全部格子的面积（= 各块之和）。</summary>
        public double TotalAreaM2;
        /// <summary>输入多边形里有几条围不成面（点数 &lt; 3）。</summary>
        public int Degenerate;
        public string Note = "";
        public bool Ok => Pieces.Count > 0;
    }

    /// <summary>
    /// 求一组平面多边形的并集轮廓。
    /// </summary>
    /// <param name="polysXy">每条 = 扁平 [x0,y0,x1,y1,...]（首尾不重复）。</param>
    /// <param name="wantCellM">想要的格距；&lt;=0 = 按范围自适应。实际格距见 <see cref="UnionResult.CellM"/>。</param>
    public static UnionResult Union(IReadOnlyList<double[]>? polysXy, double wantCellM = 0)
    {
        var g = Grid.Lattice(polysXy, wantCellM, 0, out string why);
        if (g == null)
        {
            // 一条都建不出格网时**退化条数照样要记账**（ZR2）——
            // 「0 块占地」与「N 块占地都围不成面」在界面上长得一样，而补法完全不同。
            var bad = new UnionResult { Note = why };
            if (polysXy != null)
                foreach (var p in polysXy) if (p == null || p.Length < 6) bad.Degenerate++;
            return bad;
        }
        g.FillAll(polysXy!);
        var res = g.Trace();
        res.Degenerate = g.Degenerate;
        res.Note = $"占地并集：{g.Usable} 块占地 → {res.Pieces.Count} 个连通块，"
                 + $"合计 {res.TotalAreaM2 / 1e4:0.##} 万m²（格距 {g.CellM:0.##} m，轮廓精度即此值）"
                 + (res.Degenerate > 0 ? $"；{res.Degenerate} 条占地点数不足已跳过" : "");
        return res;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  格网件 —— 同一片格网上的若干张掩膜，可膨胀、可相减
    //
    //  ── 为什么工序区域必须走这里，不走多边形偏移 ──
    //  爆破警戒区是「爆区外扩 R」，R 一般 200~300m，而一个采掘单元的宽只有几十米 ——
    //  这是**大偏移**，正是等距偏移最容易翻面的量级。ZE6 记过一笔真账：100m 见方内缩 80m，
    //  环被里外翻了个个儿，而它仍是规整方块（面积剩 36%、绕向照旧为正），
    //  `RingOffset` 的收口守卫与面积比**两条都判不出来**。
    //  栅格膨胀在任何量级上都不会翻面：它算的是「离最近一格有多远」，没有环这个概念。
    //
    //  推排带是「全幅 减 卸载带」—— 同理不走多边形布尔（Z5 已经把这条钉死了）。
    //
    //  ── 两条纪律 ──
    //  ① **相减的两张掩膜必须在同一片格网上**。各自 `Union` 一次再相减是错的：
    //     两次自适应格距不一样、原点也不一样，格与格对不上，减出来的边缘全是锯齿状假空洞。
    //     所以 `Lattice` 一次建好格网，之后所有掩膜都从 <see cref="Grid.Blank"/> 出。
    //  ② **膨胀顶到格网边界要报出来**。顶到就意味着警戒区被截断了 ——
    //     截断后的轮廓仍然是一条规规矩矩的闭合环，图上看不出任何异常。
    //     所以建格网时要按膨胀半径预留 `padM`，并在 <see cref="Grid.ClippedAtBorder"/> 上留证据。
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>同一片格网上的一张布尔掩膜。纯几何、无 IO。</summary>
    public sealed class Grid
    {
        public double MinX, MinY, CellM;
        public int Nx, Ny;
        public bool[] Mask = Array.Empty<bool>();

        /// <summary>建格网时点数不足 3 的输入条数。</summary>
        public int Degenerate;
        /// <summary>建格网时可用的输入条数。</summary>
        public int Usable;
        /// <summary>膨胀后有没有顶到格网边界（true = 结果被截断，**必须报出来**）。</summary>
        public bool ClippedAtBorder;

        public double CellAreaM2 => CellM * CellM;

        public int Count
        {
            get { int c = 0; for (int i = 0; i < Mask.Length; i++) if (Mask[i]) c++; return c; }
        }

        public double AreaM2 => Count * CellAreaM2;
        public bool Any { get { for (int i = 0; i < Mask.Length; i++) if (Mask[i]) return true; return false; } }

        /// <summary>某个世界坐标落在的那一格亮不亮（落在格网外一律 false）。</summary>
        public bool At(double x, double y)
        {
            int ix = (int)Math.Floor((x - MinX) / CellM);
            int iy = (int)Math.Floor((y - MinY) / CellM);
            if (ix < 0 || iy < 0 || ix >= Nx || iy >= Ny) return false;
            return Mask[iy * Nx + ix];
        }

        /// <summary>同一片格网上的一张空掩膜。</summary>
        public Grid Blank() => new()
        { MinX = MinX, MinY = MinY, CellM = CellM, Nx = Nx, Ny = Ny, Mask = new bool[Nx * Ny] };

        /// <summary>与另一片格网是不是同一片（相减/比较的前提）。</summary>
        public bool SameLatticeAs(Grid? o) =>
            o != null && o.Nx == Nx && o.Ny == Ny
            && Math.Abs(o.CellM - CellM) < 1e-9
            && Math.Abs(o.MinX - MinX) < 1e-6 && Math.Abs(o.MinY - MinY) < 1e-6;

        /// <summary>
        /// 按一批多边形的范围建一片格网（**只建，不填**）。
        /// </summary>
        /// <param name="polysXy">用来定范围的全部多边形 —— 后面要在同一片格网上比较/相减的，
        /// <b>都要一起传进来</b>，否则各自的格网对不上。</param>
        /// <param name="wantCellM">想要的格距；&lt;=0 = 按范围自适应。</param>
        /// <param name="padM">四周额外预留（m）—— 要膨胀 R 米就至少给 R，否则膨胀会被格网边界削掉。</param>
        public static Grid? Lattice(IReadOnlyList<double[]>? polysXy, double wantCellM, double padM, out string why)
        {
            why = "";
            if (polysXy == null || polysXy.Count == 0) { why = "没有可并的占地。"; return null; }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            int usable = 0, degenerate = 0;
            foreach (var p in polysXy)
            {
                if (p == null || p.Length < 6) { degenerate++; continue; }
                usable++;
                for (int i = 0; i + 1 < p.Length; i += 2)
                {
                    if (p[i] < minX) minX = p[i];
                    if (p[i] > maxX) maxX = p[i];
                    if (p[i + 1] < minY) minY = p[i + 1];
                    if (p[i + 1] > maxY) maxY = p[i + 1];
                }
            }
            if (usable == 0) { why = $"{degenerate} 条占地都不足 3 个点，围不成面。"; return null; }

            double span = Math.Max(maxX - minX, maxY - minY);
            if (!(span > 0)) { why = "全部占地退化成一个点。"; return null; }

            // 格距：默认按跨度取 1/600，夹在 [0.5m, 20m]；再按总格数上限放粗。
            double cell = wantCellM > 1e-6 ? wantCellM : Math.Clamp(span / 600.0, 0.5, 20.0);
            double pad = Math.Max(0, padM);
            // 四周各留一格余量：描边取格心，贴着边界的那一圈格子需要有外邻居才走得通
            minX -= cell * 2 + pad; minY -= cell * 2 + pad;
            maxX += cell * 2 + pad; maxY += cell * 2 + pad;
            int nx, ny;
            while (true)
            {
                nx = (int)((maxX - minX) / cell) + 1;
                ny = (int)((maxY - minY) / cell) + 1;
                if ((long)nx * ny <= MaxCells) break;
                cell *= 1.3;
            }

            return new Grid
            {
                MinX = minX, MinY = minY, CellM = cell, Nx = nx, Ny = ny,
                Mask = new bool[nx * ny], Usable = usable, Degenerate = degenerate,
            };
        }

        /// <summary>把一条多边形填进本掩膜（偶奇规则）。</summary>
        public void Fill(double[]? poly)
        {
            if (poly == null || poly.Length < 6) return;
            Rasterize(poly, MinX, MinY, CellM, Nx, Ny, Mask);
        }

        public void FillAll(IEnumerable<double[]>? polys)
        {
            if (polys == null) return;
            foreach (var p in polys) Fill(p);
        }

        /// <summary>
        /// 膨胀 <paramref name="radiusM"/> 米 —— 精确欧氏距离变换（Felzenszwalb 可分离算法，O(格数)）。
        /// <para>不是"逐格找邻居"（那是 O(格数 × 半径²)，300m 半径在 2m 格上要 7 万次/格），
        /// 也不是切比雪夫/棋盘距离（那会把圆膨成方，300m 半径的角上多出 124m）。</para>
        /// </summary>
        public Grid Dilate(double radiusM)
        {
            var g = Blank();
            g.Usable = Usable; g.Degenerate = Degenerate;
            if (!(radiusM > 1e-9)) { Array.Copy(Mask, g.Mask, Mask.Length); return g; }

            var d2 = Edt2(Mask, Nx, Ny);
            double rc = radiusM / CellM, lim = rc * rc;
            for (int i = 0; i < d2.Length; i++) if (d2[i] <= lim) g.Mask[i] = true;

            for (int x = 0; x < Nx && !g.ClippedAtBorder; x++)
                if (g.Mask[x] || g.Mask[(Ny - 1) * Nx + x]) g.ClippedAtBorder = true;
            for (int y = 0; y < Ny && !g.ClippedAtBorder; y++)
                if (g.Mask[y * Nx] || g.Mask[y * Nx + Nx - 1]) g.ClippedAtBorder = true;
            return g;
        }

        /// <summary>本掩膜减去另一张（同一片格网）。格网对不上返回 null 并说明 —— <b>不悄悄换一片</b>。</summary>
        public Grid? Minus(Grid? other, out string why)
        {
            why = "";
            if (other == null) { why = "被减的那张掩膜是空的。"; return null; }
            if (!SameLatticeAs(other))
            {
                why = $"两张掩膜不在同一片格网上（{Nx}×{Ny}@{CellM:0.###}m vs "
                    + $"{other.Nx}×{other.Ny}@{other.CellM:0.###}m）—— 相减无意义。"
                    + "两张要在同一片格网上算，必须用同一次 Lattice 出来的 Blank()。";
                return null;
            }
            var g = Blank();
            g.Usable = Usable; g.Degenerate = Degenerate; g.ClippedAtBorder = ClippedAtBorder;
            for (int i = 0; i < Mask.Length; i++) g.Mask[i] = Mask[i] && !other.Mask[i];
            return g;
        }

        /// <summary>掩膜 → 连通块轮廓（Moore 描边 + DP 抽稀）。格距即轮廓精度。</summary>
        public UnionResult Trace()
        {
            var res = new UnionResult { CellM = CellM, Degenerate = Degenerate };
            var comps = Components(Mask, Nx, Ny);
            double cellArea = CellM * CellM;
            foreach (var comp in comps)
            {
                var ring = TraceBoundary(Mask, Nx, Ny, comp.Start, comp.Label, comp.Map);
                var simp = Simplify(ring, 1.2);
                if (simp.Count < 3) continue;

                var piece = new Piece { CellCount = comp.Count, CellAreaM2 = comp.Count * cellArea };
                foreach (var (x, y) in simp)
                    piece.Ring.Add((MinX + (x + 0.5) * CellM, MinY + (y + 0.5) * CellM));
                piece.RingAreaM2 = Math.Abs(Shoelace(piece.Ring));
                res.Pieces.Add(piece);
                res.TotalAreaM2 += piece.CellAreaM2;
            }
            res.Pieces.Sort((a, b) => b.CellCount.CompareTo(a.CellCount));
            return res;
        }
    }

    // ── 精确欧氏距离变换（平方距离，格为单位）──────────────────────────────
    //  d[i] = 「第 i 格到最近一个 mask 格」的平方距离。mask 全空时全为 INF。
    //  INF 取有限的 1e20 而不是 double.PositiveInfinity：算法里要做 INF-INF 的减法，
    //  用真无穷会得到 NaN，抛物线下包络整个塌掉（而结果仍是一张看着正常的图）。
    private const double EdtInf = 1e20;

    private static double[] Edt2(bool[] mask, int nx, int ny)
    {
        int n = nx * ny;
        var d = new double[n];
        for (int i = 0; i < n; i++) d[i] = mask[i] ? 0.0 : EdtInf;

        var col = new double[ny];
        for (int x = 0; x < nx; x++)
        {
            for (int y = 0; y < ny; y++) col[y] = d[y * nx + x];
            var r = Dt1(col, ny);
            for (int y = 0; y < ny; y++) d[y * nx + x] = r[y];
        }
        var row = new double[nx];
        for (int y = 0; y < ny; y++)
        {
            int b = y * nx;
            for (int x = 0; x < nx; x++) row[x] = d[b + x];
            var r = Dt1(row, nx);
            for (int x = 0; x < nx; x++) d[b + x] = r[x];
        }
        return d;
    }

    /// <summary>一维平方距离变换（抛物线下包络）。</summary>
    private static double[] Dt1(double[] f, int n)
    {
        var dst = new double[n];
        if (n == 0) return dst;
        var v = new int[n];
        var z = new double[n + 1];
        int k = 0;
        v[0] = 0; z[0] = -EdtInf; z[1] = EdtInf;
        for (int q = 1; q < n; q++)
        {
            double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            while (k > 0 && s <= z[k])
            {
                k--;
                s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            }
            k++;
            v[k] = q; z[k] = s; z[k + 1] = EdtInf;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            dst[q] = dq * dq + f[v[k]];
        }
        return dst;
    }

    // ── 扫描线填充（偶奇规则）。只扫本多边形自己的行，别为一条小带扫全图 ──
    private static void Rasterize(double[] poly, double minX, double minY, double cell, int nx, int ny, bool[] mask)
    {
        int n = poly.Length / 2;
        if (n < 3) return;
        double pminY = double.MaxValue, pmaxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double y = poly[i * 2 + 1];
            if (y < pminY) pminY = y;
            if (y > pmaxY) pmaxY = y;
        }
        int y0 = Math.Clamp((int)((pminY - minY) / cell) - 1, 0, ny - 1);
        int y1 = Math.Clamp((int)((pmaxY - minY) / cell) + 1, 0, ny - 1);

        var xs = new List<double>(16);
        for (int y = y0; y <= y1; y++)
        {
            double wy = minY + (y + 0.5) * cell;
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double ya = poly[i * 2 + 1], yb = poly[j * 2 + 1];
                double xa = poly[i * 2], xb = poly[j * 2];
                if ((ya <= wy && yb > wy) || (yb <= wy && ya > wy))
                    xs.Add(xa + (wy - ya) / (yb - ya) * (xb - xa));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = (int)Math.Floor((xs[k] - minX) / cell);
                int xb = (int)Math.Floor((xs[k + 1] - minX) / cell);
                if (xb < 0 || xa > nx - 1) continue;
                xa = Math.Clamp(xa, 0, nx - 1);
                xb = Math.Clamp(xb, 0, nx - 1);
                int row = y * nx;
                for (int x = xa; x <= xb; x++) mask[row + x] = true;
            }
        }
    }

    private readonly struct CompInfo
    {
        public readonly int Start, Count, Label;
        /// <summary>格 → 连通块号（0 = 空）。描边时用它把别的块挡在外面。</summary>
        public readonly int[] Map;
        public CompInfo(int start, int count, int label, int[] map) { Start = start; Count = count; Label = label; Map = map; }
    }

    /// <summary>8-邻接连通块。<b>全部返回</b>——丢掉小块就是把一片真作业面悄悄抹掉。</summary>
    private static List<CompInfo> Components(bool[] mask, int nx, int ny)
    {
        int N = nx * ny;
        var comp = new int[N];
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        var stack = new Stack<int>();
        var list = new List<CompInfo>();
        int label = 0;
        for (int s = 0; s < N; s++)
        {
            if (!mask[s] || comp[s] != 0) continue;
            label++;
            int count = 0, start = s;
            stack.Push(s); comp[s] = label;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); count++;
                int x = c % nx, y = c / nx;
                // 起点取"最上一行里最左"的那格：Moore 描边从一个左上角格出发才走得稳
                if (y < start / nx || (y == start / nx && x < start % nx)) start = c;
                for (int k = 0; k < 8; k++)
                {
                    int ax = x + dx[k], ay = y + dy[k];
                    if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                    int ac = ay * nx + ax;
                    if (mask[ac] && comp[ac] == 0) { comp[ac] = label; stack.Push(ac); }
                }
            }
            list.Add(new CompInfo(start, count, label, comp));
        }
        return list;
    }

    /// <summary>Moore 描边（只走本连通块的格子）。</summary>
    private static List<(int x, int y)> TraceBoundary(bool[] mask, int nx, int ny, int start, int label, int[] comp)
    {
        var ring = new List<(int x, int y)>();
        int sx = start % nx, sy = start / nx;
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 }, dy = { 0, -1, -1, -1, 0, 1, 1, 1 };
        bool In(int x, int y) => x >= 0 && y >= 0 && x < nx && y < ny && mask[y * nx + x] && comp[y * nx + x] == label;

        int px = sx, py = sy, dir = 0;
        ring.Add((sx, sy));
        int guard = 0, maxG = nx * ny * 4 + 64;
        while (guard++ < maxG)
        {
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int nd = (dir + k) % 8, ax = px + dx[nd], ay = py + dy[nd];
                if (!In(ax, ay)) continue;
                px = ax; py = ay;
                ring.Add((px, py));
                dir = (nd + 6) % 8;
                found = true;
                break;
            }
            if (!found) break;
            if (px == sx && py == sy) break;
        }
        return ring;
    }

    private static List<(int x, int y)> Simplify(List<(int x, int y)> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var st = new Stack<(int a, int b)>();
        st.Push((0, pts.Count - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop();
            double maxd = -1; int idx = -1;
            double ax = pts[a].x, ay = pts[a].y, bx = pts[b].x, by = pts[b].y;
            double ex = bx - ax, ey = by - ay, len = Math.Sqrt(ex * ex + ey * ey);
            for (int i = a + 1; i < b; i++)
            {
                double d = len < 1e-9
                    ? Math.Sqrt((pts[i].x - ax) * (pts[i].x - ax) + (pts[i].y - ay) * (pts[i].y - ay))
                    : Math.Abs(ex * (ay - pts[i].y) - (ax - pts[i].x) * ey) / len;
                if (d > maxd) { maxd = d; idx = i; }
            }
            if (maxd > tol && idx > 0) { keep[idx] = true; st.Push((a, idx)); st.Push((idx, b)); }
        }
        var outp = new List<(int x, int y)>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
        // 描边是闭合回路，首尾同一格 —— 去掉重复的尾点（环隐式闭合）
        if (outp.Count > 1 && outp[0] == outp[^1]) outp.RemoveAt(outp.Count - 1);
        return outp;
    }

    private static double Shoelace(List<(double X, double Y)> ring)
    {
        double s = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            s += p.X * q.Y - q.X * p.Y;
        }
        return s * 0.5;
    }
}
