// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ProcessZoneStore.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  工序作业区台账（process_zone / V047）的读写。所有方法永不抛，失败以返回值 + 文案表达。
//
//  ── 为什么是另一张表，不是 mineable_region ──
//  那张表的类别判定是**白名单而兜底是采场**（ZoneRecord.IsPit / SimRegion.IsPit 各一份）。
//  往里塞 drill / blast_guard / dump_tip 这些新 category，它们会被**静默当成采场**
//  进三维推演的推进轮廓与路网中心线裁剪 —— 一个字的红字都不会有，
//  只是推演里凭空多出几块朝推进方向动的地，而每一块看着都挺合理。
//  全项目有 19 个文件在按那张表的 category 筛。
//
//  ── 这里的三条纪律 ──
//  ① **Z 出处为空一律拒绝入库**（P10）。points_json 里写 0 会被下游当成实测高程
//     （只有零顶点才返回 NaN），层体整体摆到 0m 而界面正常。
//  ② **upsert 按三键**（期次 + 工序 + 名字）。少了工序，同一个面的穿孔区与采装区
//     会互相覆盖 —— 而 upsert 不报错。
//  ③ **逐块独立**：一块失败不影响其余，失败原因逐条带回来。
//     「入库了 5 块」与「本该入库 7 块」是两件事，只报前者就是骗人。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>工序作业区台账的读写。</summary>
public static class ProcessZoneStore
{
    /// <summary>最近一次读写的结果文案（界面直接显示）。</summary>
    public static string LastLabel { get; private set; } = "";

    private static IProcessZoneService? Service()
    {
        try { return PitMine3D.Kylin.Data.EquipmentDataContext.ProcessZones; }
        catch (Exception ex) { LastLabel = $"工序作业区台账不可用（{Short(ex)}）"; return null; }
    }

    /// <summary>台账是否可用。</summary>
    public static bool Available => Service() != null;

    /// <summary>读一期。读不到返回空表，原因写进 <see cref="LastLabel"/>。</summary>
    /// <summary>
    /// 台账里出现过的期次（按名排序，<c>yyyy-MM</c> 的字典序即时间序）。
    /// <para>从 <c>All()</c> 归并出来，<b>不往库层加一个新 API</b> —— 加了就有第二份"期次有哪些"的口径。</para>
    /// </summary>
    public static List<string> ListPeriods()
    {
        var svc = Service();
        if (svc == null) return new List<string>();
        try
        {
            return svc.All()
                   .Select(z => (z?.Period ?? "").Trim())
                   .Where(p => p.Length > 0)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(p => p, StringComparer.Ordinal)
                   .ToList();
        }
        catch (Exception ex) { LastLabel = $"列期次失败（{Short(ex)}）"; return new List<string>(); }
    }

    public static List<ProcessZone> LoadPeriod(string period)
    {
        var svc = Service();
        if (svc == null) return new List<ProcessZone>();
        try
        {
            var list = svc.ByPeriod(period).ToList();
            LastLabel = list.Count > 0
                ? $"工序作业区台账 {period}：{list.Count} 块（"
                  + string.Join(" · ", ProcessZone.AllProcesses
                      .Select(p => (P: p, N: list.Count(z => Eq(z.Process, p))))
                      .Where(x => x.N > 0)
                      .Select(x => $"{ProcessZone.DisplayName(x.P)} {x.N}")) + "）"
                : $"工序作业区台账 {period} 为空 —— 还没生成过，或生成后没入库。";
            return list;
        }
        catch (Exception ex) { LastLabel = $"读工序作业区失败（{Short(ex)}）"; return new List<ProcessZone>(); }
    }

    /// <summary>读全表。</summary>
    public static List<ProcessZone> LoadAll()
    {
        var svc = Service();
        if (svc == null) return new List<ProcessZone>();
        try { return svc.All().ToList(); }
        catch (Exception ex) { LastLabel = $"读工序作业区失败（{Short(ex)}）"; return new List<ProcessZone>(); }
    }

    /// <summary>一期里某道工序的区域（派工按它筛：今天钻机该去哪几块地）。</summary>
    public static List<ProcessZone> LoadProcess(string period, string process)
    {
        var svc = Service();
        if (svc == null) return new List<ProcessZone>();
        try { return svc.ByProcess(period, process).Where(z => z.Active != 0).ToList(); }
        catch (Exception ex) { LastLabel = $"读工序作业区失败（{Short(ex)}）"; return new List<ProcessZone>(); }
    }

