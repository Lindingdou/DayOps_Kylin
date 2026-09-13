// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ShortTermLink.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PitMine3D.Kylin.TaskLib.Domain;     // ProcessType / MaterialCatalog / SinkNode

namespace PitMine3D.Kylin.TaskLib.Engine;

/// <summary>
/// 上承短期(月度)生产计划——业务数据流：月采出/月剥离 ÷ 有效作业日 × 作业面份额 = 各面「当日目标」喂引擎。
///
/// 程序上**不硬依赖** PlanLib（无项目引用）：两层只在业务/数据层关联，编译期互不依赖。
/// 运行时按约定软读取 PlanLib.ShortTerm.ShortTermSchemeStore.Confirmed（若 PlanLib 已加载且已确定月度方案），
/// 读不到则回落样例日目标。日后接共享数据层(DB/快照)替换本反射读取即可，两模块仍互不引用。
///
/// ── 本轮升级 ──
/// ① 月计划作业面上若带了 **去向/物料/运距**（DestinationId·DestinationName·MaterialCode·HaulDistanceKm），
///    一并带下来填进 FaceInput——月计划定的是"采哪、采多少、送哪"，日计划不该再猜去向。
///    读不到一律静默跳过：反射全程容错是本设计的底线。
/// ② 煤密度不再写死 1.4：读不到 PlanLib 常量时取 <see cref="MaterialCatalog"/> 的煤密度（1.35），
///    口径统一到物料本体，避免"同一批煤在两个模块换算出两个吨数"。
/// ③ 修掉「所有排土面被赋予同一个全矿日剥离量」的老 bug（N 个排土面 ⇒ 剥离量凭空放大 N 倍）：
///    日剥离量改为一份"待分配供给"，优先交 <see cref="FlowAssigner"/> 按运输功最小分配，
///    退而按各排土面所属汇的**剩余库容占比**分摊，再退才等分——总量恒守恒。
/// </summary>
public static class ShortTermLink
{
    private const string Fallback = "（未确定短期月度方案，用样例日目标；在「短期生产计划 · 确定月度计划」后自动接入）";

    /// <summary>
    /// 上一次装配用的是不是真数据（false = 走了兜底）。
    /// <para><b>链路体检读它，不读来源文案</b> —— 文案一改就静默抠空，
    /// 而抠空之后「兜底」会被当成「真实」，体检朝着让人放心的方向失效。</para>
    /// </summary>
    public static bool LastFromLedger { get; private set; }


    /// <summary>本包自用校核码（ViolationCodes 属共享契约、本包不改；建议后续并入其中）。</summary>
    private const string CodeStripSplit = "剥离分摊";

    /// <summary>当日目标的分摊依据（按逐日能力加权 / 退回平均摊）。</summary>
    private const string CodeDaySplit = "当日目标分摊";

