using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Cad;

/// <summary>推演里的一个月（一帧）。</summary>
public sealed class ShortTermSimFrame
{
    public int Index;
    public string Label = "";
    public int Month;

    /// <summary>本月采出 万t / 剥离 万m³实方 / 生产剥采比 / 有效作业日 / 设备利用率 % / 完成率 %。</summary>
    public double CoalWanT, StripWanM3, Ratio, Workdays, EquipUtilPct, CompletionPct;

    /// <summary>本月排弃**占容方** 万m³ = 剥离实方 × Kr。</summary>
    public double DumpWanM3;

    /// <summary>本月推进距离 m（排产时按 v=Q/(L·H·ρ) 算好的）。</summary>
    public double AdvanceM;

    /// <summary>截至本月末的累计采出 / 累计排弃占容（万）。</summary>
    public double CumCoalWanT, CumDumpWanM3;

    public bool InnerDump, IsMaintenance, IsPeak;

    /// <summary>本月主作业面。</summary>
    public string ActiveFace = "";

    /// <summary>本月末各去向剩余库容 万m³。容量不限的去向不出现。</summary>
    public Dictionary<string, double> RemainWanM3 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本月排不下的量 万m³。0 = 排得下。</summary>
    public double SpilledWanM3;

    public List<string> Notes = new();
}

/// <summary>
/// 「短期进度计划动态模拟」的**月粒度时间轴** —— §三四七 年轨那一支的月度孪生。
/// 一帧 = 一月：量与剥采比、有效作业日、设备利用、检修/高峰月、推进距离，
/// 以及**逐月扣各去向库容直到排满**。
///
/// 口径与年轨保持一字不差（同一套 <c>V容 = V实 × Kr</c>、同一套内排优先、同一套"没接台账 = 未校核"）——
/// <b>两个尺度的口径必须同源</b>：同一个矿在年图上"库容够排"、在月图上"排不下"，
/// 那种不一致最难查，因为两边都觉得自己没错。
///
/// ── 与原版的范围差异（登记）──
/// 原版 `短期进度计划动态模拟` 开的是 `DynamicSimWindow`，除量以外还有**车流 / 设备图标 /
/// 路线标注 / 班次**那一整层（整套 `Sim*Stage` 管线）。本轮只移**量与库容**这一支，
/// 其余另计并在窗口里如实写明。
/// </summary>
public static class ShortTermSimTimeline
{
    /// <summary>与年轨同一处口径，不另立一份。</summary>
    public static double RockSwell => LongTermSimTimeline.RockSwell;

