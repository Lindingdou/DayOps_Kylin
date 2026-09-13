using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 「确定入库」的<b>唯一实现</b> —— 短期计划这一组里，凡是要把一份方案写进确定簿的，都走这里。
///
/// <para><b>为什么要有这一层</b>：此前 <c>ShortTermSchemeStore.Confirmed</c> 在<b>三个按钮</b>里各赋值一次
/// （「月度计划编制」/「派生计划方案」/「量驱动采剥接续」），而三份<b>做的事根本不一样</b>：
/// 只有量驱动那份带阻断校验、并且写 <c>monthly_plan</c> 台账；另外两份<b>只写内存</b>。
/// 而 <see cref="ShortTermSchemeStore"/> 是纯内存静态类（全类没有 Save/Load），
/// 它自己注释写着"确定簿是下游唯一事实来源" —— 那句话在另外两条路径上<b>只在一次开机内成立</b>。
/// 症状是典型的不报错：关一次软件，三维模拟 / 作业计划 / 采运排一体化全都读不到，界面上只是"没东西"。</para>
///
/// <para><b>分工口径</b>：编制、派生、打分是各按钮<b>各自的动作</b>（轴不同，本来就该有多份）；
/// <b>确定是一个写动作，实现只能有一份</b>。入口有几个不要紧 —— 这一点与「采排配对」那条不同：
/// 那里 <b>配对是算法</b>，所以必须一处解、一处看；这里确定不是算法，多入口反而是对的。
/// 参见 docs/短期生产计划_按钮职责切分.md。</para>
///
/// <para><b>不做什么</b>：<b>不编制</b>。方案没算过就调进来，这里只会如实说"一个期次都没有"，
/// 绝不替调用方跑一遍 <see cref="ShortTermScheduler"/> —— 几何链那份方案的数是几何算出来的，
/// 在这儿顺手重排一次会把它按份额规则悄悄改掉，而且不报错。编制属于调用方，确定才属于这里。</para>
/// </summary>
public static class ShortTermConfirmService
{
    /// <summary>确定入库的结果。<b>失败时确定簿不变</b> —— 不存在"写了一半"的状态。</summary>
    public sealed class ConfirmOutcome
    {
        /// <summary>是否已写进确定簿。</summary>
        public bool Ok;
        /// <summary>失败原因（<see cref="Ok"/> 为 true 时为空）。</summary>
        public string Err = "";
        /// <summary>写进 <c>monthly_plan</c> 台账的月份数。0 表示这份确定只活在本次会话里。</summary>
        public int LedgerMonths;
        /// <summary>台账没写成时的原因（写成了则为空）。</summary>
        public string LedgerErr = "";
        /// <summary>下游就绪自检文本（<b>只报不拦</b>，见 <see cref="DownstreamReadiness"/>）。</summary>
        public string Readiness = "";

        /// <summary>给界面直接显示的一段话：结论 + 台账去向 + 下游就绪。三个入口显示的口径因此一致。</summary>
        public string Message = "";
    }

    /// <summary>
    /// 把 <paramref name="plan"/> <b>确定入库</b>：写内存确定簿 + 写 <c>monthly_plan</c> 台账。
    /// </summary>
    /// <param name="plan">要确定的方案。调用方负责它<b>已经编制过</b>（这里不代跑）。</param>
    /// <param name="blocking">
    /// 调用方自己那条链算出来的<b>阻断性问题</b>。非空即拒绝入库 ——
    /// 确定簿是下游唯一事实来源，往里放一份有阻断问题的计划，下游每一处都会拿它当真的用。
    /// 组织链目前没有阻断项，传 null 即可；几何链传 <c>MonthlyStripSessionResult.Blocking</c>。
    /// </param>
    public static ConfirmOutcome Confirm(ShortTermPlan? plan, IReadOnlyList<string>? blocking = null)
    {
        var o = new ConfirmOutcome();

        if (plan == null) { o.Err = "没有可入库的方案"; o.Message = "✗ " + o.Err; return o; }

        if (blocking != null && blocking.Count > 0)
        {
            o.Err = $"这份计划有 {blocking.Count} 条阻断性问题，不能当【确定方案】入库："
                  + string.Join("；", blocking.Take(3));
            o.Message = "✗ " + o.Err;
            return o;
        }

        // 方案库里没有就先放进去 —— 确定一份不在库里的方案，界面上会出现"已确定但列表里找不到"。
        if (!ShortTermSchemeStore.Schemes.Contains(plan)) ShortTermSchemeStore.Schemes.Add(plan);
        ShortTermSchemeStore.Confirmed = plan;

        // 「★已确定」标记也收在这儿：三个窗口此前各自 foreach 一遍自己的 _schemes，
        // 而那三个 _schemes 是同一个 ShortTermSchemeStore.Schemes —— 各写各的只是重复，不是隔离。
        foreach (var s in ShortTermSchemeStore.Schemes)
            s.Note = s == plan ? "★已确定" : (s.Note == "★已确定" ? "" : s.Note);

        o.Ok = true;
        o.LedgerMonths = WriteToLedger(plan, out string ledgerErr);
        o.LedgerErr = ledgerErr;
        o.Readiness = DownstreamReadiness(plan);

        o.Message = $"✔ 已确定短期主方案「{plan.Name}」"
                  + (plan.Result != null
                        ? $"（完成率 {plan.Result.CompletionRatePct:0.0}% · 全期采出 {plan.Result.TotalCoalWanT:N0}万t · 综合分 {plan.Result.CompositeScore:0}）"
                        : "（尚无编制结果）")
                  + "\n"
                  + (o.LedgerMonths > 0
                        ? $"✔ 已写入月度计划台账 monthly_plan：{o.LedgerMonths} 个月 —— **下次开机仍在**。"
                        : "◆ **没有写进月度计划台账**：" + (ledgerErr.Length > 0 ? ledgerErr : "没有可写的月份")
                          + " —— 这份确定方案只活在本次会话里，关掉软件就没了。")
                  + "\n" + o.Readiness;
        return o;
    }

