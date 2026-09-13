using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 短期(月度)生产计划「月度计划编制」内核（见 docs/短期生产计划_设计.md §5）。
/// 划月 → 以 工作历(有效作业日) × 设备可用 × 季节降效 × 作业组织 造月权重 → 摊年采出目标 →
/// 均衡平滑 → 由剥采比剖面反推月剥离 → **拆物料流 + 配去向 + 扣库容** → 推进/设备利用/累计/完成率
/// → 回填 Months + Result。纯量算的「年→月」均衡细化，不另起优化器。
///
/// 物料维/去向维（见 <see cref="PlanFlowAllocator"/>）：月采出/月剥离不再只是两个标量，
/// 而是按作业面份额 × 物料构成拆成一条条 O-D 流，面上没指定去向的按「运输功最小 + 库容够 +
/// 物料兼容」贪心分配，并逐月按占容方 Kr 扣减各去向的剩余库容 —— 排不下时给出明确警示。
///
/// 同时提供「多方案生成」：作业组织 × 工作历方案 正交派生多套供联合对比。
/// </summary>
public static class ShortTermScheduler
{
    /// <summary>对一个月度计划方案排产，回填 Months 与 Result。</summary>
    /// <param name="p">月度计划方案。</param>
    /// <param name="tgt">
    /// 逐月配置表 —— <b>「月煤量」这个量的唯一来源</b>。
    ///
    /// <para><b>null = 取会话里那一张</b>（<see cref="MonthlyTargetStore.Current"/>）：三个消费方
    /// （本排产器 / 量驱动采剥接续 / 采掘单元排产）必须拿到<b>同一行对象</b>，各读各的就是三份月煤量。</para>
    ///
    /// <para>传一张<b>空表</b>（<see cref="MonthlyTargetTable.NoOverride"/>）= 本次不取任何覆盖。
    /// 逐月配置表<b>派生初值</b>那一跑就是这么调的，两条理由：① <c>Store.Current</c> 是懒建的，
    /// 建的过程里再访问它就是无限递归；② 派生值要是把自己的人工覆盖吃进去，
    /// 「重置为派生值」就再也回不到引擎摊的那个数。</para>
    ///
    /// <para><b>只认被人工覆盖过的列</b>：表里的派生值本来就是下面这套摊分算出来的，
    /// 喂回来是自己抄自己；而表没跟着基础约束重派时，那份旧值会静默盖掉新年目标。</para>
    /// </param>
    public static void Schedule(ShortTermPlan p, MonthlyTargetTable? tgt = null)
    {
        p.Months.Clear();
        int n = Math.Clamp(p.MonthCount, 1, 24);
        int start = Math.Clamp(p.StartMonth, 1, 12);
        double rho = ShortTermPlan.DefaultCoalDensity;
        double annualCoal = Math.Max(1, p.AnnualCoalTargetWanT);
        double baseRatio = Math.Max(0.1, p.BaseRatio);

        // ── 月权重：有效作业日 × 设备可用率 × 作业组织形态 ──
        double avail = Math.Clamp(p.Field.EquipmentAvailabilityPct / 100.0, 0.3, 1.0);
        var months = Enumerable.Range(0, n).Select(i => ((start - 1 + i) % 12) + 1).ToArray();
        double[] wd = months.Select(m => p.Field.WorkdaysFor(m, p.Calendar)).ToArray();
        double[] shape = DispatchShape(p.Dispatch, n);
        double[] w = new double[n];
        for (int i = 0; i < n; i++) w[i] = Math.Max(0.05, wd[i] * avail * shape[i]);
        double wsum = w.Sum();

        // ── 摊年采出目标 → 月采出，再按月产量均衡向均值收敛 ──
        double uniform = annualCoal / n;
        double lambda = Math.Clamp(p.Balance.OutputSmooth / Math.Max(1e-6, p.Balance.Sum), 0, 1); // 均衡份额
        double[] coal = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = annualCoal * w[i] / wsum;
            coal[i] = raw * (1 - lambda) + uniform * lambda;
        }