    /// <summary>建时间轴。<paramref name="sinks"/> 可空 = 不做库容校核（只出量与曲线）。</summary>
    public static List<ShortTermSimFrame> Build(ShortTermPlan? plan, SinkRegistry? sinks = null)
    {
        var frames = new List<ShortTermSimFrame>();
        if (plan == null || plan.Months.Count == 0) return frames;

        // 库容按副本扣：推演不能改动台账上的真实已填量
        var caps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var order = new List<SinkNode>();
        if (sinks != null)
            foreach (var s in sinks.All.Where(x => x.IsDumping && x.IsCapacityLimited))
            {
                caps[s.Id] = s.RemainingM3 / 1e4;
                order.Add(s);
            }
        order = order.OrderByDescending(s => s.Kind == SinkKind.InternalDump)
                     .ThenBy(s => s.FallbackHaulKm)
                     .ThenBy(s => s.Id, StringComparer.Ordinal)
                     .ToList();

        double kr = RockSwell;
        double cumCoal = 0, cumDump = 0;

        for (int i = 0; i < plan.Months.Count; i++)
        {
            var z = plan.Months[i];
            var f = new ShortTermSimFrame
            {
                Index = i,
                Label = z.Label,
                Month = z.Month,
                CoalWanT = z.CoalWanT,
                StripWanM3 = z.StripWanM3,
                Ratio = z.Ratio,
                Workdays = z.Workdays,
                EquipUtilPct = z.EquipUtilPct,
                CompletionPct = z.CompletionPct,
                AdvanceM = z.AdvanceM,
                DumpWanM3 = z.StripWanM3 * kr,
                // 内排与否读排产算好的那一位，不在这里另推（同 §三四七）
                InnerDump = z.Dump == LongTermDumpMode.Internal,
                IsMaintenance = z.IsMaintenance,
                IsPeak = z.IsPeak,
                ActiveFace = z.ActiveFace ?? "",
            };

            cumCoal += f.CoalWanT;
            cumDump += f.DumpWanM3;
            f.CumCoalWanT = cumCoal;
            f.CumDumpWanM3 = cumDump;

            // 一个有容量的排弃去向都没有 = 没法校核，不是"全排不下"（同 §三四七 修过的那条）
            double need = order.Count == 0 ? 0 : f.DumpWanM3;
            foreach (var s in order)
            {
                if (need <= 1e-9) break;
                double have = caps[s.Id];
                if (have <= 1e-9) continue;
                double take = Math.Min(have, need);
                caps[s.Id] = have - take;
                need -= take;
                if (caps[s.Id] <= 1e-9) f.Notes.Add($"{s.Name} 于 {f.Label} 排满");
            }
            f.SpilledWanM3 = Math.Max(0, need);
            if (f.SpilledWanM3 > 1e-9)
                f.Notes.Add($"{f.Label} 排不下 {f.SpilledWanM3.ToString("0.##", CultureInfo.InvariantCulture)} 万m³（各去向已满）");
            foreach (var kv in caps) f.RemainWanM3[kv.Key] = kv.Value;

            if (plan.RatioCeiling > 0 && f.Ratio > plan.RatioCeiling + 1e-9)
                f.Notes.Add($"生产剥采比 {f.Ratio.ToString("0.##", CultureInfo.InvariantCulture)} 超上限 "
                          + plan.RatioCeiling.ToString("0.##", CultureInfo.InvariantCulture));
            // 设备利用率 > 100% 不是算错 —— 公式与原版一字不差，它的含义是
            // "按配置的设备台数 × 台月能力，本月任务根本干不完"。但图上那条线会被钳在顶端，
            // 不点出来就会被当成"一直满负荷"。故超 100% 单列一条。
            if (f.EquipUtilPct > 100 + 1e-9)
                f.Notes.Add($"设备利用 {f.EquipUtilPct.ToString("0", CultureInfo.InvariantCulture)}% 超 100%"
                          + "（按现配设备台数与台月能力，本月任务干不完 —— 该加设备或下调月目标）");
            if (f.IsMaintenance) f.Notes.Add("检修月（作业日已降效）");
            if (f.IsPeak) f.Notes.Add("峰月");

            frames.Add(f);
        }
        return frames;
    }

    /// <summary>第一次排不下的那一帧；一直排得下返回 -1。</summary>
    public static int FirstSpillIndex(IReadOnlyList<ShortTermSimFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++) if (frames[i].SpilledWanM3 > 1e-9) return i;
        return -1;
    }

    /// <summary>峰月下标（采出最大的那个月）；没有帧返回 -1。</summary>
    public static int PeakIndex(IReadOnlyList<ShortTermSimFrame> frames)
    {
        int best = -1; double top = double.NegativeInfinity;
        for (int i = 0; i < frames.Count; i++)
            if (frames[i].CoalWanT > top) { top = frames[i].CoalWanT; best = i; }
        return best;
    }

    /// <summary>
    /// 内排率 %：内排月的排弃占容 ÷ 全部排弃占容。
    /// 排弃量为 0 时返回 null（<b>不是 0</b>）—— "没有排弃量"与"内排率 0%"是两回事。
    /// </summary>
    public static double? InnerDumpPct(IReadOnlyList<ShortTermSimFrame> frames)
    {
        double all = frames.Sum(f => f.DumpWanM3);
        if (all <= 1e-9) return null;
        return frames.Where(f => f.InnerDump).Sum(f => f.DumpWanM3) / all * 100.0;
    }

    /// <summary>顶栏一句话。</summary>
    public static string Summary(ShortTermPlan? plan, IReadOnlyList<ShortTermSimFrame> frames)
    {
        if (frames.Count == 0) return "没有可推演的逐月表 —— 先排产";
        var last = frames[^1];
        int spill = FirstSpillIndex(frames);
        int peak = PeakIndex(frames);
        double? inner = InnerDumpPct(frames);
        string s = $"{frames.Count} 个月（{frames[0].Label}–{last.Label}）"
                 + $"　累计采出 {last.CumCoalWanT:N1} 万t　累计排弃占容 {last.CumDumpWanM3:N0} 万m³"
                 + $"　内排率 {(inner is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—")}";
        if (peak >= 0) s += $"　峰月 {frames[peak].Label}({frames[peak].CoalWanT:0.#} 万t)";
        // 三分不是两分：没查 ≠ 查过没问题（同 §三四七）
        bool checkedCap = frames.Any(f => f.RemainWanM3.Count > 0);
        s += !checkedCap ? "　（未接去向台账，未做库容校核）"
           : spill >= 0 ? $"　⚠ 自 {frames[spill].Label} 起排不下"
           : "　库容够排";
        return s;
    }
}