    /// <summary>
    /// 选定的那套<b>已经不在方案库里</b>了（被派生整批换掉之类）—— 把确定簿清空。
    ///
    /// <para><b>只在它自己被清掉时才清</b>：别人确定的方案不该被某个派生动作顺手取消。
    /// 「派生计划方案」点一下就把几何链辛苦跑出来的确定方案抹掉、还没有任何提示 ——
    /// 这个坑已经踩过一次，守卫留在这儿，和写入放在同一个类里，省得下次又只改一边。</para>
    /// </summary>
    public static bool ClearIfDropped()
    {
        var cur = ShortTermSchemeStore.Confirmed;
        if (cur == null || ShortTermSchemeStore.Schemes.Contains(cur)) return false;
        ShortTermSchemeStore.Confirmed = null;
        return true;
    }

    /// <summary>
    /// 把确定方案的逐月量写进 <c>monthly_plan</c> 月度计划台账 —— <b>跨会话的那一半</b>。
    /// 返回写了几个月；一个都没写时 <paramref name="err"/> 说明原因。
    ///
    /// <para><b>年月从哪来</b>：<c>PlanYear</c> + 逐月 <c>Label</c>。Label 认不出月份的行<b>不写</b> ——
    /// 编一个月份出来，会把这份计划盖到别的月份上，而两边看上去都正常。</para>
    ///
    /// <para><b>只写自营剥离</b>：外委那一列不是本方案算出来的，写 0 会把台账里原有的外委量抹掉。
    /// 上游台账里已有该月时，外委量原样保留。</para>
    ///
    /// <para>原在 <c>MonthlyStripSession.WriteToLedger</c>，<b>只有几何链走得到</b>。
    /// 提到这里之后三个入口都写台账，"确定簿是下游唯一事实来源"才第一次对所有路径成立。</para>
    /// </summary>
    private static int WriteToLedger(ShortTermPlan plan, out string err)
    {
        err = "";
        int n = 0, badLabel = 0;
        try
        {
            var svc = Data.EquipmentDataContext.Plan;
            int year = plan.PlanYear > 1900 ? plan.PlanYear : 0;
            if (year <= 0) { err = $"方案没有计划年份（PlanYear={plan.PlanYear}）"; return 0; }

            foreach (var m in plan.Months)
            {
                if (m == null) continue;
                // 月序→(年,月) 仍用 MonthlyStripSession 那份：它带着 M13 滚年的口径与判据，
                // 在这儿另写一遍就是第二套换算 —— 两套差一年时谁都不会报错。
                if (!PlanPeriodKeys.TrySplitYearMonth(year, m.Month, m.Label, out int yy, out int mo)) { badLabel++; continue; }

                var row = svc.Get(yy, mo) ?? new Data.Entities.MonthlyPlan { Year = yy, Month = mo };
                row.PlanCoalWanT = m.CoalWanT;
                row.PlanStripWanM3 = m.StripWanM3;
                // PlanOutsourceStripWanM3 原样保留 —— 它不是本方案算的，抹成 0 会让台账凭空少一块量
                svc.Upsert(row);
                n++;
            }
            if (badLabel > 0)
                err = $"有 {badLabel} 个月的期次定不出年月（首个标签「{plan.Months.FirstOrDefault()?.Label}」），那几个月没写";
        }
        catch (Exception ex) { err = $"写库失败（{ex.GetType().Name}: {ex.Message}）"; }
        return n;
    }

