// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/FaceEquipmentAutoAssigner.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  派生作业面的主设备自动指派（AS 组，2026-08-20）。
//
//  ══ 为什么要它 ══
//  主设备此前**只有一条来源**：`working_face.equipment_id`——人在「作业面台账」上填的那一列。
//  而工序作业区派生出来的面**没有人填过**，于是：
//    实测 46 个派生面里只有 11 个认领得到主机（台账里统共只有 5 条作业面档案：
//    1195/1210/1180/1240/1270 五个平盘，各绑一台电铲），
//    其余 12 个采装面 + 17 个排土面**一台都配不上** ⇒ 那些面整期排不上 ⇒
//    当日甘特里只有一两台电铲。而设备库里在用电铲 30 台、在用推土机 103 台。
//    「一台可派设备都没有」这句话读起来像设备库空了 —— 缺的其实是**面与设备的对应**。
//
//  ══ 七条口径 ══
//   AS1 **台账优先，自动只补空**。台账里认领到的面原样保留（人填的是资产，不许被覆盖）。
//   AS2 只从**在用**设备里取。报废/待报废/租赁一律不进池，并把排除了多少台说出来。
//   AS3 **一台设备只上一个面**。台账已占用的先扣掉；池子空了就有面配不上（见 AS7）。
//   AS4 **按工序取类别**：采装面 → 电铲，排土面 → 推土机。
//   AS5 **大机配大面**：面按备采储量（无则按日目标）降序，设备按型号斗容降序 ——
//       与分解器里"大机先咬大单元"同一条思路。斗容取不到的排在后面，**不猜一个数**。
//   AS6 **自动指派要标出来**。`FaceInput.MainEquipmentAuto = true`，
//       来源文案里逐面点名。人在「作业面台账」保存一次即固化成档案，之后这里就不再插手。
//   AS7 **设备不够不循环复用**。面多机少时如实报"还有 N 个面没配上"，
//       而不是把同一台铲配给两个面 —— 那样班表看着满满当当，现场一台机分不了身。
//
//  ══ 这一层**不做**什么 ══
//  不挑"哪台铲更适合这个面"（位置远近、爬坡能力、与卡车的匹配）——
//  那要设备的实时位置与调度规则，本模块拿不到。这里只保证
//  **每个面有一台在用的、类别对的、独占的主机**，让下游排得出计划；
//  真要精细指派，人在作业面台账上改，改完这里就不动它了。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>派生面的主设备自动指派。永不抛。</summary>
internal static class FaceEquipmentAutoAssigner
{
    /// <summary>一次指派的结果。</summary>
    public sealed class Result
    {
        /// <summary>自动补上的面数。</summary>
        public int Assigned;

        /// <summary>台账里本来就有主机的面数（没动它们）。</summary>
        public int FromLedger;

        /// <summary>池子空了、没配上的面（按工序分）。</summary>
        public List<string> Unfilled = new();

        /// <summary>逐条说明（界面直接显示）。</summary>
        public List<string> Notes = new();

        public string Label = "";
    }

