// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/UnitLedgerFactSource.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  槽③【几何测量】的实现 —— 采掘单元台账（月度表）→ 生产事实
//
//  「实际完成量应由实测面算出，而不是人工填报」。采掘单元台账正是那份实测：
//  一行 = 图上一个体，几何与量以模型为准、重算即刷新；<c>完成度</c> 是人按现场进度
//  在图上标的累计完成比例。两者相乘就是本期实际采出/剥离的实方。
//
//  ── 为什么它能当报表的量源（六元组齐全）──────────────────────────────────
//   期  = Row.Period（"2026-08"）        源 = Row.Region / Seam / UnitId / 质心XYZ
//   物料 = Flow.MaterialCode（coal/rock） 量 = Flow.InSituM3 × Row.Done
//   汇  = Flow.Destination                运距 = Flow.HaulKm
//  ——正是 <see cref="ProductionFact"/> 要的那六样，一样都不缺、一样都不用猜。
//
//  ── 规则（EV 组）──────────────────────────────────────────────────────────
//   EV1 一行 × 一笔 Flow = 一条事实。**逐笔流，不压平** —— 一个岩单元拆到远近两个
//       排土位置是常态，压成一笔运距就是错的（同 MiningUnitLedger.Row.Flows 的口径）。
//   EV2 计划量 = Flow.InSituM3；实绩量 = Flow.InSituM3 × Row.Done。
//       Done 是【累计】完成度，故这里给的是**期内累计实绩**，不是某一天的产量。
//   EV3 只供【期（月）】口径。日 / 周区间一律不供 ——
//       台账没有日期维，硬摊到某一天就是编。见 <see cref="Supplies"/>。
//   EV4 排土行（LedgerKind.Dump）**不产生事实**。它是"位置台账"（库容），不是产量：
//       排到哪儿、排了多少已经写在岩行的 Flow 里，再记一遍就是把剥离量翻倍。
//   EV5 物料一律走 Flow.MaterialCode → MaterialCatalog；台账没给物料码的流
//       HasMaterial=false，量类指标显示「—」，不按"岩行就是硬岩"去猜。
//   EV6 运距只认 Flow.HaulKm。null ⇒ HaulKnown=false，运输功 / 单位油耗一律不可用。
//   EV6b 去向类型先查去向登记簿（按编号，排土位置编号取其排土场名前缀再查）；
//       查不到才按物料本体的唯一允许去向**推定**，且在 <see cref="LastLabel"/> 里
//       报出推定了几笔 —— 内排/外排推错会直接把内排率算错，不能悄悄推。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>采掘单元台账 → 生产事实（<see cref="FactSource"/> 槽③的实现）。</summary>
public static class UnitLedgerFactSource
{
    /// <summary>台账目录覆盖（台架用）。null = 走 <see cref="MonthlyUnitLedgerStore.DefaultRoot"/>。</summary>
    public static string? RootOverride { get; set; }

    /// <summary>最近一次取数说明（期次 / 行数 / 笔数 / 推定了几笔去向 / 为什么没供）。</summary>
    public static string LastLabel { get; private set; } = "（尚未取数）";

    /// <summary>本源的班次标记。台账是期级的，落不到班上——用一个**看得出来**的值，别写成"全天"。</summary>
    public const string PeriodShift = "全期";

    /// <summary>台账没有设备维。用一个**说得出出处**的占位，别留空（留空会和"设备未指派"混在一起）。</summary>
    public const string NoEquipment = "（单元台账·未记设备）";

    /// <summary>
    /// EV3：本区间是不是【期（月）】口径。
    /// <para>只认 <see cref="PeriodKind.Month"/>。日 / 周 / 口径不明（null）一律不供 ——
    /// 台账一行只知道"这个体在 2026-08 采了 51%"，说不出哪一天采的，
    /// 摊到某一天的数看着完全正常，而它是编的。</para>
    /// </summary>
    public static bool Supplies(PeriodKind? period) => period == PeriodKind.Month;

    /// <summary>
    /// 槽③入口（挂到 <see cref="FactSource.GeometrySource"/>）。
    /// 事实的日期一律落在区间右端（截至日），班次为 <see cref="PeriodShift"/>。
    /// </summary>
    public static List<ProductionFact> ForRange(DateTime from, DateTime to, PeriodKind? period)
    {
        if (!Supplies(period))
        {
            LastLabel = period switch
            {
                PeriodKind.Day => "采掘单元台账是【期级】实测，出不了日报（台账没有日期维，摊到某一天就是编）",
                PeriodKind.Week => "采掘单元台账是【期级】实测，出不了周报（台账没有日期维，摊到某一周就是编）",
                _ => "口径不明，采掘单元台账不供数（只供月口径）",
            };
            return new List<ProductionFact>();
        }

        // 期次按区间右端所在的自然月取：月口径的区间是「月初 → 截至日」，两端同月。
        return ForPeriod($"{to:yyyy-MM}", to.Date);
    }

