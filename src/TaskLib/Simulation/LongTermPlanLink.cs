// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/LongTermPlanLink.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  中长远进度计划 ↔ 推演 的软接层（无编译期依赖 PlanLib）
//
//  ── 为什么单独一个文件、而不是继续塞进 SimBuilder ──
//
//  在这之前，年粒度的接线是**借短期的**：
//    · 期次   —— SimBuilder.ReadYearPeriods() 只读 LongTermSchemeStore.Confirmed，
//                读不到就直接外推（跳过了"有候选方案但还没点确定"这一大类）；
//    · H / L  —— SimMiningParams.Load() 读的是 **ShortTerm**SchemeStore.Confirmed，
//                也就是说：中长远的逐年推进距离，分母取自**月度方案**的台阶高/工作线长。
//    · 推进方位 —— 界面默认 0°，与中长远方案自己的 WorkLine.AdvanceAzimuthDeg 无关。
//
//  三条都不报错、都出数，所以看不出来。而中长远方案本来就自带这一整套
//  （LongTermPlan.BenchHeightM / WorkLine.WorkLineLenM / WorkLine.AdvanceAzimuthDeg，
//   逐年 PlanPeriod.AdvanceM 更是排产时按 v=Q/(L·H·ρ) 算好的），
//  借月度的等于把两个尺度的参数混成一套。⇒ 年粒度的一切来源集中到这里，一处可查。
//
//  ── 反射契约（改名即静默降级，不是编译错）──
//    PlanLib.LongTerm.LongTermSchemeStore  .Confirmed / .Schemes
//    PlanLib.LongTerm.LongTermPlan         .Name .Note .DesignCapacityWanTa .BenchHeightM
//                                          .StartYear .InnerDumpEnabled .InnerDumpStartYear
//                                          .EconomicStripRatioMax .SourceText .Periods .Result
//                                          .WorkLine{.WorkLineLenM .AdvanceAzimuthDeg .AdvanceModeText}
//    PlanLib.LongTerm.PlanPeriod           .Label .CoalWanT .StripWanM3 .Ratio .CapacityPct
//                                          .AdvanceM .PhaseText .DumpText .FlagText
//                                          .CumCoal .CumStrip .IsDesignCalcYear
//    PlanLib.LongTerm.LongTermResult       .ServiceLifeYears .TimeToCapacityYears .DesignCalcYearLabel
//                                          .StablePlateauYears .ProductionRatioPeak .InnerDumpPct
//                                          .Npv .CompositeScore .Ok
//  读不到一律**如实降级并写明**，绝不拿缺省值冒充计划值。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>中长远方案里的一年（推演只需要这些字段；其余留在 PlanLib 侧）。</summary>
public sealed class LongTermYearRow
{
    /// <summary>年标签（"2027"）。</summary>
    public string Label { get; set; } = "";
    /// <summary>本年采出量（万t）。</summary>
    public double CoalWanT { get; set; }
    /// <summary>本年剥离量（万m³）。</summary>
    public double StripWanM3 { get; set; }
    /// <summary>本年生产（时间）剥采比 m³/t。</summary>
    public double Ratio { get; set; }
    /// <summary>达产率 %（0..100）。</summary>
    public double CapacityPct { get; set; }
    /// <summary>
    /// 计划自带的本年推进距离 m —— 排产时按 v = Q/(L·H·ρ) 算的，**分母是中长远方案自己的 L/H**。
    /// 0 = 计划没给。它与推演按 V实/(L×H) 反算出来的那个值互为校核，两者口径不同：
    /// 计划那个只数煤（产能反算推进度），推演那个数本期挖除的全部实方。
    /// </summary>
    public double AdvanceM { get; set; }
    /// <summary>生产时相（基建/爬坡/稳产/减产）。</summary>
    public string PhaseText { get; set; } = "";
    /// <summary>排弃去向（内排/外排）。</summary>
    public string DumpText { get; set; } = "";
    /// <summary>标记（"◆达产"）。</summary>
    public string FlagText { get; set; } = "";
    /// <summary>累计采出（万t）/ 累计剥离（万m³）。</summary>
    public double CumCoal { get; set; }
    public double CumStrip { get; set; }
    /// <summary>设计计算年（首达 A_p 那一年）。</summary>
    public bool IsDesignCalcYear { get; set; }

