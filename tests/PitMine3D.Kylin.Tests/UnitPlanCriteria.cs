// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanCriteria.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <b>UnitPlanEngine 的判据本体</b> —— 与测试用例分开写，因为同一条判据要在三个地方用：
/// ① 正常算例上判"没错"；② 扫描/穷举里逐点判；③ <b>变异测试</b>里判"它真的会红"。
///
/// <para><b>每条判据都返回"哪里不对"的清单，不返回 bool</b>：bool 只能告诉你不过，
/// 说不出不过在哪 —— 而排产结果有上百行，说不出位置的判据等于没有。</para>
///
/// <para><b>判据是独立的一份账</b>，不调用 <see cref="UnitPlanResult.Validate"/>：
/// 那是引擎自己写的自检，拿它当判据等于让被告当法官。两份都跑，对不上再说谁错。</para>
///
/// <para><b>⚠ 与引擎同源的东西只有一处</b>：<see cref="SlotCode"/> 镜像了引擎里 private 的
/// 位置码编码。它漂了判据必须<b>报错而不是静默跳过</b> —— 见
/// <see cref="DumpCapacityRespected"/> 里"去向码对不上任何排土位置"那一条。</para>
/// </summary>
public static class UnitPlanCriteria
{
    /// <summary>引擎里 <c>SlotCode</c> 的镜像。<b>唯一一处与实现同源的编码</b>。</summary>
    internal static string SlotCode(DumpSlot s) => $"{s.DumpName}-L{s.Level}-{s.Order}";

    private static double Rel(double a, double b)
    {
        double m = Math.Max(Math.Abs(a), Math.Abs(b));
        return m < 1e-9 ? 0 : Math.Abs(a - b) / m;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // ════════════════════════════════════════════════════════════════════
    //  对账 ① —— 入选单元量合计 vs 月目标
    //  【比相对差，不比绝对值，更不比取整后的显示值】取整是显示，不是账。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 煤量对账：达成量必须<b>落在容差带内</b>，落不进就<b>必须有一个数说明差在哪</b>
    /// （<see cref="UnitPlanResult.ShortfallT"/>）。
    ///
    /// <para><b>这一条为什么不能只判"落在带内"</b>：目标给到超出可采总量时，
    /// 引擎本来就采不满 —— 那不是错，错的是<b>采不满却不报</b>。
    /// 只判"落在带内"会把"采不满"和"报不出来"混成一件事；
    /// 只判"有缺口"又会放过"缺口是编出来的"。两边都要判。</para>
    /// </summary>
    public static List<string> CoalVsTarget(UnitPlanResult r, UnitPlanInput inp)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        double t = inp.CoalTargetT;
        double loPct = Math.Max(0, inp.CoalTolLoPct) / 100.0;
        double hiPct = Math.Max(0, inp.CoalTolHiPct) / 100.0;

        if (t <= 1e-9)
        {
            if (r.CoalT > 1e-6)
                bad.Add($"目标煤量是 0，却采出了 {r.CoalT:0.##}t —— 没要的东西不许自己长出来");
            return bad;
        }

        double rel = (r.CoalT - t) / t;                    // ★ 相对差
        bool declared = r.ShortfallT > 1e-6;

        if (declared)
        {
            // 认了欠产：欠的量必须把账补平（达成 + 缺口 = 目标），且确实低于下容差
            if (Rel(r.CoalT + r.ShortfallT, t) > 1e-6)
                bad.Add($"认了欠产但账补不平：达成 {r.CoalT:0.##}t + 缺口 {r.ShortfallT:0.##}t "
                      + $"≠ 目标 {t:0.##}t（相对差 {Rel(r.CoalT + r.ShortfallT, t) * 100:0.####}%）");
            if (rel >= -loPct - 1e-9)
                bad.Add($"报了 {r.ShortfallT:0.##}t 的缺口，煤量却已经落在容差带内（{rel * 100:+0.###;-0.###;0}%）"
                      + " —— 这个缺口是编出来的");
        }
        else if (rel < -loPct - 1e-9)
        {
            bad.Add($"煤量欠 {(-rel) * 100:0.###}%（达成 {r.CoalT:0.##}t / 目标 {t:0.##}t，"
                  + $"下容差 {inp.CoalTolLoPct:0.##}%），却一个缺口都没报 —— "
                  + "少的那部分煤去哪儿了说不出来。ShortfallT 必须落到结果上。");
        }
        else if (rel > hiPct + 1e-9)
        {
            bad.Add($"煤量超 {rel * 100:0.###}%（达成 {r.CoalT:0.##}t / 目标 {t:0.##}t，"
                  + $"上容差 {inp.CoalTolHiPct:0.##}%）—— 超采没有兜底口径，只能是选煤逻辑越界");
        }
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  对账 ② —— 去向占容合计 ≤ 位置库容
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 逐位置 + 总量两级对账。<b>逐位置那一级不能省</b>：总量不超、单个位置超，
    /// 图上就是某一条带堆到别人头上去了，而总账还是"✓"。
    /// </summary>
    public static List<string> DumpCapacityRespected(UnitPlanResult r, IReadOnlyList<DumpSlot> slots)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        var cap = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in slots ?? Array.Empty<DumpSlot>())
        {
            if (s == null) continue;
            string c = SlotCode(s);
            if (cap.ContainsKey(c))
            { bad.Add($"排土位置码撞号：{c} —— 判据没法按位置对账，先修输入或编码"); continue; }
            cap[c] = Math.Max(0, s.CapacityM3);
        }

