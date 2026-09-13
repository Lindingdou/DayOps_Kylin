// 忠实移植自原 PitMine3D Modules/TaskLib/Scheduling/MonthSchedulePlanner.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Scheduling;

/// <summary>整月工序排班的结果 + 它是怎么推出来的。</summary>
public sealed class MonthSchedule
{
    public DateTime Month { get; init; }
    public List<DateTime> Workdays { get; } = new();
    public MonthDecomposition Decomposition { get; init; } = new();

    public List<FaceCapacity> FaceCaps { get; } = new();
    public SiteCapacity? SiteCap { get; init; }

    /// <summary>推导链：从月量与设备一路推到日工序，每一步都写明。</summary>
    public List<string> Derivation { get; } = new();
    public List<string> Violations => Decomposition.Violations;

    public IEnumerable<DayProcessOrder> OnDay(DateTime d) => Decomposition.OnDay(d);

    /// <summary>某天某工序的合计（界面按天取数用）。</summary>
    public double OnDayTotal(DateTime d, ProcessType p)
        => Decomposition.OnDay(d).Where(o => o.Process == p).Sum(o => o.Quantity);
}

/// <summary>
/// 月度任务量 + 设备信息 → 整月的日工序排班。
///
/// <para><b>入口只要两样东西</b>：月度计划里各面的月量，和这个矿的设备信息。
/// 日能力上限、穿孔能力、排土承接、开月爆堆全部**推**出来
/// （见 <see cref="CapacityFromEquipment"/>），不再要人填第二套参数 ——
/// 人要填的只有月量本身，那是计划不是参数。</para>
///
/// <para><b>推导链是结果的一部分</b>：<see cref="MonthSchedule.Derivation"/> 逐条记下
/// "这个日上限是怎么来的、开月爆堆为什么是那个数"。分解出来的日计划要能一路回溯到月计划
/// 和设备台账，否则没人敢照着干。</para>
/// </summary>
public static class MonthSchedulePlanner
{
    /// <summary>
    /// 排一个月。
    /// </summary>
    /// <param name="cfg">任务编制的配置：面、编组、班次表 —— 设备信息从这里来。</param>
    /// <param name="monthDemand">各面的月量（m³ 实方）。键按面名。</param>
    /// <param name="workdays">本月有效作业日（调用方从 WorkCalendar 取）。</param>
    /// <param name="roster">在籍设备清单（推穿孔/排土能力用）。</param>
    /// <param name="prm">工艺参数；不传用缺省。</param>
    public static MonthSchedule Plan(ExploderConfig cfg,
                                     IReadOnlyDictionary<string, double> monthDemand,
                                     IReadOnlyList<DateTime> workdays,
                                     IReadOnlyList<RosterEntry>? roster = null,
                                     ProcessParams? prm = null)
    {
        var p = prm ?? new ProcessParams();
        var days = (workdays ?? Array.Empty<DateTime>())
                   .Select(d => d.Date).Distinct().OrderBy(d => d).ToList();

        var caps = CapacityFromEquipment.ForFaces(cfg);
        var site = CapacityFromEquipment.ForSite(cfg, roster);

        var sch = new MonthSchedule
        {
            Month = days.Count > 0 ? new DateTime(days[0].Year, days[0].Month, 1) : DateTime.Today,
            SiteCap = site,
            Decomposition = new MonthDecomposition(),
        };
        sch.Workdays.AddRange(days);
        sch.FaceCaps.AddRange(caps);

        sch.Derivation.Add($"◆ 有效作业日 {days.Count} 天"
                         + (days.Count > 0 ? $"（{days[0]:MM-dd} … {days[^1]:MM-dd}）" : ""));
        foreach (var b in site.Basis) sch.Derivation.Add("◆ " + b);

        // ── 组装每个面的需求（月量来自计划，能力来自设备）──
        var demands = new List<FaceMonthDemand>();
        foreach (var f in cfg?.Faces?.Where(x => x.Process == ProcessType.Load) ?? Enumerable.Empty<FaceInput>())
        {
            if (!monthDemand.TryGetValue(f.Zone, out double m3) || m3 <= 1e-9) continue;
            var cap = caps.FirstOrDefault(c => string.Equals(c.Zone, f.Zone, StringComparison.OrdinalIgnoreCase));
            double muck = CapacityFromEquipment.AssumeOpeningMuck(m3, days.Count, p);

            demands.Add(new FaceMonthDemand
            {
                Zone = f.Zone, UnitId = f.UnitId, Material = f.Material,
                Destination = f.DestinationName, IsOre = IsOre(f),
                MonthM3 = m3,
                DailyCapM3 = cap?.DailyM3 ?? 0,
                OpeningMuckM3 = muck,
                NeedsBlast = NeedsBlast(f),
            });

            sch.Derivation.Add($"◆「{f.Zone}」月量 {m3:0} m³　"
                             + (cap != null && cap.DailyM3 > 1e-9 ? cap.Basis : "日上限推不出（见下）"));
            sch.Derivation.Add($"　 开月爆堆按**推定**取 {muck:0} m³ = 月量 ÷ {days.Count} 天 × 爆后等待 {p.BlastLeadDays} 天"
                             + " —— 月计划隐含「上月末已备好料」这个前提；有实盘数就该覆盖它。");
        }

        var dec = MonthToDayScheduler.Decompose(demands, days, p);
        // 把分解结果搬进来（Decomposition 是 init-only，直接用它的容器）
        sch.Decomposition.Orders.AddRange(dec.Orders);
        sch.Decomposition.Notes.AddRange(dec.Notes);
        sch.Decomposition.Violations.AddRange(dec.Violations);

        // ── 全矿工序能力校核：分解排出来的孔米/排土量，设备干得下来吗 ──
        CheckSiteCapacity(sch, site, days);

        return sch;
    }