        // ── 月产上限裁剪 + 余量回摊（保证年目标守恒）──
        if (p.MonthlyCoalCeilingWanT > 0) RedistributeCeiling(coal, p.MonthlyCoalCeilingWanT);

        // ── 工作历产能上限：抢产/保守要真的改得动采剥量 ──────────────────────
        //  此前工作历（Push 1.10 / Conservative 0.92）只是 wd[i] 上的一个【常数因子】，
        //  而月采出走 raw = annualCoal * w[i]/wsum —— 常数在分子分母里精确抵消。
        //  实测：三套工作历的年采出与逐月采出【逐位相同】（差 0.000 万t），
        //  它唯一改变的只有设备利用率读数（86% / 78% / 93%）。
        //  于是九套候选里 44% 的比选权重落在两个恒等的量上。
        //
        //  现在把作业日算出的【月产能力】当成一道真上限夹进来：
        //  抢产（作业日多）能力大、卡得少；保守（作业日少）能力小、会被卡住。
        //  ⚠ 卡住的量【不回摊】—— 回摊等于把"这个月干不完"变成"别的月替它干"，
        //    那正好又把工作历的影响抵消掉了。差额原样进完成率，并逐月记账。
        var warnings = new List<string>();
        var capNote = new List<string>();
        double capBefore = coal.Sum();
        double capShortMax = 0;              // 最紧那个月缺多少（只报不改时用）
        if (p.Field.EquipmentCount > 0 && p.Field.EquipMonthlyCapacityWanM3 > 1e-9)
        {
            for (int i = 0; i < n; i++)
            {
                // 与下面算设备利用率用的是同一个式子 —— 两处口径必须一致，否则
                // "被产能卡住"和"利用率 100%"会对不上，而两个数看上去都正常。
                double capM3 = p.Field.EquipmentCount * p.Field.EquipMonthlyCapacityWanM3
                             * (wd[i] / Math.Max(1, p.Field.StandardWorkdays)) * avail;
                // 能力是总作业量（采出折体积 + 剥离）的上限；按本方案的基准剥采比折回可采出的煤
                double ratioBase = Math.Max(0, p.BaseRatio);
                double capCoal = capM3 / (1.0 / rho + ratioBase);
                if (capCoal > 1e-9 && coal[i] > capCoal + 1e-9)
                {
                    capShortMax = Math.Max(capShortMax, coal[i] / capCoal);
                    if (p.EnforceCapacityCeiling)
                    {
                        capNote.Add($"{((start - 1 + i) % 12) + 1}月 {coal[i]:0.#}→{capCoal:0.#}");
                        coal[i] = capCoal;
                    }
                }
            }
        }
        if (capNote.Count > 0)
        {
            double lost = capBefore - coal.Sum();
            warnings.Add($"· 工作历【{p.CalendarText}】：有 {capNote.Count} 个月被**产能上限**卡住"
                       + $"（{string.Join("、", capNote.Take(4))}{(capNote.Count > 4 ? "…" : "")}），"
                       + $"合计少采 {lost:0.#} 万t —— **不回摊**，差额原样进完成率。");
        }
        else if (capShortMax > 1.05)
        {
            // 闸关着 ⇒ 只报不改。这条本身就是个该被看见的问题：
            // 默认样例（年 1000万t + 6500万m³ ⇒ 需 603万m³/月）与默认机队
            // （4台 × 28万m³ × 82% = 92万m³/月）差 6.5 倍 —— 两个缺省值本来就对不上。
            warnings.Add($"◆ 按现有机队（{p.Field.EquipmentCount} 台 × {p.Field.EquipMonthlyCapacityWanM3:0.#} 万m³/月 "
                       + $"× 完好率 {p.Field.EquipmentAvailabilityPct:0}%），最紧那个月的作业量是能力的 "
                       + $"**{capShortMax:0.#} 倍** —— 计划量做不出来。"
                       + "本次**没有按能力削减**（产能上限闸没开）：要让工作历/机队真的影响采剥量，"
                       + "在基础约束里打开「按设备产能限产」。");
        }

