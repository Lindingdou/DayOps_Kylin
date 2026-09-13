// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/MinePlanImporter.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Units;
namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>煤的去向（本引擎不知道台账，由调用方给）。</summary>
public sealed class CoalDestination
{
    public string Id = "";
    public string Name = "破碎站";
    public PlanSinkKind Kind = PlanSinkKind.Crusher;
    public double HaulKm = 2.0;
    public double EquivHaulKm;
}

/// <summary>导入的一条问题记录。<b>不静默</b>：每一条都要能在界面上看见。</summary>
public sealed class ImportIssue
{
    public string Code = "";      // I1..I5
    public string Text = "";
    public bool   Blocking;       // true = 这份计划不该被当成可执行的
    public override string ToString() => (Blocking ? "◆" : "⚠") + $" {Code} {Text}";
}

public sealed class ImportResult
{
    public bool Success;
    public string Error = "";
    public List<MonthPeriod> Months = new();
    public List<ImportIssue> Issues = new();
    /// <summary>由几何算出来的备采储量（万t）—— 可回写 <see cref="MineableArea.PreparedReserveWanT"/>。</summary>
    public double PreparedReserveWanT;
    /// <summary>整份计划是否可执行（导出侧 Feasible 且导入侧没有阻断性问题）。</summary>
    public bool Executable => Success && !Issues.Any(i => i.Blocking);
}

/// <summary>
/// 把「逐月采剥接续」内核的导出契约（<see cref="MinePlanExport"/>）填进短期月度计划。
///
/// <para><b>不走反射</b>：`PlanLib.csproj` 直接引用 `MineAssLib`，所以这里是普通的类型转换。
/// TaskLib 那条 `ShortTermLink` 反射桥读的是 PlanLib 自己的月计划 —— 这里填好了，那条桥一行不用改。</para>
///
/// <para><b>只填流，不填标量</b>：<see cref="MonthPeriod"/> 的 采出量/剥离量/剥采比/内外排
/// 在 <c>HasFlows</c> 时全是<b>按流派生</b>的。所以导入只负责把 <see cref="PlanFlow"/> 建对，
/// 那几个数自然就对 —— 这同时避免了"几何算的"和"形状函数编的"两个来源打架。</para>
///
/// <para><b>三条对账，对不上就记 Issue 不静默</b>：
/// ① 物料码在下游目录里认不认得；② 本引擎用的 ρ/Kr 与下游目录是否一致；
/// ③ 派生出来的月量与导出表里的数是否吻合。</para>
/// </summary>
public static class MinePlanImporter
{
    /// <summary>
    /// 把导出契约里的<b>放坡参数</b>写进短期方案 —— 三维动态模拟的真台阶层体就靠这三个字段。
    ///
    /// <para>TaskLib 的 <c>SimModel.LoadSlope</c> 用<b>反射按属性名</b>软读 α/W/β，读不到就明确降级成
    /// 「层体按<b>垂直壁</b>建」。所以这一步做了，`采运排一体化` §十 <b>已知边界 1 就关掉了</b>，
    /// 而且 <b>TaskLib 一行不用改</b>。</para>
    ///
    /// <para><b>缺的不填</b>：导出契约里 α/W 是 0 时这里也留 0 —— TaskLib 会照旧降级并写清原因。
    /// 塞个缺省角度进去等于拿猜出来的几何冒充工程量。</para>
    /// </summary>
    /// <returns>实际写进去了几项（0 = 导出契约里就没有几何参数）。</returns>
    /// <summary>
    /// 把<b>由几何算出来的备采储量</b>回写进计划的三量结构。
    ///
    /// <para><b>不写这一句，「月度计划编制」的每一套方案都判不过</b>：
    /// <c>MineableArea.PreparedReserveWanT</c> 恒 0 ⇒ <c>PreparedMonths()</c> 恒 0 ⇒
    /// <c>ShortTermScheduler.Evaluate</c> 里 <c>prepOk = 0 &gt;= MinPreparedMonths</c>（默认 2）恒 false ⇒
    /// <c>ok = completionOk &amp;&amp; ceilOk &amp;&amp; ratioOk &amp;&amp; prepOk</c> 恒 false。
    /// 界面上表现为"方案永远不达标"，而每一项分指标看上去都正常 ——
    /// 这条链此前只在 <see cref="ImportResult.PreparedReserveWanT"/> 的注释里写着"可回写"，
    /// <b>写了几个月都没人回写</b>。</para>
    ///
    /// <para>算不出来（没有月行）时<b>不写</b>，保持 0 并返回 false —— 让"没算出来"和"真的是 0"
    /// 在调用方那儿还分得开，调用方据此如实说明，而不是把一个编出来的储量灌进闸门。</para>
    /// </summary>
    public static bool ApplyPreparedReserve(ShortTermPlan? plan, ImportResult? imp)
    {
        if (plan == null || imp == null) return false;
        if (!(imp.PreparedReserveWanT > 1e-9)) return false;
        plan.Mineable.PreparedReserveWanT = imp.PreparedReserveWanT;
        return true;
    }

