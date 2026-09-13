// 忠实移植自原 PitMine3D Modules/TaskLib/Scheduling/MonthToDayScheduler.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;      // ProcessType / TaskEnumLabels.Label

namespace PitMine3D.Kylin.TaskLib.Scheduling;

/// <summary>一个面在月计划里的量与约束。调用方从短期月计划 + 编组台账填好交进来。</summary>
public sealed class FaceMonthDemand
{
    public string Zone { get; init; } = "";
    public string UnitId { get; init; } = "";
    public string Material { get; init; } = "";
    public string Destination { get; init; } = "";
    public bool IsOre { get; init; }

    /// <summary>本月该面的采装量（m³ 实方）。</summary>
    public double MonthM3 { get; init; }

    /// <summary>日能力上限（m³/日）＝ 编组班产 × 有效工时 × 班数。≤0 视为无上限并记提示。</summary>
    public double DailyCapM3 { get; init; }

    /// <summary>
    /// 逐日权重（可选）：第 i 个作业日相对能干多少。传了就**按权重摊**，不再等量均摊。
    ///
    /// <para><b>这是给 <c>DayCapacityCalendar</c> 留的口子</b>：那一层把
    /// 「今天这台铲上午定修」「下午放炮清场 40 分钟」「交接班损失」「近期非计划故障率」
    /// 都折进当天的可用工时，算出的逐日权重比"每天一样"精确得多。
    /// 这里只负责按它给的比例摊，<b>不自己再算一套可用工时</b> ——
    /// 两套口径并存的后果是两个数都对不上，谁也说不清哪个是真的。</para>
    ///
    /// <para>长度必须与作业日一致；不一致或全零就忽略，退回等量均摊并记提示。</para>
    /// </summary>
    public IReadOnlyList<double>? DailyWeights { get; init; }

    /// <summary>爆堆存量（m³ 实方）：开月时脚下已经爆好、可以直接装的量。</summary>
    public double OpeningMuckM3 { get; init; }

    /// <summary>这个面要不要爆破。煤层直接采、表土剥离用铲挖的面填 false。</summary>
    public bool NeedsBlast { get; init; } = true;
}

/// <summary>工艺参数。全部集中在这里，改一处即改整条分解链。</summary>
public sealed class ProcessParams
{
    /// <summary>爆破到可采的等待（日）：通风、检查、清撬。</summary>
    public int BlastLeadDays { get; set; } = 1;

    /// <summary>穿孔到爆破的等待（日）：装药、连线、警戒。</summary>
    public int DrillLeadDays { get; set; } = 1;

    /// <summary>一次爆破供几天采装。批次太小则天天放炮不现实，太大则爆堆压库。</summary>
    public int BlastCoversDays { get; set; } = 3;

    /// <summary>爆破方量 ÷ 孔米（m³/m）：单位孔米能爆下来多少方。</summary>
    public double M3PerDrillMeter { get; set; } = 38.0;

    /// <summary>残余膨胀 Kr：剥离实方 × Kr = 排土场承接占容方。</summary>
    public double Kr { get; set; } = 1.129;

    /// <summary>一天最多安排几次爆破（现场警戒与停产窗口有限）。</summary>
    public int MaxBlastsPerDay { get; set; } = 2;
}

/// <summary>分解出来的一条日工序任务。</summary>
public sealed class DayProcessOrder
{
    public DateTime Date { get; init; }
    public string Zone { get; init; } = "";
    public string UnitId { get; init; } = "";
    public Domain.ProcessType Process { get; init; }

    /// <summary>工程量。采装/排土是 m³，穿孔是**孔米**（m），爆破是**爆破方量** m³。</summary>
    public double Quantity { get; init; }
    public string Unit { get; init; } = "m³";

    public string Material { get; init; } = "";
    public string Destination { get; init; } = "";

    /// <summary>这一条为什么排在这一天（可读的依据，不是调试文本）。</summary>
    public string Basis { get; init; } = "";

    public string Caption => $"{Date:MM-dd} {Zone} {Process.Label()} {Quantity:0.#} {Unit}";
}

