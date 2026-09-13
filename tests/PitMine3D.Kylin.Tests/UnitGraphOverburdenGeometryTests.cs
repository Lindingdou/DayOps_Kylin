// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitGraphOverburdenGeometryTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// UC 组 · <b>压覆判据的几何验收台架</b>。
///
/// <para><b>为什么要这一组</b>：现有全部压覆判据都建在 <see cref="UnitPlanFixture.Stacked"/> 上，
/// 而那份算例把一柱三个体摆在<b>同一个 XY</b> —— 上下两级台阶的前脸轨重合、后退量为 <b>0</b>。
/// 真实工作帮不是这样：第 k 级相对第 k−1 级超前<b>一级横距 h/tanα</b>
/// （这正是 <c>MonthlyMineSchedule</c> 的 C2/H3 口径，也是 <c>StartPos(z)=(z−z₀)/tanα</c> 的口径）。
/// 「所有夹具都是退化几何、判据却全绿」在本仓库有前科 —— 倾斜煤层那次加了非退化夹具当场炸出两个真 bug。</para>
///
/// <para><b>本组不判"引擎错了"</b>，判的是<b>两套判据在什么几何下分岔、分岔的量是多少</b>
/// （边数 · 必剥闭包方量）。要不要改判据是下一步的决定，这张表是决定的输入。</para>
///
/// <para><b>参考判据与引擎只差一项</b>：引擎拿 <c>lo.RailXy</c>（前脸轨 —— <b>一条线</b>）去测 hi 的占地；
/// 参考拿 <b>lo 的占地</b>（前脸轨往推进方向铺开 <c>W_lo</c> 的<b>那一带</b>）去测。
/// 标高闸、占地定义、重叠比闸门、单侧带判定<b>逐字照抄</b>。
/// <c>across=1</c> 时参考退化成引擎口径 —— <see cref="UC1_参考实现只采前脸轨时必须与引擎逐边相同"/>
/// 就是拿这一点把参考实现钉死的。没有 UC1 的话，"两套不一样"完全可能只是我把占地测试抄错了。</para>
/// </summary>
public sealed class UnitGraphOverburdenGeometryTests
{
    private readonly ITestOutputHelper _out;
    public UnitGraphOverburdenGeometryTests(ITestOutputHelper o) => _out = o;

    private const double Z0 = 1000;        // 最下一级台阶底标高
    private const double H = 15;           // 台阶高
    private const double PanelLen = 300;   // 幅长（走向长）

    /// <summary>
    /// 参考侧沿推进方向铺几排采样点。<b>它决定重叠比的分辨率</b>（1/Across）——
    /// 用 8 排时 α=25°/W=40 那一档算出 25%，而真值 19.5%，<b>刚好跨过 20% 闸门</b>，
    /// 判据当场判红。分辨率不够会把"参考与引擎不一致"伪造出来。
    /// </summary>
    private const int Across = 64;

    /// <summary>贴着重叠比闸门 ±<b>这个带宽</b>内的算例<b>不下断言</b>（照样进表）——
    /// 那里参考侧的离散误差与闸门同量级，判红判绿都不说明问题。</summary>
    private const double GateBand = 0.03;

    /// <summary>一级横距 = 台阶高 / tanα（工作帮坡角）。</summary>
    private static double Setback(double alphaDeg) => H / Math.Tan(alphaDeg * Math.PI / 180.0);

