// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/MonthlyProcessSummary.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Units;             // UnitAssignment / UnitKind
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  月度工序量汇总 —— 穿爆采运排，全矿一张表。
//
//  这是「任务编制」真正要吃的那张表：本月全矿穿多少延米、耗多少炸药、爆几次、
//  采多少实方、推排多少占容。逐面对话框只回答"这一个面"，编制要的是合计。
//
//  ★ 量的基数取【排产结果】，不是备采储量。
//    备采储量是"这个面还剩多少可采"，排产结果才是"这个月真的要采多少"。
//    拿备采算出来的延米看着挺像回事，而它对应的是一个没人打算在这个月干完的量。
//
//  ★ 三种"账对不上"必须单列，不许并进合计里悄悄消失（[[audit-against-the-input]]）：
//      ① 没归属到任何作业面的单元 —— 它们有量，但没有工艺参数，工序量算不了；
//      ② 要穿爆但参数算不出来的面 —— 台阶高/孔网缺，延米会是 0；
//      ③ 免爆的面 —— 延米也是 0，但那是"不用穿"，与 ② 完全两回事。
//    ②③ 的延米都是 0，报表上长得一模一样 —— 所以必须分开数、分开报。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个作业面本月的工序量。</summary>
public sealed class FaceProcessQuantity
{
    public string FaceName { get; init; } = "";
    public string FaceCode { get; init; } = "";
    public string MaterialName { get; init; } = "";
    /// <summary>本面本月走不走穿爆。</summary>
    public bool NeedsBlast { get; init; }
    /// <summary>穿爆参数够不够算出量（免爆面恒为 true —— 它没有要算的）。</summary>
    public bool Computable { get; init; }
    /// <summary>算不出来时说清缺什么（Computable=true 时为空）。</summary>
    public string Note { get; init; } = "";

    /// <summary>本月采出（原位实方 m³）。</summary>
    public double InSituM3 { get; init; }
    /// <summary>本月排弃占容（m³ = V实×Kr；煤面为 0）。</summary>
    public double DumpM3 { get; init; }
    /// <summary>穿孔延米（m）。免爆或算不出来时为 0 —— 靠 <see cref="NeedsBlast"/>/<see cref="Computable"/> 区分。</summary>
    public double DrillMeters { get; init; }
    public double HoleCount { get; init; }
    /// <summary>炸药量（kg）。</summary>
    public double PowderKg { get; init; }
    public int BlastCount { get; init; }

    /// <summary>台阶高用的是哪个（工艺里填的 / 库表兜底 / 没有）—— 溯源。</summary>
    public string BenchSource { get; init; } = "";

    /// <summary>这一行的工序量状态文案。<b>「免爆」和「算不出」必须能分开读</b>。</summary>
    public string StateText => !NeedsBlast ? "免爆" : Computable ? "已算" : "◆ 算不出";
}

/// <summary>全矿本月的工序量汇总。</summary>
public sealed class MonthlyProcessSummary
{
    public int Year { get; init; }
    public int Month { get; init; }

    public List<FaceProcessQuantity> Faces { get; } = new();

    /// <summary>没归属到任何作业面的单元：个数与量。<b>它们的工序量算不了</b>。</summary>
    public int UnattributedUnits { get; internal set; }
    public double UnattributedInSituM3 { get; internal set; }

    public List<string> Notes { get; } = new();

    // ── 合计（只对【算得出来】的那些求和 —— 把算不出的当 0 加进来就是把缺口藏起来）──
    public double TotalInSituM3 => Faces.Sum(f => f.InSituM3);
    public double TotalDumpM3 => Faces.Sum(f => f.DumpM3);
    public double TotalDrillMeters => Faces.Where(f => f.Computable).Sum(f => f.DrillMeters);
    public double TotalHoleCount => Faces.Where(f => f.Computable).Sum(f => f.HoleCount);
    public double TotalPowderKg => Faces.Where(f => f.Computable).Sum(f => f.PowderKg);
    public int TotalBlastCount => Faces.Where(f => f.Computable).Sum(f => f.BlastCount);

    /// <summary>要穿爆却算不出量的面 / 它们的实方 —— <b>这一块的穿孔需求是缺的，不是 0</b>。</summary>
    public IEnumerable<FaceProcessQuantity> NotComputable => Faces.Where(f => f.NeedsBlast && !f.Computable);
    public double NotComputableInSituM3 => NotComputable.Sum(f => f.InSituM3);

    /// <summary>免爆面的实方（这一块<b>确实</b>不用穿，与上面那个不是一回事）。</summary>
    public double NoBlastInSituM3 => Faces.Where(f => !f.NeedsBlast).Sum(f => f.InSituM3);