        // ── 剥采比月剖面：集中强采峰值高，多面/均衡更平；剥采比均衡份额拉平 ──
        double ratioLambda = Math.Clamp(p.Balance.RatioSmooth / Math.Max(1e-6, p.Balance.Sum), 0, 1);
        double amp = p.Dispatch switch
        {
            DispatchStrategy.Concentrated => 0.22,
            DispatchStrategy.MultiFace => 0.08,
            _ => 0.12
        };
        amp *= 1 - ratioLambda;   // 剥采比均衡越重，月间波动越小

        double cumCoal = 0, cumStrip = 0, advTot = 0;
        double utilSum = 0; int utilCnt = 0;

        // ── 去向 + 库容台账（读 GeoDataBase，读不到用内置样例）──
        // 排产全程持有同一本账，逐月按【占容方 V容 = V实×Kr】扣减，
        // 「第 8 个月北排土场排满」这种事就在月计划阶段暴露出来，不留到现场才发现。
        var ledger = PlanDumpLedger.FromCatalog();
        ApplyInternalDumpTiming(p, ledger, warnings);   // 内排启用时机（承上游中长远的内排开关/起转年）

        // ── 逐月配置表 · 月采出的人工覆盖 ────────────────────────────────────
        //  落在【月上限回摊之后】：覆盖值是人填的最终值，不该再被削峰回摊改一遍
        //  （改了就是"人填的 12 个数"变成"人没填过的 12 个数"，而表面上看不出来）。
        var tgtRows = ResolveTargetRows(p, tgt, n, start, warnings);
        int coalOver = 0;
        for (int i = 0; i < n; i++)
        {
            var t = tgtRows[i];
            if (t == null || !t.IsOverridden(MonthlyTargetField.Coal)) continue;
            double was = coal[i];
            coal[i] = t.CoalWanT;
            coalOver++;
            warnings.Add($"· 逐月配置表覆盖 {t.PeriodKey} 的月采出：{was:0.#} → {coal[i]:0.#} 万t（人工填的，不再按年目标摊）。");
            if (p.MonthlyCoalCeilingWanT > 0 && coal[i] > p.MonthlyCoalCeilingWanT + 1e-6)
                warnings.Add($"◆ {t.PeriodKey} 人工月采出 {coal[i]:0.#} 万t 高过月产上限 {p.MonthlyCoalCeilingWanT:0.#} 万t —— "
                           + "覆盖值优先，**没有再削峰**（硬校核那条会照报）。");
        }
        if (coalOver > 0)
        {
            double sumCoal = coal.Sum();
            warnings.Add($"· 本次有 {coalOver} 个月的采出来自逐月配置表的人工覆盖：逐月合计 {sumCoal:0.#} 万t / "
                       + $"年目标 {p.AnnualCoalTargetWanT:0.#} 万t（差 {sumCoal - p.AnnualCoalTargetWanT:+0.#;-0.#;0} 万t）"
                       + " —— **不回摊、不缩放**，差额原样进完成率。");
        }

        // 峰值月要在【覆盖之后】才算得准 —— 覆盖前算的话，人工填出来的那个最大月不会被标成峰值，
        // 而峰值月是比选表里直接看的一列。
        double peakCoal = coal.Max();