    public bool IsInternalDump => DumpText.Contains("内排", StringComparison.Ordinal);
}

/// <summary>一套中长远进度计划方案（推演侧视图）。</summary>
public sealed class LongTermSchemeInfo
{
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>是「确定进度计划」选定的那一套。</summary>
    public bool IsConfirmed { get; set; }
    /// <summary>已排产（Result 非空）。未排产的方案没有逐年表，演不了。</summary>
    public bool Solved { get; set; }

    /// <summary>设计生产能力 A_p（万t/a）—— 逐年图上那条达产线。0 = 没读到。</summary>
    public double DesignCapacityWanTa { get; set; }
    /// <summary>采场台阶高 H（m）。0 = 没读到。</summary>
    public double BenchHeightM { get; set; }
    /// <summary>工作线长 L（m）。0 = 没读到。</summary>
    public double WorkLineLenM { get; set; }
    /// <summary>推进方位（°）。</summary>
    public double AdvanceAzimuthDeg { get; set; }
    /// <summary>推进方式文案（平行推进/定点回转/动点回转）。</summary>
    public string AdvanceModeText { get; set; } = "";
    /// <summary>起始年。</summary>
    public int StartYear { get; set; }
    /// <summary>经济合理剥采比 n经（m³/t）—— 逐年剥采比折线上的报警线。0 = 没读到。</summary>
    public double EconomicStripRatioMax { get; set; }
    /// <summary>内排：启用与否 + 起转年（相对起始年的偏移）。</summary>
    public bool InnerDumpEnabled { get; set; }
    public int InnerDumpStartYear { get; set; }
    /// <summary>来源文案（承接的开采程序方案名，或"（样例储量）"）。</summary>
    public string SourceText { get; set; } = "";

    /// <summary>逐年表（排产产物）。</summary>
    public List<LongTermYearRow> Years { get; set; } = new();

    // ── 系统指标（右栏"方案指标"那一块）──
    public double ServiceLifeYears { get; set; }
    public double TimeToCapacityYears { get; set; }
    public string DesignCalcYearLabel { get; set; } = "—";
    public double StablePlateauYears { get; set; }
    public double ProductionRatioPeak { get; set; }
    public double InnerDumpPct { get; set; }
    public double Npv { get; set; }
    public double CompositeScore { get; set; }
    public bool Ok { get; set; }

    /// <summary>内排起转的那一年在逐年表里的下标；未启用/未发生返回 -1。</summary>
    public int InnerDumpSwitchIndex
    {
        get
        {
            for (int i = 0; i < Years.Count; i++)
                if (Years[i].IsInternalDump) return i;
            return -1;
        }
    }

    public string Caption =>
        $"{Name}　A_p {DesignCapacityWanTa:0}万t/a · L={WorkLineLenM:0}m · H={BenchHeightM:0.##}m · 推进 {AdvanceAzimuthDeg:0}°"
        + (Years.Count > 0 ? $" · {Years.Count} 年" : " · 未排产");
}

/// <summary>
/// 中长远方案的软读入口。<b>整个推演侧只经这一个类读 PlanLib.LongTerm</b>，
/// 读不到就返回空表 + 一句降级原因，调用方据此走"三态"，不外推冒充。
/// </summary>
public static class LongTermPlanLink
{
    private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;

    /// <summary>上一次装载的降级原因（界面直接显示这一句）。空 = 正常。</summary>
    public static string LastNote { get; private set; } = "";

