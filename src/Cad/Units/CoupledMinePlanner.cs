// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/CoupledMinePlanner.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

public sealed class CoupledPlanInput
{
    public MonthlyScheduleInput Schedule = null!;
    public DumpAllocationInput  Dump = null!;

    /// <summary>各排土位置在<b>推进轴 u</b> 上的坐标（与 <c>Dump.Slots</c> 同序，与 <see cref="RockProfile"/> 同轴）。</summary>
    public double[] SlotU = Array.Empty<double>();
    /// <summary>各排土位置到推进轴的<b>横向偏距</b>（km）—— 运距的固定分量。</summary>
    public double[] SlotOffsetKm = Array.Empty<double>();
    /// <summary>路网迂回系数：直线距离 → 实际运距。缺路网时的兜底口径，接 RoadLib 后由真路网替代。</summary>
    public double RoadTortuosity = 1.3;

    /// <summary>
    /// 等效运距模型（含坡度折算）。null = 用 <see cref="RoadTortuosity"/> 建一个默认的。
    /// <para><b>高差不能不算</b>：内排通常是下排、外排常要爬升 —— 只算平距会让
    /// 「内排优先」和「运输功最小」在该分开的地方分不开。见 <see cref="HaulModel"/>。</para>
    /// </summary>
    public HaulModel? Haul;

    /// <summary>
    /// <b>真路网运距</b>（可选）。给了就用它替代
    /// 「<c>|u_t − u_slot|/1000 + 横向偏距) × 迂回系数</c>」那套几何兜底。
    ///
    /// <para>签名 <c>(月, 排土位置, 本月源在推进轴上的位置 u, 本月源的加权平均标高 z) → 等效公里</c>；
    /// <b>返回 null = 这一对 O-D 路网上解不出来</b>（源或汇落不到节点、不可达），
    /// 那一笔<b>自动退回几何兜底</b>并计入 <see cref="CoupledPlanResult.HaulNote"/> 的命中率。</para>
    ///
    /// <para><b>为什么不让本模块自己去查路网</b>：`MineAssLib` 不引用 `RoadLib`，也不该引用 ——
    /// 采剥接续是几何/量的事，路网是运输的事。把它做成回调，谁有路网谁注入
    /// （现在是 `PlanLib.ShortTerm.RoadHaulProvider`），本模块保持零依赖、照样可脱 GUI 测。</para>
    /// </summary>
    public Func<int, DumpSlot, double, double, double?>? RoadHaul;

    /// <summary>内排位置要落在采煤前界<b>后方</b>至少这么远才算"采空区已形成"（m）。</summary>
    public double InternalClearanceM = 200;
    /// <summary>采空区可回填比例：内排累计占容 ≤ 已形成采空区 × 此系数。</summary>
    public double VoidFillFactor = 0.9;
    /// <summary>煤的原位密度（把万t 采出量折回采空区体积用）。</summary>
    public double CoalDensity = 1.35;

    public int    MaxIterations = 5;
    /// <summary>
    /// 收敛判据：内排率/运输功/总剥离三项的相对变化都 &lt; 此值（%）即算收敛。
    /// <para><b>刻意无入口</b>：这是<b>求解器的内部收敛阈</b>，不是计划口径。放到界面上，
    /// 同一份输入会因为"这次容差放松了"而给出不同的方案，而两次都自洽 ——
    /// 何况未收敛时引擎本来就会明说"末轮仍有 X% 摆动"，不拿末轮冒充答案。</para>
    /// </summary>
    public double ConvergeTolPct = 1.0;
}

/// <summary>一轮迭代的关键数。</summary>
public sealed class CoupledIteration
{
    public int    Round;
    public bool   ScheduleOk;
    public string Note = "";
    public double TotalRockM3;
    public double InternalRatePct;
    public double TransportWorkTKm;
    public double UnplacedM3;
    public int    InternalSlotsEnabled;
    /// <summary>与上一轮相比三项指标的最大相对变化（%）。首轮为 100。</summary>
    public double MaxDeltaPct = 100;