        var rows = new List<MonthPeriod>();
        for (int i = 0; i < n; i++)
        {
            int m = months[i];
            // 月剥采比：随月产相对均值轻微起伏（强采月剥采比偏高），上限裁剪
            double dev = uniform > 1e-6 ? (coal[i] - uniform) / uniform : 0;
            double ratio = baseRatio * (1 + amp * dev);
            if (p.RatioCeiling > 0) ratio = Math.Min(ratio, p.RatioCeiling);
            ratio = Math.Max(0.1, ratio);
            double strip = coal[i] * ratio;
            if (p.MonthlyStripCeilingWanM3 > 0 && strip > p.MonthlyStripCeilingWanM3)
            {
                strip = p.MonthlyStripCeilingWanM3;
                ratio = coal[i] > 1e-6 ? strip / coal[i] : ratio;
            }

            // ── 逐月配置表 · 月剥离 / 作业日的人工覆盖 ────────────────────────
            //  ★★ 必须落在 **PlanFlowAllocator.BuildMonthFlows 之前**：
            //     MonthPeriod.CoalWanT / StripWanM3 / Ratio 的标量 setter 在 Flows 非空时是
            //     【无声空转】（getter 走 Flows 派生，见 MonthPeriod.HasFlows）——
            //     插在 BuildMonthFlows 之后一行代码都不会报错，但一个数也不会变。
            //  也必须落在下面的推进/设备利用率之前：那两个量按本月真剥离量算。
            var tgtRow = tgtRows[i];
            var ovNotes = new List<string>();
            if (tgtRow != null && tgtRow.IsOverridden(MonthlyTargetField.Strip))
            {
                double wasStrip = strip;
                strip = tgtRow.StripWanM3;
                ratio = coal[i] > 1e-6 ? strip / coal[i] : ratio;
                ovNotes.Add($"· 逐月配置表覆盖 {tgtRow.PeriodKey} 的月剥离：{wasStrip:0} → {strip:0} 万m³"
                          + $"（剥采比随之成 {ratio:0.00} —— 剥采比只有「剥离÷采出」这一个来源）。");
                if (p.MonthlyStripCeilingWanM3 > 0 && strip > p.MonthlyStripCeilingWanM3 + 1e-6)
                    ovNotes.Add($"◆ {tgtRow.PeriodKey} 人工月剥离 {strip:0} 万m³ 高过月剥离上限 "
                              + $"{p.MonthlyStripCeilingWanM3:0} 万m³ —— 覆盖值优先，没有夹回。");
                if (p.RatioCeiling > 0 && ratio > p.RatioCeiling + 1e-6)
                    ovNotes.Add($"◆ {tgtRow.PeriodKey} 覆盖后的剥采比 {ratio:0.00} 高过上限 {p.RatioCeiling:0.00} —— 硬校核那条会报。");
            }
            if (tgtRow != null && tgtRow.IsOverridden(MonthlyTargetField.Workdays))
            {
                double wasWd = wd[i];
                wd[i] = tgtRow.Workdays;
                ovNotes.Add($"· 逐月配置表覆盖 {tgtRow.PeriodKey} 的作业日：{wasWd:0.#} → {wd[i]:0.#} 天"
                          + "（进本月的设备利用率与回填值；**月采出的摊分在上面已经做完，不回头重摊** —— "
                          + "回头重摊会把刚覆盖好的采出又冲一遍）。");
            }

            // 推进：由月采出反算（年化 → AdvanceRateFrom → 回月）
            double v = MiningProgramPlan.AdvanceRateFrom(coal[i] * 12, p.WorkLineLenM, p.BenchHeightM, rho) / 12.0;
            advTot += v;

            // 设备利用率：本月总作业量(万m³) ÷ 可用台班能力
            double volM3 = coal[i] / rho + strip;                       // 万m³（采出折体积 + 剥离）
            double capThisMonth = p.Field.EquipmentCount * p.Field.EquipMonthlyCapacityWanM3
                                   * (wd[i] / Math.Max(1, p.Field.StandardWorkdays)) * avail;
            double util = capThisMonth > 1e-6 ? volM3 / capThisMonth * 100 : 0;
            utilSum += util; utilCnt++;

            bool isMaint = p.Field.MaintenanceMonth == m;
            string face = ActiveFaceFor(p, i, n);
            string label = $"{p.PlanYear}-{m:00}";

            // ── 物料流：按作业面份额 × 物料构成把月采出/月剥离拆成 O-D 流 →
            //    面上钉了去向的落位、没钉的按「运输功最小 + 库容够 + 物料兼容」贪心分配 → 逐月扣占容 ──
            int warnFrom = warnings.Count;
            // 覆盖说明放在 warnFrom 之后 —— 这样它会跟着落到本月那一行的 Warning 列上，
            // 「这一行的数是人填的」在逐月表上当场看得见，不用去翻方案级警示。
            warnings.AddRange(ovNotes);
            var flows = PlanFlowAllocator.BuildMonthFlows(p, coal[i], strip, ledger, p.PlanYear, m, label, warnings);

            var row = new MonthPeriod
            {
                Label = label, Month = m, Workdays = Math.Round(wd[i], 1),
                Flows = flows,
                // 标量同时回填一份：Flows 非空时读派生值，这里的赋值只作为清空流后的兼容兜底。
                CoalWanT = Math.Round(coal[i], 1), StripWanM3 = Math.Round(strip, 0), Ratio = Math.Round(ratio, 2),
                AdvanceM = Math.Round(v, 1), EquipUtilPct = Math.Round(util, 0),
                // 内/外排不再写死：由本月各流实际落到的去向类别聚合出来（MonthPeriod.Dump 派生自 Flows）。
                // 内排是否可选，取决于台账里有没有内排去向、以及采空区是否已形成（PlanDestination.IsOpenAt）。
                ActiveFace = face, IsMaintenance = isMaint,
                IsPeak = Math.Abs(coal[i] - peakCoal) < 1e-6,
                Warning = warnings.Count > warnFrom ? string.Join("\n", warnings.Skip(warnFrom)) : "",
            };

            cumCoal += row.CoalWanT; cumStrip += row.StripWanM3;
            row.CumCoal = Math.Round(cumCoal, 1);
            row.CumStrip = Math.Round(cumStrip, 0);
            row.CompletionPct = Math.Round(cumCoal / annualCoal * 100, 1);
            rows.Add(row);
        }
        foreach (var r in rows) p.Months.Add(r);