    /// <summary>
    /// 列出会话里全部中长远方案（确定的排在最前）。
    ///
    /// <para><b>为什么不能只看 Confirmed</b>：那个属性是纯内存的、要点过「确定进度计划」才有值，
    /// 关一次软件就没了。原来的 <c>ReadYearPeriods</c> 只读它、读不到就直接外推 ——
    /// 于是"派生了 4 套候选、还没点确定"这种最常见的状态，时间轴上出来的是一串**推算年**，
    /// 而推算年和真年份在界面上长得一模一样。与月度那条路 2026-08-10 修过的死路是同一个。</para>
    /// </summary>
    public static List<LongTermSchemeInfo> ListSchemes()
    {
        var list = new List<LongTermSchemeInfo>();
        LastNote = "";
        try
        {
            var t = Type.GetType("PlanLib.LongTerm.LongTermSchemeStore, PlanLib");
            if (t == null)
            {
                LastNote = "PlanLib 未加载（本窗口独立起时的正常状态）—— 读不到任何中长远方案。";
                return list;
            }

            object? confirmed = t.GetProperty("Confirmed", Pub)?.GetValue(null);
            var seen = new List<object>();

            if (confirmed != null) seen.Add(confirmed);
            if (t.GetProperty("Schemes", Pub)?.GetValue(null) is IEnumerable rows)
                foreach (var r in rows)
                    if (r != null && !seen.Any(x => ReferenceEquals(x, r))) seen.Add(r);

            foreach (var p in seen)
            {
                var info = Read(p);
                info.IsConfirmed = confirmed != null && ReferenceEquals(p, confirmed);
                list.Add(info);
            }

            if (list.Count == 0)
            {
                LastNote = "中长远方案库是空的 —— 先在「中长远进度计划编制」里配置基础约束、再「规划计算」排一次产。";
            }
            else if (confirmed == null)
            {
                // ⚠ 措辞不许写死成"取综合得分最高的" —— CompositeScore 只有走过
                //   「方案综合对比」/「一键编制」才会被 LongTermComparer.Score 填上；
                //   直接派生出来的候选全是 0，那时按分排序等于"按原顺序取第一套"。
                //   说成"得分最高"会让人以为系统比过了，而根本没比。
                bool scored = list.Any(s => s.CompositeScore > 1e-9);
                LastNote = $"方案库里有 {list.Count} 套，但**没有一套点过「确定进度计划」** —— "
                         + (scored
                            ? "已默认取综合得分最高的已排产方案来演；"
                            : "这些方案**还没有比过分**（综合得分都是 0，要评分请走「方案综合对比」或「一键编制」），"
                              + "已按方案库顺序取第一套已排产的来演；")
                         + "要固定某一套请回「规划计算」点确定。";
            }
        }
        catch (Exception ex)
        {
            LastNote = $"读中长远方案失败（{ex.GetType().Name}）：{ex.Message}";
        }
        return list;
    }