    public override string ToString()
        => $"第{Round}轮: {(ScheduleOk ? "" : "排产失败 —— ")}"
         + $"岩 {TotalRockM3 / 1e4:0.0}万m³ · 内排率 {InternalRatePct:0.0}% · 运输功 {TransportWorkTKm / 1e4:0.0}万t·km"
         + $" · 内排位置启用 {InternalSlotsEnabled} 个"
         + (UnplacedM3 > 1e-6 ? $" · ◆排不下 {UnplacedM3 / 1e4:0.0}万m³" : "")
         + $" · 变化 {MaxDeltaPct:0.00}%" + (string.IsNullOrEmpty(Note) ? "" : $" · {Note}");
}

public sealed class CoupledPlanResult
{
    public bool   Success;
    public string Error = "";
    public MonthlyScheduleResult? Schedule;
    public DumpAllocationResult?  Dump;
    public readonly List<CoupledIteration> Iterations = new();
    public bool   Converged;
    public string ConvergenceNote = "";
    /// <summary>运距是怎么算出来的（口径要跟着结果走，别让人以为是真路网）。</summary>
    public string HaulNote = "";
}

/// <summary>
/// 采—排—运的<b>外循环</b>。三者互相咬着：
///
/// <code>
///   月末几何 ──► 内排启用时机 + 采空区容量 ──► 剥离上包络 ──► 剥离量
///       ▲                                                       │
///       │                                          逐月 O-D 运距 ──► 配对
///       └───────────────────────────────────────────────────────┘
/// </code>
///
/// <para><b>为什么会收敛</b>：这条环是<b>单调上升</b>的 —— 剥得多 → 采空区大 → 内排容量大 →
/// 上包络松 → 能剥得更多。单调增且有上界（外排能力 / 最终境界 / R34 天花板），
/// 从"全外排"这个保守起点出发必收敛。</para>
///
/// <para><b>为什么不会转很多圈</b>：煤前界由煤量目标<b>唯一确定</b>（第1步零自由度），
/// 所以内排位置的启用时机与逐月运距在第 1 轮之后就不再变；剩下的只有"采空区容量"这一支在动。
/// 实测 2~3 轮到位。</para>
///
/// <para><b>不保证单调的那一支要如实说</b>：逐月运距变了会让"运输功最小"重挑去向，
/// 那一支没有单调性保证。所以本类<b>不声称一定收敛</b> —— 到轮次上限还在动就明说
/// "未收敛，末轮仍有 X% 摆动"，不静默拿最后一轮冒充答案。</para>
/// </summary>
public static class CoupledMinePlanner
{
    public static CoupledPlanResult Solve(CoupledPlanInput inp)
    {
        var r = new CoupledPlanResult();
        if (inp?.Schedule?.Rock == null) { r.Error = "没有岩量剖面"; return r; }
        if (inp.Dump?.Slots == null || inp.Dump.Slots.Count == 0) { r.Error = "没有排土位置"; return r; }
        int T = inp.Schedule.MonthCount;
        if (T <= 0) { r.Error = "没有月度煤量目标"; return r; }

        var slots = inp.Dump.Slots;
        int S = slots.Count;
        bool hasU = inp.SlotU != null && inp.SlotU.Length == S;
        bool hasOff = inp.SlotOffsetKm != null && inp.SlotOffsetKm.Length == S;

        // 各物料的体积加权 Kr（把"占容"换回"实方"用）。逐笔精确换算在配对里做，
        // 这里只为把库容折成剥离上包络 —— 是个上界估计，标出来。
        double krAvg = 1.15;
        if (inp.Dump.Materials is { Length: > 0 })
        {
            var ks = inp.Dump.Materials.Where(m => m != null && m.Kr > 0).Select(m => m.Kr).ToList();
            if (ks.Count > 0) krAvg = ks.Average();
        }

        // 记住每个内排位置原本的启用月（用户填的），迭代只会把它【往后推】不会提前
        var userFrom = slots.Select(s => s.AvailableFromMonth).ToArray();

        int roadHit = 0, roadMiss = 0;               // 真路网命中/落空（末轮的数才算数）
        int offAxis = 0;                             // 投不到推进轴上、只能用静态运距的笔数
        MonthlyScheduleResult? sched = null;
        MonthlyScheduleResult? lastGood = null;      // 采空区容量那一支只能拿【解成功】的轮次反馈
        double[]? front = null;                       // 煤前界：第1步产物，失败的轮次照样有
        DumpAllocationResult? dump = null;
        double prevRock = 0, prevRate = 0, prevWork = 0;

        for (int round = 0; round <= inp.MaxIterations; round++)
        {
            // ── 内排启用时机：第 0 轮全关（保守起点）；之后由上一轮的煤前界反推 ──
            // 【用 CoalFrontU 而不是 Months[].CoalFrontU】：第 0 轮全外排排不下是常态，
            // 那一轮 Months 是空的。拿"整体成功"当门槛会让内排永远开不了 —— 循环死锁。踩过一次。
            int enabled = 0;
            for (int i = 0; i < S; i++)
            {
                var s = slots[i];
                if (!s.IsInternal) { s.AvailableFromMonth = Math.Max(1, userFrom[i]); continue; }
                if (round == 0 || front == null || !hasU) { s.AvailableFromMonth = int.MaxValue; continue; }
                int from = int.MaxValue;
                for (int t = 1; t <= T && t < front.Length; t++)
                    if (front[t] - inp.SlotU![i] >= inp.InternalClearanceM)
                    { from = t; break; }                            // 前界推过它 + 退距 = 采空区已形成
                s.AvailableFromMonth = Math.Max(from, userFrom[i]);  // 用户填的启用期是硬下限，只能更晚
                if (s.AvailableFromMonth <= T) enabled++;
            }

            // ── 排土容量 → 剥离的累计上包络（含采空区回填上限）──
            var extraCum = BuildDumpCeiling(inp, slots, T, krAvg, lastGood);

            var si = CloneSchedule(inp.Schedule);
            si.ExtraCumCapM3 = extraCum;
            sched = MonthlyMineScheduler.Solve(si);

            if (sched.CoalFrontU is { Length: > 0 }) front = sched.CoalFrontU;   // 失败的轮次也拿得到

            var it = new CoupledIteration { Round = round, ScheduleOk = sched.Success, InternalSlotsEnabled = enabled };
            if (!sched.Success)
            {
                it.Note = sched.Error;
                r.Iterations.Add(it);
                // 排产失败但煤前界已经有了 ⇒ 下一轮能把内排开出来再试。
                // 只有【内排已经全开还是不行】才是真的没救。
                if (enabled == 0 && front != null) continue;
                r.Error = "外循环中排产失败 —— " + sched.Error;
                return r;
            }

            lastGood = sched;

            // ── 逐月运距 + 采空区回填上限 → 配对 ──
            var di = CloneDump(inp.Dump, slots);
            // 内排的累计占容上限 = 已形成采空区 × 回填比。只卡剥离总量是不够的：
            // 那只管"能剥多少"，不管"剥出来往哪儿放"。
            var intCum = new double[T + 1];
            for (int t = 1; t <= T; t++)
            {
                var mm = sched.Months[t - 1];
                double voidM3 = mm.RockCumM3 + mm.CoalCumWt * 1e4 / Math.Max(0.1, inp.CoalDensity);
                intCum[t] = voidM3 * Math.Max(0, inp.VoidFillFactor);
            }
            di.InternalCumCapM3 = intCum;
            if (hasU)
            {
                var frontByMonth = sched.Months.ToDictionary(m => m.Month, m => m.CoalFrontU);
                var srcZByMonth = sched.Months.ToDictionary(m => m.Month, m => m.MeanSourceZ);
                var uOf = new Dictionary<DumpSlot, double>();
                var offOf = new Dictionary<DumpSlot, double>();
                for (int i = 0; i < S; i++) { uOf[slots[i]] = inp.SlotU![i]; offOf[slots[i]] = hasOff ? inp.SlotOffsetKm![i] : 0; }
                var haul = inp.Haul ?? new HaulModel { Tortuosity = Math.Max(1.0, inp.RoadTortuosity) };
                // 路网命中率要数出来 —— "接了真路网"和"接了但一笔都没命中"是两回事，
                // 后者的数与纯兜底一模一样，不数就分不出来。
                roadHit = 0; roadMiss = 0; offAxis = 0;
                di.HaulProvider = (month, slot) =>
                {
                    double u = frontByMonth.TryGetValue(month, out double f) ? f : 0;
                    double su = uOf.TryGetValue(slot, out double v) ? v : 0;
                    double off = offOf.TryGetValue(slot, out double o) ? o : 0;
                    // 高差：从本月剥离量的加权平均源标高，抬/降到该排土位置的质心标高
                    double srcZ = srcZByMonth.TryGetValue(month, out double z) ? z : inp.Schedule.ZDatum;

                    if (inp.RoadHaul != null)
                    {
                        double? km = null;
                        // 路网侧的异常不许把整条排产带走 —— 解不出来就退回几何兜底。
                        try { km = inp.RoadHaul(month, slot, u, srcZ); } catch { km = null; }
                        if (km is > 0 and < 1e6) { roadHit++; return km.Value; }
                        roadMiss++;
                    }
                    // ⚠ `SlotU` 里的 −∞ 表示"投不到推进轴上"（`DumpSlotAdapter` 的口径：
                    //    那意味着它一直在采场后方，采空区一形成就可用）。那个约定**只对"启用时机"成立**，
                    //    直接拿它算距离会得到 |u−(−∞)| = ∞ ⇒ 运输功 ∞：
                    //      · 「运输功最小」在一堆 ∞ 里挑不出东西，退化成任意选；
                    //      · 派生比选的运输功维度归一化被 ∞ 毁掉；
                    //      · **`ToJson` 直接抛异常** —— 唯一的持久化路径当场断掉。
                    //    外排土场本来就在采场侧后方、投不上是常态，所以这条路径是常走的。
                    double planKm;
                    if (double.IsFinite(su))
                        planKm = Math.Abs(u - su) / 1000.0 + off;        // 坑越推越远，平距逐月在变
                    else
                    {
                        // 投不上就用它自带的静态运距；没有就退到横向偏距。**都不是 ∞，而且要报出来。**
                        planKm = slot.HaulKm > 1e-9 ? slot.HaulKm : off;
                        offAxis++;
                    }
                    double eq = haul.EquivalentKm(planKm, slot.Cz - srcZ);
                    // 兜死：当量模型里再出非有限值也不许流到下游（宁可用平距，也不给 ∞）
                    return double.IsFinite(eq) && eq >= 0 ? eq : Math.Max(0, planKm);
                };
                r.HaulNote = haul.Text();
            }
            dump = DumpAllocator.Allocate(sched, di);
            if (!dump.Success) { r.Error = "外循环中采排配对失败 —— " + dump.Error; return r; }

            // 投不到推进轴上的那些笔用了静态运距 —— 它们的"逐月运距在变"是假的，必须说。
            if (offAxis > 0)
                r.HaulNote = $"其中 {offAxis} 笔的排土位置**投不到推进轴上**（外排土场常态），"
                           + "用它自带的静态运距，不随推进变；" + r.HaulNote;

            // 运距口径要跟着结果走。**接了真路网但一笔都没命中**，数与纯兜底一模一样 ——
            // 不把命中率写出来，没人分得出这两种情况。
            if (inp.RoadHaul != null)
            {
                int tot = roadHit + roadMiss;
                r.HaulNote = tot == 0
                    ? "真路网已接入但本轮一次都没被问到（没有逐月运距需求）；" + r.HaulNote
                    : roadHit == 0
                    ? $"⚠ 真路网已接入但 {tot} 笔 O-D **一笔也没解出来**（源/汇落不到路网节点或不可达）"
                      + $" —— 全部退回几何兜底：{r.HaulNote}"
                    : $"真路网命中 {roadHit}/{tot}（{100.0 * roadHit / tot:0.#}%）"
                      + (roadMiss > 0 ? $"，其余 {roadMiss} 笔退回几何兜底：{r.HaulNote}" : "");
            }

            it.TotalRockM3 = sched.TotalRockM3;
            it.InternalRatePct = dump.OverallInternalRatePct;
            it.TransportWorkTKm = dump.TotalTransportWorkTKm;
            it.UnplacedM3 = dump.TotalUnplacedM3;
            if (round > 0)
                it.MaxDeltaPct = Math.Max(Rel(prevRock, it.TotalRockM3),
                                 Math.Max(Rel(prevWork, it.TransportWorkTKm), Math.Abs(it.InternalRatePct - prevRate)));
            r.Iterations.Add(it);

            bool converged = round > 0 && it.MaxDeltaPct <= inp.ConvergeTolPct;
            prevRock = it.TotalRockM3; prevRate = it.InternalRatePct; prevWork = it.TransportWorkTKm;
            r.Schedule = sched; r.Dump = dump;

            if (converged)
            {
                r.Converged = true;
                r.ConvergenceNote = $"第{round}轮收敛（末轮变化 {it.MaxDeltaPct:0.00}% ≤ 容差 {inp.ConvergeTolPct:0.#}%）";
                r.Success = true;
                return r;
            }
        }

        // 到轮次上限还在动：如实说，不拿最后一轮冒充答案
        double last = r.Iterations.Count > 0 ? r.Iterations[^1].MaxDeltaPct : 100;
        r.Converged = false;
        r.ConvergenceNote = $"⚠ 跑满 {inp.MaxIterations} 轮仍未收敛：末轮仍有 {last:0.00}% 摆动"
                          + "（多半是逐月运距那一支在来回挑去向 —— 它没有单调性保证）。"
                          + "结果取末轮，用前请先看逐轮表。";
        r.Success = r.Schedule != null && r.Dump != null;
        if (!r.Success) r.Error = "外循环没跑出任何可用结果";
        return r;
    }