    /// <summary>
    /// <b>下游就绪自检</b> —— 确定簿是下游<b>唯一事实来源</b>，而下游读不到东西时<b>不会报错</b>：
    /// 三维模拟 <c>SimBuilder.Build</c> 全程 catch 不抛，缺什么就静默降级（层体退回垂直壁、
    /// 期次退回外推、设备/运输线一个都不画），界面上只是"没东西"。
    ///
    /// <para>所以在<b>入库这一刻</b>把"下游到底拿不拿得到"摆出来。<b>只报不拦</b>：
    /// 缺流、缺作业面都是合法状态（份额驱动那条链本来就没有流），拦下来反而不对。</para>
    ///
    /// <para>原是「月度计划编制」窗口的私有方法，只有那一个入口看得到；
    /// 另外两个入口确定完什么都不说。提到这里之后三处口径一致。</para>
    /// </summary>
    public static string DownstreamReadiness(ShortTermPlan cur)
    {
        var months = cur.Months;
        if (months == null || months.Count == 0) return "◆ 这份方案一个期次都没有 —— 三维模拟会退回「按当日盘子等速外推」。";

        int withFlows = months.Count(m => m.HasFlows);
        int withFace = months.Count(m => !string.IsNullOrWhiteSpace(m.ActiveFace));
        int withWd = months.Count(m => m.Workdays > 1e-9);
        int withAdv = months.Count(m => m.AdvanceM > 1e-9);

        var bits = new List<string>();
        bits.Add(withFlows == months.Count ? $"物料流 {withFlows}/{months.Count} ✔"
               : withFlows == 0 ? $"物料流 0/{months.Count} ◆ 全是份额摊分 —— 采排配对与三维运输线**没有去向明细**可用"
               : $"物料流 {withFlows}/{months.Count} ◆ 部分月份没有流，那几个月的内排率/运输功是空的");
        bits.Add(withFace == months.Count ? "主作业面 ✔"
               : $"主作业面 {withFace}/{months.Count} ◆ 缺的月份三维里标不出在哪个面作业");
        bits.Add(withWd == months.Count ? "作业日 ✔"
               : $"作业日 {withWd}/{months.Count} ◆ 缺的月份下游按 25 天折算卸点通过能力（引擎替你定的）");
        bits.Add(withAdv == months.Count ? "推进 ✔"
               : $"推进 {withAdv}/{months.Count} ◆ 缺的月份只能按 V实/(L×H) 反算，与计划自带值互校不了");

        // 放坡参数：三维层体按它长；读不到就静默退回**垂直壁**（本仓库钉过判据 I4）
        //   ⚠ 用计划自己的 HasSlopeGeometry（TaskLib 反射读的就是 ShortTermPlan 上那三个属性），
        //     别在这儿另写一套"算不算齐"的判断 —— 两处口径不一致时，这里说 ✔ 而那边照样退垂直壁。
        bits.Add(cur.HasSlopeGeometry
            ? $"放坡参数 ✔（坡面角 {cur.BenchFaceAngleDeg:0.#}° · 平盘 {cur.BermWidthM:0.#}m）"
            : "放坡参数 ◆ 缺（坡面角/平盘宽）—— 三维层体会**退回垂直壁**，不报错");

        return "下游就绪：" + string.Join(" · ", bits);
    }
}

/// <summary>月序/标签 → 真实年月（原 <c>MonthlyStripSession.TrySplitYearMonth</c>；先搬到这儿供确定入库用，量驱动窗口移植后共用）。</summary>
public static class PlanPeriodKeys
{
    /// <summary>① 月序（1 起）滚年；② 标签里带真年月（"2027-01" / "2027年1月"）；③ "M08" 按月序滚年。</summary>
    public static bool TrySplitYearMonth(int baseYear, int monthOrdinal, string? label, out int year, out int month)
    {
        year = 0; month = 0;
        if (baseYear <= 1900) return false;
        if (monthOrdinal >= 1)
        {
            year = baseYear + (monthOrdinal - 1) / 12;
            month = (monthOrdinal - 1) % 12 + 1;
            return true;
        }
        var s = (label ?? "").Trim();
        if (s.Length == 0) return false;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"((?:19|20)\d{2})\s*[-/年]\s*(\d{1,2})");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int y2) && int.TryParse(m.Groups[2].Value, out int mo2) && mo2 >= 1 && mo2 <= 12)
        { year = y2; month = mo2; return true; }
        var m3 = System.Text.RegularExpressions.Regex.Match(s, @"^[Mm](\d{1,2})$");
        if (m3.Success && int.TryParse(m3.Groups[1].Value, out int ord) && ord >= 1)
        {
            year = baseYear + (ord - 1) / 12;
            month = (ord - 1) % 12 + 1;
            return true;
        }
        return false;
    }
}