    public static int ApplyGeometry(MinePlanExport? e, ShortTermPlan? plan, ImportResult? issues = null)
    {
        if (e?.Geometry == null || plan == null) return 0;
        var g = e.Geometry;
        int n = 0;
        if (g.RockFaceDeg > 0) { plan.BenchFaceAngleDeg = g.RockFaceDeg; n++; }
        if (g.MinBermM > 0) { plan.BermWidthM = g.MinBermM; n++; }
        if (g.WorkingSlopeDeg > 0) { plan.OverallSlopeAngleDeg = g.WorkingSlopeDeg; n++; }
        if (g.RockBenchHeightM > 0) plan.BenchHeightM = g.RockBenchHeightM;
        if (n == 0)
            issues?.Issues.Add(new ImportIssue
            {
                Code = "I8",
                Text = "导出契约里没有放坡参数（坡面角/平盘宽/工作帮坡角）—— "
                     + "三维层体会按**垂直壁**建。跑「采场剖面」时把台阶参数一并给上即可",
            });
        return n;
    }

    /// <summary>ρ/Kr 与下游目录的允许偏差（%）。超了记 Issue —— 两边各算各的是最难查的一类错。</summary>
    public const double CoefTolPct = 2.0;

    /// <param name="workdays">
    /// 逐月有效作业日。空 = 本通道没有（记 <b>I9</b>，下游会按缺省 25 天折算卸点能力）。
    /// 长度可以是 1（各月相同）或与月数相同；其它长度<b>整个忽略并记 I9</b>，不截断不补齐。
    /// </param>
    public static ImportResult Import(MinePlanExport? e, CoalDestination? coalTo = null, int startMonth = 1,
                                      double[]? workdays = null)
    {
        var r = new ImportResult();
        if (e == null) { r.Error = "导出契约为空"; return r; }
        if (e.SchemaVersion != MinePlanExport.CurrentSchema)
        { r.Error = $"契约版本不匹配：{e.SchemaVersion} ≠ {MinePlanExport.CurrentSchema}"; return r; }

        // 导出侧自己先自洽 —— 它不自洽，导进来只会把问题摊开
        foreach (var b in e.Validate()) r.Issues.Add(new ImportIssue { Code = "I1", Text = "导出契约不自洽：" + b, Blocking = true });
        if (!e.Feasible)
            r.Issues.Add(new ImportIssue { Code = "I2", Text = "内核判定不可行（硬约束未全过或有排不下的方量），不得当成可执行计划", Blocking = true });
        if (e.Coal.Count == 0)
            r.Issues.Add(new ImportIssue { Code = "I3", Text = "导出契约里没有采出侧明细 —— 按流派生的采出量会是 0", Blocking = true });
        // R36：摊过的必须让人看见。不阻断 —— 摊是合法的，瞒着才不合法。
        if (e.CoalAllocatedByEngine)
            r.Issues.Add(new ImportIssue
            {
                Code = "I7",
                Text = "逐层煤量是【引擎摊的】不是用户填的 —— " + e.CoalAllocationMethod,
            });

        coalTo ??= new CoalDestination();

        // 作业日：长度不符**整个忽略**（截断/补齐都会悄悄改掉用户的意思），并在下面记 I9
        double[] wd = Array.Empty<double>();
        if (workdays is { Length: > 0 })
        {
            if (workdays.Length == 1) wd = Enumerable.Repeat(workdays[0], e.Months.Count).ToArray();
            else if (workdays.Length == e.Months.Count) wd = workdays;
            else r.Issues.Add(new ImportIssue
            {
                Code = "I9",
                Text = $"给了 {workdays.Length} 个作业日，但计划有 {e.Months.Count} 个月 —— **整个忽略**，"
                     + "下游按缺省 25 天算",
            });
        }

        int mi = 0;
        foreach (var em in e.Months)
        {
            var mp = new MonthPeriod
            {
                Month = startMonth + em.Month - 1,
                Label = $"M{startMonth + em.Month - 1:00}",
                CumCoal = Math.Round(em.CumCoalWanT, 1),
                CumStrip = Math.Round(em.CumStripWanM3, 0),
                AdvanceM = Math.Round(em.AdvanceM, 1),
                Workdays = mi < wd.Length && wd[mi] > 0 ? wd[mi] : 0,
            };
            mi++;

            // ① 采出侧：源=煤层、物料=coal、汇由台账给
            foreach (var c in e.Coal.Where(x => x.Month == em.Month))
                mp.Flows.Add(new PlanFlow
                {
                    SourceName = c.SeamName, SourceRefId = $"seam:{c.SeamIndex}",
                    MaterialCode = PlanMaterialCatalog.Coal,
                    InSituWanM3 = Math.Round(c.InSituWanM3, 3),
                    DestinationId = coalTo.Id, DestinationName = coalTo.Name, DestinationKind = coalTo.Kind,
                    HaulKm = coalTo.HaulKm, EquivHaulKm = coalTo.EquivHaulKm,
                });

            // ② 剥离侧：六元组已经全了，直接搬
            foreach (var f in e.Flows.Where(x => x.Month == em.Month))
                mp.Flows.Add(new PlanFlow
                {
                    SourceName = f.SourceName, SourceRefId = $"gap:{f.SourceGap}",
                    MaterialCode = MapMaterial(f.MaterialCode, f.MaterialName, r),
                    InSituWanM3 = Math.Round(f.InSituWanM3, 3),
                    DestinationName = f.DestinationName,
                    DestinationKind = f.IsInternalDump ? PlanSinkKind.InternalDump : PlanSinkKind.ExternalDump,
                    HaulKm = f.HaulKm, EquivHaulKm = f.HaulKm,
                });

            r.Months.Add(mp);
        }

        VerifyCoefficients(e, r);
        VerifyDerived(e, r);

        // I9：本通道填不了「有效作业日 / 主作业面」——**必须说，不能让它悄悄缺着**。
        // 作业日属于工作历（`ShortTermBase`），主作业面属于开采程序，都不在采剥接续的输入里。
        // 后果是实的：`SimBuilder` 见 Workdays<=0 会**静默按 25 天算**卸点通过能力
        // （`SimBuilder.ReadMonthPeriods`），而 25 是个缺省值不是这个矿的数。
        if (r.Months.Count > 0 && r.Months.All(m => m.Workdays <= 1e-9))
            r.Issues.Add(new ImportIssue
            {
                Code = "I9",
                Text = "本通道没有「有效作业日」——三维模拟会按缺省 25 天折算卸点通过能力。"
                     + "要用真作业历，请在「短期生产计划编制」里录工作历后回填，或直接给 Workdays",
            });
        if (r.Months.Count > 0 && r.Months.All(m => string.IsNullOrEmpty(m.ActiveFace)))
            r.Issues.Add(new ImportIssue
            {
                Code = "I9",
                Text = "本通道没有「当月主作业面」——采剥接续的源粒度是【标高格×层间】不是作业面，"
                     + "所以这里不猜。三维模拟的作业面标注会是空的（量与推进不受影响）",
            });

        // 备采储量：**由几何算出来的**，不再靠录入（短期设计 Phase2 待办 #2）
        r.PreparedReserveWanT = e.Months.Count > 0 ? e.Months.Min(m => m.PreparedWanT) : 0;

        r.Success = true;
        return r;
    }