    /// <summary>
    /// 把勾选的候选写进台账（按 期次 + 工序 + 名字 upsert）。
    /// <para><b>逐块独立</b>，失败原因逐条带回。</para>
    /// </summary>
    public static string Apply(string period, IEnumerable<ProcessZoneProposal>? chosen,
                               out int inserted, out int updated, out List<string> failures)
    {
        inserted = 0; updated = 0;
        failures = new List<string>();
        var list = (chosen ?? Enumerable.Empty<ProcessZoneProposal>()).Where(p => p != null).ToList();
        if (list.Count == 0) return "没有勾选任何工序作业区。";

        // ── ① 先把关，再看库 ──────────────────────────────────────────────
        //  顺序反过来（库不可用就直接 return）会让**几何与高程的把关整条不跑** ——
        //  于是"台账没开"和"这几块本来就不该入库"在文案上变成同一句话，
        //  而离线台架里所有台账路径本来就静默走兜底（见 offline-real-db-harness）。
        //  把关是对数据的判断，与库在不在没有关系，必须先做。
        var fit = new List<ProcessZoneProposal>();
        foreach (var p in list)
        {
            if (p.Ring.Count < 3) { failures.Add($"{p.Name}·{p.ProcessZh}：只有 {p.Ring.Count} 个顶点"); continue; }
            // P10：Z 出处为空 = 解不出高程，一律拒绝入库，不补 0。
            if (p.ZSource.Length == 0) { failures.Add($"{p.Name}·{p.ProcessZh}：解不出高程，拒绝入库"); continue; }
            if (p.Ring.Any(v => double.IsNaN(v.Z) || double.IsInfinity(v.Z)))
            { failures.Add($"{p.Name}·{p.ProcessZh}：环上有非法高程，拒绝入库"); continue; }
            fit.Add(p);
        }

        // ── ② 库 ────────────────────────────────────────────────────────
        var svc = fit.Count > 0 ? Service() : null;
        if (fit.Count > 0 && svc == null)
        {
            failures.Add($"{fit.Count} 块合格的区域写不进去：{LastLabel}");
            string dead = $"一块也没入库：{LastLabel}"
                        + (failures.Count > 1 ? $"　另有 {failures.Count - 1} 块本来就不合格" : "");
            LastLabel = dead;
            return dead;
        }

        foreach (var p in fit)
        {
            try
            {
                bool exists = svc.Find(period, p.Process, p.Name) != null;
                var e = new ProcessZone
                {
                    Period = period,
                    Process = p.Process,
                    Name = p.Name,
                    GroupKey = p.GroupKey,
                    PieceIndex = p.PieceIndex,
                    PieceCount = p.PieceCount,
                    PointsJson = ZoneStore.SerializeRing(p.Ring),
                    ZSource = p.ZSource,
                    CellM = p.CellM,
                    AreaM2 = p.AreaM2,
                    RingAreaM2 = p.RingAreaM2,
                    ClippedAtBorder = p.ClippedAtBorder ? 1 : 0,
                    UnitIds = string.Join("、", p.Blocks.Select(b => b.UnitId).Distinct()),
                    UnitCount = p.Blocks.Select(b => b.UnitId).Distinct().Count(),
                    VolumeM3 = p.VolumeM3,
                    Basis = p.Basis,
                    EquipRoleName = p.EquipRole,
                    LeadDays = p.LeadDays,
                    GuardRadiusM = p.GuardRadiusM,
                    BandWidthM = p.BandWidthM,
                    Note = p.Note,
                };
                long id = svc.Upsert(e);
                if (id == 0) { failures.Add($"{p.Name}·{p.ProcessZh}：写库返回 0"); continue; }
                if (exists) updated++; else inserted++;
            }
            catch (Exception ex) { failures.Add($"{p.Name}·{p.ProcessZh}：{Short(ex)}"); }
        }

        string msg = $"已入库：新增 {inserted} 块 · 改边界 {updated} 块";
        if (failures.Count > 0)
            msg += $"　◆ {failures.Count} 块没入库：{string.Join("；", failures.Take(3))}"
                 + (failures.Count > 3 ? " …" : "");
        LastLabel = msg;
        return msg;
    }

    /// <summary>删掉一整期（重排一期前先清）。返回删掉几条。</summary>
    public static int DeletePeriod(string period)
    {
        var svc = Service();
        if (svc == null) return 0;
        try { int n = svc.DeletePeriod(period); LastLabel = $"已清掉 {period} 的 {n} 块工序作业区。"; return n; }
        catch (Exception ex) { LastLabel = $"清期失败（{Short(ex)}）"; return 0; }
    }

    /// <summary>points_json → 环。</summary>
    public static List<ZonePoint> ParseRing(ProcessZone? z)
        => z == null ? new List<ZonePoint>() : ZoneStore.ParseRing(z.PointsJson);

    private static bool Eq(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