    // ════════════════════════════════════════════════════════════════════
    //  算例：真实工作帮几何
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 走向沿 <b>Y</b>、推进沿 <b>+X</b> 的工作帮。第 k 级台阶底标高 <c>Z0+k·h</c>，
    /// 前脸轨在 <c>u_k = k·h/tanα</c> —— <b>上面的台阶超前</b>（C2）。
    /// 第 0 级是煤，其余是岩；每级沿走向切 <paramref name="panels"/> 幅。
    ///
    /// <para><b>幅长跟着 W 走</b>（<c>max(300, 20W)</c>）：相邻幅能互相够到的采样点最多占
    /// <c>2W/幅长 = 10%</c>，低于重叠比闸门 20% ⇒ <b>跨幅结构上连不出边</b>，
    /// 表里每一条边都只可能是"同一幅的上下级"，手算得清。
    /// <b>第一版写死 300m 并断言"W ≤ 120 都安全"，被 W=120 当场判红</b>（跨幅漏进来 12 条）——
    /// 幅长与 W 的比值才是那条闸，不是幅长本身。<see cref="UC1b_工作帮算例不许出现跨幅的边"/> 守着它。</para>
    /// </summary>
    private static List<MineUnit> WorkingSlope(int benches, int panels, double alphaDeg, double advanceW,
                                               double panelLen = 0)
    {
        if (panelLen <= 0) panelLen = Math.Max(PanelLen, 20 * advanceW);
        double u1 = Setback(alphaDeg);
        var list = new List<MineUnit>();
        for (int k = 0; k < benches; k++)
        {
            double u = k * u1;
            double zLo = Z0 + k * H, zHi = zLo + H;
            bool coal = k == 0;
            string seam = coal ? "煤" : $"岩{zLo:0}";
            for (int p = 1; p <= panels; p++)
            {
                double yLo = (p - 1) * panelLen, yHi = p * panelLen;
                list.Add(new MineUnit
                {
                    UnitId = $"{seam}-B1-P{p}",
                    Kind = coal ? UnitKind.Coal : UnitKind.Rock,
                    SeamCode = seam,
                    BandId = 1, PanelIndex = p, PanelCount = panels,
                    Cx = u + advanceW * 0.5, Cy = (yLo + yHi) * 0.5, Cz = (zLo + zHi) * 0.5,
                    ZLo = zLo, ZHi = zHi,
                    StrikeLenM = panelLen, WidthM = advanceW, ThickM = H,
                    InSituM3 = panelLen * advanceW * H,
                    MaterialIndex = 0,
                    // 前脸轨 = 沿走向的一条线段（真实模型里它来自采掘带的坡顶线 CrestXyz）
                    RailXy = new[] { u, yLo, u, yHi },
                    // 包围盒按推进宽膨胀 —— 与 MineUnitAdapter.ToUnits 同一写法
                    MinX = u - advanceW, MinY = yLo - advanceW,
                    MaxX = u + advanceW, MaxY = yHi + advanceW,
                });
            }
        }
        return list;
    }