    /// <summary>
    /// 排土容量 → 剥离的<b>累计</b>上包络（m³ 实方）。
    /// 内排那部分还要再受<b>采空区已形成体积</b>的约束（剥出来的坑还没挖出来，回填无从谈起）。
    /// </summary>
    private static double[] BuildDumpCeiling(CoupledPlanInput inp, List<DumpSlot> slots, int T,
                                             double krAvg, MonthlyScheduleResult? prev)
    {
        var cum = new double[T + 1];
        for (int t = 0; t <= T; t++)
        {
            double ext = 0, intn = 0;
            foreach (var s in slots)
            {
                if (s.AvailableFromMonth > t) continue;
                if (s.IsInternal) intn += s.CapacityM3; else ext += s.CapacityM3;
            }
            // 采空区：到 t 月为止已经挖走的体积（煤 + 岩，都折成原位实方）
            if (prev != null && t >= 1 && t <= prev.Months.Count)
            {
                var m = prev.Months[t - 1];
                double voidM3 = m.RockCumM3 + m.CoalCumWt * 1e4 / Math.Max(0.1, inp.CoalDensity);
                intn = Math.Min(intn, voidM3 * Math.Max(0, inp.VoidFillFactor));
            }
            else if (t == 0) intn = 0;
            cum[t] = (ext + intn) / Math.Max(1e-6, krAvg);      // 占容 → 实方
        }
        for (int t = 1; t <= T; t++) if (cum[t] < cum[t - 1]) cum[t] = cum[t - 1];
        return cum;
    }

