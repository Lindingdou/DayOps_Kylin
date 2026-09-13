// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/UnitGraph.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>
/// 采掘单元之间的<b>先后关系图</b>（压覆 DAG 降到体的粒度）。
///
/// <para><b>与量层是同一条不等式，换了粒度</b>：量层写成 <c>x_A − x_B ≥ Δ</c>
/// （见 `docs/月度采剥接续_设计.md`），体的粒度上它就是一条边 A→B。
/// 煤岩混排同一条链 —— 「剥岩才能采煤」和「层间岩等上层煤采完」不是两套机制。</para>
///
/// <para><b>U3 压覆边只能几何判，不能靠编号</b>：岩单元按<b>标高格</b>分幅
/// （<c>RockBenchLevels</c> 以底标高命名），煤单元按<b>露头带</b>分幅，
/// 两套 <c>BandId</c>/<c>PanelIndex</c> 根本不同源。拿编号配对会静默错位 ——
/// 错位的图跑出来的计划每一项校核都是 ✓。</para>
///
/// <para><b>⚠ 只有一带时压覆边为 0 是【对的】，不是 bug。</b>
/// 第 1 带的煤和岩都露在地表、并排摆着，谁都能直接挖，本来就没有先后。
/// 压覆发生在<b>跨带</b>：上一级第 j 带往里推之后就压到本级第 j 带头上
/// （退距 <c>Δz/tanα</c> 通常小于带宽 W，所以是部分重叠）。
/// 采场侧目前只出一带（见 `docs/采掘单元排产_设计.md` §2），所以图现在是空的 ——
/// <see cref="UnitGraph.Notes"/> 必须把这句说出来。不说的话，将来多带枚举做出来、
/// 边却因为某个 bug 仍然是 0，<b>没有任何人会发现</b>。</para>
/// </summary>
public sealed class UnitGraph
{
    public IReadOnlyList<MineUnit> Units { get; }

    /// <summary>前驱表：<c>Pred[i]</c> = 必须在单元 i <b>之前</b>完成的单元下标。</summary>
    public IReadOnlyList<IReadOnlyList<int>> Pred { get; }

    /// <summary>引擎替用户做的判断 / 需要用户知道的事实。<b>一条都不许静默</b>。</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>压覆边总数。</summary>
    public int EdgeCount { get; }

    private readonly Dictionary<string, int> _byId;

