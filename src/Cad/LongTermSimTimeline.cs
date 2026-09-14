using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Cad;

/// <summary>推演里的一年（一帧）。</summary>
public sealed class LongTermSimFrame
{
    public int Index;
    public string Label = "";
    public PlanPhase Phase;

    /// <summary>本年采出 万t / 剥离 万m³实方 / 生产剥采比 m³·t⁻¹ / 达产率 %。</summary>
    public double CoalWanT, StripWanM3, Ratio, CapacityPct;

    /// <summary>本年排弃**占容方** 万m³ = 剥离实方 × Kr（排土场按这个扣库容，不是实方）。</summary>
    public double DumpWanM3;

    /// <summary>截至本年末的累计采出 / 累计排弃占容（万）。</summary>
    public double CumCoalWanT, CumDumpWanM3;

    /// <summary>本年是否内排（起转年及以后）。</summary>
    public bool InnerDump;

    /// <summary>本年是否设计计算年（达产年）。</summary>
    public bool IsDesignCalcYear;

    /// <summary>本年末各去向的剩余库容 万m³（键 = SinkNode.Id）。容量不限的去向不出现在表里。</summary>
    public Dictionary<string, double> RemainWanM3 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本年**排不下**的量 万m³（所有排弃类去向都满了还剩这么多）。0 = 排得下。</summary>
    public double SpilledWanM3;

    /// <summary>本年要说的话（排满 / 剥采比超 n经 / 达产 …）。</summary>
    public List<string> Notes = new();
}

/// <summary>
/// 「中长远规划动态模拟」的**年粒度时间轴**（移植原 <c>TaskLib.Simulation</c> 里年轨那一支的口径）。
/// 一帧 = 一年：逐年扣各去向库容直到排满、逐年记内排起转与达产、逐年给量与剥采比。
///
/// ── 与原版的结构差异（登记）──
/// 原版 <c>LongTermPlanLink</c> 是一层**反射软读**（TaskLib 不能引用 PlanLib，
/// 逐年表只能按属性名反射着取，改名即静默降级）。Kylin 没有那道模块墙，
/// <see cref="LongTermPlan"/> 就在手边 —— <b>整层反射不必移植</b>，直接吃对象。
/// 原注释里那三条"借了月度参数"的坑（期次只认已确定方案、H/L 取自月度方案、推进方位默认 0°）
/// 在这里结构性不存在：本类只吃中长远方案自己那一份。
///
/// ── 演的是"推演算出来的那一份"，不是把计划表原样搬过来 ──
/// 两者本该一致，<b>不一致正是要看的东西</b>：逐期扣库容之后排不下的量（<see cref="LongTermSimFrame.SpilledWanM3"/>）
/// 在计划表里是看不见的。
/// </summary>
public static class LongTermSimTimeline
{
    /// <summary>剥离实方 → 排弃占容方的残余松散系数。取硬岩（剥离物以岩为主），与物料目录同一处口径。</summary>
    public static double RockSwell => MaterialCatalog.Resolve(MaterialCatalog.Rock).ResidualSwellFactor;