    /// <summary>
    /// 取某一期次（"2026-08"）的全部事实，日期统一戳在 <paramref name="stamp"/> 上。
    /// </summary>
    public static List<ProductionFact> ForPeriod(string period, DateTime stamp)
    {
        var facts = new List<ProductionFact>();

        MonthlyUnitLedgerStore store;
        try { store = new MonthlyUnitLedgerStore(RootOverride); }
        catch (Exception ex) { LastLabel = $"采掘单元台账打不开（{ex.GetType().Name}）"; return facts; }

        if (!store.Exists(period))
        {
            LastLabel = $"采掘单元台账里没有 {period} 这一期（有的期次：{Join(store.ListMonths())}）";
            return facts;
        }

        if (!store.TryLoad(period, out var rows, out var issues) || rows.Count == 0)
        {
            LastLabel = $"采掘单元台账 {period} 读不出行" + (issues.Count > 0 ? $"（{issues[0]}）" : "");
            return facts;
        }

        var sinks = TryLoadSinks();
        string mine = ProjectScope.MineName;
        int mined = 0, skippedDump = 0, noMaterial = 0, noHaul = 0, guessedKind = 0;

        foreach (var r in rows)
        {
            if (r == null) continue;

            // EV4：排土行不产生事实。
            if (r.Kind == LedgerKind.Dump) { skippedDump++; continue; }

            // 期次列与文件名对不上的行不要 —— 文件是按期存的，行里的期次才是行的身份。
            if (r.Period.Length > 0 && !string.Equals(r.Period, period, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Flows.Count == 0) continue;

            double sum = r.FlowSumM3;
            double done = Math.Clamp(r.Done, 0, 1);
            mined++;

            // 主物料条 = 量最大的那一笔（任务级计数指标按它去重）
            double topM3 = r.Flows.Max(f => f.InSituM3);
            bool topTaken = false;

            for (int i = 0; i < r.Flows.Count; i++)
            {
                var leg = r.Flows[i];
                var spec = MaterialCatalog.Exists(leg.MaterialCode) ? MaterialCatalog.Resolve(leg.MaterialCode) : null;
                if (spec == null) noMaterial++;

                bool primary = !topTaken && Math.Abs(leg.InSituM3 - topM3) < 1e-9;
                if (primary) topTaken = true;

                bool haulKnown = leg.HaulKm is > 1e-6;
                if (!haulKnown) noHaul++;

                var (destKind, destName, guessed) = ResolveDest(sinks, leg.Destination, spec);
                if (guessed) guessedKind++;
                bool hasDest = leg.Destination.Length > 0;

                double plan = leg.InSituM3;
                double actual = plan * done;

                var f = new ProductionFact
                {
                    TaskId = $"UL-{r.UnitId}",
                    MaterialFraction = sum > 1e-9 ? leg.InSituM3 / sum : 1,
                    IsPrimaryMaterial = primary,

                    Date = stamp,
                    Shift = PeriodShift,
                    Mine = mine,
                    Panel = PanelOf(r),
                    BenchElevationM = r.Cz,
                    // 单元号就是"这一方量对应图上哪一块"的连接键 —— 这正是本字段设的用途。
                    EngineeringPositionId = r.UnitId,

                    Equipment = NoEquipment,
                    Process = ProcessType.Load.Label(),
                    ProcessKind = ProcessType.Load,
                    CountsAsMined = true,
                    IsDumpReceipt = false,
                    MovesMaterial = true,

                    Material = spec?.Name ?? "",
                    MaterialCode = spec?.Code ?? "",
                    MaterialName = spec?.Name ?? "—",
                    MaterialKind = spec == null ? "—" : spec.Kind.Label(),
                    IsOre = spec?.IsOre ?? false,
                    HasMaterial = spec != null,
                    DensityTPerM3 = spec?.InSituDensityTPerM3 ?? 0,

                    DestinationId = hasDest ? leg.Destination : "",
                    DestinationName = hasDest ? destName : "",
                    DestinationKind = destKind,
                    DestinationKindLabel = hasDest ? destKind.Label() + (guessed ? "（推定）" : "") : "—",
                    HasDestination = hasDest,
                    IsDumpingDestination = hasDest && destKind.IsDumping(),

                    PlanVolumeM3 = plan,
                    ActualVolumeM3 = actual,
                    ShortfallM3 = Math.Max(0, plan - actual),
                    PlanTonnage = spec?.ToTonnage(plan) ?? 0,
                    ActualTonnage = spec?.ToTonnage(actual) ?? 0,
                    PlanLooseM3 = spec?.ToLooseM3(plan) ?? 0,
                    ActualLooseM3 = spec?.ToLooseM3(actual) ?? 0,
                    PlanDumpM3 = spec?.ToDumpM3(plan) ?? 0,
                    DumpVolumeM3 = destKind.IsDumping() ? spec?.ToDumpM3(actual) ?? 0 : 0,

                    // 台账没有工时维。留 0 而不是编一个班时长 —— 工时类指标据此显示「—」。
                    PlannedHours = 0,
                    ActualHours = 0,

                    HaulKnown = haulKnown,
                    HaulDistanceKm = haulKnown ? leg.HaulKm!.Value : 0,
                    EquivHaulKm = haulKnown ? leg.HaulKm!.Value : 0,
                    EffectiveHaulKm = haulKnown ? leg.HaulKm!.Value : 0,

                    Status = StatusOf(r.Status, done),
                    TopReason = "",
                };

                f.PlanTransportWorkTKm = haulKnown ? f.PlanTonnage * f.EffectiveHaulKm : 0;
                f.TransportWorkTKm = haulKnown ? f.ActualTonnage * f.EffectiveHaulKm : 0;

                facts.Add(f);
            }
        }

        LastLabel = $"采掘单元台账 {period}（{store.Root}）："
                  + $"采场单元 {mined} 个 → {facts.Count} 笔流"
                  + $"　·　量 = 逐笔流量 × 完成度（期内累计，非当日）"
                  + (skippedDump > 0 ? $"　·　排土位置 {skippedDump} 行不计量（汇侧，计量会与岩行重复）" : "")
                  + (noMaterial > 0 ? $"　·　◆ {noMaterial} 笔没有物料码，量类指标显示「—」" : "")
                  + (noHaul > 0 ? $"　·　◆ {noHaul} 笔没有运距，运输功/油耗不可用" : "")
                  + (guessedKind > 0 ? $"　·　◆ {guessedKind} 笔的去向类型是**按物料推定**的（登记簿里查不到这个去向），内排率一类指标据此有偏" : "")
                  + (issues.Count > 0 ? $"　·　读表提示：{issues[0]}" : "");
        return facts;
    }

    /// <summary>作业面维 = 采场 · 层/台阶。单元号太细（几百个），按它分组的报表没法看。</summary>
    private static string PanelOf(MiningUnitLedger.Row r)
    {
        string region = r.Region.Length > 0 ? r.Region : "（未记采场）";
        return r.Seam.Length > 0 ? $"{region}·{r.Seam}" : region;
    }

    /// <summary>完成度 → 报表状态。以完成度为准，不以状态列为准（状态列是人填的，完成度是量）。</summary>
    private static string StatusOf(string status, double done)
        => done <= 1e-6 ? "计划" : done >= 0.98 ? "完成" : "部分完成";

    private static SinkRegistry? TryLoadSinks()
    {
        try { return SinkRegistryLoader.Current; }
        catch { return null; }
    }

    /// <summary>
    /// EV6b：去向编号 → (类型, 名称, 是否推定)。
    /// <para>① 按编号直查登记簿；② 排土位置编号取「排土场名」前缀再按名查；
    /// ③ 都查不到才按物料本体的**唯一**允许去向推定，并把"推定"这件事带出去。</para>
    /// </summary>
    private static (SinkKind Kind, string Name, bool Guessed) ResolveDest(
        SinkRegistry? reg, string destId, MaterialSpec? spec)
    {
        if (destId.Length == 0) return (SinkKind.ExternalDump, "", false);

        if (reg != null)
        {
            var byId = reg.All.FirstOrDefault(s => string.Equals(s.Id, destId, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return (byId.Kind, byId.Name, false);

            // 排土位置编号 = 「排土场名-L级-P幅-S带」，前缀就是排土场（这是编号的构造约定，不是猜名字）
            string yard = DumpYardOf(destId);
            if (yard.Length > 0)
            {
                var byName = reg.All.FirstOrDefault(s => string.Equals(s.Name, yard, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return (byName.Kind, byName.Name, false);
            }
        }

        // 推定：物料本体只允许一种去向时用它；否则按"是矿→破碎站 / 非矿→外排"，两者都标推定。
        if (spec != null && spec.AllowedSinks.Count == 1)
            return (spec.AllowedSinks.First(), destId, true);
        bool ore = spec?.IsOre ?? false;
        return (ore ? SinkKind.Crusher : SinkKind.ExternalDump, destId, true);
    }

    /// <summary>排土位置编号里的排土场名（"北排土场1-L2-1001000" → "北排土场1"）。不是排土编号则为空串。</summary>
    private static string DumpYardOf(string code)
    {
        int i = code.IndexOf("-L", StringComparison.Ordinal);
        return i > 0 ? code.Substring(0, i) : "";
    }

    private static string Join(IEnumerable<string> xs)
    {
        var list = xs?.ToList() ?? new List<string>();
        return list.Count == 0 ? "一期都没有" : string.Join("、", list);
    }
}