    /// <summary>
    /// 全矿能力校核：某一天排出来的孔米超过钻机日能力、或排土量超过推土机日能力，就报。
    /// <para>面级的日上限管的是"这个面一天挖多少"，管不到"全矿的钻机够不够"——
    /// 那是另一维，漏掉的话计划在纸面上完全成立、到现场钻机不够用。</para>
    /// </summary>
    private static void CheckSiteCapacity(MonthSchedule sch, SiteCapacity site, List<DateTime> days)
    {
        foreach (var d in days)
        {
            double drill = sch.OnDayTotal(d, ProcessType.Drill);
            if (drill > 1e-9 && site.DrillMetersPerDay > 1e-9 && drill > site.DrillMetersPerDay + 1e-6)
                sch.Decomposition.Violations.Add(
                    $"⚠ {d:MM-dd} 排了 {drill:0} 孔米，超过全矿钻机日能力 {site.DrillMetersPerDay:0} 孔米"
                  + $"（{site.Drills} 台）⇒ 缺 {drill - site.DrillMetersPerDay:0} 孔米。"
                  + "面级日上限管不到这一维 —— 得错开爆破批次或加钻机。");
            else if (drill > 1e-9 && site.DrillMetersPerDay <= 1e-9)
                sch.Decomposition.Violations.Add(
                    $"⚠ {d:MM-dd} 排了 {drill:0} 孔米，但**在籍设备里没有钻机** ⇒ 这一天的备孔没人干。");

            double dump = sch.OnDayTotal(d, ProcessType.Dump);
            if (dump > 1e-9 && site.DumpM3PerDay > 1e-9 && dump > site.DumpM3PerDay + 1e-6)
                sch.Decomposition.Violations.Add(
                    $"⚠ {d:MM-dd} 排弃 {dump:0} m³占容，超过推土机日承接 {site.DumpM3PerDay:0} m³"
                  + $"（{site.Dozers} 台）⇒ 排土场摊不开，料会堆在卸点。");
        }
    }

    private static bool IsOre(FaceInput f)
    {
        string m = (f.Material ?? "") + " " + (f.MaterialCode ?? "");
        return m.Contains("煤", StringComparison.Ordinal);
    }

    /// <summary>
    /// 这个面要不要爆破：表土/黄土直接挖，岩与煤岩混采要爆。
    /// <para>拿物料名判 —— 台账上没有"需不需要爆破"这一列，而物料名是现成且稳定的那个信号。
    /// 判错的后果是多排或少排一批穿孔爆破，会在校核里露出来（S6b 那条量守恒）。</para>
    /// </summary>
    private static bool NeedsBlast(FaceInput f)
    {
        string m = (f.Material ?? "") + " " + (f.Zone ?? "");
        if (m.Contains("表土", StringComparison.Ordinal)
         || m.Contains("黄土", StringComparison.Ordinal)
         || m.Contains("剥离面·北", StringComparison.Ordinal)) return false;
        return true;
    }
}
