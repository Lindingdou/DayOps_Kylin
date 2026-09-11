using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 侧面三角网·顶线↔底线放样成直纹面三角网（对应原 <c>MeshEditLib.Tools.SideSurfaceBuilder.LoftRaw</c>）。
///
/// 对齐逻辑照原版：开线按端点就近翻转；闭环先对齐绕向(XY 带符号面积同号)、再旋转底环使起点最近。
/// 缝合改为**最短横档和动态规划**(Christiansen–Sederberg「最短对角线」的全局最优版)，替掉原版的
/// 「归一化弧长拉链」—— 弧长拉链假设顶底两线"走过的比例"一一对应，一旦起点错位 / 一侧多绕一段 / 疏密悬殊，
/// 参数就整体漂移，到拐角处把"角前一点 + 角点 + 角后一点"缝成横跨拐角的大扇形三角(用户截图所见的拓扑错误)。
/// DP 在 (i,j) 单调网格上找「所用横档(顶点↔底点连线)总长最短」的推进路径：每个顶点都被缝到对面最近的
/// 一段，拐角自然对拐角；顶底节点数不等 / 一侧有绕行 / 起点不齐 都只是局部代价，不会全局漂移。
/// 不用「最小面积」作代价：台阶面近似平面时任何单调三角化面积几乎相等，代价打平会退化成"先扫完一侧再扫另一侧"
/// 的双扇形(实测), 横档长度则天然排斥长横档。
/// 闭环另试起点最近的前几个候选旋转取总代价最小者；超大规模走带状 DP(回溯表按字节预算限带宽)。
/// 纯几何、可单测。只出 (verts, tris)；闭环时首尾顶点不重复(索引回绕), 侧壁自身即拓扑闭合的环带。
/// </summary>
public static class SideSurface
{
    private const double EpsLen2 = 1e-12;
    private const int BackPtrBudget = 16_000_000;   // 回溯表字节上限：超过则沿弧长对角线带状 DP
    private const int MinHalfBand = 48;             // 带状 DP 最小半带宽(格)
    private const int ClosedStartCandidates = 3;    // 闭环起点候选数(底环离顶环起点最近的前 K 个顶点)
    private const long CandidateCellLimit = 4_000_000;  // 超过此规模只试 1 个起点