    /// <summary>
    /// 挑一套来演：<b>指定名 → 已确定 → 综合得分最高的已排产 → 第一套已排产</b>。
    /// 全都不成立返回 null（调用方必须如实说"没有可演的方案"，不许外推）。
    /// </summary>
    public static LongTermSchemeInfo? Pick(IReadOnlyList<LongTermSchemeInfo> all, string? wantName)
    {
        if (all.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(wantName))
        {
            var hit = all.FirstOrDefault(s => string.Equals(s.Name, wantName, StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        return all.FirstOrDefault(s => s.IsConfirmed && s.Years.Count > 0)
            ?? all.Where(s => s.Years.Count > 0).OrderByDescending(s => s.CompositeScore).FirstOrDefault()
            ?? all.FirstOrDefault(s => s.IsConfirmed)
            ?? all[0];
    }

    /// <summary>按方案的 H / L / 推进方位造推演参数。<b>不落到短期方案上</b>。</summary>
    public static SimMiningParams ParamsOf(LongTermSchemeInfo? s)
    {
        var p = new SimMiningParams();
        if (s == null)
        {
            p.SourceLabel = "没有可演的中长远方案，采场 H/L 用界面缺省值（推进距离仅示意）";
            return p;
        }
        if (s.BenchHeightM > 1e-6 && s.WorkLineLenM > 1e-6)
        {
            p.BenchHeightM = s.BenchHeightM;
            p.WorkLineLengthM = s.WorkLineLenM;
            p.Resolved = true;
            p.SourceLabel = $"采场 H/L 取自**中长远**方案「{s.Name}」（H={s.BenchHeightM:0.##}m · L={s.WorkLineLenM:0}m）"
                          + "　—— 不是短期月度方案的那一套";
        }
        else
        {
            p.SourceLabel = $"中长远方案「{s.Name}」未录台阶高/工作线长，采场 H/L 用界面缺省值（推进距离仅示意）";
        }
        // 中长远方案上没有 α/W 字段（那是采场设计侧的量），保持未解析 → 层体按垂直壁建。
        p.SlopeSourceLabel = "中长远方案不带坡面角 α / 平盘宽 W（那是采场设计侧的量），层体按**垂直壁**建；"
                           + "要看放坡形态请在上方填 α/W 后点「应用参数」。";
        return p;
    }

    // ── 反射细活 ────────────────────────────────────────────────────────────

    private static LongTermSchemeInfo Read(object plan)
    {
        var s = new LongTermSchemeInfo
        {
            Name = Str(plan, "Name"),
            Note = Str(plan, "Note"),
            DesignCapacityWanTa = Dbl(plan, "DesignCapacityWanTa"),
            BenchHeightM = Dbl(plan, "BenchHeightM"),
            StartYear = (int)Dbl(plan, "StartYear"),
            EconomicStripRatioMax = Dbl(plan, "EconomicStripRatioMax"),
            InnerDumpEnabled = Bl(plan, "InnerDumpEnabled"),
            InnerDumpStartYear = (int)Dbl(plan, "InnerDumpStartYear"),
            SourceText = Str(plan, "SourceText"),
        };
        if (s.Name.Length == 0) s.Name = "（未命名方案）";

        if (Get(plan, "WorkLine") is { } wl)
        {
            s.WorkLineLenM = Dbl(wl, "WorkLineLenM");
            s.AdvanceAzimuthDeg = Dbl(wl, "AdvanceAzimuthDeg");
            s.AdvanceModeText = Str(wl, "AdvanceModeText");
        }

        if (Get(plan, "Result") is { } r)
        {
            s.Solved = true;
            s.ServiceLifeYears = Dbl(r, "ServiceLifeYears");
            s.TimeToCapacityYears = Dbl(r, "TimeToCapacityYears");
            s.DesignCalcYearLabel = Str(r, "DesignCalcYearLabel");
            s.StablePlateauYears = Dbl(r, "StablePlateauYears");
            s.ProductionRatioPeak = Dbl(r, "ProductionRatioPeak");
            s.InnerDumpPct = Dbl(r, "InnerDumpPct");
            s.Npv = Dbl(r, "Npv");
            s.CompositeScore = Dbl(r, "CompositeScore");
            s.Ok = Bl(r, "Ok");
            if (s.DesignCalcYearLabel.Length == 0) s.DesignCalcYearLabel = "—";
        }

        if (Get(plan, "Periods") is IEnumerable years)
        {
            foreach (var y in years)
            {
                if (y == null) continue;
                var row = new LongTermYearRow
                {
                    Label = Str(y, "Label"),
                    CoalWanT = Dbl(y, "CoalWanT"),
                    StripWanM3 = Dbl(y, "StripWanM3"),
                    Ratio = Dbl(y, "Ratio"),
                    CapacityPct = Dbl(y, "CapacityPct"),
                    AdvanceM = Dbl(y, "AdvanceM"),
                    PhaseText = Str(y, "PhaseText"),
                    DumpText = Str(y, "DumpText"),
                    FlagText = Str(y, "FlagText"),
                    CumCoal = Dbl(y, "CumCoal"),
                    CumStrip = Dbl(y, "CumStrip"),
                    IsDesignCalcYear = Bl(y, "IsDesignCalcYear"),
                };
                if (row.Label.Length == 0) row.Label = $"第{s.Years.Count + 1}期";
                s.Years.Add(row);
            }
        }
        return s;
    }

    private static object? Get(object o, string prop)
    {
        try { return o.GetType().GetProperty(prop)?.GetValue(o); }
        catch { return null; }
    }

    private static string Str(object o, string prop)
    {
        try { return o.GetType().GetProperty(prop)?.GetValue(o)?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static double Dbl(object o, string prop)
    {
        try
        {
            var v = o.GetType().GetProperty(prop)?.GetValue(o);
            return v is IConvertible c ? Convert.ToDouble(c, CultureInfo.InvariantCulture) : 0;
        }
        catch { return 0; }
    }

    private static bool Bl(object o, string prop)
    {
        try { return o.GetType().GetProperty(prop)?.GetValue(o) is bool b && b; }
        catch { return false; }
    }
}