    private static double Rel(double a, double b)
        => Math.Abs(a) < 1e-9 ? (Math.Abs(b) < 1e-9 ? 0 : 100) : Math.Abs(b - a) / Math.Abs(a) * 100;

    /// <summary>
    /// 复制一份排产输入（外循环每轮要改 <see cref="MonthlyScheduleInput.ExtraCumCapM3"/>，
    /// 不能改到调用方那份上）。
    ///
    /// <para><b>⚠ 加字段就要同步加到这里</b>。这种手写 clone 漏字段是**静默**的：
    /// 漏掉的字段变成缺省值，排产照样跑通、照样出计划，只是<b>按另一套输入算的</b>。
    /// 实测漏过 <see cref="MonthlyScheduleInput.CoalTargetBySeam"/> ——
    /// 用户填的逐层煤量在外循环里被丢掉，引擎改按统一前界自己摊，
    /// 而且<b>如实报告"这是引擎摊的"</b>：R36 那套诚实机制照常工作，
    /// 却<b>正好把丢字段这件事掩盖成了一个合理的说明</b>。判据 G18j 用反射兜住这一类。</para>
    /// </summary>
    private static MonthlyScheduleInput CloneSchedule(MonthlyScheduleInput s) => new()
    {
        Rock = s.Rock, AlphaDeg = s.AlphaDeg, ZDatum = s.ZDatum,
        CoalTargetWt = (double[])s.CoalTargetWt.Clone(),
        // 逐层目标是**用户的分配决策**（R36）。丢了它 = 引擎替用户重新分了一遍。
        CoalTargetBySeam = s.CoalTargetBySeam?.Select(a => (double[])(a ?? Array.Empty<double>()).Clone())
                                              .ToArray() ?? Array.Empty<double[]>(),
        StripCapM3 = (double[])(s.StripCapM3 ?? Array.Empty<double>()).Clone(),
        // 外循环随后会改写它；照抄一份不影响结果，却让判据不必开"例外名单" ——
        // 例外名单一开，下一个真漏掉的字段就有地方藏。
        ExtraCumCapM3 = (double[])(s.ExtraCumCapM3 ?? Array.Empty<double>()).Clone(),
        LookaheadMonths = s.LookaheadMonths, RecoveryTotalWt = s.RecoveryTotalWt,
        RatioCeiling = s.RatioCeiling, CoalStartWt = s.CoalStartWt,
        StartInSteadyState = s.StartInSteadyState,
        InitialBenchX = (double[])(s.InitialBenchX ?? Array.Empty<double>()).Clone(),
        Pace = s.Pace,
    };