/// <summary>分解结果。</summary>
public sealed class MonthDecomposition
{
    public List<DayProcessOrder> Orders { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Violations { get; } = new();

    public IEnumerable<DayProcessOrder> OnDay(DateTime d)
        => Orders.Where(o => o.Date.Date == d.Date);

    public IEnumerable<DayProcessOrder> OfProcess(Domain.ProcessType p)
        => Orders.Where(o => o.Process == p);

    /// <summary>某个面某工序的月合计。</summary>
    public double Total(string zone, Domain.ProcessType p)
        => Orders.Where(o => o.Process == p
                          && string.Equals(o.Zone, zone, StringComparison.OrdinalIgnoreCase))
                 .Sum(o => o.Quantity);
}

/// <summary>
/// 月度计划 → 日工序任务的分解。
///
/// <para><b>现在这条线只做了"月量 ÷ 作业日"的平均摊，而且只摊给采装面</b> ——
/// 穿孔和爆破根本没从计划里生出来（界面上"无穿孔计划 / 无爆破事件"就是这么来的）。
/// 结果是任务编制拿到的每天都一样，工序链是断的。</para>
///
/// <para><b>这一层把它补齐，分五步，每一步单独可判</b>：</para>
/// <list type="number">
///   <item><b>S1 有效作业日</b>：月量只摊到日历给的有效日上，不摊到停产日。</item>
///   <item><b>S2 受限均摊</b>：先均摊，超过日能力上限的削平，削下来的回摊给还有余量的日子；
///   迭代到收敛。摊不完就是**月度欠产**，如实报出来不静默丢。</item>
///   <item><b>S3 爆破反推</b>：按"一次爆破供 N 天"成批，爆破日 = 首个受供采装日 − 爆破等待期。</item>
///   <item><b>S4 穿孔反推</b>：孔米 = 爆破方量 ÷ 单位孔米爆破量，穿孔日 = 爆破日 − 装药等待期。</item>
///   <item><b>S5 爆堆互锁</b>：任何一天的累计采装量不得超过"开月存量 + 到当日为止累计已爆量"。
///   违反就是**采装超前于爆破**，报出来。</item>
/// </list>
///
/// <para><b>不做的事</b>：不改月总量（分解只重排时间，不重定量）、不猜缺失的能力上限、
/// 不把欠产悄悄摊平。这三条任一破了，分解出来的日计划就不能回溯到月计划，那就白做了。</para>
/// </summary>
public static class MonthToDayScheduler
{
    /// <summary>
    /// 分解。<paramref name="workdays"/> 是本月**有效作业日**（调用方从 WorkCalendar 取）。
    /// </summary>
    public static MonthDecomposition Decompose(IReadOnlyList<FaceMonthDemand> demands,
                                               IReadOnlyList<DateTime> workdays,
                                               ProcessParams? prm = null)
    {
        var p = prm ?? new ProcessParams();
        var res = new MonthDecomposition();

        if (demands == null || demands.Count == 0)
        { res.Notes.Add("月计划里没有面 —— 没有需求就没有可分解的东西（不是漏算）。"); return res; }

        var days = (workdays ?? Array.Empty<DateTime>())
                   .Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0)
        { res.Violations.Add("本月**没有有效作业日**（日历里全是停产日？）⇒ 月量一天都摊不下去。"); return res; }