    /// <summary>建时间轴。<paramref name="sinks"/> 可空 = 不做库容校核（只出量与曲线）。</summary>
    public static List<LongTermSimFrame> Build(LongTermPlan? plan, SinkRegistry? sinks = null)
    {
        var frames = new List<LongTermSimFrame>();
        if (plan == null || plan.Periods.Count == 0) return frames;

        // 库容按**副本**扣：推演不能改动台账上的真实已填量
        var caps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var order = new List<SinkNode>();
        if (sinks != null)
            foreach (var s in sinks.All.Where(x => x.IsDumping && x.IsCapacityLimited))
            {
                caps[s.Id] = s.RemainingM3 / 1e4;      // 万 m³
                order.Add(s);
            }
        // 内排优先扣：内排在采空区里、运距最短，现场就是先往那儿排
        order = order.OrderByDescending(s => s.Kind == SinkKind.InternalDump)
                     .ThenBy(s => s.FallbackHaulKm)
                     .ThenBy(s => s.Id, StringComparer.Ordinal)
                     .ToList();

        double kr = RockSwell;
        double cumCoal = 0, cumDump = 0;

        for (int i = 0; i < plan.Periods.Count; i++)
        {
            var z = plan.Periods[i];
            var f = new LongTermSimFrame
            {
                Index = i,
                Label = z.Label,
                Phase = z.Phase,
                CoalWanT = z.CoalWanT,
                StripWanM3 = z.StripWanM3,
                Ratio = z.Ratio,
                CapacityPct = z.CapacityPct,
                DumpWanM3 = z.StripWanM3 * kr,
                IsDesignCalcYear = z.IsDesignCalcYear,
            };
            // 内排与否**直接读排产时算好的那一位**，不在这里照 InnerDumpStartYear 再推一遍：
            // 那个字段是"相对生产年"的偏移，而这里的 i 还含基建期 —— 自己推准错，
            // 而且推出来的数与逐年表对不上时谁也说不清哪个算数。
            f.InnerDump = z.Dump == LongTermDumpMode.Internal;

            cumCoal += f.CoalWanT;
            cumDump += f.DumpWanM3;
            f.CumCoalWanT = cumCoal;
            f.CumDumpWanM3 = cumDump;

            // 逐年扣库容。**一个有容量的排弃去向都没有 = 没法校核，不是"全排不下"** ——
            // 后者会让"没配台账"和"库容真的不够"显示成同一件事，而它们要采取的行动完全相反。
            double need = order.Count == 0 ? 0 : f.DumpWanM3;
            foreach (var s in order)
            {
                if (need <= 1e-9) break;
                double have = caps[s.Id];
                if (have <= 1e-9) continue;
                double take = Math.Min(have, need);
                caps[s.Id] = have - take;
                need -= take;
                if (caps[s.Id] <= 1e-9)
                    f.Notes.Add($"{s.Name} 于 {f.Label} 排满");
            }
            f.SpilledWanM3 = Math.Max(0, need);
            if (f.SpilledWanM3 > 1e-9)
                f.Notes.Add($"{f.Label} 排不下 {f.SpilledWanM3.ToString("0.##", CultureInfo.InvariantCulture)} 万m³（各去向已满）");

            foreach (var kv in caps) f.RemainWanM3[kv.Key] = kv.Value;

            if (plan.EconomicStripRatioMax > 0 && f.Ratio > plan.EconomicStripRatioMax + 1e-9)
                f.Notes.Add($"生产剥采比 {f.Ratio.ToString("0.##", CultureInfo.InvariantCulture)} 超 n经 "
                          + plan.EconomicStripRatioMax.ToString("0.##", CultureInfo.InvariantCulture));
            if (f.IsDesignCalcYear) f.Notes.Add("设计计算年（达产）");

            frames.Add(f);
        }
        return frames;
    }

    /// <summary>第一次排不下的那一帧下标；一直排得下返回 -1。</summary>
    public static int FirstSpillIndex(IReadOnlyList<LongTermSimFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++) if (frames[i].SpilledWanM3 > 1e-9) return i;
        return -1;
    }

    /// <summary>内排起转的那一帧下标；不内排返回 -1。</summary>
    public static int InnerDumpStartIndex(IReadOnlyList<LongTermSimFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++) if (frames[i].InnerDump) return i;
        return -1;
    }

    /// <summary>整条时间轴的一句话结论（顶栏用）。</summary>
    public static string Summary(LongTermPlan? plan, IReadOnlyList<LongTermSimFrame> frames)
    {
        if (frames.Count == 0) return "没有可推演的逐年表 —— 先排产";
        var last = frames[^1];
        int spill = FirstSpillIndex(frames);
        int inner = InnerDumpStartIndex(frames);
        string s = $"{frames.Count} 年（{frames[0].Label}–{last.Label}）"
                 + $"　累计采出 {last.CumCoalWanT:N0} 万t　累计排弃占容 {last.CumDumpWanM3:N0} 万m³";
        if (inner >= 0) s += $"　内排自 {frames[inner].Label} 起";
        // 排不下要顶到最前面说 —— 这正是"推演"比"看计划表"多出来的那句话。
        // 【三分不是两分】没接去向台账时既不是"够排"也不是"排不下"，是**没校核**；
        // 报成"库容够排"等于拿"没查"冒充"查过没问题"。
        bool checkedCap = frames.Any(f => f.RemainWanM3.Count > 0);
        s += !checkedCap ? "　（未接去向台账，未做库容校核）"
           : spill >= 0 ? $"　⚠ 自 {frames[spill].Label} 起排不下"
           : "　库容够排";
        return s;
    }
}