    // ════════════════════════════════════════════════════════════════════
    //  参考判据（与 UnitGraphBuilder 逐字同构，只多一个 across）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>点在不在 hi 的占地里 —— 与 <c>UnitGraphBuilder.InFootprint</c> 逐字同构。</summary>
    private static bool InFootprint(double px, double py, MineUnit hi, double adx, double ady, bool hasDir)
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
        if (!hasDir) return true;
        return bestVx * adx + bestVy * ady >= -1e-6;
    }

    /// <summary>
    /// lo 的采样点。沿前脸轨按 <paramref name="step"/> 取点，再沿推进方向铺 <paramref name="across"/> 排。
    /// <para><b><paramref name="across"/> = 1 ⇒ 只取前脸轨本身 = 引擎现在的口径。</b></para>
    /// </summary>
    private static IEnumerable<(double X, double Y)> Samples(MineUnit lo, double adx, double ady,
                                                             bool hasDir, double step, int across)
    {
        double w = lo.WidthM > 1e-6 ? lo.WidthM : 40.0;
        int segs = lo.RailXy.Length / 2 - 1;
        int rows = Math.Max(1, across);
        for (int r = 0; r < rows; r++)
        {
            double off = rows <= 1 ? 0 : w * (r + 0.5) / rows;      // r 排离前脸轨多远
            double ox = hasDir ? off * adx : 0, oy = hasDir ? off * ady : 0;
            for (int s = 0; s < segs; s++)
            {
                double x0 = lo.RailXy[s * 2], y0 = lo.RailXy[s * 2 + 1];
                double x1 = lo.RailXy[s * 2 + 2], y1 = lo.RailXy[s * 2 + 3];
                double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                int k = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1e-3, step)));
                for (int t = 0; t < k; t++)
                {
                    double f = (t + 0.5) / k;
                    yield return (x0 + (x1 - x0) * f + ox, y0 + (y1 - y0) * f + oy);
                }
            }
        }
    }

    private static double OverlapRatio(MineUnit lo, MineUnit hi, double adx, double ady,
                                       bool hasDir, double step, int across)
    {
        int inC = 0, tot = 0;
        foreach (var (px, py) in Samples(lo, adx, ady, hasDir, step, across))
        {
            tot++;
            if (InFootprint(px, py, hi, adx, ady, hasDir)) inC++;
        }
        if (tot == 0) { tot = 1; if (InFootprint(lo.Cx, lo.Cy, hi, adx, ady, hasDir)) inC = 1; }
        return (double)inC / tot;
    }

    /// <summary>参考判据的边集，元素是 <c>(lo, hi)</c> = 「hi 压在 lo 上」。</summary>
    private static HashSet<(int Lo, int Hi)> RefEdges(IReadOnlyList<MineUnit> us, double adx, double ady,
                                                      int across, double zTol = 0.5,
                                                      double minOverlap = 0.20, double step = 10.0)
    {
        bool hasDir = Math.Abs(adx) + Math.Abs(ady) > 1e-9;
        if (hasDir) { double L = Math.Sqrt(adx * adx + ady * ady); adx /= L; ady /= L; }
        var e = new HashSet<(int, int)>();
        for (int i = 0; i < us.Count; i++)
        {
            var lo = us[i];
            if (lo.RailXy.Length < 4) continue;
            for (int j = 0; j < us.Count; j++)
            {
                if (i == j) continue;
                var hi = us[j];
                if (hi.RailXy.Length < 4) continue;
                if (hi.ZLo < lo.ZHi - zTol) continue;                  // ① 标高（照抄）
                // ② 包围盒粗筛【故意不做】：它是引擎的加速手段，参考侧要的是真值。
                //    只采前脸轨时它不会否掉真阳（盒子按各自 W 膨胀过），所以 UC1 仍然对得上。
                if (OverlapRatio(lo, hi, adx, ady, hasDir, step, across) >= minOverlap) e.Add((i, j));
            }
        }
        return e;
    }

    private static HashSet<(int Lo, int Hi)> EngineEdges(UnitGraph g)
    {
        var e = new HashSet<(int, int)>();
        for (int i = 0; i < g.Pred.Count; i++)
            foreach (int j in g.Pred[i]) e.Add((i, j));
        return e;
    }

    private static UnitGraph Build(IReadOnlyList<MineUnit> us, double adx, double ady)
        => UnitGraphBuilder.Build(us, new UnitGraphBuilder.Options { AdvanceDx = adx, AdvanceDy = ady });

    /// <summary>在给定边集上求未完成前驱闭包的方量（与 <c>UnitGraph.Closure</c> 同语义，自带一份是为了不开 InternalsVisibleTo）。</summary>
    private static double ClosureM3(IReadOnlyList<MineUnit> us, HashSet<(int Lo, int Hi)> edges, int i)
    {
        var pred = new Dictionary<int, List<int>>();
        foreach (var (lo, hi) in edges)
        {
            if (!pred.TryGetValue(lo, out var l)) pred[lo] = l = new List<int>();
            l.Add(hi);
        }
        double sum = 0;
        var seen = new HashSet<int> { i };
        var stack = new Stack<int>();
        stack.Push(i);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            if (!pred.TryGetValue(cur, out var ps)) continue;
            foreach (int p in ps)
            {
                if (!seen.Add(p)) continue;
                if (us[p].RemainM3 <= 1e-9) continue;
                sum += us[p].RemainM3;
                stack.Push(p);
            }
        }
        return sum;
    }

    // ════════════════════════════════════════════════════════════════════
    //  UC1 · 参考实现的构造性自检 —— 台架的地基
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【只采前脸轨时，参考判据必须与引擎<b>逐边相同</b>】
    ///
    /// <para>这条不过，下面每一张表都不能信 —— "两套判据不一样"会退化成"我把占地测试抄错了"。
    /// 六个算例覆盖：零后退 / 真后退 · 给推进方向 / 不给（双侧走廊） · 推得慢 / 推得快。</para>
    /// </summary>
    [Fact]
    public void UC1_参考实现只采前脸轨时必须与引擎逐边相同()
    {
        var cases = new (string Name, List<MineUnit> Units, double Adx, double Ady)[]
        {
            ("叠柱·零后退·双侧走廊",      UnitPlanFixture.Stacked(2, 4), 0, 0),
            ("叠柱·零后退·推进+X",        UnitPlanFixture.Stacked(2, 4), 1, 0),
            ("平摊算例·推进+X",           UnitPlanFixture.FlatFaces(),   1, 0),
            ("工作帮 α=15° W=40·推进+X",  WorkingSlope(4, 3, 15, 40),    1, 0),
            ("工作帮 α=15° W=90·推进+X",  WorkingSlope(4, 3, 15, 90),    1, 0),
            ("工作帮 α=15° W=90·双侧",    WorkingSlope(4, 3, 15, 90),    0, 0),
        };

        var bad = new List<string>();
        foreach (var (name, us, adx, ady) in cases)
        {
            var eng = EngineEdges(Build(us, adx, ady));
            var rf = RefEdges(us, adx, ady, across: 1);
            var onlyEng = eng.Except(rf).ToList();
            var onlyRef = rf.Except(eng).ToList();
            _out.WriteLine($"{name,-26} 引擎 {eng.Count,3} 条 · 参考(仅前脸轨) {rf.Count,3} 条");
            if (onlyEng.Count > 0)
                bad.Add($"{name}：引擎有而参考没有 {onlyEng.Count} 条，例如 {us[onlyEng[0].Lo].UnitId} ← {us[onlyEng[0].Hi].UnitId}");
            if (onlyRef.Count > 0)
                bad.Add($"{name}：参考有而引擎没有 {onlyRef.Count} 条，例如 {us[onlyRef[0].Lo].UnitId} ← {us[onlyRef[0].Hi].UnitId}");
        }
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);
    }

    /// <summary>
    /// 【算例自检：工作帮算例里不许出现跨幅的边】
    ///
    /// 幅长 = max(300, 20W) ⇒ 跨幅采样点占比 ≤ 10% &lt; 20%（重叠比闸门）⇒ 只可能同幅上下级连边。
    /// 这条保证 UC2/UC3/UC4 表里的每条边都手算得清；破了的话那些表里混着"边界蹭到"的噪声，
    /// 而且噪声会随 W 增长 —— <b>看上去正好像"推得越快压覆越多"这个真结论</b>。
    /// 扫的是 UC4 用到的整条 W 区间，不是挑几个点。
    /// </summary>
    [Fact]
    public void UC1b_工作帮算例不许出现跨幅的边()
    {
        var bad = new List<string>();
        for (double w = 20; w <= 200.001; w += 10)
        {
            var us = WorkingSlope(4, 3, 15, w);
            foreach (var (lo, hi) in RefEdges(us, 1, 0, across: Across))
                if (us[lo].PanelIndex != us[hi].PanelIndex)
                    bad.Add($"W={w}：{us[lo].UnitId} ← {us[hi].UnitId} 跨幅了");
        }
        _out.WriteLine($"W = 20..200 扫完，跨幅边 {bad.Count} 条");
        foreach (var b in bad.Take(10)) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);
    }

    // ════════════════════════════════════════════════════════════════════
    //  UC2 · 分岔在哪儿
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 【真实工作帮几何下，两套判据的分岔表】
    ///
    /// <para><b>物理口径</b>：第 k 级本月挖的是 <c>[u_k, u_k+W]</c> 这一带；它正上方
    /// 第 k+1 级的地面范围也是 <c>[u_k, u_k+W]</c>，而第 k+1 级的<b>已采边界</b>在 <c>u_{k+1}=u_k+一级横距</c>。
    /// 所以：<b>W ≤ 一级横距</b> ⇒ 上面那块本月之前就已经剥掉了，<b>本月无先后关系</b>（引擎报 0 是对的）；
    /// <b>W &gt; 一级横距</b> ⇒ <b>探出量 = W − 一级横距</b> 那一段还压着，必须先剥。</para>
    ///
    /// <para><b>⚠ 探出量还要过重叠比闸门</b>：探出的那一段占 lo 占地的
    /// <c>探出量/W</c>，低于 <c>MinOverlapRatio</c>(0.20) 就连不出边。
    /// 这条不是参考实现的缺陷 —— <b>0.20 这个闸门本身就是"部分压覆全有全无"的那道坎</b>，
    /// 写第一版判据时我按"W &gt; 一级横距就该有边"下断言，被 W=60（探出 4.0m / 6.7%）当场判红。
    /// 结论保留成表里的第三种状态，而不是把闸门调小把它抹掉。</para>
    ///
    /// <para>判据只钉这三种状态之间的边界 —— 具体条数换地质会变，边界不会。</para>
    /// </summary>
    [Fact]
    public void UC2_工作帮几何下引擎与参考的分岔表()
    {
        const double gate = 0.20;                          // = UnitGraphBuilder.Options.MinOverlapRatio 缺省
        _out.WriteLine("台阶高 15m · 幅长 max(300, 20W)（保证跨幅不连边）· 4 级 × 3 幅 = 12 个单元 · 推进方向 +X");
        _out.WriteLine("");
        _out.WriteLine(" 坡角α | 一级横距 |  推进宽W | 探出m | 探出/W | 引擎边 | 参考边 | 结论");
        _out.WriteLine("-------|----------|----------|-------|--------|--------|--------|--------------------");

        var bad = new List<string>();
        foreach (var (alpha, w) in new[]
        {
            (15.0, 30.0), (15.0, 40.0), (15.0, 55.0),      // W < 一级横距(55.98) ⇒ 本月确无压覆
            (15.0, 60.0),                                  // 探出 4.0m = 6.7% ⇒ 有压覆，闸门滤掉
            (15.0, 70.0),                                  // 探出 14.0m = 20.0% ⇒ 刚好过闸
            (15.0, 90.0), (15.0, 120.0),                   // 探出充分
            (25.0, 38.0),                                  // 横距 32.2 ⇒ 探出 5.8m = 15.3%，闸门下方
            (25.0, 40.0),                                  // 横距 32.2 ⇒ 探出 7.8m = 19.5%，【贴着闸门，不判】
            (25.0, 45.0),                                  // 横距 32.2 ⇒ 探出 12.8m = 28.4%
            (10.0, 40.0),                                  // 横距 85.1 > W ⇒ 无压覆
        })
        {
            var us = WorkingSlope(4, 3, alpha, w);
            double sb = Setback(alpha);
            double over = w - sb, frac = over / w;
            int eng = EngineEdges(Build(us, 1, 0)).Count;
            int rf = RefEdges(us, 1, 0, across: Across).Count;

            bool physical = over > 1e-6;                    // 几何上真有东西压着
            bool onGate = physical && Math.Abs(frac - gate) < GateBand;
            bool passGate = physical && frac >= gate;        // 且探出的那块够得着闸门
            string verdict = !physical ? "本月确无压覆"
                           : onGate ? $"探出 {frac:P1} 贴着闸门 {gate:P0}（±{GateBand:P0}）—— 入表不判"
                           : !passGate ? $"有压覆但探出仅 {frac:P1} < 闸门 {gate:P0}，被滤掉"
                                       : "有压覆，参考抓到";
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " {0,5:0.#}° | {1,8:0.0} | {2,8:0.0} | {3,5:0.0} | {4,6:0.0%} | {5,6} | {6,6} | {7}",
                alpha, sb, w, Math.Max(0, over), Math.Max(0, frac), eng, rf, verdict));

            // ① 引擎在【任何】推进宽下都是 0 —— 这就是要证的那件事
            if (eng != 0) bad.Add($"α={alpha} W={w}：引擎报了 {eng} 条边（本台架的预期是 0）");
            // ② 参考侧必须与物理口径 + 闸门一致 —— 贴着闸门的那一档不判（见 GateBand）
            if (onGate) continue;
            if (passGate && rf == 0) bad.Add($"α={alpha} W={w}：探出 {over:0.0}m（{frac:P1}）已过闸门，参考却一条边都没有");
            if (!passGate && rf != 0) bad.Add($"α={alpha} W={w}：探出 {Math.Max(0, over):0.0}m（{Math.Max(0, frac):P1}）没过闸门，参考却连了 {rf} 条边");
        }
        _out.WriteLine("");
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);
    }

    /// <summary>
    /// 【同一份几何：<b>给了推进方向反而一条边都没有</b>】
    ///
    /// <para>不给推进方向时引擎退「双侧走廊」，注释说这是"偏保守：可能多连边，不会漏"。
    /// 在真实后退几何上这句话有一半是反的 —— 双侧走廊确实连出了边（包括真该有的那些），
    /// 但那是<b>因为丢掉了方向</b>；一旦把真方向给它（更多的信息），单侧带判定
    /// 就把「lo 在 hi 后方」全部否掉，边数掉到 <b>0</b>。</para>
    ///
    /// <para>跨幅伪边数只<b>报不判</b>：它随幅长/W 的比值变，是算例的性质不是引擎的性质
    /// （第一版逐档断言"必有跨幅伪边"，W=70 那档 0 条，判红 —— 判据把算例的偶然当成了结论）。
    /// 要判的是<b>方向</b>：给方向 = 0 条 · 不给方向 ≥ 真值。</para>
    /// </summary>
    [Fact]
    public void UC6_给了推进方向反而一条压覆边都没有()
    {
        var bad = new List<string>();
        int crossPanelTotal = 0;
        foreach (double w in new[] { 70.0, 90.0, 120.0 })
        {
            var us = WorkingSlope(4, 3, 15, w);
            var dir = EngineEdges(Build(us, 1, 0));
            var both = EngineEdges(Build(us, 0, 0));
            int crossPanel = both.Count(e => us[e.Lo].PanelIndex != us[e.Hi].PanelIndex);
            crossPanelTotal += crossPanel;
            int truth = RefEdges(us, 1, 0, across: Across).Count;
            _out.WriteLine($"W={w,-6} 参考(真值) {truth,3} 条 · 引擎·给方向 {dir.Count,3} 条 · "
                         + $"引擎·双侧走廊 {both.Count,3} 条（其中跨幅 {crossPanel} 条）");
            if (dir.Count != 0) bad.Add($"W={w}：给方向时引擎有 {dir.Count} 条（本台架预期 0）");
            // 双侧走廊的「不会漏」那一半：它至少要盖住真值的条数
            if (both.Count < truth) bad.Add($"W={w}：双侧走廊 {both.Count} 条 < 真值 {truth} 条 —— 「不会漏」不成立");
        }
        _out.WriteLine($"跨幅伪边合计 {crossPanelTotal} 条（只报不判 —— 它是算例幅长/W 的性质）");
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);
    }

    /// <summary>
    /// 【漏掉的是哪几对，以及漏掉多少必剥方量】
    ///
    /// 边数是抽象的，必剥闭包方量才是排产真正吃进去的那个数（U2：闭包是硬下界，不可削）。
    /// </summary>
    [Fact]
    public void UC3_漏掉的必剥闭包方量()
    {
        const double alpha = 15, w = 90;                   // 一级横距 55.98，W 探出 34.0m
        var us = WorkingSlope(4, 3, alpha, w);
        var eng = EngineEdges(Build(us, 1, 0));
        var rf = RefEdges(us, 1, 0, across: Across);

        _out.WriteLine($"α={alpha}° · 一级横距 {Setback(alpha):0.0}m · 推进宽 {w}m（探出 {w - Setback(alpha):0.0}m）");
        _out.WriteLine($"引擎边 {eng.Count} 条 · 参考边 {rf.Count} 条 · 漏 {rf.Except(eng).Count()} 条");
        _out.WriteLine("");
        foreach (var (lo, hi) in rf.Except(eng).OrderBy(e => us[e.Lo].UnitId, StringComparer.Ordinal)
                                                .ThenBy(e => us[e.Hi].UnitId, StringComparer.Ordinal))
            _out.WriteLine($"   漏：{us[lo].UnitId,-14} ← {us[hi].UnitId,-14}"
                         + $"（Δz={us[hi].ZLo - us[lo].ZLo:0}m）");

        _out.WriteLine("");
        _out.WriteLine("煤单元 |  引擎闭包万m³ | 参考闭包万m³ |  差");
        _out.WriteLine("-------|---------------|--------------|--------");
        double sumE = 0, sumR = 0;
        for (int i = 0; i < us.Count; i++)
        {
            if (!us[i].IsCoal) continue;
            double a = ClosureM3(us, eng, i), b = ClosureM3(us, rf, i);
            sumE += a; sumR += b;
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-7}|{1,14:0.00} |{2,13:0.00} | {3,6:0.00}", us[i].UnitId, a / 1e4, b / 1e4, (b - a) / 1e4));
        }
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "合计   |{0,14:0.00} |{1,13:0.00} | {2,6:0.00}", sumE / 1e4, sumR / 1e4, (sumR - sumE) / 1e4));

        // 方向判据：引擎认为煤"没有任何压覆、拿起来就能采"，参考认为压着整摞岩。
        Assert.Equal(0, sumE);
        Assert.True(sumR > 0, "参考侧的必剥闭包也是 0 —— 那就不是引擎的问题，是算例摆得不对");
    }

    /// <summary>
    /// 【扫描：推进宽从远小于横距扫到远大于横距】
    ///
    /// 单点结论容易是算例凑出来的。这条判 <b>单调性</b>：推得越快，压覆关系只会更多不会更少；
    /// 而引擎在整条扫描线上恒为 0 —— 说明它对这个几何量<b>完全不敏感</b>，不是"阈值调得不对"。
    /// </summary>
    [Fact]
    public void UC4_推进宽扫描下参考单调而引擎恒零()
    {
        const double alpha = 15;
        double sb = Setback(alpha);
        _out.WriteLine($"α={alpha}° · 一级横距 {sb:0.00}m · 4 级 × 3 幅");
        _out.WriteLine("");
        _out.WriteLine("  W(m) | W/横距 | 引擎边 | 参考边");
        _out.WriteLine("-------|--------|--------|--------");

        var bad = new List<string>();
        int prevRef = -1;
        for (double w = 20; w <= 200.001; w += 10)
        {
            var us = WorkingSlope(4, 3, alpha, w);
            int eng = EngineEdges(Build(us, 1, 0)).Count;
            int rf = RefEdges(us, 1, 0, across: Across).Count;
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,6:0.#} | {1,6:0.00} | {2,6} | {3,6}", w, w / sb, eng, rf));
            if (eng != 0) bad.Add($"W={w}：引擎报了 {eng} 条");
            if (prevRef >= 0 && rf < prevRef) bad.Add($"W={w}：参考边数 {rf} < 上一档 {prevRef}，推得更快反而压覆更少");
            prevRef = rf;
        }
        _out.WriteLine("");
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);
        Assert.True(prevRef > 0, "整条扫描线参考侧都是 0 —— 算例没摆出压覆，表不成立");
    }

    /// <summary>
    /// 【反向闸门：把工作帮压回零后退，两套判据必须重新合上】
    ///
    /// UC2/UC3/UC4 都在说"后退量一上来就分岔"。这条反过来判：<b>后退量是不是唯一的原因</b>。
    /// 把同一份算例的前脸轨全推到 u=0（其余一切不变），两套判据必须逐边相同 ——
    /// 否则分岔里还混着别的东西（幅长、包围盒、采样步长…），上面那三张表的归因就不成立。
    /// </summary>
    [Fact]
    public void UC5_把后退量压成零两套判据必须重新合上()
    {
        var bad = new List<string>();
        foreach (double w in new[] { 40.0, 90.0, 120.0 })
        {
            var us = WorkingSlope(4, 3, 15, w);
            foreach (var u in us)                                  // 只动前脸轨与包围盒，标高/量/幅号全不动
            {
                double dx = u.RailXy[0];
                for (int i = 0; i < u.RailXy.Length; i += 2) u.RailXy[i] -= dx;
                u.Cx -= dx; u.MinX -= dx; u.MaxX -= dx;
            }
            var eng = EngineEdges(Build(us, 1, 0));
            var rf = RefEdges(us, 1, 0, across: Across);
            _out.WriteLine($"零后退 W={w,-5} 引擎 {eng.Count,3} 条 · 参考 {rf.Count,3} 条");
            if (!eng.SetEquals(rf))
                bad.Add($"W={w}：零后退下两套仍不同（引擎独有 {eng.Except(rf).Count()} · 参考独有 {rf.Except(eng).Count()}）");
            if (eng.Count == 0) bad.Add($"W={w}：零后退下引擎也是 0 条 —— 那本组的对照失去意义");
        }
        foreach (var b in bad) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);
    }
}