        foreach (var f in demands.Where(f => f.MonthM3 > 1e-6))
        {
            // ── S2 受限均摊 ──
            var daily = LevelledSplit(f, days, res);
            if (daily.All(v => v <= 1e-9)) continue;

            // ── 采装：逐日下单 ──
            for (int i = 0; i < days.Count; i++)
            {
                if (daily[i] <= 1e-9) continue;
                res.Orders.Add(new DayProcessOrder
                {
                    Date = days[i], Zone = f.Zone, UnitId = f.UnitId,
                    Process = Domain.ProcessType.Load,
                    Quantity = Math.Round(daily[i]), Unit = "m³",
                    Material = f.Material, Destination = f.Destination,
                    Basis = f.DailyCapM3 > 1e-9 && daily[i] >= f.DailyCapM3 - 1e-6
                        ? $"受限于日能力上限 {f.DailyCapM3:0} m³/日"
                        : $"月量 {f.MonthM3:0} m³ 在 {days.Count} 个有效作业日上受限均摊",
                });

                // ── 排土：剥离才进排土场；同日承接，量按 Kr 折成占容方 ──
                if (!f.IsOre)
                    res.Orders.Add(new DayProcessOrder
                    {
                        Date = days[i], Zone = f.Destination.Length > 0 ? f.Destination : "排土场",
                        UnitId = f.UnitId, Process = Domain.ProcessType.Dump,
                        Quantity = Math.Round(daily[i] * p.Kr), Unit = "m³占容",
                        Material = f.Material, Destination = f.Destination,
                        Basis = $"{f.Zone} 当日剥离 {daily[i]:0} m³实方 × Kr{p.Kr:0.###}",
                    });
            }

            // ── S3/S4 爆破与穿孔反推 ──
            if (f.NeedsBlast) BlastAndDrill(f, days, daily, p, res);

            // ── S5 爆堆互锁 ──
            CheckMuckBalance(f, days, daily, p, res);
        }

        res.Notes.Add($"分解完成：{demands.Count} 个面 × {days.Count} 个有效作业日 ⇒ {res.Orders.Count} 条日工序任务。"
                    + "分解只重排**时间**，不改月总量 —— 每个面的日量之和恒等于它的月量（欠产除外，已单列）。");
        return res;
    }

    /// <summary>
    /// S2 受限均摊：均摊 → 削平超上限的 → 把削下来的回摊给还有余量的日子 → 迭代。
    ///
    /// <para><b>为什么不能一次除完</b>：日能力上限是硬的（编组班产 × 工时 × 班数）。
    /// 直接 月量÷日数 得到的日目标可能超上限，那个数排产器根本执行不了，
    /// 会在每一天都产生"当日欠产"，看着像天天没干完，其实是计划本身就排不下。</para>
    /// </summary>
    private static double[] LevelledSplit(FaceMonthDemand f, List<DateTime> days, MonthDecomposition res)
    {
        int n = days.Count;
        var v = new double[n];
        double cap = f.DailyCapM3;

        if (cap <= 1e-9)
        {
            // 上限未知 ⇒ 退回纯均摊，并**说清楚**：这时候排不排得下没人知道
            double flat = f.MonthM3 / n;
            for (int i = 0; i < n; i++) v[i] = flat;
            res.Notes.Add($"「{f.Zone}」没有日能力上限（编组班产台账没给）⇒ 退回纯均摊 {flat:0} m³/日。"
                        + "**排不排得下没有校核** —— 补上编组班产之后这一面才会做削峰回摊。");
            return v;
        }

        // 逐日权重：有就按它摊（DayCapacityCalendar 把检修/清场/故障率折进去了），
        // 没有就等量。权重长度对不上或全零一律忽略并说明 —— 静默退回是最难查的那种。
        var w = new double[n];
        bool weighted = false;
        if (f.DailyWeights is { Count: > 0 })
        {
            if (f.DailyWeights.Count != n)
                res.Notes.Add($"「{f.Zone}」逐日权重给了 {f.DailyWeights.Count} 个，作业日有 {n} 天 ⇒ 对不上，退回等量均摊。");
            else if (f.DailyWeights.Sum() <= 1e-9)
                res.Notes.Add($"「{f.Zone}」逐日权重全是 0（整月一天都干不了？）⇒ 退回等量均摊。");
            else
            {
                for (int i = 0; i < n; i++) w[i] = Math.Max(0, f.DailyWeights[i]);
                weighted = true;
                res.Notes.Add($"「{f.Zone}」按**逐日能力权重**摊（检修/清场/故障率已折入），不是等量均摊。");
            }
        }
        if (!weighted) for (int i = 0; i < n; i++) w[i] = 1;

        double remain = f.MonthM3;
        var free = Enumerable.Range(0, n).Where(i => w[i] > 1e-9).ToList();   // 权重为 0 的日子不摊
        int guard = 0;
        while (remain > 1e-6 && free.Count > 0 && guard++ < 64)
        {
            double wsum = free.Sum(i => w[i]);
            if (wsum <= 1e-9) break;
            var stillFree = new List<int>();
            double placed = 0;
            foreach (int i in free)
            {
                double room = cap - v[i];
                double add = Math.Min(room, remain * w[i] / wsum);
                v[i] += add; placed += add;
                if (cap - v[i] > 1e-6) stillFree.Add(i);
            }
            remain -= placed;
            free = stillFree;
            if (placed <= 1e-9) break;                   // 一点都放不进去了
        }

        if (remain > 1e-6)
            res.Violations.Add($"⚠「{f.Zone}」月量排不下：还剩 {remain:0} m³ 无处安放"
                             + $"（{n} 个作业日 × 日上限 {cap:0} = {n * cap:0} m³ < 月量 {f.MonthM3:0} m³）。"
                             + "这是**计划本身超能力**，不是执行问题 —— 要么加设备、要么减月量、要么加作业日。");
        return v;
    }