    /// <summary>
    /// 给还没有主设备的面自动指派一台。
    /// </summary>
    /// <param name="faces">派生出来的全部面（本方法会就地改 <c>Group.MainEquipment</c>）。</param>
    public static Result Assign(IReadOnlyList<FaceInput>? faces)
    {
        var res = new Result();
        var list = (faces ?? Array.Empty<FaceInput>()).Where(f => f != null).ToList();
        if (list.Count == 0) { res.Label = "自动指派：没有面"; return res; }

        // AS3：一台设备只上一个面。
        //
        // ★ 认领这一步会让**多个面共用同一台铲**：台账里只有 5 条作业面档案，
        //   而认领是按"平盘标高 ±8m"配的 —— 29 个派生的采装面于是全落到那 5 台上。
        //   下游 M2「一台设备同一班只在一处」再一挡，同时只有 5 个面能动，
        //   其余的面整期一条任务都排不出来。**这不是台账在说"这五台各管好几个面"**，
        //   是派生 + 容差匹配的产物。所以这里按面的大小留一个、其余的重新配。
        var need = new List<FaceInput>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dupCleared = 0;

        foreach (var g in list.Where(f => !string.IsNullOrWhiteSpace(f.Group?.MainEquipment))
                              .GroupBy(f => f.Group!.MainEquipment.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = g.OrderByDescending(Size).ThenBy(f => f.Zone, StringComparer.Ordinal).ToList();
            taken.Add(ordered[0].Group!.MainEquipment.Trim());       // 最大的那个面留住这台机
            for (int i = 1; i < ordered.Count; i++)
            {
                ordered[i].Group!.MainEquipment = "";                // 其余的重新配一台自己的
                need.Add(ordered[i]);
                dupCleared++;
            }
        }
        res.FromLedger = taken.Count;

        need.AddRange(list.Where(f => string.IsNullOrWhiteSpace(f.Group?.MainEquipment) && !need.Contains(f)));
        if (need.Count == 0)
        {
            res.Label = $"自动指派：{res.FromLedger} 个面的主设备都来自作业面台账，无需补";
            return res;
        }

        List<Equipment> all;
        Dictionary<string, double> bucket;
        try
        {
            all = EquipmentDataContext.Equipment.All().Where(e => e != null).ToList();
            bucket = BucketByModel();
        }
        catch (Exception ex)
        {
            res.Label = $"自动指派：设备台账读不到（{Short(ex)}），{need.Count} 个面没有主设备";
            return res;
        }

        int excluded = all.Count(e => !IsUsable(e));
        var shovels = Pool(all, bucket, taken, "Shovel");
        var dozers = Pool(all, bucket, taken, "Dozer");

        // AS5：大面先挑（备采储量优先，没有就按日目标）
        foreach (var f in need.OrderByDescending(Size).ThenBy(f => f.Zone, StringComparer.Ordinal))
        {
            bool isDump = f.Process == ProcessType.Dump;
            var pool = isDump ? dozers : shovels;
            if (pool.Count == 0)
            {
                res.Unfilled.Add($"{f.Zone}（{(isDump ? "排土·推土机" : "采装·电铲")}）");
                continue;
            }

            var pick = pool[0];
            pool.RemoveAt(0);
            f.Group.MainEquipment = pick.EquipmentId;
            f.MainEquipmentAuto = true;
            res.Assigned++;
        }

        // AS7：没配上的要点名，不能只报一个总数
        if (res.Unfilled.Count > 0)
            res.Notes.Add($"◆ {res.Unfilled.Count} 个面**没有可配的主设备**（在用设备已全部各就各位，"
                        + "不给同一台机配两个面）：" + string.Join("、", res.Unfilled.Take(6))
                        + (res.Unfilled.Count > 6 ? $" …等 {res.Unfilled.Count} 个" : ""));

        if (dupCleared > 0)
            res.Notes.Add($"· {dupCleared} 个面原本与别的面**共用同一台主机**（台账里只有 {res.FromLedger} 条作业面档案，"
                        + "认领是按平盘标高 ±8m 配的，几十个派生面因此全落到那几台上）—— "
                        + "已按面的大小留一个、其余各配一台。**共用不改的话它们整期一条任务都排不出来**："
                        + "下游「一台设备同一班只在一处」会把它们全挡下，而甘特上只是少几行。");

        if (res.Assigned > 0)
            res.Notes.Add($"· {res.Assigned} 个面的主设备是**自动指派**的（按「在用 + 类别对 + 一机一面 + 大机配大面」排），"
                        + "不是台账档案。位置远近、与卡车的匹配这一层没算 —— "
                        + "要精细指派就在「作业面台账」改，改完这里不再插手。");

        res.Label = $"主设备：台账认领 {res.FromLedger} 台 · 自动补 {res.Assigned} 个面"
                  + (dupCleared > 0 ? $"（其中 {dupCleared} 个原本与别的面共用一台）" : "")
                  + (res.Unfilled.Count > 0 ? $" · 仍缺 {res.Unfilled.Count} 个" : "")
                  + (excluded > 0 ? $"（在册 {all.Count} 台里排除了 {excluded} 台非在用）" : "");
        return res;
    }

    /// <summary>
    /// 按编组解出来的 <b>n*</b> 给各面自动配车（定车制：一台车一班只跟一台铲）。
    ///
    /// <para>
    /// <b>为什么必须有它</b>：`Group.Trucks`（实配车队）本来只有一条来源 ——
    /// 作业面台账里人填的那一列，而派生出来的面**没有人填过**。
    /// 于是每个面都是「配 0 车（荐 6）」⇒ 推演里系统瓶颈恒报「铲等车·运力不足」，
    /// 而在用卡车有 200 多台 —— **不是车不够，是没人把车配上去**。这个瓶颈是假的。
    /// </para>
    /// <para>
    /// 与主设备同一条纪律：**台账填过的不动**、**一台车只上一个面**、
    /// **车不够就有面配不满并如实报**（不循环复用：那样派车单看着满满当当，而一台车分不了身）。
    /// </para>
    /// </summary>
    /// <param name="faces">全部作业面（就地改 <c>Group.Trucks</c>）。</param>
    /// <param name="pool">在用卡车编号（调用方从设备台账取；空 = 不配，并说明）。</param>
    public static string AssignTrucks(IReadOnlyList<FaceInput>? faces, IReadOnlyList<string>? pool)
    {
        var loads = (faces ?? Array.Empty<FaceInput>())
                    .Where(f => f != null && f.Process == ProcessType.Load).ToList();
        if (loads.Count == 0) return "";

        // 台账已经配过车的面：原样保留，并把那些车从池子里扣掉
        var taken = new HashSet<string>(loads.SelectMany(f => f.Group.Trucks)
                                             .Where(x => !string.IsNullOrWhiteSpace(x)),
                                        StringComparer.OrdinalIgnoreCase);
        int fromLedger = loads.Count(f => f.Group.Trucks.Count > 0);

        var free = (pool ?? Array.Empty<string>())
                   .Where(x => !string.IsNullOrWhiteSpace(x) && !taken.Contains(x.Trim()))
                   .Select(x => x.Trim())
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(x => x, StringComparer.Ordinal)      // 稳定序：换一次跑不许换一批车
                   .ToList();
        if (free.Count == 0)
            return fromLedger > 0 ? "" : "配车：**车池空的**（在用卡车取不到）—— 各面只有推荐车数，没有实配";

        // 大面先配（日目标降序）：车不够时先保量大的那几个面
        int assigned = 0, shortFaces = 0, shortTrucks = 0, cursor = 0;
        foreach (var f in loads.Where(x => x.Group.Trucks.Count == 0)
                               .OrderByDescending(x => x.DayTargetM3)
                               .ThenBy(x => x.Zone, StringComparer.Ordinal))
        {
            int want = Math.Max(0, f.Group.RecommendedTrucks);
            if (want == 0) continue;                    // 编组没解出来就不配（不猜一个车数）

            int take = Math.Min(want, free.Count - cursor);
            if (take <= 0) { shortFaces++; shortTrucks += want; continue; }

            for (int i = 0; i < take; i++) f.Group.Trucks.Add(free[cursor + i]);
            cursor += take;
            assigned += take;
            if (take < want) { shortFaces++; shortTrucks += want - take; }
        }

        // ★ **一台都没配上时不许一声不吭**（2026-08-20 修）。
        //
        //   原来这里 `assigned == 0 && shortFaces == 0` 直接返回空串，而调用方
        //   （ProductionPlanContext）对空串是 `if (!IsNullOrWhiteSpace) 才追加` ——
        //   于是"这一步跑了、什么也没配上"与"这一步压根没跑"在界面上**长得一模一样**：
        //   装配来源文案里一个字都没有。而下游派车单三个班全展不开，
        //   逐条报「尚未配车（建议 6 台）」—— 现场看到的是一个没有出处的结论。
        //
        //   走到这里且 assigned==0 只有一种可能：**每个面的荐车数都是 0**
        //   （编组没解出来 ⇒ 没有 n*）。那是上游的事，但必须在这儿说出来，
        //   否则没人知道该往哪儿查。判据 TA3。
        if (assigned == 0 && shortFaces == 0)
        {
            int wantNone = loads.Count(f => f.Group.Trucks.Count == 0 && f.Group.RecommendedTrucks <= 0);
            if (wantNone == 0) return "";      // 面全在台账里配过车 —— 那是正常状态
            return $"配车：**{wantNone} 个面的荐车数是 0，一台车也没配**（在用车池 {free.Count} 台没动）"
                 + " —— 荐车数由编组求解给（n\\*），编组解不出来就没有它。"
                 + "多半是这些面还缺去向或运距（没有运距就算不出循环时间 T_c）。"
                 + "**派车单会因此三个班全展不开**，逐条报「尚未配车」。";
        }

        return $"配车：自动配 {assigned} 台（定车制，一台车只跟一台铲）"
             + (fromLedger > 0 ? $" · 台账已配 {fromLedger} 个面" : "")
             + (shortFaces > 0
                ? $" · ◆ {shortFaces} 个面配不满，还缺 {shortTrucks} 台（在用车池 {free.Count} 台已排完，"
                  + "**不循环复用**：同一台车配给两个面，派车单看着满满当当而现场分不了身）"
                : "");
    }

    // ── 内部 ──────────────────────────────────────────────────────────

    /// <summary>面的"大小"：备采储量优先，没录就用日目标。两个都没有就是 0（排在最后）。</summary>
    private static double Size(FaceInput f)
        => f.AvailableReserveM3 > 1e-6 ? f.AvailableReserveM3 : Math.Max(0, f.DayTargetM3);

    /// <summary>AS2：只有"在用"进池。空状态按在用处理（老台账没填状态的行不该被一刀切掉）。</summary>
    private static bool IsUsable(Equipment e)
    {
        string s = (e.Status ?? "").Trim();
        if (s.Length == 0) return true;
        return s == "在用";
    }

    private static List<Equipment> Pool(List<Equipment> all, Dictionary<string, double> bucket,
                                        HashSet<string> taken, string category)
        => all.Where(e => string.Equals((e.Category ?? "").Trim(), category, StringComparison.OrdinalIgnoreCase))
              .Where(IsUsable)
              .Where(e => !string.IsNullOrWhiteSpace(e.EquipmentId) && !taken.Contains(e.EquipmentId.Trim()))
              .OrderByDescending(e => bucket.TryGetValue((e.Model ?? "").Trim(), out double b) ? b : -1)
              .ThenBy(e => e.EquipmentId, StringComparer.Ordinal)     // 稳定序：同型号按编号，换一次跑不许换一批机
              .ToList();

    /// <summary>型号 → 斗容 m³（取不到的型号不进表，排序时排在最后 —— <b>不猜一个斗容</b>）。</summary>
    private static Dictionary<string, double> BucketByModel()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var m in EquipmentDataContext.Models.All())
                if (m != null && !string.IsNullOrWhiteSpace(m.Model) && m.BucketM3 is > 0)
                    map[m.Model.Trim()] = m.BucketM3.Value;
        }
        catch { /* 型号库读不到 → 全按编号排，稳定但不分大小 */ }
        return map;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? "";
        int i = m.IndexOf('\n');
        if (i > 0) m = m[..i];
        return m.Length > 60 ? m[..60] + "…" : m;
    }
}