    internal UnitGraph(IReadOnlyList<MineUnit> units, List<List<int>> pred, List<string> notes)
    {
        Units = units;
        Pred = pred;
        Notes = notes;
        EdgeCount = pred.Sum(p => p.Count);
        _byId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < units.Count; i++) _byId[units[i].UnitId] = i;
    }

    /// <summary>按 UnitId 找下标，找不到返回 −1。</summary>
    public int IndexOf(string unitId) => _byId.TryGetValue(unitId ?? "", out int i) ? i : -1;

    /// <summary>
    /// 单元 i 的<b>未完成前驱闭包</b>（不含 i 自己）—— 「要采 i，还必须先剥掉哪些体」。
    ///
    /// <para><paramref name="doneOrPlanned"/> 里的单元视为已完成，不再往下展开。
    /// 传本月已入选的集合进来，闭包就是<b>增量</b>，不会把同一个岩单元数两遍。</para>
    /// </summary>
    public List<int> Closure(int i, HashSet<int>? doneOrPlanned = null)
    {
        var result = new List<int>();
        if (i < 0 || i >= Units.Count) return result;
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(i);
        seen.Add(i);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            foreach (int p in Pred[cur])
            {
                if (!seen.Add(p)) continue;
                if (doneOrPlanned != null && doneOrPlanned.Contains(p)) continue;
                if (Units[p].RemainM3 <= 1e-9) continue;      // 已采完的不算欠账
                result.Add(p);
                stack.Push(p);
            }
        }
        return result;
    }

    /// <summary>单元 i 的前驱是否全部完成（在 <paramref name="done"/> 里或 <c>RemainM3</c> 为 0）—— 它在不在可采前沿上。</summary>
    public bool IsFree(int i, HashSet<int>? done = null)
    {
        if (i < 0 || i >= Units.Count) return false;
        foreach (int p in Pred[i])
        {
            if (Units[p].RemainM3 <= 1e-9) continue;
            if (done != null && done.Contains(p)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// 拓扑序（前驱在前）。图有环时把环上的单元<b>按原序附在末尾并报出来</b> ——
    /// 环意味着几何判出了自相矛盾的压覆（A 压 B 又 B 压 A），
    /// <b>吞掉它比报出来危险</b>：吞掉的话计划照出，只是顺序悄悄错了。
    /// </summary>
    public List<int> TopoOrder(out int cycleCount)
    {
        int n = Units.Count;
        var indeg = new int[n];
        var succ = new List<int>[n];
        for (int i = 0; i < n; i++) succ[i] = new List<int>();
        for (int i = 0; i < n; i++)
            foreach (int p in Pred[i]) { indeg[i]++; succ[p].Add(i); }

        var q = new Queue<int>();
        for (int i = 0; i < n; i++) if (indeg[i] == 0) q.Enqueue(i);
        var order = new List<int>(n);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            order.Add(cur);
            foreach (int s in succ[cur]) if (--indeg[s] == 0) q.Enqueue(s);
        }
        cycleCount = n - order.Count;
        if (cycleCount > 0)
            for (int i = 0; i < n; i++) if (indeg[i] > 0) order.Add(i);
        return order;
    }
}

/// <summary>建图。纯几何，不碰 GUI、不碰图纸 —— 台架上整条能跑。</summary>
public static class UnitGraphBuilder
{
    public sealed class Options
    {
        /// <summary>
        /// 推进方向单位向量（水平）。给了就按<b>单侧带</b>判占地（体只往这个方向占 W）；
        /// 不给就退回<b>双侧走廊</b>并留条。
        ///
        /// <para><b>为什么退回的是"更宽"那一侧</b>：宽了只会多出压覆边（把本可以并行的两个体
        /// 排成先后，计划偏保守）；窄了会<b>漏掉真实的压覆</b>，排出"煤上面的岩还没剥"的计划。
        /// 两种错的代价不对称，所以兜底往保守那边倒。</para>
        /// </summary>
        public double AdvanceDx, AdvanceDy;

        /// <summary>
        /// 标高判据容差（m）。上方单元底 ≥ 下方单元顶 − 本值，才算"压在上面"。
        /// <para><b>刻意无入口</b>（本类两个阈值都是）：压覆是<b>几何事实</b>，不是计划选项 ——
        /// 放到排产界面上，同一批体会因为"这次容差调大了"而变出不同的先后关系，
        /// 而两次都自洽、谁也解释不了谁。要调只能在判据/台架里显式给。</para>
        /// </summary>
        public double ZToleranceM = 0.5;

        /// <summary>
        /// 平面重叠比例闸门：下方单元的采样点有多少落进上方单元的占地，才算压覆。
        ///
        /// <para><b>不能取 0</b>：相邻两级台阶本来就共边，边上蹭到一两个点是常态，
        /// 那样每一对相邻单元都会连边，图退化成全序 ⇒ 一个月只采得动一个体。</para>
        /// </summary>
        public double MinOverlapRatio = 0.20;

        /// <summary>沿前脸轨的采样步长（m）。</summary>
        public double SampleStepM = 10.0;
    }

    public static UnitGraph Build(IReadOnlyList<MineUnit>? units, Options? opt = null)
    {
        opt ??= new Options();
        var notes = new List<string>();
        var us = units ?? (IReadOnlyList<MineUnit>)Array.Empty<MineUnit>();
        int n = us.Count;
        var pred = new List<List<int>>(n);
        for (int i = 0; i < n; i++) pred.Add(new List<int>());
        if (n == 0)
        {
            notes.Add("◆ 没有采掘单元 —— 先在「采矿模型」里生成一次。");
            return new UnitGraph(us, pred, notes);
        }

        bool hasDir = Math.Abs(opt.AdvanceDx) + Math.Abs(opt.AdvanceDy) > 1e-9;
        double adx = 0, ady = 0;
        if (hasDir)
        {
            double len = Math.Sqrt(opt.AdvanceDx * opt.AdvanceDx + opt.AdvanceDy * opt.AdvanceDy);
            adx = opt.AdvanceDx / len; ady = opt.AdvanceDy / len;
        }
        else
        {
            notes.Add("· 没给推进方向，占地按【双侧走廊】判（偏保守：可能多出压覆边，不会漏）。"
                    + "要精确请传工作线推进方向。");
        }

        // 逐对判：j 压 i 吗？先按标高粗筛（绝大多数对在这里就否了），再按包围盒，最后才逐点。
        for (int i = 0; i < n; i++)
        {
            var lo = us[i];
            if (lo.RailXy.Length < 4) continue;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                var hi = us[j];
                if (hi.RailXy.Length < 4) continue;

                // ① 标高：hi 必须整体在 lo 之上
                if (hi.ZLo < lo.ZHi - opt.ZToleranceM) continue;
                // ② 包围盒
                if (hi.MaxX < lo.MinX || hi.MinX > lo.MaxX || hi.MaxY < lo.MinY || hi.MinY > lo.MaxY) continue;
                // ③ 逐点
                if (OverlapRatio(lo, hi, adx, ady, hasDir, opt.SampleStepM) >= opt.MinOverlapRatio)
                    pred[i].Add(j);
            }
        }

        int edges = pred.Sum(p => p.Count);
        if (edges == 0 && n > 1)
        {
            // ⚠ 这句不能省。见类头注释：只有一带时边为 0 是对的，
            //    但"边为 0"和"建图坏了"长得一模一样，必须由引擎自己说清是哪一种。
            int levels = us.Select(u => Math.Round(u.ZLo, 1)).Distinct().Count();
            notes.Add($"· 压覆边 0 条（{n} 个单元 / {levels} 个标高）。"
                    + "采掘单元都在同一推进带上、各自露在地表时这是【正常】的 —— 本月内无先后约束。"
                    + "若采矿模型已经出了多个推进带却仍是 0 条，那就是几何判出了问题。");
        }
        else if (edges > 0)
        {
            notes.Add($"· 压覆边 {edges} 条（{n} 个单元），"
                    + $"平均每个单元 {(double)edges / n:0.##} 个前驱。");
        }

        var g = new UnitGraph(us, pred, notes);
        g.TopoOrder(out int cyc);
        if (cyc > 0)
            notes.Add($"◆ 压覆图里有环，涉及 {cyc} 个单元 —— 几何判出了 A 压 B 又 B 压 A。"
                    + "这些单元的先后不可信，已按原序附在末尾。检查标高容差与重叠闸门。");
        return g;
    }

    /// <summary>lo 的采样点里，有多大比例落进 hi 的占地。</summary>
    private static double OverlapRatio(MineUnit lo, MineUnit hi,
                                       double adx, double ady, bool hasDir, double step)
    {
        int inCount = 0, total = 0;
        int segs = lo.RailXy.Length / 2 - 1;
        for (int s = 0; s < segs; s++)
        {
            double x0 = lo.RailXy[s * 2], y0 = lo.RailXy[s * 2 + 1];
            double x1 = lo.RailXy[s * 2 + 2], y1 = lo.RailXy[s * 2 + 3];
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int k = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1e-3, step)));
            for (int t = 0; t < k; t++)
            {
                double f = (t + 0.5) / k;
                double px = x0 + (x1 - x0) * f, py = y0 + (y1 - y0) * f;
                total++;
                if (InFootprint(px, py, hi, adx, ady, hasDir)) inCount++;
            }
        }
        // 单段退化线（只有两点且极短）时至少判一次质心
        if (total == 0) { total = 1; if (InFootprint(lo.Cx, lo.Cy, hi, adx, ady, hasDir)) inCount = 1; }
        return (double)inCount / total;
    }

    /// <summary>点在不在 hi 的占地里：到前脸轨的距离 ≤ W，且（给了推进方向时）在推进那一侧。</summary>
    private static bool InFootprint(double px, double py, MineUnit hi,
                                    double adx, double ady, bool hasDir)
    {
        double w = hi.WidthM > 1e-6 ? hi.WidthM : 40.0;
        double bestD2 = double.MaxValue, bestVx = 0, bestVy = 0;
        int segs = hi.RailXy.Length / 2 - 1;
        for (int s = 0; s < segs; s++)
        {
            double x0 = hi.RailXy[s * 2], y0 = hi.RailXy[s * 2 + 1];
            double x1 = hi.RailXy[s * 2 + 2], y1 = hi.RailXy[s * 2 + 3];
            double dx = x1 - x0, dy = y1 - y0;
            double L2 = dx * dx + dy * dy;
            double t = L2 <= 1e-12 ? 0 : ((px - x0) * dx + (py - y0) * dy) / L2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double qx = x0 + dx * t, qy = y0 + dy * t;
            double vx = px - qx, vy = py - qy;
            double d2 = vx * vx + vy * vy;
            if (d2 < bestD2) { bestD2 = d2; bestVx = vx; bestVy = vy; }
        }
        if (bestD2 > w * w) return false;
        if (!hasDir) return true;                          // 双侧走廊（保守兜底）
        return bestVx * adx + bestVy * ady >= -1e-6;       // 单侧带：只算推进那一侧
    }
}