    /// <summary>账平不平：逐面实方 + 未归属实方 = 本月总实方。</summary>
    public double AccountedInSituM3 => TotalInSituM3 + UnattributedInSituM3;

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"{Year:0000}-{Month:00} 月度工序量：采出 {TotalInSituM3 / 1e4:0.##} 万m³实方 · "
                + $"排弃 {TotalDumpM3 / 1e4:0.##} 万m³占容 · "
                + $"穿孔 {TotalDrillMeters:N0} 延米（{TotalHoleCount:N0} 孔）· "
                + $"炸药 {TotalPowderKg / 1000:0.##} t · 爆破 {TotalBlastCount} 次");

        // 三种"没算进去的量"分开报 —— 合并成一句，读的人就分不清哪块是真不用穿
        if (NotComputableInSituM3 > 1e-6)
            sb.Append($"\n◆ 另有 {NotComputableInSituM3 / 1e4:0.##} 万m³（{NotComputable.Count()} 个面）"
                    + "**要穿爆但参数算不出来**，上面的延米与炸药量<b>不含这一块</b> —— "
                    + "这是缺口，不是 0。");
        if (NoBlastInSituM3 > 1e-6)
            sb.Append($"\n· 其中 {NoBlastInSituM3 / 1e4:0.##} 万m³ 走免爆（表土/风化层/煤），本来就没有穿爆量。");
        if (UnattributedUnits > 0)
            sb.Append($"\n◆ 还有 {UnattributedUnits} 个单元、{UnattributedInSituM3 / 1e4:0.##} 万m³"
                    + "**没归属到任何作业面**，工序量一概算不了 —— 这一块也不在上面的合计里。");
        return sb.ToString();
    }

    /// <summary>导出 CSV（任务编制/火工品计划直接吃）。</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Year:0000}-{Month:00} 月度工序量汇总【计划】—— 基数取自本月排产结果，非实绩");
        sb.AppendLine("# 免爆 与 算不出 的延米都是 0，靠「状态」列区分 —— 别只看数字");
        sb.AppendLine("作业面,面编号,物料,状态,台阶高来源,采出m3实方,排弃m3占容,穿孔延米m,孔数,炸药kg,爆破次数,说明");
        foreach (var f in Faces.OrderByDescending(x => x.InSituM3))
            sb.AppendLine(string.Join(",", new[]
            {
                Q(f.FaceName), Q(f.FaceCode), Q(f.MaterialName), f.StateText, Q(f.BenchSource),
                f.InSituM3.ToString("0.##", CultureInfo.InvariantCulture),
                f.DumpM3.ToString("0.##", CultureInfo.InvariantCulture),
                f.Computable ? f.DrillMeters.ToString("0.#", CultureInfo.InvariantCulture) : "",
                f.Computable ? f.HoleCount.ToString("0", CultureInfo.InvariantCulture) : "",
                f.Computable ? f.PowderKg.ToString("0.#", CultureInfo.InvariantCulture) : "",
                f.Computable ? f.BlastCount.ToString(CultureInfo.InvariantCulture) : "",
                Q(f.Note),
            }));

        if (UnattributedUnits > 0)
            sb.AppendLine(string.Join(",", new[]
            {
                "（未归属单元）", "", "", "◆ 无工艺", "",
                UnattributedInSituM3.ToString("0.##", CultureInfo.InvariantCulture), "", "", "", "", "",
                Q($"{UnattributedUnits} 个单元没配上作业面，工序量算不了"),
            }));

        sb.AppendLine();
        sb.AppendLine($"合计,,,,,{TotalInSituM3:0.##},{TotalDumpM3:0.##},{TotalDrillMeters:0.#},"
                    + $"{TotalHoleCount:0},{TotalPowderKg:0.#},{TotalBlastCount},"
                    + Q("延米/孔数/炸药/次数只含【算得出来】的面"));
        return sb.ToString();
    }

    private static string Q(string s)
        => (s ?? "").IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? (s ?? "")
           : "\"" + s.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// 由<b>排产结果</b> + 作业面工艺 + 归属，算全矿月度工序量。<b>纯计算</b>。