        p.Result = Evaluate(p, rows, advTot, utilCnt > 0 ? utilSum / utilCnt : 0, warnings);
    }

    /// <summary>
    /// 把本方案的每个计划月对上逐月配置表里的那一行。
    ///
    /// <para><b>按唯一期次键 <c>yyyy-MM</c> 对行，不按 Label</b>：本排产器写进
    /// <see cref="MonthPeriod.Label"/> 的标签<b>不带跨年进位</b>（<c>{PlanYear}-{month:00}</c>），
    /// 计划月数 &gt; 12 时 2027-01 会出现两次 —— 按它对行会把第 13 个月对到第 1 个月那一行上。</para>
    ///
    /// <para>对不上的月份返回 null（那几个月按年目标摊分），<b>并在表里确有人工覆盖时报一条</b> ——
    /// 「我填了表可是没生效」是这条链上最难查的一种失败。</para>
    /// </summary>
    private static MonthlyTargetRow?[] ResolveTargetRows(ShortTermPlan p, MonthlyTargetTable? tgt,
                                                         int n, int start, List<string> warnings)
    {
        var res = new MonthlyTargetRow?[n];

        MonthlyTargetTable table;
        if (tgt != null) table = tgt;
        else
        {
            // 会话表是懒建的（首次访问会跑一次派生）。建不出来不许把排产带走 ——
            // 排产在没有这张表的时候本来就跑得通，那是本次改动之前的行为。
            try { table = MonthlyTargetStore.Current; }
            catch (Exception ex)
            {
                warnings.Add($"◆ 取不到逐月配置表（{ex.Message}）—— 本次全部按年目标摊分，"
                           + "表里的人工覆盖一条都没生效。");
                return res;
            }
        }
        if (table.Rows.Count == 0) return res;

        int miss = 0;
        var missKeys = new List<string>();
        for (int i = 0; i < n; i++)
        {
            int m = ((start - 1 + i) % 12) + 1;
            int year = p.PlanYear + (start - 1 + i) / 12;     // 跨年进位，与 MonthlyTargetRow.PeriodKey 同口径
            string key = $"{year:0000}-{m:00}";
            res[i] = table.Find(key);
            if (res[i] == null) { miss++; if (missKeys.Count < 6) missKeys.Add(key); }
        }

        if (miss > 0 && table.ManualRowCount > 0)
            warnings.Add($"◆ 逐月配置表里没有本方案的 {miss} 个月（{string.Join("、", missKeys)}{(miss > 6 ? " …" : "")}）"
                       + " —— 这几个月按年目标摊分，表里的人工覆盖管不到它们。"
                       + "多半是表的起始月/月数与本方案不一样，去「逐月配置表」按当前参数重派一次。");
        return res;
    }

    /// <summary>
    /// 内排启用时机 —— 内排土场必须等采空区形成才能接排，这是"内排与否"的第二个前提
    /// （第一个前提是台账里有没有内排去向）。
    ///
    /// 有上游中长远来源时按上游的决策判定：内排总开关 <c>InnerDumpEnabled</c> 关着 → 全年只能外排；
    /// 计划年早于内排起转年（StartYear + InnerDumpStartYear）→ 本年也只能外排。
    /// 无上游来源时不额外限制，以台账 dump_site.start_date / status 为准。
    /// 两种情况都写一条说明进警示列表 —— 内排率为 0 必须有据可查，不能让人以为是算漏了。
    /// </summary>
    private static void ApplyInternalDumpTiming(ShortTermPlan p, PlanDumpLedger ledger, List<string> warnings)
    {
        var lt = p.SourceLongTerm;
        if (lt == null) return;

        var inner = ledger.Destinations.Where(d => d.IsInternalDump).ToList();
        if (inner.Count == 0) return;

        if (!lt.InnerDumpEnabled)
        {
            foreach (var d in inner) d.Status = "closed";
            warnings.Add($"上游进度计划「{lt.Name}」未启用内排：本年剥离全部按外排安排（{inner.Count} 个内排去向按停用处理）。");
            return;
        }

        int openYear = Math.Clamp(lt.StartYear + Math.Max(0, lt.InnerDumpStartYear), 1, 9999);
        if (p.PlanYear >= openYear) return;                      // 已到起转年，内排照常可用

        var openFrom = new DateTime(openYear, 1, 1);
        foreach (var d in inner)
            if (d.OpenFrom == null || d.OpenFrom < openFrom) d.OpenFrom = openFrom;
        warnings.Add($"内排尚未起转：上游内排起转年 {openYear}，本计划年 {p.PlanYear} 未到 —— 采空区还没形成，本年剥离只能外排（运距与成本相应偏高）。");
    }

    /// <summary>作业组织对月分布的形态因子（归一前的相对权重）。</summary>
    private static double[] DispatchShape(DispatchStrategy d, int n)
    {
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0.5 : (double)i / (n - 1);   // 0..1
            s[i] = d switch
            {
                // 集中强采：前中段抬高（早完成年目标），尾段收
                DispatchStrategy.Concentrated => 1.0 + 0.35 * Math.Sin(Math.PI * Math.Clamp(t * 1.1, 0, 1)),
                // 多面展开：接近平直，轻微中段高（多面并行稳产）
                DispatchStrategy.MultiFace => 1.0 + 0.05 * Math.Sin(Math.PI * t),
                // 均衡：完全平直
                _ => 1.0
            };
        }
        return s;
    }

    /// <summary>当月主作业面（按接续次序 + 进度推进切面）。</summary>
    private static string ActiveFaceFor(ShortTermPlan p, int i, int n)
    {
        if (p.Faces.Count == 0) return "";
        var ordered = p.Faces.OrderBy(f => f.Order).ToList();
        if (p.Dispatch == DispatchStrategy.MultiFace)
            return string.Join("+", ordered.Take(Math.Min(2, ordered.Count)).Select(f => f.Name));
        // 单/集中：按进度在接续面间切换
        int idx = (int)Math.Floor((double)i / Math.Max(1, n) * ordered.Count);
        idx = Math.Clamp(idx, 0, ordered.Count - 1);
        return ordered[idx].Name;
    }

    /// <summary>对超过月上限的月做削峰，把溢出量回摊到未触顶的月（年合计守恒）。</summary>
    private static void RedistributeCeiling(double[] coal, double ceiling)
    {
        for (int iter = 0; iter < 6; iter++)
        {
            double overflow = 0;
            var room = new List<int>();
            for (int i = 0; i < coal.Length; i++)
            {
                if (coal[i] > ceiling) { overflow += coal[i] - ceiling; coal[i] = ceiling; }
                else if (coal[i] < ceiling - 1e-6) room.Add(i);
            }
            if (overflow < 1e-6 || room.Count == 0) break;
            double each = overflow / room.Count;
            foreach (var i in room) coal[i] = Math.Min(ceiling, coal[i] + each);
        }
    }

    private static ShortTermResult Evaluate(ShortTermPlan p, List<MonthPeriod> rows, double advTot, double avgUtil,
                                            List<string>? warnings = null)
    {
        double totCoal = rows.Sum(z => z.CoalWanT);
        double totStrip = rows.Sum(z => z.StripWanM3);
        double completion = p.AnnualCoalTargetWanT > 1e-6 ? totCoal / p.AnnualCoalTargetWanT * 100 : 0;
        double avgRatio = totCoal > 1e-6 ? totStrip / totCoal : 0;

        double[] coal = rows.Select(z => z.CoalWanT).ToArray();
        double[] ratio = rows.Select(z => z.Ratio).ToArray();
        double outCv = Cv(coal), ratCv = Cv(ratio);
        var peak = rows.OrderByDescending(z => z.CoalWanT).First();
        double balance = Math.Clamp(1 - outCv, 0, 1);
        double monthlyCoalAvg = coal.Length > 0 ? coal.Average() : 0;
        double prepMonths = p.Mineable.PreparedMonths(monthlyCoalAvg);

        bool completionOk = Math.Abs(completion - 100) <= p.CompletionTolerancePct + 1e-6;
        bool ceilOk = p.MonthlyCoalCeilingWanT <= 0 || coal.Max() <= p.MonthlyCoalCeilingWanT + 1e-6;
        bool ratioOk = p.RatioCeiling <= 0 || ratio.Max() <= p.RatioCeiling + 1e-6;
        // ── 三量保有：区分「没录数据」与「不达标」 ──
        //  此前两者被当成一件事：Base.Mineable.PreparedReserveWanT 全仓无人写 ⇒ 恒 0 ⇒
        //  prepOk 恒 false ⇒ ok 恒 false ⇒ 每一套方案都判不过，而每项分指标看上去都正常。
        //  连锁后果：ShortTermComparer:41 的 `if (Result.Ok && ...)` 分支永不进 ⇒
        //  比选里的"可行方案优先"静默失效，每次都落到无条件兜底。
        var prepState = p.Mineable.PreparedReserveWanT <= 1e-9
            ? PrepCheckState.NotEntered
            : (prepMonths >= p.MinPreparedMonths - 1e-6 ? PrepCheckState.Pass : PrepCheckState.Fail);

        warnings ??= new List<string>();
        if (prepState == PrepCheckState.NotEntered)
            warnings.Add($"◆ 备采储量没有数据 ⇒ **三量保有未校核**（下限 {p.MinPreparedMonths:0.#} 个月）。"
                       + "这不是「不达标」，是判不了 —— 备采储量由几何算出（走「量驱动采剥接续」那条链），"
                       + "或在基础约束里录入三量。");

        // 没数据的那一项【不参与】硬约束总判定 —— 一个永远拿不到数的闸门恒关，等于把功能锁死
        bool ok = completionOk && ceilOk && ratioOk && prepState != PrepCheckState.Fail;

        // ── 物料/去向维聚合（全部由逐月 Flows 得来，不另写口径）──
        double dumped = rows.SelectMany(z => z.Flows).Where(f => f.IsDumping).Sum(f => f.DumpWanM3);  // 全期排弃占容 万m³
        double innerDumped = rows.SelectMany(z => z.Flows).Where(f => f.IsInternalDump).Sum(f => f.DumpWanM3);
        double work = rows.Sum(z => z.TransportWorkWanTKm);                 // 全期运输功 万t·km
        double tonn = rows.SelectMany(z => z.Flows).Sum(f => f.TonnageWanT);// 全期吨量 万t

        return new ShortTermResult
        {
            TotalCoalWanT = Math.Round(totCoal, 0),
            TotalStripWanM3 = Math.Round(totStrip, 0),
            CompletionRatePct = Math.Round(completion, 1),
            AvgRatio = Math.Round(avgRatio, 2),
            PeakMonthCoalWanT = Math.Round(peak.CoalWanT, 1),
            PeakMonthLabel = peak.Label,
            OutputCv = Math.Round(outCv, 3),
            RatioCv = Math.Round(ratCv, 3),
            AvgEquipUtilPct = Math.Round(avgUtil, 0),
            AdvanceTotalM = Math.Round(advTot, 0),
            PreparedMonths = Math.Round(prepMonths, 1),
            BalanceCoef = Math.Round(balance, 2),
            Ok = ok,
            PrepState = prepState,

            TotalDumpedWanM3 = Math.Round(dumped, 0),
            InternalDumpPct = dumped > 1e-9 ? Math.Round(innerDumped / dumped * 100, 1) : 0,
            TransportWorkWanTKm = Math.Round(work, 0),
            WeightedAvgHaulKm = tonn > 1e-9 ? Math.Round(work / tonn, 2) : 0,
            DumpSourceText = PlanDestinationCatalog.SourceText,
            Warnings = warnings ?? new List<string>(),
        };
    }

    private static double Cv(double[] a)
    {
        if (a.Length == 0) return 0;
        double mean = a.Average();
        if (mean <= 1e-9) return 0;
        double var = a.Select(x => (x - mean) * (x - mean)).Average();
        return Math.Sqrt(var) / mean;
    }

    // ── 多方案生成：作业组织 × 工作历方案 正交派生 ──

    public readonly record struct DispatchSpec(string Label, DispatchStrategy Strategy);
    public readonly record struct CalendarSpec(string Label, CalendarScenario Scenario);

    public static List<DispatchSpec> DefaultDispatches() => new()
    {
        new("均衡型", DispatchStrategy.Balanced),
        new("多面展开", DispatchStrategy.MultiFace),
        new("集中强采", DispatchStrategy.Concentrated),
    };

    public static List<CalendarSpec> DefaultCalendars() => new()
    {
        new("标准", CalendarScenario.Standard),
        new("抢产", CalendarScenario.Push),
        new("保守", CalendarScenario.Conservative),
    };

    /// <summary>
    /// 作业组织 × 工作历方案 正交生成多套月度计划（笛卡尔积），每套独立排产。
    /// 单套 = 只给一种组织 + 一种工作历；多套 = 多组织 × 多工作历 → 联合对比。
    /// </summary>
    public static List<ShortTermPlan> GenerateVariants(ShortTermPlan basePlan,
        IReadOnlyList<DispatchSpec> dispatches, IReadOnlyList<CalendarSpec> calendars, bool schedule = true)
    {
        var result = new List<ShortTermPlan>();
        foreach (var d in dispatches)
            foreach (var c in calendars)
            {
                var v = basePlan.Clone();
                v.Name = $"{Trunk(basePlan.Name)}·{d.Label}{c.Label}";
                v.Note = ""; v.Participate = true;
                v.Dispatch = d.Strategy; v.Calendar = c.Scenario;
                if (schedule) Schedule(v);
                result.Add(v);
            }
        return result;
    }

    private static string Trunk(string name)
    {
        if (string.IsNullOrEmpty(name)) return "月度计划";
        int i = name.IndexOf('·');
        return i > 0 ? name[..i] : name;
    }

    /// <summary>
    /// 在一套基础约束之上，按 作业组织 × 工作历方案 的笛卡尔积生成候选月度计划，每套独立排产。
    /// </summary>
    public static List<ShortTermPlan> Generate(ShortTermBase basis,
        IReadOnlyList<DispatchSpec> dispatches, IReadOnlyList<CalendarSpec> calendars, bool schedule = true)
    {
        bool showCal = calendars.Count > 1;
        var result = new List<ShortTermPlan>();
        foreach (var d in dispatches)
            foreach (var c in calendars)
            {
                string name = $"{d.Label}" + (showCal ? $"·{c.Label}" : "");
                var p = basis.NewCandidate(d.Strategy, c.Scenario, name);
                if (schedule) Schedule(p);
                result.Add(p);
            }
        return result;
    }
}
