// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimFlowParamResolver.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  车流强度三个输入的取数 —— 每一项都必须带得回「这个数打哪来」
//
//  λ = T/(W·H) 与三个数成简单比例：错一个，图上的车数整体等比例错，而画面看不出任何异常。
//  所以本类的产物不是三个 double，是**三个 (值, 来源)**。
//  取不到就把来源留空 —— <see cref="SimFlowParams.Resolved"/> 因此为 false，
//  图例与状态栏整体标「示意」。**绝不静默用缺省值填上去**。
//
//  ── 三个数各自的家（都不是本文件编的）──
//   · 载重 W_t   ← `dispatch_rule × equipment_model`（PitMine3D.Kylin.Data.Legacy.CsvDataStore，
//                  它已经把车型载重 join 进规则；裸的 DispatchRule 没有这一列）。
//   · 期作业小时 H ← 已确定的短期月度方案：该月**有效作业日**（已含季节/检修降效）
//                  × 每日班次 × 班时长。前两项是现场参数，第三项没有家 —— 见下。
//   · 车速         ← RoadLib.Routing.TruckProfile（坡阻模型的缺省经验值）。
//
//  ── ⚠ 班时长（h/班）在本仓库**没有家** ──
//  FieldParams 有 StandardWorkdays / ShiftsPerDay，没有「一个班几小时」。
//  本类按 8 h/班 折算，并把这一句**写进来源文案**（"班时长按 8h 折算 —— 现场参数里没有这一项"）。
//  这不是「缺省值冒充真值」：值的来源里明说了哪一段是假设的，读的人一眼看得见该去补哪一格。
//  真要补，正确做法是往 FieldParams 加一个 ShiftHours 并在「现场参数提取」开输入口，
//  而不是在这里换一个数。
//
//  ── 为什么软读 PlanLib（反射）──
//  TaskLib 不引用 PlanLib（`TaskLib.csproj` 只引 Platform / GeoDataBase / RoadLib / BlockModelLib）。
//  与 SimBuilder.ReadMonthPeriods 同一套写法，不另起一种机制。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>一个带出处的标量。<see cref="Source"/> 为空 = 没取到（调用方据此标「示意」）。</summary>
public readonly record struct SimSourcedValue(double Value, string Source)
{
    public bool Resolved => Source.Length > 0 && Value > 0;
}

public static class SimFlowParamResolver
{
    /// <summary>班时长（h/班）。<b>本仓库没有这一项</b>，见文件头。</summary>
    public const double AssumedShiftHours = 8.0;

    /// <summary>
    /// 单车载重 W_t。取 `dispatch_rule` 里所有有载重的规则的**中位数**
    /// （不取平均：一条录错的 999 t 会把平均拖走，中位数不会）。
    /// </summary>
    public static SimSourcedValue ResolvePayloadT()
    {
        try
        {
            var store = new PitMine3D.Kylin.Data.Legacy.CsvDataStore();
            var rules = store.GetDispatchRules() ?? new List<PitMine3D.Kylin.Data.Legacy.DispatchRule>();
            var w = rules.Select(r => r.TruckPayloadT).Where(v => v > 1e-6).OrderBy(v => v).ToList();
            if (w.Count == 0) return new SimSourcedValue(0, "");

            double median = w.Count % 2 == 1 ? w[w.Count / 2] : (w[w.Count / 2 - 1] + w[w.Count / 2]) * 0.5;
            string spread = w.Count > 1 ? $"，{w.Count} 条规则跨 {w[0]:0.#}~{w[^1]:0.#} t" : "";
            return new SimSourcedValue(median, $"设备编组台账中位数{spread}");
        }
        catch { return new SimSourcedValue(0, ""); }
    }

    /// <summary>
    /// 本期作业小时 H_期 = 该期有效作业日 × 每日班次 × 班时长。
    /// <param name="periodLabel">期标签（<c>SimFrame.Period</c> / <c>Label</c>），用来在月行里找那一月。</param>
    /// </summary>
    public static SimSourcedValue ResolveWorkHours(string? periodLabel)
    {
        try
        {
            var t = Type.GetType("PlanLib.ShortTerm.ShortTermSchemeStore, PlanLib");
            object? plan = t?.GetProperty("Confirmed", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (plan == null) return new SimSourcedValue(0, "");

            object? field = Prop(plan, "Field");
            double shifts = D(field, "ShiftsPerDay");
            if (shifts <= 0) return new SimSourcedValue(0, "");

            // 先找**本期那一月**的有效作业日（已含季节/检修降效）；找不到退到月标准作业日。
            double days = 0;
            string how = "";
            if (Prop(plan, "Months") is IEnumerable rows && !string.IsNullOrWhiteSpace(periodLabel))
            {
                foreach (var r in rows)
                {
                    if (r == null) continue;
                    string lab = S(r, "Label");
                    if (lab.Length == 0 || !Match(lab, periodLabel!)) continue;
                    double d = D(r, "Workdays");
                    if (d > 0) { days = d; how = $"「{lab}」有效作业日"; }
                    break;
                }
            }
            if (days <= 0)
            {
                days = D(field, "StandardWorkdays");
                if (days > 0) how = "月标准作业日（**没匹配到本期那一月**，季节/检修降效未计）";
            }
            if (days <= 0) return new SimSourcedValue(0, "");

            double hours = days * shifts * AssumedShiftHours;
            return new SimSourcedValue(hours,
                $"已确定短期方案 · {how} {days:0.#}d × {shifts:0}班 × {AssumedShiftHours:0}h"
              + "（班时长按 8h 折算 —— 现场参数里没有这一项）");
        }
        catch { return new SimSourcedValue(0, ""); }
    }

    /// <summary>
    /// 期标签匹配：计划里的月标签写法不一定与时间轴一致（<c>2026-08</c> / <c>2026年8月</c> / <c>第1月</c>）。
    /// 只认**同为 yyyy-MM 归一化后相等**，认不出就当没匹配上（宁可退到月标准作业日并说明，
    /// 也不要把 7 月的作业日当成 8 月的用 —— 那种错在图上完全看不出来）。
    /// </summary>
    private static bool Match(string a, string b)
    {
        string na = Norm(a), nb = Norm(b);
        return na.Length > 0 && na == nb;
    }

    private static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var digits = new List<int>();
        int cur = -1;
        foreach (char c in s)
        {
            if (c >= '0' && c <= '9') { cur = cur < 0 ? c - '0' : cur * 10 + (c - '0'); }
            else if (cur >= 0) { digits.Add(cur); cur = -1; }
        }
        if (cur >= 0) digits.Add(cur);
        // 需要「年 + 月」两个数才算认得出；只有一个数（第1月）没有年份，不认。
        if (digits.Count < 2) return "";
        int y = digits[0], m = digits[1];
        if (y < 1900 || y > 2999 || m < 1 || m > 12) return "";
        return y.ToString(CultureInfo.InvariantCulture) + "-" + m.ToString("00", CultureInfo.InvariantCulture);
    }

    private static object? Prop(object? o, string p)
    {
        if (o == null) return null;
        try { return o.GetType().GetProperty(p)?.GetValue(o); }
        catch { return null; }
    }

    private static double D(object? o, string p)
    {
        object? v = Prop(o, p);
        try { return v == null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static string S(object? o, string p) => Prop(o, p)?.ToString() ?? "";
}