    /// <summary>按短期月计划改写 cfg 各面 DayTargetM3（含可选去向/物料/运距）；返回来源文案（供 UI 显示）。</summary>
    /// <param name="violations">可选：分摊依据/口径提示写入此表（Info 级），不传则只体现在返回文案里。</param>
    public static string ApplyToConfig(ExploderConfig cfg, List<PlanViolation>? violations = null)
    {
        double dens0 = Math.Max(0.1, Density());
        DateTime day0;
        try { day0 = ProjectScope.WorkDate.Date; } catch { day0 = DateTime.Today; }
        var week0 = WeekTargetLink.Resolve(day0, dens0);

        // ★ 周目标**不以月度方案为前提**（WK8，2026-08-20）。
        //
        //   月度方案读不到（没确定、或 PlanLib 没加载）时，原来这里直接 return Fallback ——
        //   于是"这一周要干多少"就算已经下达过，也一个字都用不上：周这一层在
        //   最需要它的那个状态（月计划还没确定、活照干）下**恰好是死的**。
        //   周目标是人给的量，本来就不需要月计划才能拆到日。
        object? plan = ReadConfirmedPlan();
        object? mp0 = null;
        if (plan != null && Prop(plan, "Months") is IList ms0 && ms0.Count > 0)
            mp0 = PickMonth(ms0, MonthOf(cfg.DateLabel));

        if (mp0 == null)
            return week0.HasTarget
                ? ApplyWeekOnly(cfg, week0, violations)
                : SetFallback();

        if (plan == null) { LastFromLedger = false; return Fallback; }
        if (Prop(plan, "Months") is not IList months || months.Count == 0) { LastFromLedger = false; return Fallback; }

        object? mp = PickMonth(months, MonthOf(cfg.DateLabel));
        if (mp == null) { LastFromLedger = false; return Fallback; }

        double coal = GetD(mp, "CoalWanT");
        double strip = GetD(mp, "StripWanM3");
        double workdays = ResolveWorkdays(cfg, GetD(mp, "Workdays"), violations, out string workdayBasis);
        double density = Math.Max(0.1, Density());

        double dayCoalM3 = coal * 1e4 / density / workdays;   // 万t → t → m³ → /日
        double dayRockM3 = strip * 1e4 / workdays;            // 万m³ → m³ → /日

        // ── 周目标优先（WK7/WK8，2026-08-20 补）─────────────────────────────
        //  下过周目标就走「周剩余量 → 今天」，没下过才走上面那两行「月量 ÷ 作业日」。
        //  两条路的数不一样，**来源文案必须写明走的是哪一条** —— 看的人要能分清。
        //  与月计划的差额不拦、不改月计划：拦下来现场就会去改月计划把差额抹掉，
        //  "计划与实际差了多少"这件事就永远查不到了（WK7）。
        DateTime today;
        try { today = ProjectScope.WorkDate.Date; } catch { today = DateTime.Today; }
        var week = WeekTargetLink.Resolve(today, density);

        // ── 采装面：日目标 ──
        //  两条路，单元路优先：
        //   ★ 单元路：本面所绑采掘单元的**剩余量** ÷ 作业日。份额从"人填的百分比"变成
        //     "图上那个体还剩多少方"算出来的比例，且顺带把备采储量带出来（三量从此有数）。
        //   · 份额路：月采出 ÷ 作业日 × SharePct（老路，永远算得出，但不知道"采哪"）。
        //  对不上一律整条退回份额路，绝不混用（见 UnitPlanLink 纪律①）。
        var loadFaces = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();
        var planFaces = ReadPlanFaces(plan);
        var matched = loadFaces.Select((f, i) => MatchPlanFace(planFaces, f, i)).ToList();

        var unitPlan = UnitPlanLink.Resolve(cfg, MonthKey(cfg));
        double[] shares = unitPlan.Usable
            ? UnitShares(loadFaces, unitPlan)
            : FaceShares(matched, planFaces, loadFaces.Count);

        // ── 月量 → 当日目标：按**这一天真正能干多少**加权，不是整月平均 ────────────
        //  平均摊（月量 ÷ 作业日）把每天当成一模一样：今天这台电铲上午定修、今天下午放炮清场、
        //  这个面备采只够两天 —— 三件事对当天目标毫无影响。计划排满、现场干不出来，
        //  月末回头是一堆"当日欠产"，而每一天当时看着都正常。
        //  见 DayCapacityCalendar：当天权重 = 班产 × 当天可用工时（扣检修/爆破/交接）× 设备可用率。
        //  周路：把"要摊的总量"从月量换成**本周剩余量**，把"摊的窗口"从整月换成**本周剩余作业日**。
        //  权重口径一个字没变（DayCapacityCalendar：检修/爆破清场/交接/设备可用率）——
        //  换的只是分子与分母的范围。
        string dayBasis;
        double dayRatio = 0;

        if (week.HasTarget && !week.TodayIsWorkday)
        {
            // 下过周目标、而今天不在本周作业日里 ⇒ **当日目标就是 0**。
            // 不能退回月路：月路那条在"今天不是作业日"时会整条退成「月量÷作业日」平均摊，
            // 于是一个本来不出勤的日子照样排出一天的活，甘特上看不出任何异常。
            foreach (var f in loadFaces) f.DayTargetM3 = 0;
            dayRockM3 = 0;
            dayBasis = "当日目标 0 —— " + week.Label;
            violations?.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Info,
                Code = CodeDaySplit,
                Message = dayBasis,
            });
        }
        else if (week.Usable)
        {
            //  周路：把"要摊的总量"从月量换成**本周剩余量**，把"摊的窗口"从整月换成**本周剩余作业日**。
            //  权重口径一个字没变（DayCapacityCalendar：检修/爆破清场/交接/设备可用率）——
            //  换的只是分子与分母的范围。
            dayBasis = ApplyDailyWeights(cfg, loadFaces, shares,
                                         week.RemainCoalM3, week.RemainCoalM3 / week.RemainWorkdays.Count,
                                         week.RemainWorkdays, violations, out dayRatio);

            // 剥离那条腿跟着同一份现场状况走：同一天的检修/爆破/可用率对采装与剥离是同一件事。
            // 权重不可用时退回「剩余量 ÷ 剩余作业日」等分（WK6）。
            dayRockM3 = dayRatio > 1e-12
                ? week.RemainStripM3 * dayRatio
                : week.RemainStripM3 / week.RemainWorkdays.Count;
        }
        else
        {
            dayBasis = ApplyDailyWeights(cfg, loadFaces, shares, coal * 1e4 / density, dayCoalM3,
                                         null, violations, out dayRatio);
        }

        int broughtDest = 0, broughtHaul = 0, broughtMat = 0;
        for (int i = 0; i < loadFaces.Count; i++)
        {
            if (matched[i] == null) continue;
            ApplyPlanFaceExtras(cfg, loadFaces[i], matched[i]!, violations, ref broughtDest, ref broughtHaul, ref broughtMat);
        }

        // 单元路顺带把备采储量带下来：单元剩余量就是这个面脚下还有多少方。
        // **只补没录过的面**——人在台账上手填过的数是实测/实盘，不许被模型量顶掉。
        string unitBasis = unitPlan.Usable
            ? ApplyUnitReserves(loadFaces, unitPlan, violations)
            : "";

        // ── 排土面：日剥离量作为一份待分配供给（不再每面同量）──
        string dumpBasis = DistributeStrip(cfg, dayRockM3, violations);

        string extra = broughtDest > 0 || broughtHaul > 0 || broughtMat > 0
            ? $" · 月计划带回 {broughtDest} 面去向"
              + (broughtHaul > 0 ? $"/{broughtHaul} 面运距" : "")
              + (broughtMat > 0 ? $"/{broughtMat} 面物料" : "")
            : " · 月计划未带去向（日计划侧自行分配）";

        LastFromLedger = true;

        // 走的是哪一条路必须写在最前面：周路与月路的当日目标不是一个数，
        // 而两者都"看着正常"——不写明的话没人分得出自己在看哪一条。
        string route = week.HasTarget
            ? $"【按周目标拆日】{week.Label}　·　"
            : "【按月计划÷作业日】（本周未下达周目标，在「周计划编制」里填即可改走周路）　·　";

        return route
             + $"短期计划「{GetS(plan, "Name")}」· {GetS(mp, "Label")}月 · 月采出 {coal:0}万t / 剥离 {strip:0}万m³ · 作业日 {workdays:0}（{workdayBasis}）"
             + $" → 日采出 {dayCoalM3 / 1e4:0.00}万m³（ρ{density:0.##}）"
             + $" · 当日目标 {dayBasis}"
             + $" · {unitPlan.Label}"
             + (unitBasis.Length > 0 ? $" · {unitBasis}" : "")
             + extra
             + (dumpBasis.Length > 0 ? $" · 剥离分配：{dumpBasis}" : "");
    }

    private static string SetFallback()
    {
        LastFromLedger = false;
        return Fallback;
    }

    /// <summary>
    /// <b>只有周目标、没有月度方案</b>时的当日目标（WK8）。
    ///
    /// <para>
    /// 量与窗口全部来自周目标（本周剩余量 → 本周剩余作业日），权重口径与月路完全一致。
    /// 面的份额没有月计划可依，只能取「采掘单元剩余量占比」；连单元也对不上时**等分**并说明 ——
    /// 等分是个能算出来的数，但它不知道"该采哪"，这件事必须写在来源文案里。
    /// </para>
    /// <para>
    /// <b>LastFromLedger = true</b>：周目标是人下达的真实数据，不是样例兜底。
    /// 体检那一格标「样例」会让人以为盘子里的量是编的。
    /// </para>
    /// </summary>
    private static string ApplyWeekOnly(ExploderConfig cfg, WeekTargetResolution week, List<PlanViolation>? violations)
    {
        var loadFaces = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();

        if (!week.TodayIsWorkday)
        {
            foreach (var f in loadFaces) f.DayTargetM3 = 0;
            DistributeStrip(cfg, 0, violations);
            LastFromLedger = true;
            return "【按周目标拆日·无月度方案】当日目标 0 —— " + week.Label;
        }

        var unitPlan = UnitPlanLink.Resolve(cfg, MonthKey(cfg));
        double[] shares;
        string shareBasis;
        if (unitPlan.Usable)
        {
            shares = UnitShares(loadFaces, unitPlan);
            shareBasis = "面份额按采掘单元剩余量占比（" + unitPlan.Label + "）";
        }
        else
        {
            shares = new double[loadFaces.Count];
            for (int i = 0; i < shares.Length; i++) shares[i] = loadFaces.Count > 0 ? 1.0 / loadFaces.Count : 0;
            shareBasis = "⚠ 面份额**等分** —— 没有月度方案、采掘单元也没对上号，"
                       + "所以只知道全矿这周要干多少，不知道该分给哪个面（量是真的，分法是编的）";
            violations?.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn,
                Code = CodeDaySplit,
                Message = shareBasis,
            });
        }

        string basis = ApplyDailyWeights(cfg, loadFaces, shares,
                                         week.RemainCoalM3, week.RemainCoalM3 / week.RemainWorkdays.Count,
                                         week.RemainWorkdays, violations, out double ratio);

        double dayRock = ratio > 1e-12
            ? week.RemainStripM3 * ratio
            : week.RemainStripM3 / week.RemainWorkdays.Count;
        string dumpBasis = DistributeStrip(cfg, dayRock, violations);

        LastFromLedger = true;
        return $"【按周目标拆日·无月度方案】{week.Label}"
             + $"　·　当日目标 {basis}"
             + $"　·　{shareBasis}"
             + (dumpBasis.Length > 0 ? $"　·　剥离分配：{dumpBasis}" : "");
    }

    /// <summary>
    /// 把各面的<b>月量</b>按逐日能力摊到作业日，取出「今天」那一份写进 <c>DayTargetM3</c>。
    ///
    /// <para><b>算法</b>（见 <see cref="DayCapacityCalendar"/>）：</para>
    /// <code>
    ///   当天权重 w(d) = 班产 × 当天可用工时（班时窗 − 检修 − 爆破清场 − 交接） × 设备可用率
    ///   当日目标      = 该面月量 × w(今天) ÷ Σ_{本月作业日} w(d)
    ///   再受备采封顶：目标 ≤ 备采储量（备采没录则不封）
    /// </code>
    ///
    /// <para>
    /// 日历建不起来（台账没接通）时**整条退回平均摊**并如实标注 —— 半套加权比平均摊更难查：
    /// 有的面按能力摊、有的面按平均摊，两者加起来不等于月量，而没有任何地方会报错。
    /// </para>
    /// </summary>
    /// <returns>依据文案（写进来源标签）。</returns>
    /// <param name="window">
    /// null = 整月（月路，老行为）；非 null = 只在这些天里摊（周路：本周剩余作业日）。
    /// <b>权重口径两条路完全一样</b>，换的只是分子（要摊的总量）与分母（摊到哪些天）的范围。
    /// </param>
    /// <param name="dayRatio">今天占窗口权重和的比例——剥离那条腿复用它，好让两条腿说的是同一天的现场状况。</param>
    private static string ApplyDailyWeights(
        ExploderConfig cfg, List<FaceInput> loadFaces, double[] shares,
        double totalM3, double flatDayM3, IReadOnlyList<DateTime>? window,
        List<PlanViolation>? violations, out double dayRatio)
    {
        dayRatio = 0;
        bool byWeek = window != null && window.Count > 0;
        string basisName = byWeek ? "周剩余量" : "月量";
        string divisorName = byWeek ? "周剩余量÷剩余作业日" : "月量÷作业日";

        DateTime day;
        try { day = ProjectScope.WorkDate.Date; }
        catch { day = DateTime.Today; }

        var cals = new List<List<CapacityCalendar>?>();
        bool allOk = true;
        for (int i = 0; i < loadFaces.Count; i++)
        {
            var f = loadFaces[i];
            double cap = f.Group.GroupCapacityM3PerH * cfg.WeatherFactor;
            if (cap <= 1e-6 || string.IsNullOrWhiteSpace(f.Group.MainEquipment)) { cals.Add(null); allOk = false; continue; }

            // 跨月的周（周一 7/29、周日 8/4）要两份日历：DayCapacityCalendar 一次只建一个月，
            // 而每份日历对不属于自己那个月的日子返回 0，所以直接相加是安全的。
            var list = new List<CapacityCalendar>();
            foreach (var anyDay in MonthAnchors(byWeek ? window! : new[] { day }))
            {
                try
                {
                    var c = DayCapacityCalendar.Build(f.Group.MainEquipment, anyDay, cfg.Shifts, cap, cfg.HandoverRampH);
                    if (c != null) list.Add(c);
                }
                catch { /* 建不起来 = 这个面用不了加权，下面统一降级 */ }
            }

            double sum = byWeek ? window!.Sum(d => WeightOn(list, d)) : list.Sum(c => c.TotalWeight);
            if (list.Count == 0 || sum <= 1e-9 || WeightOn(list, day) <= 1e-9) allOk = false;
            cals.Add(list);
        }

        if (!allOk)
        {
            for (int i = 0; i < loadFaces.Count; i++) loadFaces[i].DayTargetM3 = Math.Round(flatDayM3 * shares[i]);
            string why = $"逐日能力日历不可用（缺班产/主设备，或今天不在作业日内），当日目标整条退回「{divisorName}」平均摊";
            violations?.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = CodeDaySplit, Message = why });
            dayRatio = byWeek && window!.Count > 0 ? 1.0 / window.Count : 0;
            return "平均摊（" + why + "）";
        }

        var notes = new List<string>();
        int capped = 0;
        double wToday = 0, wWindow = 0;
        for (int i = 0; i < loadFaces.Count; i++)
        {
            var f = loadFaces[i];
            var cal = cals[i]!;
            double share = totalM3 * shares[i];
            double sum = byWeek ? window!.Sum(d => WeightOn(cal, d)) : cal.Sum(c => c.TotalWeight);
            double w = WeightOn(cal, day);
            wToday += w;
            wWindow += sum;
            double target = sum > 1e-9 ? share * w / sum : 0;

            // 备采封顶：脚下就这么多方，排多了是排给一个采不出来的面
            if (f.AvailableReserveM3 > 1e-6 && target > f.AvailableReserveM3)
            {
                target = f.AvailableReserveM3;
                capped++;
            }

            f.DayTargetM3 = Math.Round(target);

            var todayCap = cal.Select(c => c.On(day)).FirstOrDefault(x => x != null);
            if (todayCap is { LostHours: > 0.05 })
                notes.Add($"{f.Zone} 今日少 {todayCap.LostHours:0.#}h（{todayCap.LostReason}）");
            double avail = cal.Count > 0 ? cal.Min(c => c.AvailabilityFactor) : 1.0;
            if (avail < 0.999)
                notes.Add($"{f.Group.MainEquipment} 可用率 {avail:0.00}");
        }

        dayRatio = wWindow > 1e-9 ? wToday / wWindow : 0;

        string basis = $"按逐日能力加权（检修/爆破/交接/可用率）· 摊的是{basisName}"
                     + (byWeek ? $"，窗口 {window!.Count} 个作业日（今天占 {dayRatio * 100:0.#}%）" : "")
                     + (capped > 0 ? $" · {capped} 个面被备采封顶" : "")
                     + (notes.Count > 0 ? " · " + string.Join("；", notes.Take(4)) : "");
        violations?.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = CodeDaySplit, Message = "当日目标" + basis });
        return basis;
    }

    /// <summary>这些天各自所在月份的锚点（每月一个，去重）——跨月的周要建两份能力日历。</summary>
    private static IEnumerable<DateTime> MonthAnchors(IEnumerable<DateTime> days)
        => days.Select(d => new DateTime(d.Year, d.Month, 1)).Distinct().OrderBy(d => d);

    /// <summary>
    /// 某天的权重（多份日历相加）。每份日历对不属于自己那个月的日子返回 0，所以相加是安全的。
    /// </summary>
    private static double WeightOn(IReadOnlyList<CapacityCalendar> cals, DateTime d)
        => cals.Sum(c => c.WeightOn(d));

    /// <summary>月计划目标的来源。</summary>
    public enum MonthPlanSource
    {
        /// <summary>没有月计划（既没确定方案，台账里也没有这个月）。</summary>
        None,
        /// <summary>本次会话在 PlanLib 里确定的月度方案（最新决策）。</summary>
        ConfirmedScheme,
        /// <summary>monthly_plan 月度计划台账（跨会话仍在）。</summary>
        Ledger,
    }

    /// <summary>当月计划目标（供日常实绩向上对账/反验）。HasPlan=false 表示未确定月度方案。</summary>
    public sealed class MonthInfo
    {
        public bool HasPlan;
        public MonthPlanSource Source = MonthPlanSource.None;
        public string PlanName = "";
        public string MonthLabel = "";
        public double CoalWanT;
        public double StripWanM3;
        public double Workdays = 25;
        /// <summary>煤的原位密度 t/m³（口径统一到 MaterialCatalog，不再写死 1.4）。</summary>
        public double Density = MaterialCatalog.Resolve(MaterialCatalog.Coal).InSituDensityTPerM3;
        /// <summary>月度采剥总材料（万m³）= 采出折m³ + 剥离。</summary>
        public double MaterialWanM3 => Density > 0.1 ? CoalWanT / Density + StripWanM3 : StripWanM3;
    }

    /// <summary>
    /// 读当月计划目标。两层：
    ///  ① PlanLib 本次会话已确定的月度方案（软读取，无编译期依赖）——它是最新的决策；
    ///  ② 读不到就读 <c>monthly_plan</c> 台账表——**方案存在内存里，关软件即丢**，
    ///     而月计划目标是达成评价的分母，不能因为"今天没先去点一次确定方案"就变成没有。
    /// 两层都没有时 <see cref="MonthInfo.HasPlan"/> = false，调用方须如实说明，不得自行推算。
    /// </summary>
    public static MonthInfo GetMonthInfo(string dateLabel)
    {
        var info = new MonthInfo { Density = Math.Max(0.1, Density()) };

        // ── ① 会话内已确定的方案 ──
        object? plan = ReadConfirmedPlan();
        if (plan != null && Prop(plan, "Months") is IList months && months.Count > 0)
        {
            object? mp = PickMonth(months, MonthOf(dateLabel));
            if (mp != null)
            {
                info.HasPlan = true;
                info.Source = MonthPlanSource.ConfirmedScheme;
                info.PlanName = GetS(plan, "Name");
                info.MonthLabel = GetS(mp, "Label");
                info.CoalWanT = GetD(mp, "CoalWanT");
                info.StripWanM3 = GetD(mp, "StripWanM3");
                info.Workdays = GetD(mp, "Workdays"); if (info.Workdays <= 0) info.Workdays = 25;
                return info;
            }
        }

        // ── ② 月度计划台账 ──
        return FromLedger(info, dateLabel);
    }

    /// <summary>月计划台账（<c>monthly_plan</c>）→ MonthInfo；读不到保持 HasPlan=false。</summary>
    private static MonthInfo FromLedger(MonthInfo info, string dateLabel)
    {
        try
        {
            int year = YearOf(dateLabel), month = MonthOf(dateLabel);
            var row = PitMine3D.Kylin.Data.EquipmentDataContext.Plan.Get(year, month);
            if (row == null) return info;

            info.HasPlan = true;
            info.Source = MonthPlanSource.Ledger;
            info.PlanName = "月度计划台账";
            info.MonthLabel = month.ToString(CultureInfo.InvariantCulture);
            info.CoalWanT = row.PlanCoalWanT;
            // 自营 + 外委都是本矿要完成的剥离量；只算自营会让达成率虚高
            info.StripWanM3 = row.PlanStripWanM3 + row.PlanOutsourceStripWanM3;
            info.Workdays = WorkdaysOf(year, month);
        }
        catch { /* 台账未接通 → HasPlan 保持 false，调用方如实说明 */ }
        return info;
    }

    /// <summary>
    /// 当月作业日数：优先数班次日历里有排班的日子，数不出来按日历天数。
    /// ★ 不用"25 天"这类经验常数当默认——它会悄悄改变日均口径。
    /// </summary>
    private static double WorkdaysOf(int year, int month)
    {
        try
        {
            var from = new DateTime(year, month, 1);
            var to = from.AddMonths(1).AddDays(-1);
            var rows = PitMine3D.Kylin.Data.EquipmentDataContext.ShiftCalendar.InRange(from, to);
            int days = rows.Select(r => r.Date.Date).Distinct().Count();
            if (days > 0) return days;
        }
        catch { }
        return DateTime.DaysInMonth(year, month);
    }

    private static int YearOf(string dateLabel)
    {
        var token = (dateLabel ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parts = token.Split('-');
        return parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) && y > 1900
            ? y
            : DateTime.Today.Year;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  月计划作业面（PlanLib.ShortTerm.WorkingFace 的软映射）
    //
    //  必读字段：SharePct（无则等分）
    //  可选字段（工作包9 正在给 PlanLib 的 WorkingFace 补，读不到静默跳过）：
    //    Name / Zone / FaceName            作业面名（用于与日计划面对名，避免只能按下标对齐）
    //    MaterialCode / Material           物料码或中文物料文本
    //    DestinationId / DestId / SinkId   去向 Id（对 SinkRegistry.Id）
    //    DestinationName / DestName / SinkName   去向名
    //    HaulDistanceKm / HaulKm           实际运距 km
    //    EquivHaulKm                       等效运距 km（含坡度折算）
    // ─────────────────────────────────────────────────────────────────────
    private sealed class PlanFace
    {
        public string Name = "";
        public double SharePct;
        public string MaterialCode = "";
        public string DestinationId = "";
        public string DestinationName = "";
        public double HaulKm;
        public double EquivHaulKm;
        public bool HasDestination => DestinationId.Length > 0 || DestinationName.Length > 0;
    }

    private static List<PlanFace> ReadPlanFaces(object plan)
    {
        var list = new List<PlanFace>();
        try
        {
            if (Prop(plan, "Faces") is not IList faces) return list;
            foreach (var f in faces)
            {
                if (f == null) continue;
                var pf = new PlanFace
                {
                    Name = FirstS(f, "Name", "Zone", "FaceName", "WorkZone"),
                    SharePct = GetD(f, "SharePct"),
                    DestinationId = FirstS(f, "DestinationId", "DestId", "SinkId"),
                    DestinationName = FirstS(f, "DestinationName", "DestName", "SinkName"),
                    HaulKm = FirstD(f, "HaulDistanceKm", "HaulKm"),
                    EquivHaulKm = FirstD(f, "EquivHaulKm"),
                };
                string mat = FirstS(f, "MaterialCode", "Material");
                pf.MaterialCode = MaterialCatalog.Exists(mat) ? mat : MaterialCatalog.CodeFromText(mat);
                list.Add(pf);
            }
        }
        catch { /* 反射全程容错：读不到就当月计划没给 */ }
        return list;
    }

    /// <summary>月计划面 → 日计划面：先按名字对，对不上按下标对（保持历史行为）。</summary>
    private static PlanFace? MatchPlanFace(List<PlanFace> planFaces, FaceInput face, int index)
    {
        var byName = planFaces.FirstOrDefault(p => p.Name.Length > 0 &&
            (string.Equals(p.Name, face.Zone, StringComparison.OrdinalIgnoreCase)
             || p.Name.Contains(face.Zone, StringComparison.OrdinalIgnoreCase)
             || face.Zone.Contains(p.Name, StringComparison.OrdinalIgnoreCase)));
        if (byName != null) return byName;
        return index >= 0 && index < planFaces.Count ? planFaces[index] : null;
    }

    /// <summary>把月计划带下来的去向/运距/物料填进作业面（只补空，不覆盖日计划侧已填的手工值）。</summary>
    private static void ApplyPlanFaceExtras(ExploderConfig cfg, FaceInput f, PlanFace pf,
                                            List<PlanViolation>? vs, ref int dest, ref int haul, ref int mat)
    {
        // ① 物料码：面上已有结构化物料（MaterialCode/Mix）或历史文本本身是混采构成时，以面上为准。
        //    月计划粒度粗（一面一码），不能把「煤7∶岩3」压成「煤」——那会让剥离量凭空消失。
        if (pf.MaterialCode.Length > 0)
        {
            var legacy = MaterialMix.Parse(f.Material);
            bool faceHasStructured = f.Mix != null || MaterialCatalog.Exists(f.MaterialCode);
            bool legacyIsMix = legacy.Shares.Count > 1;

            if (!faceHasStructured && !legacyIsMix)
            {
                f.MaterialCode = pf.MaterialCode;
                if (string.IsNullOrWhiteSpace(f.Material)) f.Material = MaterialCatalog.Resolve(pf.MaterialCode).Name;
                mat++;
            }
            else if (legacyIsMix && !string.Equals(legacy.PrimaryCode, pf.MaterialCode, StringComparison.OrdinalIgnoreCase))
            {
                Add(vs, ViolationSeverity.Info, ViolationCodes.MaterialRejected,
                    $"{f.Zone}：月计划物料「{MaterialCatalog.Resolve(pf.MaterialCode).Name}」与面上混采构成「{legacy.Caption}」不一致，按面上构成执行。");
            }
        }

        // ② 去向：只在面上没填去向时带下来
        if (pf.HasDestination && !f.HasDestination)
        {
            var node = FindSink(cfg, pf.DestinationId, pf.DestinationName);
            f.DestinationId = node?.Id ?? pf.DestinationId;
            f.DestinationName = node?.Name ?? pf.DestinationName;
            if (node != null) f.DestinationKind = node.Kind;
            dest++;
            if (node == null)
                Add(vs, ViolationSeverity.Info, ViolationCodes.SinkClosed,
                    $"{f.Zone}：月计划去向「{(pf.DestinationName.Length > 0 ? pf.DestinationName : pf.DestinationId)}」不在去向登记簿内，已按名带下，运距按兜底取值。");
        }

        // ③ 运距：月计划给了就用（层2），没给留 0 由运距求解器/汇的兜底运距补
        if (pf.HaulKm > 1e-6 && f.HaulDistanceKm <= 1e-6) { f.HaulDistanceKm = pf.HaulKm; haul++; }
        if (pf.EquivHaulKm > 1e-6 && f.EquivHaulKm <= 1e-6) f.EquivHaulKm = pf.EquivHaulKm;
    }

    /// <summary>
    /// 日剥离量分配到各排土面。三层：
    ///   ① 有去向台账 → 交 <see cref="FlowAssigner"/> 按运输功最小分配（内排优先由等效运距自然选出）；
    ///   ② 排土面能对上汇 → 按各汇**剩余库容占比**分摊（库容富余的先接，符合排土场轮换实际）；
    ///   ③ 都对不上 → 等分。
    /// 三层都保证 Σ各面日目标 = 全矿日剥离量（老实现是每面各给一份，总量放大 N 倍）。
    /// </summary>
    private static string DistributeStrip(ExploderConfig cfg, double dayRockM3, List<PlanViolation>? vs)
    {
        var dumps = cfg.Faces.Where(f => f.Process == ProcessType.Dump).ToList();
        if (dumps.Count == 0 || dayRockM3 <= 1e-6) return "";

        if (dumps.Count == 1)
        {
            dumps[0].DayTargetM3 = Math.Round(dayRockM3);
            return $"单排土面「{dumps[0].Zone}」全量 {dayRockM3 / 1e4:0.00}万m³";
        }

        // 盘子没带去向登记簿时先尝试装载一次（与 FlowAssigner 同一套兜底策略），
        // 否则「按库容/按运输功分配」根本无从谈起，只能退回等分。
        if (cfg.Sinks.All.Count == 0)
        {
            var (reg, _) = PeerEngines.LoadSinks();
            if (reg != null) cfg.Sinks = reg;
        }

        // ① 流向求解器
        if (cfg.Sinks.All.Count > 0)
        {
            try
            {
                var p = new FlowProblem
                {
                    Supplies =
                    {
                        new FlowSupply
                        {
                            SourceId = "STRIP", SourceName = "全矿剥离",
                            MaterialCode = MaterialCatalog.Rock, InSituM3 = dayRockM3,
                        },
                    },
                    Sinks = cfg.Sinks, Period = cfg.Period, PeriodHours = 24,
                };
                var fp = FlowAssigner.Assign(p);
                double got = 0;
                var hit = new Dictionary<FaceInput, double>();
                foreach (var f in dumps)
                {
                    var load = fp.SinkLoads.FirstOrDefault(s => FlowAssigner.MatchesFace(s, f));
                    double v = load?.InSituM3 ?? 0;
                    hit[f] = v; got += v;
                }
                if (got > 1)
                {
                    // 求解器分给了「没有对应排土面」的汇（如破碎站旁的临时堆场）时，按已命中面等比放大回总量
                    double scale = dayRockM3 / got;
                    foreach (var f in dumps) f.DayTargetM3 = Math.Round(hit[f] * scale);
                    Add(vs, ViolationSeverity.Info, CodeStripSplit,
                        $"日剥离 {dayRockM3 / 1e4:0.00}万m³ 按流向分配（运输功最小）落到 {hit.Count(x => x.Value > 0)} 个排土面：" +
                        string.Join("、", dumps.Where(f => f.DayTargetM3 > 0).Select(f => $"{f.Zone} {f.DayTargetM3 / 1e4:0.00}万m³")) +
                        (fp.Feasible ? "" : "；" + fp.Explain));
                    return $"流向求解（{fp.WeightedAvgHaulKm:0.##}km / 内排率 {fp.InternalDumpPct:0.#}%）";
                }
            }
            catch { /* 求解失败 → 落到 ② */ }
        }

        // ② 按所属汇剩余库容占比
        var weights = dumps.Select(f =>
        {
            var s = FindSink(cfg, f.DestinationId, string.IsNullOrWhiteSpace(f.DestinationName) ? f.Zone : f.DestinationName);
            if (s == null || !s.IsActive) return 0.0;
            return s.IsCapacityLimited ? Math.Max(0, s.RemainingM3) : 0.0;
        }).ToList();

        double sum = weights.Sum();
        if (sum > 1e-6)
        {
            for (int i = 0; i < dumps.Count; i++) dumps[i].DayTargetM3 = Math.Round(dayRockM3 * weights[i] / sum);
            Add(vs, ViolationSeverity.Info, CodeStripSplit,
                $"日剥离 {dayRockM3 / 1e4:0.00}万m³ 按各排土场剩余库容占比分摊：" +
                string.Join("、", dumps.Select((f, i) => $"{f.Zone} {weights[i] / sum * 100:0.#}%")));
            return "按剩余库容占比";
        }

        // ③ 等分
        double each = dayRockM3 / dumps.Count;
        foreach (var f in dumps) f.DayTargetM3 = Math.Round(each);
        Add(vs, ViolationSeverity.Info, ViolationCodes.NoDestination,
            $"排土面未关联去向台账，日剥离 {dayRockM3 / 1e4:0.00}万m³ 按 {dumps.Count} 面等分（录入排土场后自动改为按库容/运输功分配）。");
        return $"{dumps.Count} 面等分（缺去向台账）";
    }

    // ── 运行时软读取（无编译期依赖）──
    private static object? ReadConfirmedPlan()
    {
        try
        {
            var t = Type.GetType("PlanLib.ShortTerm.ShortTermSchemeStore, PlanLib");
            return t?.GetProperty("Confirmed", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch { return null; }
    }

    private static object? PickMonth(IList months, int month)
    {
        object? first = null, byMonth = null, firstWithCoal = null;
        foreach (var m in months)
        {
            if (m == null) continue;
            first ??= m;
            if (byMonth == null && (int)Math.Round(GetD(m, "Month")) == month) byMonth = m;
            if (firstWithCoal == null && GetD(m, "CoalWanT") > 0) firstWithCoal = m;
        }
        return byMonth ?? firstWithCoal ?? first;
    }

    /// <summary>
    /// 各面采出份额（按 SharePct 归一；缺失/不足则等分）。
    /// 若各日计划面都唯一对上了月计划面（按名），份额按**对上的那个面**取，
    /// 免得「份额按下标、去向按名字」两套对齐口径打架。
    /// </summary>
    private static double[] FaceShares(List<PlanFace?> matched, List<PlanFace> planFaces, int n)
    {
        if (n <= 0) return Array.Empty<double>();

        if (matched.Count == n && matched.All(m => m != null)
            && matched.Select(m => m!).Distinct().Count() == n)
        {
            double s0 = matched.Sum(m => m!.SharePct);
            if (s0 > 1e-6) return matched.Select(m => m!.SharePct / s0).ToArray();
        }

        var sh = planFaces.Take(n).Select(p => p.SharePct).ToList();
        double sum = sh.Sum();
        if (sh.Count < n || sum <= 1e-6) return Enumerable.Repeat(1.0 / n, n).ToArray();
        return sh.Select(x => x / sum).ToArray();
    }

    /// <summary>
    /// 煤的原位密度 t/m³：先软读 PlanLib.ShortTerm.ShortTermPlan.DefaultCoalDensity（保持与月计划同口径），
    /// 读不到取 MaterialCatalog 的煤密度（1.35）——口径统一到物料本体，严禁在此再写死常量。
    /// </summary>
    /// <summary>
    /// 煤密度 t/m³（万t ↔ 实方 m³ 的换算口径）。
    /// <para>周计划窗口显示"周目标折合多少万m³"时要用**同一个数**——各写各的就会出现
    /// 两个都自洽、但互相对不上的量。</para>
    /// </summary>
    internal static double Density()
    {
        double fallback = MaterialCatalog.Resolve(MaterialCatalog.Coal).InSituDensityTPerM3;
        try
        {
            var t = Type.GetType("PlanLib.ShortTerm.ShortTermPlan, PlanLib");
            var f = t?.GetField("DefaultCoalDensity", BindingFlags.Public | BindingFlags.Static);
            var v = f?.GetRawConstantValue() ?? f?.GetValue(null);
            return v is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// 定本月的有效作业日 —— 月→日整条裂解都在除以它，除数错则下面每一个日目标都错。
    ///
    /// <para>三层，**日历口径优先**：</para>
    /// <list type="number">
    /// <item><b>班次日历</b>（<see cref="WorkCalendar"/>）：本月有几天排了班，这是"实际出勤几天"的事实；</item>
    /// <item><b>月计划自带的 Workdays</b>：月目标是按它编出来的，日历不可用时退到它；</item>
    /// <item><see cref="WorkCalendar.FallbackMonthWorkdays"/>：两者都没有时的兜底常数。</item>
    /// </list>
    ///
    /// <para>
    /// <b>为什么日历赢</b>：月目标是承诺量。日历说本月只有 22 天出勤，仍按月计划的 25 天摊，
    /// 每天就少排 12%，到月末必然欠一大截，而且**直到月末才发现**。按 22 天折算后日目标变大，
    /// 当日装不下会当场报「当日欠产」——同一个问题，一个月末暴露，一个当天暴露。
    /// </para>
    /// <para>
    /// 两者不一致时记一条 Info 级校核（不是 Warn：这不是错误，是两个口径的正常差异，
    /// 但看计划的人有权知道自己看到的日目标是按哪个数摊出来的）。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 三层裁定本身已抽到 <see cref="WorkCalendar.ResolveWorkdays"/>（周计划编制要用同一个除数）；
    /// 本方法只负责把裁定结果翻成装箱侧的校核条目。
    /// </remarks>
    private static double ResolveWorkdays(ExploderConfig cfg, double planWorkdays, List<PlanViolation>? violations, out string basis)
    {
        var r = WorkCalendar.ResolveWorkdays(DateOf(cfg), planWorkdays);
        var info = r.Info;
        basis = r.Basis;

        switch (r.Source)
        {
            case WorkdaySource.Calendar when r.Disagrees:
                violations?.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Info, Code = ViolationCodes.WorkdayBasis,
                    Message = $"{info.MonthLabel} 月计划按 {planWorkdays:0} 个作业日编制，"
                            + $"班次日历实为 {info.Workdays} 天——日目标已按日历口径 {info.Workdays} 天折算"
                            + (info.Workdays < planWorkdays ? "（每日强度上调）" : "（每日强度下调）"),
                });
                break;

            case WorkdaySource.MonthPlan:
                violations?.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Info, Code = ViolationCodes.WorkdayBasis,
                    Message = $"{info.Label}，作业日退回月计划口径 {planWorkdays:0} 天",
                });
                break;

            case WorkdaySource.Fallback:
                violations?.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Warn, Code = ViolationCodes.WorkdayBasis,
                    Message = $"{info.Label}，月计划也没给作业日——日目标按兜底 {WorkCalendar.FallbackMonthWorkdays:0} 天摊，"
                            + "这个数是拍的，请在班次日历里补齐本月排班",
                });
                break;
        }

        return r.Workdays;
    }

    /// <summary>盘子对应的月份键 <c>yyyy-MM</c>（月度单元台账按它取那一份 CSV）。</summary>
    private static string MonthKey(ExploderConfig cfg) => DateOf(cfg).ToString("yyyy-MM");

    /// <summary>
    /// 按采掘单元剩余量算各面份额。
    /// <para>
    /// 与份额法的本质差别：份额法的分母是人填的百分比之和（永远是 100），
    /// 这里的分母是**所有已绑单元的剩余方量之和**——一个采了八成的单元，
    /// 它的面今天就该少排，这件事份额法表达不了（除非人每天去改百分比）。
    /// </para>
    /// <para>没绑单元的面份额为 0：它不在本月单元台账里，本来就不该分到量。</para>
    /// </summary>
    private static double[] UnitShares(List<FaceInput> loadFaces, UnitPlanLink.Result plan)
    {
        var byZone = plan.Faces
            .GroupBy(x => x.Zone ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.RemainingM3), StringComparer.OrdinalIgnoreCase);

        double total = byZone.Values.Sum();
        var shares = new double[loadFaces.Count];
        if (total <= 1e-6) return shares;

        for (int i = 0; i < loadFaces.Count; i++)
            shares[i] = byZone.TryGetValue(loadFaces[i].Zone ?? "", out double m3) ? m3 / total : 0;

        return shares;
    }

    /// <summary>
    /// 把单元剩余量当备采储量带下来，<b>只补没录过的面</b>。
    /// <para>
    /// 人在台账上手填的备采是实测/实盘的数，模型算出来的是几何量，两者不一致时以人填的为准——
    /// 反过来会让"我明明盘过方了"的那个数每次装配都被悄悄改掉。
    /// </para>
    /// </summary>
    private static string ApplyUnitReserves(List<FaceInput> loadFaces, UnitPlanLink.Result plan, List<PlanViolation>? violations)
    {
        var byZone = plan.Faces
            .GroupBy(x => x.Zone ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.RemainingM3), StringComparer.OrdinalIgnoreCase);

        int filled = 0, kept = 0;
        foreach (var f in loadFaces)
        {
            if (!byZone.TryGetValue(f.Zone ?? "", out double m3) || m3 <= 1e-6) continue;
            if (f.AvailableReserveM3 > 1e-6) { kept++; continue; }   // 人填过，不动
            f.AvailableReserveM3 = m3;
            filled++;
        }

        if (filled == 0 && kept == 0) return "";

        if (filled > 0)
            violations?.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Info, Code = ViolationCodes.PreparedReserve,
                Message = $"{filled} 个面的备采储量取自采掘单元剩余量（几何量）"
                        + (kept > 0 ? $"；另有 {kept} 个面用的是台账里手填的数（实盘优先，不覆盖）" : ""),
            });

        return $"备采：{filled} 面取自单元剩余量" + (kept > 0 ? $" · {kept} 面用手填值" : "");
    }

    /// <summary>盘子对应的日期：先解 DateLabel 首段（"2026-06-17 周二"），解不出退项目上下文的作业日。</summary>
    private static DateTime DateOf(ExploderConfig cfg)
    {
        var token = (cfg?.DateLabel ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.Date
            : ProjectScope.WorkDate;
    }

    private static int MonthOf(string dateLabel)
    {
        var token = (dateLabel ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parts = token.Split('-');
        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) && m is >= 1 and <= 12)
            return m;
        return 1;
    }

    private static SinkNode? FindSink(ExploderConfig cfg, string? id, string? name)
    {
        var reg = cfg.Sinks;
        if (reg == null) return null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = reg.Find(id);
            if (byId != null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(name))
            return reg.All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static void Add(List<PlanViolation>? vs, ViolationSeverity sev, string code, string msg)
        => vs?.Add(new PlanViolation { Severity = sev, Code = code, Message = msg });

    private static object? Prop(object o, string p)
    {
        try { return o.GetType().GetProperty(p)?.GetValue(o); }
        catch { return null; }
    }
    private static double GetD(object o, string p) => Prop(o, p) is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : 0;
    private static string GetS(object o, string p) => Prop(o, p)?.ToString() ?? "";

    /// <summary>按别名依次尝试读字符串（跨模块字段命名未定稿时的容错）。</summary>
    private static string FirstS(object o, params string[] names)
    {
        foreach (var n in names)
        {
            string s = GetS(o, n);
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return "";
    }

    /// <summary>按别名依次尝试读数值。</summary>
    private static double FirstD(object o, params string[] names)
    {
        foreach (var n in names)
        {
            double d = GetD(o, n);
            if (Math.Abs(d) > 1e-9) return d;
        }
        return 0;
    }
}