    /// <summary>放样。top/bot=有序 3D 点线; closed=闭环(首尾重合的开线也按闭环); flip=翻转三角朝向。失败返回空。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) Loft(
        IReadOnlyList<(double x, double y, double z)> topLine, IReadOnlyList<(double x, double y, double z)> botLine,
        bool closed, bool flip)
    {
        var verts = new List<(double x, double y, double z)>();
        var tris = new List<(int a, int b, int c)>();
        var top = Dedupe(new List<(double x, double y, double z)>(topLine ?? new List<(double, double, double)>()));
        var bot = Dedupe(new List<(double x, double y, double z)>(botLine ?? new List<(double, double, double)>()));
        if (top.Count < 2 || bot.Count < 2) return (verts, tris);

        // 两条线都首尾重合 → 几何上就是环, 按闭环处理(否则开线缝合会把首尾接缝当端点硬配对)
        if (!closed && top.Count >= 4 && bot.Count >= 4 && Dist2(top[0], top[^1]) < EpsLen2 && Dist2(bot[0], bot[^1]) < EpsLen2)
            closed = true;

        if (!closed)
        {
            AlignOpen(top, bot);
            var (steps, _) = Stitch(top, bot);
            if (steps == null) return (verts, tris);
            verts.AddRange(top); verts.AddRange(bot);
            Emit(steps, top.Count, bot.Count, flip, tris);
            return (verts, tris);
        }

        if (top.Count > 2 && Dist2(top[^1], top[0]) < EpsLen2) top.RemoveAt(top.Count - 1);
        if (bot.Count > 2 && Dist2(bot[^1], bot[0]) < EpsLen2) bot.RemoveAt(bot.Count - 1);
        if (SignedAreaXY(top) * SignedAreaXY(bot) < 0) bot.Reverse();

        int n = top.Count, m = bot.Count;
        var topRing = new List<(double x, double y, double z)>(n + 1); topRing.AddRange(top); topRing.Add(top[0]);
        int k = (long)n * m <= CandidateCellLimit ? Math.Min(ClosedStartCandidates, m) : 1;
        bool[]? bestSteps = null; double bestCost = double.MaxValue; int bestStart = 0;
        foreach (int start in NearestIndices(top[0], bot, k))
        {
            var botRing = Rotate(bot, start); botRing.Add(botRing[0]);
            var (steps, cost) = Stitch(topRing, botRing);
            if (steps != null && cost < bestCost) { bestCost = cost; bestSteps = steps; bestStart = start; }
        }
        if (bestSteps == null) return (verts, tris);
        verts.AddRange(top);
        verts.AddRange(Rotate(bot, bestStart));
        Emit(bestSteps, n, m, flip, tris);   // 环上第 n / m 号位置即首点, 索引回绕不重顶点
        return (verts, tris);
    }

    /// <summary>
    /// 最短横档和 DP 缝合：状态 (i,j)=当前横档 (T_i,B_j)，从 (0,0) 单调推进到 (n-1,m-1)，
    /// 每步推顶(三角 T_{i-1},B_j,T_i)或推底(三角 T_i,B_{j-1},B_j)，路径上所有横档长度之和最小。
    /// 返回推进序列(true=推顶)与总代价；退化返回 (null, +∞)。
    /// </summary>
    internal static (bool[]? steps, double cost) Stitch(
        IReadOnlyList<(double x, double y, double z)> T, IReadOnlyList<(double x, double y, double z)> B)
    {
        int n = T.Count, m = B.Count;
        if (n < 2 || m < 2) return (null, double.MaxValue);

        // 带宽：整表放得下就全表；否则沿弧长对角线开带(带是单调阶梯, 保证 (0,0)→(n-1,m-1) 连通)
        long full = (long)n * m;
        int half = full <= BackPtrBudget ? m : Math.Max(MinHalfBand, (int)(BackPtrBudget / (2L * n)));
        var lo = new int[n]; var hi = new int[n]; var off = new long[n];
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            long jc = (long)i * (m - 1) / (n - 1);
            lo[i] = (int)Math.Max(0, jc - half); hi[i] = (int)Math.Min(m - 1, jc + half);
            if (i > 0 && lo[i] > hi[i - 1]) lo[i] = hi[i - 1];   // 相邻两行至少共一列, 带内路径才连通
            off[i] = total; total += hi[i] - lo[i] + 1;
        }
        if (total > int.MaxValue) return (null, double.MaxValue);
        var back = new byte[total];            // 0=来自推顶 (i-1,j)  1=来自推底 (i,j-1)
        var prev = new double[m]; var cur = new double[m];
        Array.Fill(prev, double.PositiveInfinity);
        prev[0] = 0;
        for (int j = 1; j <= hi[0]; j++)
        {
            prev[j] = prev[j - 1] + Math.Sqrt(Dist2(T[0], B[j]));
            back[off[0] + j] = 1;
        }
        for (int i = 1; i < n; i++)
        {
            Array.Fill(cur, double.PositiveInfinity);
            for (int j = lo[i]; j <= hi[i]; j++)
            {
                double fromTop = (j >= lo[i - 1] && j <= hi[i - 1]) ? prev[j] : double.PositiveInfinity;
                double fromBot = j > lo[i] ? cur[j - 1] : double.PositiveInfinity;
                double rung = Math.Sqrt(Dist2(T[i], B[j]));
                if (fromTop <= fromBot) { cur[j] = fromTop + rung; back[off[i] + j - lo[i]] = 0; }
                else { cur[j] = fromBot + rung; back[off[i] + j - lo[i]] = 1; }
            }
            (prev, cur) = (cur, prev);
        }
        double best = prev[m - 1];
        if (double.IsPositiveInfinity(best) || double.IsNaN(best)) return (null, double.MaxValue);

        var steps = new bool[n + m - 2];
        int si = steps.Length, ci = n - 1, cj = m - 1;
        while (ci > 0 || cj > 0)
        {
            byte b = back[off[ci] + cj - lo[ci]];
            if (ci > 0 && (b == 0 || cj == 0)) { steps[--si] = true; ci--; }
            else { steps[--si] = false; cj--; }
        }
        return (steps, best);
    }

    // 按推进序列出三角；topMod/botMod=真实顶点数(闭环时序列比顶点多走一格, 回绕到首点)
    private static void Emit(bool[] steps, int topMod, int botMod, bool flip, List<(int a, int b, int c)> tris)
    {
        int i = 0, j = 0, botBase = topMod;
        foreach (bool advanceTop in steps)
        {
            if (advanceTop) { AddTri(tris, i % topMod, botBase + j % botMod, (i + 1) % topMod, flip); i++; }
            else { AddTri(tris, i % topMod, botBase + j % botMod, botBase + (j + 1) % botMod, flip); j++; }
        }
    }

    private static List<(double x, double y, double z)> Dedupe(List<(double x, double y, double z)> pts)
    {
        var outp = new List<(double x, double y, double z)>(pts.Count);
        foreach (var p in pts)
            if (outp.Count == 0 || Dist2(outp[^1], p) > EpsLen2) outp.Add(p);
        return outp;
    }

    // 开放线：端点配对距离哪种更小就用哪种（必要时翻转底线）
    private static void AlignOpen(List<(double x, double y, double z)> top, List<(double x, double y, double z)> bot)
    {
        double straight = Dist2(top[0], bot[0]) + Dist2(top[^1], bot[^1]);
        double reversed = Dist2(top[0], bot[^1]) + Dist2(top[^1], bot[0]);
        if (reversed < straight) bot.Reverse();
    }

    // 离 p 最近的前 k 个顶点下标(按距离升序)
    private static List<int> NearestIndices((double x, double y, double z) p, List<(double x, double y, double z)> pts, int k)
    {
        var idx = new List<int>(pts.Count);
        for (int i = 0; i < pts.Count; i++) idx.Add(i);
        idx.Sort((a, b) => Dist2(p, pts[a]).CompareTo(Dist2(p, pts[b])));
        if (idx.Count > k) idx.RemoveRange(k, idx.Count - k);
        return idx;
    }

    private static List<(double x, double y, double z)> Rotate(List<(double x, double y, double z)> pts, int start)
    {
        var rot = new List<(double x, double y, double z)>(pts.Count + 1);
        for (int k = 0; k < pts.Count; k++) rot.Add(pts[(start + k) % pts.Count]);
        return rot;
    }

    private static double SignedAreaXY(List<(double x, double y, double z)> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            s += a.x * b.y - b.x * a.y;
        }
        return 0.5 * s;
    }

    private static void AddTri(List<(int a, int b, int c)> idx, int a, int b, int c, bool flip)
    {
        if (!flip) idx.Add((a, b, c)); else idx.Add((a, c, b));
    }

    private static double Dist2((double x, double y, double z) a, (double x, double y, double z) b)
    {
        double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx * dx + dy * dy + dz * dz;
    }
}