    /// <summary>
    /// S3/S4：按批爆破，孔米按爆破方量反推，各自前移等待期。
    ///
    /// <para><b>两件被判据抓出来的事</b>：</para>
    /// <list type="bullet">
    ///   <item>等待期前移之后，头几批的爆破日会落到**月首之前** —— 第一版直接就那么下单了，
    ///   等于把活悄悄排到本月计划窗口之外（那是上个月的事）。现在改成：落到月外的批次
    ///   一律**不在本月下单**，改为记入"开月必须备好的爆堆"，备不够就报出来。</item>
    ///   <item>爆破日必须落在**作业日**上 —— 停产日放不了炮。往前吸附到最近的作业日；
    ///   吸不到就同样归入开月备料。</item>
    /// </list>
    /// </summary>
    private static void BlastAndDrill(FaceMonthDemand f, List<DateTime> days, double[] daily,
                                      ProcessParams p, MonthDecomposition res)
    {
        int batch = Math.Max(1, p.BlastCoversDays);
        var workset = new HashSet<DateTime>(days);
        double preMonthNeed = 0;

        for (int i = 0; i < days.Count; i += batch)
        {
            double vol = 0;
            for (int k = i; k < Math.Min(i + batch, days.Count); k++) vol += daily[k];
            if (vol <= 1e-9) continue;

            // 首个受供采装日往前推爆破等待期，再往前吸附到最近的作业日
            DateTime want = days[i].AddDays(-Math.Max(0, p.BlastLeadDays));
            DateTime? blastDay = SnapBack(want, workset, days[0]);

            if (blastDay == null)
            {
                // 排不进本月 ⇒ 这一批的料必须在开月之前就爆好
                preMonthNeed += vol;
                continue;
            }

            res.Orders.Add(new DayProcessOrder
            {
                Date = blastDay.Value, Zone = f.Zone, UnitId = f.UnitId,
                Process = Domain.ProcessType.Blast,
                Quantity = Math.Round(vol), Unit = "m³",
                Material = f.Material,
                Basis = $"供 {days[i]:MM-dd} 起 {Math.Min(batch, days.Count - i)} 天采装共 {vol:0} m³；"
                      + $"爆后等待 {p.BlastLeadDays} 天（通风/检查/清撬）"
                      + (blastDay.Value != want ? $"；{want:MM-dd} 非作业日，前移到 {blastDay:MM-dd}" : ""),
            });

            double meters = p.M3PerDrillMeter > 1e-9 ? vol / p.M3PerDrillMeter : 0;
            if (meters > 1e-9)
            {
                DateTime dWant = blastDay.Value.AddDays(-Math.Max(0, p.DrillLeadDays));
                DateTime? drillDay = SnapBack(dWant, workset, days[0]);
                if (drillDay != null)
                    res.Orders.Add(new DayProcessOrder
                    {
                        Date = drillDay.Value, Zone = f.Zone, UnitId = f.UnitId,
                        Process = Domain.ProcessType.Drill,
                        Quantity = Math.Round(meters), Unit = "m",
                        Material = f.Material,
                        Basis = $"爆破方量 {vol:0} m³ ÷ {p.M3PerDrillMeter:0.#} m³/孔米；"
                              + $"爆前留 {p.DrillLeadDays} 天装药连线"
                              + (drillDay.Value != dWant ? $"；{dWant:MM-dd} 非作业日，前移到 {drillDay:MM-dd}" : ""),
                    });
                else
                    res.Violations.Add($"⚠「{f.Zone}」{blastDay:MM-dd} 那一炮的**穿孔排不进本月**"
                                     + $"（要 {meters:0} 孔米，爆前须留 {p.DrillLeadDays} 天）⇒ 得在上月末打完。");
            }
        }

        if (preMonthNeed > 1e-6)
        {
            double gap = preMonthNeed - f.OpeningMuckM3;
            string head = $"「{f.Zone}」头几批采装靠的是**开月爆堆**：需要 {preMonthNeed:0} m³"
                        + $"（爆后等待 {p.BlastLeadDays} 天，这几炮排不进本月，属上月末的活），"
                        + $"实有开月存量 {f.OpeningMuckM3:0} m³";
            if (gap > 1e-6)
                res.Violations.Add($"⚠ {head} ⇒ **缺 {gap:0} m³**。"
                                 + "要么上月末补一次预爆、要么把本月头几天的采装往后推。");
            else
                res.Notes.Add($"{head} ⇒ 够用（余 {-gap:0} m³）。");
        }

        // 一天最多几次爆破 —— 超了现场警戒排不开
        foreach (var g in res.OfProcess(Domain.ProcessType.Blast).GroupBy(o => o.Date))
            if (g.Count() > p.MaxBlastsPerDay && !res.Violations.Any(x => x.Contains($"{g.Key:MM-dd} 排了")))
                res.Violations.Add($"⚠ {g.Key:MM-dd} 排了 {g.Count()} 次爆破，超过每日上限 {p.MaxBlastsPerDay} 次"
                                 + "（警戒与停产窗口排不开）⇒ 需要错开批次或并炮。");
    }