/// </summary>
public static class MonthlyProcessRollup
{
    /// <summary>
    /// 汇总。
    /// </summary>
    /// <param name="assignments">本月排产结果（<c>UnitPlanResult.Assignments</c>）—— 量的基数。</param>
    /// <param name="faces">作业面清单（带工艺）。</param>
    /// <param name="unitToFace">单元→面的归属（<see cref="FaceUnitResolver"/> 出的那份）。</param>
    /// <param name="benchHeightOf">按 face_code 取库表台阶高的委托（取不到给 0）。可空。</param>
    public static MonthlyProcessSummary Build(IEnumerable<UnitAssignment>? assignments,
                                              IEnumerable<WorkingFace>? faces,
                                              IReadOnlyDictionary<string, string>? unitToFace,
                                              int year, int month,
                                              Func<string, double>? benchHeightOf = null)
    {
        var res = new MonthlyProcessSummary { Year = year, Month = month };
        var aList = (assignments ?? Enumerable.Empty<UnitAssignment>())
                    .Where(a => a != null && a.UnitId.Length > 0).ToList();
        var fList = (faces ?? Enumerable.Empty<WorkingFace>())
                    .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name)).ToList();

        if (aList.Count == 0)
        {
            res.Notes.Add("· 本月没有排产结果 —— 先跑一次「采掘单元清单 → 按目标排产」。");
            return res;
        }

        // ── 按面归集量：实方 + 排弃占容 ────────────────────────────────
        var inSitu = new Dictionary<string, double>(StringComparer.Ordinal);
        var dump = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var a in aList)
        {
            string? face = null;
            if (unitToFace != null && unitToFace.TryGetValue(a.UnitId, out string? f0)) face = f0;
            if (string.IsNullOrWhiteSpace(face))
            {
                // 未归属：量单独记账，绝不并进某个面
                res.UnattributedUnits++;
                res.UnattributedInSituM3 += Math.Max(0, a.InSituM3);
                continue;
            }
            inSitu[face] = (inSitu.TryGetValue(face, out double v) ? v : 0) + Math.Max(0, a.InSituM3);
            // 排弃占容取排产已经算好的那一份（UnitFlow.DumpM3），不在这里拿 Kr 再乘一遍
            double d = a.Flows?.Sum(x => Math.Max(0, x.DumpM3)) ?? 0;
            dump[face] = (dump.TryGetValue(face, out double w) ? w : 0) + d;
        }

        // ── 逐面算工序量 ──────────────────────────────────────────────
        foreach (var f in fList)
        {
            double v = inSitu.TryGetValue(f.Name, out double a1) ? a1 : 0;
            if (v <= 1e-9 && !(dump.TryGetValue(f.Name, out double d0) && d0 > 1e-9))
                continue;                                   // 这个面本月没量，不出行（出了就是一排 0）

            var p = f.Process ?? new FaceProcessChain();
            double benchFallback = 0; string benchSrc;
            if (p.BenchHeightM > 0) benchSrc = $"工艺 {p.BenchHeightM:0.##}m";
            else
            {
                benchFallback = benchHeightOf?.Invoke(f.FaceCode ?? "") ?? 0;
                benchSrc = benchFallback > 0 ? $"台账 {benchFallback:0.##}m" : "◆ 没有";
            }

            bool needBlast = p.ResolveDrilling(f.MaterialCode);
            var bad = p.CheckComputable(f.Name, f.MaterialCode, benchFallback);
            bool ok = bad.Count == 0;

            res.Faces.Add(new FaceProcessQuantity
            {
                FaceName = f.Name,
                FaceCode = f.FaceCode ?? "",
                MaterialName = f.MaterialName,
                NeedsBlast = needBlast,
                Computable = ok,
                Note = ok ? "" : string.Join("；", bad),
                BenchSource = benchSrc,
                InSituM3 = v,
                DumpM3 = dump.TryGetValue(f.Name, out double d1) ? d1 : 0,
                DrillMeters = needBlast && ok ? p.MonthlyDrillMeters(v, benchFallback) : 0,
                HoleCount = needBlast && ok ? p.MonthlyHoleCount(v, benchFallback) : 0,
                PowderKg = needBlast && ok ? p.MonthlyPowderKg(v) : 0,
                BlastCount = needBlast && ok ? p.MonthlyBlastCount(v) : 0,
            });
        }

        // ── 口径说明与缺口，逐条报 ─────────────────────────────────────
        res.Notes.Add("· 基数取自【本月排产结果】的原位实方，不是备采储量 —— "
                    + "备采是「还剩多少可采」，排产结果才是「这个月真的要采多少」。");
        res.Notes.Add("· 排弃占容取排产已算好的 UnitFlow.DumpM3（V实×Kr），本层<b>不再乘一遍 Kr</b>。");

        int nc = res.NotComputable.Count();
        if (nc > 0)
            res.Notes.Add($"◆ {nc} 个面要穿爆但参数算不出来（合计 {res.NotComputableInSituM3 / 1e4:0.##} 万m³），"
                        + "它们的延米/炸药<b>没有计入合计</b>。"
                        + "◆ 注意：它们和免爆面的延米<b>都是 0</b>，只有「状态」列分得开 —— "
                        + "把这一块当成 0 加进合计，等于把缺口藏起来。");
        if (res.UnattributedUnits > 0)
            res.Notes.Add($"◆ {res.UnattributedUnits} 个单元没归属到作业面（{res.UnattributedInSituM3 / 1e4:0.##} 万m³），"
                        + "没有工艺参数可用，工序量一概算不了。去「确定开采程序」补面，或在归属里手工指定。");

        // 账要平：逐面 + 未归属 = 排产总量。差了就是有量在中间掉了。
        double total = aList.Sum(a => Math.Max(0, a.InSituM3));
        if (Math.Abs(res.AccountedInSituM3 - total) > Math.Max(1e-6, total * 1e-9))
            res.Notes.Add($"◆ 账没对上：逐面 {res.TotalInSituM3:0.##} + 未归属 {res.UnattributedInSituM3:0.##} "
                        + $"= {res.AccountedInSituM3:0.##} ≠ 排产总量 {total:0.##} m³ —— 有量在汇总里掉了。");

        return res;
    }
}