        var used = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var f in r.Rock.SelectMany(a => a.Flows))
        {
            if (f.IsCoalSink) continue;
            if (!cap.ContainsKey(f.DestinationCode))
            {
                // ⚠ 不许静默跳过：跳过的话编码一漂，这条判据就永远绿
                bad.Add($"去向码「{f.DestinationCode}」对不上任何排土位置 —— "
                      + "引擎的位置码编码与判据这一份漂了（两处必须同源）");
                continue;
            }
            used[f.DestinationCode] = used.GetValueOrDefault(f.DestinationCode) + f.DumpM3;
        }

        foreach (var kv in used)
            if (kv.Value > cap[kv.Key] + 1e-6)
                bad.Add($"{kv.Key} 占容超库容：排了 {kv.Value:0.##}m³占容 > 库容 {cap[kv.Key]:0.##}m³"
                      + $"（超 {(kv.Value - cap[kv.Key]) / Math.Max(1e-9, cap[kv.Key]) * 100:0.##}%）");

        double tot = used.Values.Sum(), totCap = cap.Values.Sum();
        if (tot > totCap + 1e-6)
            bad.Add($"总占容 {tot:0.##}m³ 超总库容 {totCap:0.##}m³");
        return bad;
    }

    /// <summary>
    /// 排土<b>自下而上</b>（硬约束）：同一去向，只要往 Level L 排过一方，
    /// 所有 Level &lt; L 且本月已启用的位置都<b>必须填满</b>。
    /// <para>这条与 <see cref="DumpCapacityRespected"/> 是两件事：库容不超只管"别堆爆"，
    /// 自下而上管"堆的次序对不对"。极性翻错时库容那条全绿、这条才会红。</para>
    /// </summary>
    public static List<string> DumpBottomUp(UnitPlanResult r, IReadOnlyList<DumpSlot> slots, int month)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        var used = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var f in r.Rock.SelectMany(a => a.Flows))
        {
            if (f.IsCoalSink) continue;
            used[f.DestinationCode] = used.GetValueOrDefault(f.DestinationCode) + f.DumpM3;
        }

        foreach (var grp in (slots ?? Array.Empty<DumpSlot>()).Where(s => s != null)
                                                              .GroupBy(s => s.DumpName, StringComparer.Ordinal))
        {
            int maxUsed = int.MinValue;
            foreach (var s in grp)
                if (used.GetValueOrDefault(SlotCode(s)) > 1e-6) maxUsed = Math.Max(maxUsed, s.Level);
            if (maxUsed == int.MinValue) continue;            // 这个去向本月一方都没排

            foreach (var s in grp)
            {
                if (s.Level >= maxUsed) continue;
                if (s.AvailableFromMonth > month) continue;   // 本月还没启用的不算欠填
                double u = used.GetValueOrDefault(SlotCode(s));
                double c = Math.Max(0, s.CapacityM3);
                if (u < c - 1e-6)
                    bad.Add($"{grp.Key}：{SlotCode(s)}（Level {s.Level}）还空着 {c - u:0.##}m³，"
                          + $"却已经往 Level {maxUsed} 排了 —— 排土台阶必须自下而上承接");
            }
        }
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  对账 ③ —— 每个入选单元的前驱在本月或更早（拓扑自洽）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 两件事一起判：
    /// <list type="number">
    /// <item><b>累计完成度</b>：前驱本月末的完成度 ≥ 本单元本月末的完成度。
    /// （U10：煤幅只采 70%，压在它头上的岩剥到 70% 就够 —— 但不能少于 70%。）</item>
    /// <item><b>月内推进序</b>：前驱如果也在本月的计划里，它的 <c>Seq</c> 必须更小。</item>
    /// </list>
    /// <para>只判第 2 条会空过：前驱压根没进计划时无序可判，而那正是最危险的一种错
    /// —— 岩没剥、煤照采。</para>
    /// </summary>
    public static List<string> TopologySound(UnitPlanResult r, UnitGraph g)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        var seq = new Dictionary<string, int>(StringComparer.Ordinal);
        var after = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var a in r.Assignments) { seq[a.UnitId] = a.Seq; after[a.UnitId] = a.DoneAfter; }

        foreach (var a in r.Assignments)
        {
            int i = g.IndexOf(a.UnitId);
            if (i < 0)
            { bad.Add($"{a.UnitId} 不在压覆图里 —— 结果里出现了输入没有的单元"); continue; }

            foreach (int p in g.Pred[i])
            {
                var pu = g.Units[p];
                double pDone = after.TryGetValue(pu.UnitId, out double d) ? d : Clamp01(pu.DoneFraction);
                if (pDone < a.DoneAfter - 1e-6)
                    bad.Add($"{a.UnitId} 本月末要到 {a.DoneAfter * 100:0.#}%，"
                          + $"压在它头上的 {pu.UnitId} 只到 {pDone * 100:0.#}% —— 前驱没做完就采了下面");
                if (seq.TryGetValue(pu.UnitId, out int ps) && ps >= a.Seq)
                    bad.Add($"{a.UnitId}（序 {a.Seq}）排在前驱 {pu.UnitId}（序 {ps}）之前 —— 月内推进序违反压覆先后");
            }
        }
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  量的口径 —— 独立于 UnitPlanResult.Validate 的一份账
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 逐单元 → 汇总、逐流 → 单元、系数换算、序列完备、范围、非工程量的数。
    /// <para><b>煤的流向账有一个明写的豁免</b>：没给出矿位置时引擎<b>不建流</b>
    /// （明说"煤流不计运输功"），这时候不判煤的流向合计 —— 但<b>必须留了条</b>，见
    /// <see cref="NotSilent"/>。</para>
    /// </summary>
    public static List<string> QuantitiesSound(UnitPlanResult r, UnitPlanInput inp)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        bool noSink = inp.CoalSinks == null || inp.CoalSinks.Count == 0;

        // 汇总口径：煤对吨、岩对实方
        double coal = r.Coal.Sum(a => a.TonnageT);
        if (Rel(coal, r.CoalT) > 1e-9)
            bad.Add($"煤量对不上：逐单元 {coal:0.####}t vs 汇总 {r.CoalT:0.####}t");
        double rock = r.Rock.Sum(a => a.InSituM3);
        if (Rel(rock, r.StripM3) > 1e-9)
            bad.Add($"剥离量对不上：逐单元 {rock:0.####}m³ vs 汇总 {r.StripM3:0.####}m³");
        double work = r.Assignments.SelectMany(a => a.Flows).Sum(f => f.TonnageT * f.HaulKm);
        if (Rel(work, r.TransportWorkTKm) > 1e-9)
            bad.Add($"运输功对不上：逐流 {work:0.####} vs 汇总 {r.TransportWorkTKm:0.####}");
        double unplaced = r.Rock.Sum(a => a.UnplacedM3);
        if (Rel(unplaced, r.UnplacedM3) > 1e-9)
            bad.Add($"排不下的量对不上：逐单元 {unplaced:0.####}m³ vs 汇总 {r.UnplacedM3:0.####}m³");

        // 序列完备：Seq 是 1..n 的排列，UnitId 不重
        var seqs = r.Assignments.Select(a => a.Seq).OrderBy(v => v).ToList();
        for (int i = 0; i < seqs.Count; i++)
            if (seqs[i] != i + 1)
            { bad.Add($"推进序不是 1..{seqs.Count} 的排列（第 {i + 1} 位是 {seqs[i]}）"); break; }
        var dup = r.Assignments.GroupBy(a => a.UnitId, StringComparer.Ordinal).FirstOrDefault(gp => gp.Count() > 1);
        if (dup != null) bad.Add($"UnitId 重复：{dup.Key} 出现 {dup.Count()} 次");

        foreach (var a in r.Assignments)
        {
            if (a.Fraction < -1e-9 || a.Fraction > 1 + 1e-9) bad.Add($"{a.UnitId} 本月采出比例 {a.Fraction:0.####} 越界");
            if (a.DoneAfter < -1e-9 || a.DoneAfter > 1 + 1e-9) bad.Add($"{a.UnitId} 累计完成度 {a.DoneAfter:0.####} 越界");
            if (a.InSituM3 < -1e-9) bad.Add($"{a.UnitId} 采出量为负");
            if (a.UnplacedM3 < -1e-9) bad.Add($"{a.UnitId} 未落地量为负");
            if (Bad(a.InSituM3) || Bad(a.TonnageT) || Bad(a.DoneAfter))
                bad.Add($"{a.UnitId} 出现非工程量的数（NaN/∞）");

            bool skipFlowSum = a.Kind == UnitKind.Coal && noSink;
            if (!skipFlowSum)
            {
                double fs = a.Flows.Sum(f => f.InSituM3);
                if (Rel(fs + a.UnplacedM3, a.InSituM3) > 1e-9)
                    bad.Add($"{a.UnitId} 流向合计 {fs:0.####} + 未落地 {a.UnplacedM3:0.####} ≠ 采出 {a.InSituM3:0.####}");
            }
            foreach (var f in a.Flows)
            {
                if (f.DestinationCode.Length == 0 && f.DestinationName.Length == 0)
                    bad.Add($"{a.UnitId} 有流没有去向 —— 缺去向要计入未落地，不许留空流");
                if (Bad(f.HaulKm) || f.HaulKm < -1e-9) bad.Add($"{a.UnitId} 运距 {f.HaulKm} 不是工程量");
                if (f.Density > 0 && Rel(f.InSituM3 * f.Density, f.TonnageT) > 1e-9)
                    bad.Add($"{a.UnitId}→{f.DestinationCode} 吨量 ≠ 实方×ρ");
                if (!f.IsCoalSink && f.Kr > 0 && Rel(f.InSituM3 * f.Kr, f.DumpM3) > 1e-9)
                    bad.Add($"{a.UnitId}→{f.DestinationCode} 占容 ≠ 实方×Kr");
                if (f.IsCoalSink && f.DumpM3 > 1e-9)
                    bad.Add($"{a.UnitId}→{f.DestinationCode} 是煤流却占了排土库容");
            }
        }

        if (r.InternalRatePct < -1.0000001 || r.InternalRatePct > 100.0000001)
            bad.Add($"内排率 {r.InternalRatePct:0.###}% 越界（−1 = 没有排土流，不是 0）");
        if (r.ShortfallT < -1e-9 || r.ShortStripM3 < -1e-9 || r.UnplacedM3 < -1e-9 || r.UnhauledM3 < -1e-9)
            bad.Add("缺口为负");
        if (r.HaulHits > r.HaulTotal || r.HaulHits < 0)
            bad.Add($"运距命中率越界：{r.HaulHits}/{r.HaulTotal}");
        if (Bad(r.CoalT) || Bad(r.StripM3) || Bad(r.TransportWorkTKm) || Bad(r.InternalRatePct))
            bad.Add("汇总里出现非工程量的数（NaN/∞）");
        return bad;

        static bool Bad(double v) => double.IsNaN(v) || double.IsInfinity(v);
    }

    // ════════════════════════════════════════════════════════════════════
    //  对账 ①b —— 剥离量 vs 给定的排弃量（U13）
    //  【与煤那条各判各的】：煤对吨、岩对实方；煤欠产不许把岩的账一起缩（U13.3）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 排弃量对账：给了排弃量时，<b>有效目标</b>必须是它（过完闭包下界/能力上限两道夹的那个数），
    /// 且 <c>达成 + 剥不够 + 车队闸主动削 = 有效目标</c>。
    ///
    /// <para><b>为什么要判"有效目标是怎么来的"而不只判达成量</b>：目标被谁悄悄换掉
    /// （按比重算了一遍、被煤量缩过一次）时，达成量与目标仍然一致 —— <b>两个数一起错，账照样平</b>。
    /// 所以要拿<b>输入</b>里那个绝对量去比，并把两道夹的条件写成构造性的：
    /// 没触发夹时有效目标必须<b>逐位等于</b>输入。</para>
    ///
    /// <para><b>⚠ 不判"剥采比接近目标比"</b>：给了排弃量之后剥采比是<b>结果</b>，
    /// 煤量在容差带里飘 ±2% 它就跟着飘，拿它当判据会把正确行为判红。</para>
    /// </summary>
    public static List<string> StripVsTarget(UnitPlanResult r, UnitPlanInput inp)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        // ① 账要平：达成 + 剥不够 + 主动削 = 有效目标
        if (r.StripTargetM3 > 1e-9)
        {
            double sum = r.StripM3 + r.ShortStripM3 + r.TrimmedByFleetM3;
            if (Rel(sum, r.StripTargetM3) > 5e-3)
                bad.Add($"剥离量的账不平：达成 {r.StripM3:0.##} + 剥不够 {r.ShortStripM3:0.##}"
                      + $" + 车队闸削 {r.TrimmedByFleetM3:0.##} = {sum:0.##} ≠ 有效目标 {r.StripTargetM3:0.##} m³"
                      + $"（相对差 {Rel(sum, r.StripTargetM3) * 100:0.###}%）");
            if (r.StripTargetNote.Length == 0)
                bad.Add("有效目标解出来了却没说来源（U2 三级来源 + 两道夹必须留条）");
        }

        // ② 有效目标必须是【给定的那个绝对量】—— 除非被两道夹改过，而夹的条件是可判的
        if (inp.StripTargetM3 > 0)
        {
            double lo = r.MandatoryStripM3;                                   // 硬下界：必剥闭包
            double hi = inp.StripCapM3 > 0 ? Math.Max(lo, inp.StripCapM3)     // 硬上界：剥离能力
                                           : double.PositiveInfinity;
            double want = Math.Min(Math.Max(inp.StripTargetM3, lo), hi);
            if (Rel(r.StripTargetM3, want) > 1e-6)
                bad.Add($"有效目标不是【给定排弃量过两道夹】的结果：给定 {inp.StripTargetM3:0.##}、"
                      + $"闭包 {lo:0.##}、能力 {(double.IsInfinity(hi) ? "不限" : hi.ToString("0.##"))} "
                      + $"⇒ 应为 {want:0.##}，实际 {r.StripTargetM3:0.##} m³");

            // ③ ★U13.3 与煤量脱钩：给定量原样进目标，不许被"比 × 实际煤量"改写。
            //    煤欠产时这一条最容易破 —— 那正是它存在的理由。
            bool clamped = inp.StripTargetM3 < lo - 1e-6
                        || (inp.StripCapM3 > 0 && inp.StripTargetM3 > inp.StripCapM3 + 1e-6);
            if (!clamped && Rel(r.StripTargetM3, inp.StripTargetM3) > 1e-9)
                bad.Add($"两道夹都没触发，有效目标却不等于给定排弃量："
                      + $"{r.StripTargetM3:0.####} vs {inp.StripTargetM3:0.####} m³ —— 目标被谁改写过（U13.3）");
        }

        // ④ 缺口不许编：报了剥不够就必须真的没达成，反之亦然
        if (r.ShortStripM3 > 1e-6 && r.StripTargetM3 > 1e-9
            && r.StripM3 >= r.StripTargetM3 - 1e-6)
            bad.Add($"报了 {r.ShortStripM3:0.##}m³ 的「剥不够」，剥离量却已经达到目标 —— 这个缺口是编出来的");
        if (r.ShortStripM3 < -1e-9) bad.Add("「剥不够」为负");
        if (r.TrimmedByFleetM3 > 1e-6 && inp.FleetCapTKm <= 0)
            bad.Add($"没卡车队能力，却报了 {r.TrimmedByFleetM3:0.##}m³ 被车队闸削掉 —— 那道闸根本没启用");
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  作业组织指标（U11）—— 报出来的数必须与【排出来的顺序】一致
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 转场次数 / 跨层转场 / 作业面数 / 卸点数，全部按 <c>Seq</c> 顺序重算一遍再对。
    /// <para><b>为什么必须重算</b>：这四个数是比选表的一整组维度，用户按它挑方案。
    /// 它们要是按"选中的集合"数而不是按"排出来的顺序"数，
    /// 换个定序轴指标纹丝不动，比选表就成了摆设 —— 而每一项单独看都合理。</para>
    /// <para><b>前提</b>：不存在体积被判为 0 而被丢掉的入选单元（引擎的转场数按 <c>picked</c> 数、
    /// 结果行按过滤后的清单出，两者只有在没有零量入选时才同基。算例里已保证。）</para>
    /// </summary>
    public static List<string> OrgMetricsMatchOrder(UnitPlanResult r)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;

        var seq = r.Assignments.OrderBy(a => a.Seq).ToList();
        int moves = 0, cross = 0;
        for (int i = 1; i < seq.Count; i++)
        {
            var p = seq[i - 1]; var q = seq[i];
            bool sameSeam = string.Equals(p.SeamCode, q.SeamCode, StringComparison.Ordinal);
            bool inPlace = sameSeam && p.BandId == q.BandId && Math.Abs(p.PanelIndex - q.PanelIndex) <= 1;
            if (!inPlace) moves++;
            if (!sameSeam) cross++;
        }
        if (r.MoveCount != moves)
            bad.Add($"转场次数对不上：结果报 {r.MoveCount}，按排出来的顺序数是 {moves}");
        if (r.CrossLevelMoves != cross)
            bad.Add($"跨层转场对不上：结果报 {r.CrossLevelMoves}，按排出来的顺序数是 {cross}");

        int faces = seq.Select(a => (a.SeamCode, a.BandId)).Distinct().Count();
        if (r.FaceCount != faces)
            bad.Add($"作业面数对不上：结果报 {r.FaceCount}，按（层,带）去重是 {faces}");

        int used = r.Rock.SelectMany(a => a.Flows).Select(f => f.DestinationCode)
                         .Distinct(StringComparer.Ordinal).Count();
        if (r.DumpSlotCount != used)
            bad.Add($"卸点数对不上：结果报 {r.DumpSlotCount}，按去向码去重是 {used}");

        if (r.PartialCount != r.Assignments.Count(a => a.DoneAfter < 1 - 1e-6))
            bad.Add("跨月幅个数与逐行完成度对不上");
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  U1 —— 每月最多一个跨月【煤】幅；遗留幅优先
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>构造性成立</b>：凑量是全局的，只需要切一幅就能对准目标，所以出现第二个跨月煤幅
    /// 说明选煤那一段错了（比如按容差边界切、或者切完没 break）。
    /// <para>岩这边<b>不判这一条</b>：必剥闭包的 <c>mandFrac</c> 天然可以是小数，
    /// 多个岩幅同时是跨月幅是【对的】。拿煤的规则去卡岩会误报。</para>
    /// </summary>
    public static List<string> AtMostOnePartialCoal(UnitPlanResult r)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;
        var partial = r.Coal.Where(a => a.IsPartial).ToList();
        if (partial.Count > 1)
            bad.Add($"U1 破了：本月有 {partial.Count} 个跨月煤幅（{string.Join(" / ", partial.Select(a => $"{a.UnitId}@{a.DoneAfter * 100:0.#}%"))}）"
                  + " —— 凑量是全局的，最多只该切一幅");
        return bad;
    }

    /// <summary>
    /// 遗留幅优先（U1）：只要有<b>任何一个非遗留</b>煤幅进了计划，
    /// 那么<b>每一个</b>还没采完的遗留煤幅都必须也在计划里。
    /// <para>选煤是在固定序上取前缀，所以这条是可判的硬性质；
    /// 写成"遗留幅的 Seq 最小"是<b>错的判据</b> —— Seq 由定序链决定，与选煤优先级无关。</para>
    /// </summary>
    public static List<string> CarryOverFirst(UnitPlanResult r, IReadOnlyList<MineUnit> units)
    {
        var bad = new List<string>();
        if (!r.Success) return bad;
        var inPlan = new HashSet<string>(r.Assignments.Select(a => a.UnitId), StringComparer.Ordinal);
        var carry = units.Where(u => u.IsCoal && u.IsCarryOver && u.RemainM3 > 1e-9).ToList();
        if (carry.Count == 0) return bad;

        bool anyFresh = units.Any(u => u.IsCoal && !u.IsCarryOver && inPlan.Contains(u.UnitId));
        if (!anyFresh) return bad;
        foreach (var u in carry)
            if (!inPlan.Contains(u.UnitId))
                bad.Add($"遗留幅 {u.UnitId}（已采 {u.DoneFraction * 100:0.#}%）没进本月计划，"
                      + "却排了全新的幅 —— 遗留幅是最高优先级（U1）");
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  可行性与"不静默"
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Feasible</c> 必须与三种缺口一致，而且<b>"可行"这个结论要经得起对着输入量看一眼</b>：
    /// 判为可行、实际只采到目标的一半，那说明差的那部分既没进 <c>ShortfallT</c>
    /// 也没进任何别的口子 —— <b>缺口丢在半路上了</b>。
    /// </summary>
    public static List<string> FeasibilityHonest(UnitPlanResult r, UnitPlanInput inp)
    {
        var bad = new List<string>();
        if (!r.Success)
        {
            if (r.Error.Length == 0) bad.Add("求解失败却没有给出原因（Error 为空）");
            return bad;
        }
        bool gapless = r.ShortfallT <= 1e-6 && r.ShortStripM3 <= 1e-6
                    && r.UnplacedM3 <= 1e-6 && r.UnhauledM3 <= 1e-6;
        if (r.Feasible != gapless)
            bad.Add($"Feasible={r.Feasible} 与四种缺口不一致"
                  + $"（煤欠 {r.ShortfallT:0.##}t / 剥不够 {r.ShortStripM3:0.##}m³"
                  + $" / 排不下 {r.UnplacedM3:0.##}m³ / 拉不动 {r.UnhauledM3:0.##}m³）");

        if (r.Feasible && inp.CoalTargetT > 1e-9)
        {
            double rel = (r.CoalT - inp.CoalTargetT) / inp.CoalTargetT;
            if (rel < -Math.Max(0, inp.CoalTolLoPct) / 100.0 - 1e-9)
                bad.Add($"判为【可行】，实际只采到目标的 {(1 + rel) * 100:0.##}%"
                      + $"（{r.CoalT:0.##}t / {inp.CoalTargetT:0.##}t）—— 差的那部分没有落进任何一种缺口");
        }
        return bad;
    }

    /// <summary>
    /// 退化输入<b>不静默</b>：要么明确失败并说原因，要么出了计划；
    /// 出计划时凡是引擎替用户做的决定（默认值 / 近似 / 降级 / 缺口）都得在 <c>Notes</c> 里留条。
    /// </summary>
    public static List<string> NotSilent(UnitPlanResult r, UnitPlanInput inp)
    {
        var bad = new List<string>();
        if (!r.Success)
        {
            if (r.Error.Length == 0) bad.Add("失败却没有原因");
            return bad;
        }
        if (inp.CoalSinks == null || inp.CoalSinks.Count == 0)
        {
            if (r.Coal.Any() && !r.Notes.Any(n => n.Contains("出矿位置")))
                bad.Add("没有出矿位置、煤流全空，却没有一条 Note 说明 —— 运输功里煤那一半悄悄缺了");
        }
        if (inp.Materials == null || inp.Materials.Length == 0)
        {
            if (r.Rock.Any() && !r.Notes.Any(n => n.Contains("物料")))
                bad.Add("没给物料参数、岩按默认 ρ/Kr 排了，却没有一条 Note 说明");
        }
        if (inp.CoalDensity <= 0 && !r.Notes.Any(n => n.Contains("煤视密度")))
            bad.Add("煤视密度非正被夹回，却没有一条 Note 说明");
        if (r.UnplacedM3 > 1e-6 && r.Feasible)
            bad.Add("有排不下的量却判为可行");
        return bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  逐位相同（U7 纯函数性）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>把结果压成一条可逐位比较的指纹（含每一笔流）。</summary>
    public static string Fingerprint(UnitPlanResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(r.Success).Append('|').Append(r.Error).Append('|')
          .Append(R(r.CoalT)).Append('|').Append(R(r.StripM3)).Append('|')
          .Append(R(r.MandatoryStripM3)).Append('|').Append(R(r.TransportWorkTKm)).Append('|')
          .Append(R(r.InternalRatePct)).Append('|')
          .Append(R(r.StripTargetM3)).Append('|').Append(r.StripTargetNote).Append('|')
          .Append(R(r.ShortfallT)).Append('|').Append(R(r.ShortStripM3)).Append('|')
          .Append(R(r.TrimmedByFleetM3)).Append('|')
          .Append(R(r.UnplacedM3)).Append('|').Append(R(r.UnhauledM3)).Append('|')
          .Append(r.FaceCount).Append('|').Append(r.MoveCount).Append('|').Append(r.CrossLevelMoves).Append('|')
          .Append(R(r.MoveCost)).Append('|').Append(r.DumpSlotCount).Append('|')
          .Append(r.HaulHits).Append('/').Append(r.HaulTotal).Append('\n');
        foreach (var a in r.Assignments.OrderBy(x => x.Seq))
        {
            sb.Append(a.Seq).Append(' ').Append(a.UnitId).Append(' ').Append(a.Kind).Append(' ')
              .Append(R(a.InSituM3)).Append(' ').Append(R(a.Fraction)).Append(' ').Append(R(a.DoneAfter)).Append(' ')
              .Append(R(a.TonnageT)).Append(' ').Append(a.IsMandatory).Append(' ').Append(R(a.UnplacedM3));
            foreach (var f in a.Flows)
                sb.Append(" >").Append(f.DestinationCode).Append(':').Append(R(f.InSituM3))
                  .Append(':').Append(R(f.DumpM3)).Append(':').Append(R(f.TonnageT))
                  .Append(':').Append(R(f.HaulKm)).Append(':').Append(f.HaulFromNetwork);
            sb.Append('\n');
        }
        foreach (var n in r.Notes) sb.Append("N ").Append(n).Append('\n');
        return sb.ToString();

        static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>本月排弃序列（岩流按推进序展开的去向码），U12 那条轴的可观测量。</summary>
    public static List<string> DumpSequence(UnitPlanResult r)
        => r.Assignments.OrderBy(a => a.Seq)
                        .SelectMany(a => a.Flows.Where(f => !f.IsCoalSink))
                        .Select(f => f.DestinationCode).ToList();

    // ════════════════════════════════════════════════════════════════════

    /// <summary>一次跑完全部通用判据（不含只在特定算例成立的那几条）。</summary>
    public static List<string> All(UnitPlanResult r, UnitPlanInput inp, UnitGraph g, IReadOnlyList<DumpSlot> slots)
    {
        var bad = new List<string>();
        bad.AddRange(QuantitiesSound(r, inp));
        bad.AddRange(CoalVsTarget(r, inp));
        bad.AddRange(StripVsTarget(r, inp));
        bad.AddRange(DumpCapacityRespected(r, slots));
        bad.AddRange(DumpBottomUp(r, slots, inp.Month));
        bad.AddRange(TopologySound(r, g));
        bad.AddRange(OrgMetricsMatchOrder(r));
        bad.AddRange(AtMostOnePartialCoal(r));
        bad.AddRange(CarryOverFirst(r, inp.Units));
        bad.AddRange(FeasibilityHonest(r, inp));
        bad.AddRange(NotSilent(r, inp));
        return bad;
    }
}