    /// <summary>
    /// 往前吸附到最近的作业日（含当天）。早于 <paramref name="floor"/> 就吸不到了，返回 null。
    /// <para>停产日放不了炮、打不了孔 —— 排在那天等于排了一个执行不了的任务。</para>
    /// </summary>
    private static DateTime? SnapBack(DateTime want, HashSet<DateTime> workdays, DateTime floor)
    {
        for (var d = want.Date; d >= floor.Date; d = d.AddDays(-1))
            if (workdays.Contains(d)) return d;
        return null;
    }

    /// <summary>
    /// S5 爆堆互锁：任一天的累计采装 ≤ 开月存量 + 到当日为止累计已爆。
    /// <para>破了就是**采装超前于爆破** —— 图上看不出来，到现场就是铲停在没爆的岩体前面。</para>
    /// </summary>
    private static void CheckMuckBalance(FaceMonthDemand f, List<DateTime> days, double[] daily,
                                         ProcessParams p, MonthDecomposition res)
    {
        if (!f.NeedsBlast) return;
        var blasts = res.OfProcess(Domain.ProcessType.Blast)
                        .Where(o => string.Equals(o.Zone, f.Zone, StringComparison.OrdinalIgnoreCase))
                        .ToList();

        double cumLoad = 0;
        for (int i = 0; i < days.Count; i++)
        {
            cumLoad += daily[i];
            // 到今天为止**已经可采**的爆破量（爆破日 + 等待期 ≤ 今天）
            double ready = f.OpeningMuckM3 + blasts
                .Where(b => b.Date.AddDays(p.BlastLeadDays) <= days[i])
                .Sum(b => b.Quantity);

            if (cumLoad > ready + 1e-6)
            {
                res.Violations.Add($"⚠「{f.Zone}」{days[i]:MM-dd} **采装超前于爆破**："
                                 + $"累计要采 {cumLoad:0} m³，而到当日可采的爆堆只有 {ready:0} m³"
                                 + $"（开月存量 {f.OpeningMuckM3:0} + 已爆可采部分）⇒ 缺 {cumLoad - ready:0} m³。"
                                 + "要么把爆破前移、要么把采装后移、要么开月先补一次爆破。");
                return;   // 一个面报一次就够，逐日刷屏没有额外信息
            }
        }
    }
}
