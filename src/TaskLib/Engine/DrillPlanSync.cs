// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/DrillPlanSync.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;                   // EquipmentDataContext
using PitMine3D.Kylin.Data.Entities;          // DrillPlan / ProcessZone
using PitMine3D.Kylin.TaskLib.Domain;                        // ProcessType
using PitMine3D.Kylin.TaskLib.Zoning;                        // ProcessZoneStore

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  班组计划里的穿孔笔 → `drill_plan`（V044）。工序链「穿孔 → 爆破 → 采装」的第一环。
//
//  ══ 为什么由分解器写，而不是排产那一侧 ══
//  `drill_plan` 建表之后一直**没有生产者**：只有人手工往里录。于是台账模式下
//  钻机一条任务都排不出来 —— 甘特没有穿孔条、工序进度的穿孔一栏恒 0%、
//  「钻爆计划衔接」拿不到穿孔窗口。
//
//  能写它的地方有两处：MineAssLib 的排产结果，和本模块的逐班分解器。
//  **只能留一条** —— 两个写入方 upsert 同一个主键（设备 × 日期 × 起始时刻），
//  后写的覆盖先写的，而两边各自都自洽：谁也不会报错，只是穿孔计划会随着
//  "最后点了哪个按钮"来回变。这正是本仓库反复吃亏的那个形状。
//  分解器现在是当日盘子的主路径（`ProductionPlanContext.BuildAndApply`），所以由它写。
//
//  ══ 三条口径 ══
//  **W1 孔数与延米留 NULL，不写 0。**
//      孔网参数（孔距/排距/超深）在 PlanLib 的工艺链里，本模块拿不到。
//      **「免爆」与「算不出」的延米都是 0，报表上一模一样** —— 写 0 等于把缺口藏起来。
//      V044 这两列本就可空，NULL 才是「未录」的正确表达。
//  **W2 待爆区取自穿孔工序区，取不到就留空并点名。**
//      穿孔区是采装区沿推进方向**前推超前期**的那一段地。拿采装区的位置写钻机，
//      钻机就被派到了电铲脚下 —— 而报表上每个数都正常。
//      zone 空着时「钻爆计划衔接」按 待爆区 + 日期 对不上炮次，那一段接续从此看不见。
//  **W3 只写本期，且先清后写。**
//      重排一期时不清掉旧的，会留下上一版排的、现在已经不成立的孔 ——
//      而它们在表里和新的一模一样。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次同步的结果。永不抛。</summary>
public sealed class DrillSyncResult
{
    public int Written, Deleted, NoZone, NoHoleParams;
    public List<string> Notes = new();
    public string Headline = "";
    public bool Ok => Written > 0;
}

/// <summary>班组计划的穿孔笔 → drill_plan。</summary>
public static class DrillPlanSync
{
    /// <summary>
    /// 把本期班组计划里的穿孔笔写进 <c>drill_plan</c>。
    /// </summary>
    /// <param name="period">期次 yyyy-MM。</param>
    /// <param name="plan">分解出来的班组计划。</param>
    /// <param name="dryRun">true = 只算不写盘（界面先给人看一眼）。</param>
    public static DrillSyncResult Sync(string period, ShiftPlanResult? plan, bool dryRun = false)
    {
        var res = new DrillSyncResult();
        var drills = (plan?.Rows ?? new List<ShiftTaskRow>())
                     .Where(r => r.Process == ProcessType.Drill).ToList();
        if (drills.Count == 0)
        {
            res.Headline = plan == null || plan.Rows.Count == 0
                ? "没有班组计划 —— 先分解一次本期。"
                : "本期班组计划里**没有穿孔笔** —— 要么全是免爆的面，要么没有钻机可派。";
            return res;
        }

        // W2：单元号 → 穿孔工序区
        var zoneOf = DrillZones(period, res);

        var rows = new List<DrillPlan>();
        foreach (var r in drills)
        {
            zoneOf.TryGetValue(r.UnitId ?? "", out var z);
            if (z.Zone == null) res.NoZone++;
            res.NoHoleParams++;

            rows.Add(new DrillPlan
            {
                EquipmentId = r.MachineId,
                PlanDate = r.Date,
                StartTime = Hhmm(r.StartHour),
                EndTime = Hhmm(r.EndHour),
                Zone = z.Zone ?? "",
                BenchElevationM = z.BenchZ,
                HoleCount = null,          // W1：算不出就是 NULL，不是 0
                HoleLengthM = null,
                Status = "计划",
                Note = $"由班组计划生成｜期次 {period}｜{r.Shift}班｜单元 {r.UnitId}"
                     + $"｜控制方量 {r.VolumeM3:N0} m³"
                     + (r.IsBlastShift ? "｜本班是爆破班（有效时长已扣清场）" : "")
                     + (z.Zone == null ? "｜⚠ 没配上穿孔工序区（钻爆衔接对不上）" : "")
                     + "｜孔数与延米未录（孔网参数不在本模块可达范围，见 W1）",
            });
        }

        if (!dryRun)
        {
            try
            {
                var svc = EquipmentDataContext.DrillPlans;
                // W3：先清后写 —— 不清会留下上一版排的、现在已不成立的孔，而它们看着一模一样
                var dates = rows.Select(x => x.PlanDate).Distinct().ToList();
                foreach (var d in dates)
                {
                    if (!DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out var dt)) continue;
                    foreach (var old in svc.ByDate(dt).ToList())
                        if ((old.Note ?? "").Contains("由班组计划生成", StringComparison.Ordinal))
                        { svc.Delete(old.EquipmentId, dt, old.StartTime); res.Deleted++; }
                }
                foreach (var x in rows) { svc.Upsert(x); res.Written++; }
            }
            catch (Exception ex)
            {
                res.Headline = $"写 drill_plan 失败（{ex.GetType().Name}）—— 已写入 {res.Written} 条。";
                return res;
            }
        }
        else res.Written = rows.Count;