    /// <summary>把导出侧的物料码对到下游目录；认不出来就退回硬岩并<b>记 Issue</b>，不静默。</summary>
    private static string MapMaterial(string code, string name, ImportResult r)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var spec = PlanMaterialCatalog.Resolve(code);
            if (spec != null && string.Equals(spec.Code, code, StringComparison.OrdinalIgnoreCase)) return spec.Code;
        }
        r.Issues.Add(new ImportIssue
        {
            Code = "I4",
            Text = $"物料码「{(string.IsNullOrWhiteSpace(code) ? "(空)" : code)}」（{name}）不在下游目录里，已退回 rock —— "
                 + "Ks/Kr/是否需爆破/允许去向都会按硬岩算，请核对物料映射",
        });
        return PlanMaterialCatalog.Rock;
    }

    /// <summary>
    /// ρ/Kr 对账：本引擎用的系数与下游目录必须一致。
    /// <b>两边各算各的是最难查的一类错</b> —— 库容按一套扣、配车按另一套算，账面全都"达标"。
    /// </summary>
    private static void VerifyCoefficients(MinePlanExport e, ImportResult r)
    {
        foreach (var g in e.Flows.Where(f => !string.IsNullOrWhiteSpace(f.MaterialCode))
                                 .GroupBy(f => f.MaterialCode))
        {
            var spec = PlanMaterialCatalog.Resolve(g.Key);
            if (spec == null) continue;
            var f = g.First();
            if (Rel(f.DensityUsed, spec.InSituDensityTPerM3) > CoefTolPct)
                r.Issues.Add(new ImportIssue
                {
                    Code = "I5",
                    Text = $"{g.Key} 容重：内核用 {f.DensityUsed:0.###}，下游目录 {spec.InSituDensityTPerM3:0.###} —— 两边对不上",
                });
            if (Rel(f.KrUsed, spec.ResidualSwellFactor) > CoefTolPct)
                r.Issues.Add(new ImportIssue
                {
                    Code = "I5",
                    Text = $"{g.Key} 残余膨胀 Kr：内核用 {f.KrUsed:0.###}，下游目录 {spec.ResidualSwellFactor:0.###} —— 库容会算错",
                });
        }
    }

    /// <summary>
    /// 派生量对账：按流派生出来的月采出/月剥离，必须与导出表里的数吻合。
    ///
    /// <para><b>⚠ 对账要拿【未取整的流合计】，不能拿 <see cref="MonthPeriod"/> 的标量。</b>
    /// 那几个标量是 PlanLib 给报表定的<b>显示粒度</b>（<c>CoalWanT</c> 舍到 0.1 万t、
    /// <c>StripWanM3</c> 舍到 1 万m³），把它当对账依据会踩两个坑：
    /// <list type="number">
    ///   <item>用相对百分比 ⇒ 小量级月份必红（取整本身就能差 0.5 万）——
    ///     <b>每一份真实计划都被判成"不可执行"</b>。</item>
    ///   <item>改用绝对取整粒度 ⇒ 正好卡在取整边界上时（39.499 → 显示 40）差 0.501 &gt; 0.5，
    ///     <b>照样误报</b>。端到端判据上抓到的就是这一个。</item>
    /// </list>
    /// 根子在于：取整是<b>显示</b>，不是<b>账</b>。账要对在流上。</para>
    /// </summary>
    private const double DerivedTolPct = 0.5;

    private static void VerifyDerived(MinePlanExport e, ImportResult r)
    {
        for (int i = 0; i < r.Months.Count && i < e.Months.Count; i++)
        {
            var mp = r.Months[i]; var em = e.Months[i];
            // 未取整的流合计 —— 与 MonthPeriod 派生标量同一套口径，只是不经过显示取整
            double coalRaw = mp.Flows.Where(f => f.IsOre).Sum(f => f.TonnageWanT);
            double stripRaw = mp.Flows.Where(f => !f.IsOre).Sum(f => f.InSituWanM3);

            if (Rel(coalRaw, em.CoalWanT) > DerivedTolPct)
                r.Issues.Add(new ImportIssue
                {
                    Code = "I6", Blocking = true,
                    Text = $"第{em.Month}月：按流合计的采出 {coalRaw:0.000} 万t 与导出表的 {em.CoalWanT:0.000} 万t 对不上",
                });
            double wantStrip = em.StripWanM3 - em.UnplacedWanM3;
            if (Rel(stripRaw, wantStrip) > DerivedTolPct)
                r.Issues.Add(new ImportIssue
                {
                    Code = "I6", Blocking = true,
                    Text = $"第{em.Month}月：按流合计的剥离 {stripRaw:0.000} 万m³ 与导出表的 {wantStrip:0.000} 万m³ 对不上",
                });
        }
    }

    private static double Rel(double a, double b)
        => Math.Abs(a) < 1e-9 && Math.Abs(b) < 1e-9 ? 0
         : Math.Abs(a - b) / Math.Max(1e-9, Math.Max(Math.Abs(a), Math.Abs(b))) * 100;

    /// <summary>一行摘要（命令行/界面直接显示）。</summary>
    public static string Summary(ImportResult r)
    {
        if (!r.Success) return "导入失败：" + r.Error;
        double coal = r.Months.Sum(m => m.CoalWanT), strip = r.Months.Sum(m => m.StripWanM3);
        return $"导入 {r.Months.Count} 个月 · 煤 {coal:0.0}万t · 岩 {strip:0.0}万m³ · "
             + $"流 {r.Months.Sum(m => m.Flows.Count)} 笔 · 备采 {r.PreparedReserveWanT:0.0}万t（几何算） · "
             + (r.Executable ? "可执行 ✓" : $"不可执行 ✗（{r.Issues.Count(i => i.Blocking)} 条阻断）");
    }
}
