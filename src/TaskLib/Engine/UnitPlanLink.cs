// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/UnitPlanLink.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;            // MiningUnitLedger / MonthlyUnitLedgerStore
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  月计划「按采掘单元」下发 —— 把标量换成实体。
//
//  ── 原来是怎么下发的 ──
//    日采出 = 月采出(万t) ÷ 密度 ÷ 作业日 ；然后按各面的 SharePct 摊。
//  这条路唯一需要的输入是三个数，好处是永远算得出来，代价是**它不知道"采哪"**：
//  份额是人在月计划里填的百分比，与图上哪个体被采、那个体还剩多少方，没有任何关系。
//  于是备采核销做不了、期次覆盖率算不出、任务落不回图纸。
//
//  ── 现在这条路 ──
//  短期计划侧早就把「本月排哪些单元」落成了月度台账（一个期次一份 CSV，
//  一行 = 一个采掘单元 = 图上一个体，带方量/去向/完成度）。作业面台账上又填了 UnitId。
//  两边一对，就得到：**这个面本月要采的是这个体，还剩这么多方**。
//      本面月量 = Σ(所绑单元的剩余量)          剩余量 = 单元量 × (1 − 完成度)
//      本面日目标 = 本面月量 ÷ 本月有效作业日
//  份额从"人填的百分比"变成"实体量算出来的比例"，且天然带出备采储量（= 剩余量）。
//
//  ── 三条纪律 ──
//  ① **对不上就整条退回标量法**，不做"一半单元一半份额"的混合：两种口径混着摊，
//     总量会对不上月目标，而且没人查得出是哪一半错了。退回时把原因写进来源文案。
//  ② 只认**本月期次**的行（Period == yyyy-MM）。没有期次的行是基表里还没排产的单元，
//     把它们算进来等于把整个矿的家底当成本月任务。
//  ③ 单元量按物料取：煤取 CoalM3（实方 m³），岩取 NetRockM3（净岩量）。
//     **不用 Qty**——那是报表主量，煤那一档给的是吨，混进 m³ 的求和里就是量纲错。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个作业面按采掘单元算出来的本月家底。</summary>
public sealed class FaceUnitPlan
{
    /// <summary>作业面（<see cref="FaceInput.Zone"/>）。</summary>
    public string Zone = "";

    /// <summary>本面绑的单元号。</summary>
    public string UnitId = "";

    /// <summary>本月计划量（m³ 原位实方）—— 单元的总量，不扣完成度。</summary>
    public double MonthM3;

    /// <summary>剩余量（m³ 实方）= 计划量 × (1 − 完成度)。这才是"还能采多少"。</summary>
    public double RemainingM3;

    /// <summary>单元类型（煤 / 岩 / 排土）。</summary>
    public string Kind = "";
}

/// <summary>月度采掘单元台账 → 各作业面的月量与日目标。</summary>
public static class UnitPlanLink
{
    /// <summary>本月台账里带期次的行数、绑上的面数等（供来源文案与校核）。</summary>
    public sealed class Result
    {
        /// <summary>能不能按单元下发（false = 调用方须退回标量法）。</summary>
        public bool Usable;

        /// <summary>按面算出来的家底（只含绑上且在本月台账里的面）。</summary>
        public List<FaceUnitPlan> Faces = new();

        /// <summary>本月台账里带期次的单元数。</summary>
        public int UnitsInMonth;

        /// <summary>为什么不可用 / 可用时的口径说明。</summary>
        public string Label = "";
    }

    /// <summary>
    /// 读本月单元台账，按作业面的 UnitId 归集。
    /// <paramref name="month"/> 形如 <c>2026-08</c>。
    /// </summary>
    public static Result Resolve(ExploderConfig cfg, string month)
    {
        var res = new Result();
        if (cfg == null || string.IsNullOrWhiteSpace(month))
        { res.Label = "按单元下发：没有期次"; return res; }

        var bound = cfg.Faces
            .Where(f => f.Process == ProcessType.Load && f.HasUnit)
            .ToList();

        if (bound.Count == 0)
        { res.Label = "按单元下发：没有作业面绑单元号，退回份额法"; return res; }

        List<MiningUnitLedger.Row> rows;
        try
        {
            var store = new MonthlyUnitLedgerStore();
            if (!store.Exists(month))
            { res.Label = $"按单元下发：{month} 没有月度台账，退回份额法"; return res; }

            store.TryLoad(month, out rows, out _);
        }
        catch (Exception ex)
        { res.Label = $"按单元下发：月度台账读取失败（{Short(ex)}），退回份额法"; return res; }

        // ② 只认本月期次的行；没有期次 = 基表里还没排产的单元，算进来等于把全矿家底当本月任务
        var inMonth = (rows ?? new List<MiningUnitLedger.Row>())
            .Where(r => r != null
                     && !string.IsNullOrWhiteSpace(r.UnitId)
                     && string.Equals((r.Period ?? "").Trim(), month, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.UnitId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        res.UnitsInMonth = inMonth.Count;
        if (inMonth.Count == 0)
        { res.Label = $"按单元下发：{month} 台账里没有排到本期的单元，退回份额法"; return res; }

        // ① 对不上就整条退回：不做"一半单元一半份额"的混合口径
        var missing = bound.Where(f => !inMonth.ContainsKey(f.UnitId.Trim())).ToList();
        if (missing.Count > 0)
        {
            res.Label = $"按单元下发：{missing.Count}/{bound.Count} 个已绑面的单元不在 {month} 台账里"
                      + $"（如 {string.Join("、", missing.Take(3).Select(f => f.UnitId))}），"
                      + "整条退回份额法 —— 两种口径混着摊会对不上月总量，且查不出是哪一半错了";
            return res;
        }

        foreach (var f in bound)
        {
            var u = inMonth[f.UnitId.Trim()];
            double m3 = UnitVolumeM3(u);
            double done = Math.Clamp(u.Done, 0, 1);   // 累计完成度 0~1
            res.Faces.Add(new FaceUnitPlan
            {
                Zone = f.Zone,
                UnitId = f.UnitId.Trim(),
                MonthM3 = m3,
                RemainingM3 = m3 * (1 - done),
                Kind = MiningUnitLedger.KindToText(u.Kind),
            });
        }

        double total = res.Faces.Sum(x => x.RemainingM3);
        if (total <= 1e-6)
        {
            res.Label = $"按单元下发：{month} 已绑的 {bound.Count} 个单元剩余量都是 0（已采完？），退回份额法";
            return res;
        }

        res.Usable = true;
        res.Label = $"按单元下发：{res.Faces.Count} 面绑到 {month} 台账（本期 {inMonth.Count} 个单元 · "
                  + $"已绑单元剩余 {total / 1e4:0.00} 万m³）";
        return res;
    }

    /// <summary>
    /// 单元的实方体积（m³）。
    /// <b>不用 <c>Row.Qty</c></b>：那是报表主量，煤那一档给的是<b>吨</b>，
    /// 混进 m³ 的求和里就是量纲错——一个 5 万吨的煤单元会被当成 5 万 m³ 参与摊分。
    /// </summary>
    private static double UnitVolumeM3(MiningUnitLedger.Row u) => u.Kind switch
    {
        LedgerKind.Coal => u.CoalM3 ?? 0,
        LedgerKind.Rock => u.NetRockM3 ?? 0,
        _ => u.DumpCapM3 ?? 0,
    };

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