        res.Headline = (dryRun ? "试算：" : "已写入：")
                     + $"{res.Written} 条穿孔计划"
                     + $"（{rows.Select(x => x.EquipmentId).Distinct().Count()} 台钻机 · "
                     + $"{rows.Select(x => x.PlanDate).Distinct().Count()} 个作业日）"
                     + (res.Deleted > 0 ? $"，先清掉上一版 {res.Deleted} 条" : "");

        if (res.NoZone > 0)
            res.Notes.Add($"◆ {res.NoZone} 条**没配上穿孔工序区**（待爆区留空）—— "
                        + "「钻爆计划衔接」按 待爆区 + 日期 对照穿孔与炮次，空着就对不上，"
                        + "那一段接续从此看不见。"
                        + "★ 不拿采装区顶替：穿孔区是采装区沿推进方向**前推超前期**的那一段地，"
                        + "拿采装区写钻机，钻机就被派到了电铲脚下，而报表上每个数都正常。"
                        + "补法：到「作业区划分 · 工序作业区」为本期生成穿孔区并入库。");
        res.Notes.Add($"· {res.NoHoleParams} 条的**孔数与延米是 NULL（算不出），不是 0**。"
                    + "「免爆」与「算不出」的延米都是 0，报表上一模一样 —— 所以留空而不是填 0。"
                    + "要延米就在工艺链上填孔网参数，由那一侧回填。");
        return res;
    }

    /// <summary>单元号 → (穿孔区名, 台阶标高)。<b>取不到就是取不到</b>，不拿采装区顶替。</summary>
    private static Dictionary<string, (string? Zone, double? BenchZ)> DrillZones(
        string period, DrillSyncResult res)
    {
        var map = new Dictionary<string, (string?, double?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var zones = ProcessZoneStore.LoadProcess(period, ProcessZone.ProcDrill);
            if (zones.Count == 0)
            {
                res.Notes.Add($"◆ {period} 没有**穿孔工序区** —— 写出来的计划全都不带待爆区。");
                return map;
            }
            foreach (var z in zones)
            {
                var ring = ProcessZoneStore.ParseRing(z);
                double? bench = ring.Count > 0 ? ring.Average(p => p.Z) : null;
                foreach (var id in (z.UnitIds ?? "").Split(new[] { '、', ',', ';', '；' },
                                                           StringSplitOptions.RemoveEmptyEntries))
                {
                    string k = id.Trim();
                    if (k.Length > 0 && !map.ContainsKey(k)) map[k] = (z.Name, bench);
                }
            }
        }
        catch (Exception ex) { res.Notes.Add($"◆ 工序作业区读不到（{ex.GetType().Name}）。"); }
        return map;
    }

    /// <summary>小时数 → "HH:mm"。<b>≥24:00 截到 23:59</b>（V044 要求同日内）。</summary>
    internal static string Hhmm(double hour)
    {
        if (double.IsNaN(hour) || hour < 0) return "00:00";
        if (hour >= 24.0) return "23:59";
        int h = (int)Math.Floor(hour);
        int m = (int)Math.Round((hour - h) * 60);
        if (m >= 60) { h++; m -= 60; }
        return h >= 24 ? "23:59" : $"{h:00}:{m:00}";
    }
}