    /// <summary>判据入口：手写 clone 漏字段是静默的，只能靠反射逐字段比（见 G18j）。</summary>
    public static MonthlyScheduleInput CloneScheduleForTest(MonthlyScheduleInput s) => CloneSchedule(s);

    /// <summary>
    /// 复制配对输入（外循环每轮换一批 slot 的启用期，不能改到调用方那份上）。
    ///
    /// <para><b>⚠ 加字段就要同步加到这里</b>（判据 G18j）。曾漏过 <c>HaulProvider</c>：
    /// 下面那句 <c>di.HaulProvider = …</c> 在 <c>if (hasU)</c> <b>里面</b> ——
    /// 没有工作线时它根本不执行，于是调用方自己带的逐月运距函数被<b>静默丢掉</b>，
    /// 配对退回 <see cref="DumpSlot.HaulKm"/> 那个静态值。
    /// 而"没有工作线"在界面上曾经是<b>常态</b>（见判据 S21）。</para>
    ///
    /// <para><c>Materials</c> 是只读物料目录、<c>Slots</c> 是调用方新造的一批，
    /// 都<b>刻意共享</b>，不做深拷。</para>
    /// </summary>
    private static DumpAllocationInput CloneDump(DumpAllocationInput d, List<DumpSlot> slots) => new()
    {
        Slots = slots, Materials = d.Materials, Strategy = d.Strategy,
        InternalCumCapM3 = d.InternalCumCapM3,
        HaulProvider = d.HaulProvider,      // hasU 时下面会覆写；不抄的话 !hasU 就丢了
    };

    /// <summary>判据入口（G18j）。</summary>
    public static DumpAllocationInput CloneDumpForTest(DumpAllocationInput d, List<DumpSlot> slots)
        => CloneDump(d, slots);

    /// <summary>逐轮表（命令行 / 报表直接打）。</summary>
    public static string IterationReport(CoupledPlanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("外循环（采空区容量 ⇄ 推进 ⇄ 逐月运距）：");
        sb.AppendLine("  运距口径：" + (r.HaulNote.Length > 0 ? r.HaulNote : "静态（未接几何）"));
        foreach (var it in r.Iterations) sb.AppendLine("  " + it);
        sb.AppendLine("  " + (r.Converged ? "✓ " : "") + r.ConvergenceNote);
        return sb.ToString();
    }
}
