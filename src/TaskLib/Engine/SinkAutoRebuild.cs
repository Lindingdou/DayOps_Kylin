// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/SinkAutoRebuild.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  去向自动重建（SR 组，2026-08-20）—— 规划链的**第 0 步**。
//
//  ══ 为什么它必须是算法的一步，而不是一个要人去点的按钮 ══
//  整条下游全挂在"去向"上：去向 → 运距 → 循环时间 → 配车数 → 车次 → 入方 → 排土
//  → 内排率 → 采排守恒。而 `dump_site` 现在是**空表**（V048 把 V004 编的两条种子删了），
//  于是 46 条腿全"去向未定"、一条运输任务都生不成、17 个排土面整月零入方。
//  排土场清单**不需要人填**：采掘单元台账里就有（2112 个排土位置，按场名分组即可），
//  能算出来的东西不该拦在一个按钮后面。
//
//  ══ 三条纪律 ══
//   SR1 **只在库里一个可用排土场都没有时才自动建**。人建过/改过的一律不碰 ——
//       自动重建去覆盖人填的坐标、通过能力、兜底运距，是把事实换成派生值。
//   SR2 **场名原样取台账的「采场/排土场」列**（`SinkFromLedgerBuilder` 已如此）。
//       绑定端读的是同一列，同一个源就不会差字符 —— 此前台账写「内排土场1」、
//       去向台账叫「内排土场」，按名字精确配差一个字符 ⇒ 17 个排土面整月零入方，
//       而甘特上只是少几行、不报错。
//   SR3 **派生得出来的才写**：场名、设计容量（占容方）、台阶高。
//       坐标 / 通过能力 / 工作线长 / 兜底运距 / 开放时窗一律留空 ——
//       编一个出来会让运距、库容告警、时窗全都看着正常而实际是假的。
//
//  ⚠ 自动建完要在来源文案里**说出来**：这批场是派生的、有几项没录，
//    否则下游拿着一个"容量有、坐标没有"的场算运距，人会以为运距是真的。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>去向（排土场）按采掘单元台账自动重建。永不抛。</summary>
public static class SinkAutoRebuild
{
    /// <summary>最近一次的结果文案（装配层直接拼进来源标签）。</summary>
    public static string LastLabel { get; private set; } = "";

    /// <summary>最近一次要人知道的事（逐条）。</summary>
    public static List<string> LastNotes { get; } = new();

    /// <summary>本进程内已经试过一次（失败也不反复重试，免得每装配一次盘子就读一遍台账）。</summary>
    private static bool _tried;

    /// <summary>判据用：忘掉"已经试过"。</summary>
    internal static void ResetForTest() { _tried = false; LastLabel = ""; LastNotes.Clear(); }

    /// <summary>
    /// 库里没有可用排土场时，按采掘单元台账建一批（SR1）。
    /// </summary>
    /// <param name="force">true = 不管库里有没有都重建一次（界面上那个按钮走这条）。</param>
    /// <returns>这次**新建**了几个场；0 = 没建（本来就有 / 台账读不到 / 台账里没有排土位置）。</returns>
    public static int EnsureSinks(bool force = false)
    {
        if (_tried && !force) return 0;
        _tried = true;
        LastNotes.Clear();

        // ── SR1：先看库里有没有 ──
        List<DumpSite> have;
        try { have = EquipmentDataContext.DumpSites.All(activeOnly: false).Where(d => d != null).ToList(); }
        catch (Exception ex)
        {
            LastLabel = $"去向重建：排土场台账读不到（{Short(ex)}）";
            return 0;
        }

        if (have.Count > 0 && !force)
        {
            // ★ 场已经有了，但**代表点可能还没有**（SR1 只保护人填的事实，
            //   而坐标是派生得出来的一个洞）。没有坐标运距就整条落到兜底，
            //   兜底一变循环时间/配车数/编组班产跟着一起偏 —— 所以这一格要补上。
            int filled = BackfillPoints(have);
            LastLabel = filled > 0
                ? $"去向：给 {filled} 个已有排土场补上了**派生代表点**（按台账排土位置的库容加权质心）—— 运距因此可走路网"
                : "";
            return 0;
        }

        // ── 台账基表 ──
        List<MiningUnitLedger.Row> rows;
        try
        {
            var store = new MonthlyUnitLedgerStore();
            if (!store.TryLoadBase(out rows, out var issues) || rows == null || rows.Count == 0)
            {
                LastLabel = "去向重建：采掘单元台账基表读不到 —— 排土场派生不出来"
                          + (issues is { Count: > 0 } ? $"（{issues[0]}）" : "");
                return 0;
            }
        }
        catch (Exception ex)
        {
            LastLabel = $"去向重建：台账读取出错（{Short(ex)}）";
            return 0;
        }

        var built = SinkFromLedgerBuilder.Build(rows);
        if (!built.Ok)
        {
            LastLabel = "去向重建：" + built.Headline;
            foreach (var n in built.Notes) LastNotes.Add(n);
            return 0;
        }

        // ── 写库（SR3：只写派生得出来的三项）──
        var byName = have.ToDictionary(d => (d.Name ?? "").Trim(), d => d, StringComparer.Ordinal);
        int created = 0, updated = 0, withPoint = 0;
        var errs = new List<string>();

        foreach (var sk in built.Sinks)
        {
            try
            {
                if (byName.TryGetValue(sk.Name, out var old))
                {
                    // 同名场只补设计容量 —— 整行 Upsert 会把带外键的列一起重写
                    EquipmentDataContext.DumpSites.UpdateDesignCapacity(old.DumpId, Math.Round(sk.CapacityM3 / 1e4, 3));
                    updated++;
                    continue;
                }

                EquipmentDataContext.DumpSites.Upsert(new DumpSite
                {
                    DumpId = SinkFromLedgerBuilder.IdOf(sk.Name),
                    Name = sk.Name,
                    // 名字里带「内排」按内排，否则外排；认不出按外排（保守：外排不吃采空区约束）
                    DumpType = sk.Name.Contains("内排") ? "internal" : "external",
                    DesignCapacityWanM3 = Math.Round(sk.CapacityM3 / 1e4, 3),
                    CurrentFilledWanM3 = 0,     // 0 = **还没盘点**，不是"空的"
                    BenchHeightM = sk.BenchHeightM > 0 ? sk.BenchHeightM : null,
                    Status = "active",
                    Notes = $"按采掘单元台账自动派生（{sk.SlotCount} 个排土位置）—— "
                          + "坐标/通过能力/工作线长/兜底运距/时窗均未录",
                    // ⚠ 这两列在库里是 NOT NULL DEFAULT CURRENT_TIMESTAMP，
                    //   而仓储的 INSERT 会把实体上的每一列都写一遍 —— 留 null 就撞 NOT NULL，
                    //   报出来是「一个都没写进去」而看不出是哪一列。缺省值只在"没写这一列"时才生效。
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                });
                created++;

                // ★ 代表点写进 sink_profile（坐标不在 dump_site 那张表里）。
                //   有坐标，运距才走得了路网；没有就整条落到兜底运距 ——
                //   兜底一变，循环时间、配车数、编组班产跟着一起偏，而每个数看着都正常。
                //   ⚠ 只写坐标：**通过能力、工作线长、开放时窗一律不碰**，
                //     那些是现场的事实，台账里没有，编一个出来会让卸点校核"算得出数"而说的是假的。
                if (sk.HasPoint)
                {
                    try
                    {
                        EquipmentDataContext.SinkProfiles.Upsert(new SinkProfile
                        {
                            SinkId = SinkFromLedgerBuilder.IdOf(sk.Name),
                            SinkKind = sk.Name.Contains("内排") ? "InternalDump" : "ExternalDump",
                            Status = "active",
                            X = Math.Round(sk.X, 3),
                            Y = Math.Round(sk.Y, 3),
                            Z = Math.Round(sk.Z, 3),
                            Note = "代表点按采掘单元台账各排土位置的**库容加权质心**派生 —— 不是实测卸点位置",
                        });
                        withPoint++;
                    }
                    catch (Exception ex) { errs.Add($"{sk.Name} 坐标：{Short(ex)}"); }
                }
            }
            catch (Exception ex) { errs.Add($"{sk.Name}：{Short(ex)}"); }
        }

        double capWan = built.Sinks.Sum(s => s.CapacityM3) / 1e4;
        LastLabel = created + updated == 0
            ? $"去向重建：{built.Sinks.Count} 个场一个都没写进去（{(errs.Count > 0 ? errs[0] : "原因不明")}）"
            : $"去向：按采掘单元台账**自动建了 {created} 个排土场**"
              + (updated > 0 ? $"（另有 {updated} 个同名场补了容量）" : "")
              + $"，合计设计容量 {capWan:N0} 万m³"
              + (withPoint > 0 ? $" · {withPoint} 个带派生代表点（运距可走路网）" : " · 无坐标，运距只能走兜底");

        // ★ 建完必须让去向登记簿重读：`SinkRegistryLoader.Current` 是**静态缓存**，
        //   本次装配早一步已经拿空表装载过一次并落到了「样例去向」。
        //   不失效的话，库里明明建好了场，这一盘用的还是样例 —— 而文案还写着"dump_site 空表"。
        if (created + updated > 0) { try { SinkRegistryLoader.Invalidate(); } catch { } }

        if (created > 0)
            LastNotes.Add($"· {created} 个排土场是**按台账自动派生**的（场名原样取台账的「采场/排土场」列，"
                        + "所以与作业面天然对得上）。**坐标、通过能力、工作线长、兜底运距、开放时窗都还没录** —— "
                        + "运距会走路网或兜底，卸点能力校核在这些场上不起作用。要精确就到「去向台账」补这几列。");
        if (errs.Count > 0)
            LastNotes.Add($"◆ {errs.Count} 个场没写进去：{string.Join("；", errs.Take(3))}");
        foreach (var n in built.Notes) LastNotes.Add(n);

        return created;
    }

    /// <summary>
    /// 给已有的排土场补**派生代表点**（只写 X/Y/Z，别的列一个不碰）。
    /// <para>
    /// 已经有坐标的跳过 —— 那可能是人测过的点，派生质心不该盖掉它。
    /// 返回补上了几个。永不抛。
    /// </para>
    /// </summary>
    /// <summary>
    /// 派生标记 —— 备注里带它就是**我们写的**（可以被下一版改进），没有就是人录的（一个字不碰）。
    /// </summary>
    private const string DerivedMark = "【派生代表点】";

    /// <summary>旧版派生备注里的固定片段（那一版还没有标记）。</summary>
    private const string LegacyDerivedMark = "不是实测卸点位置";

    /// <summary>当前作业日所在的期次 yyyy-MM（取不到返回空串 —— 空就退全场，不猜一个月份）。</summary>
    private static string PeriodKey()
    {
        try { return ProjectScope.WorkDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture); }
        catch { return ""; }
    }

    private static int BackfillPoints(List<DumpSite> have)
    {
        // ★ **卸载点是块体的重心，不是整个场的重心**（2026-08-20 晚改）。
        //
        //   卡车去的是"当期正在排的那个块"，而不是全场 2112 个位置的加权中心 ——
        //   内排土场1 沿走向铺开好几公里，两者能差 1 km 以上，
        //   而运距直接决定循环时间 → 配车数 → 编组班产。
        //   所以优先用**本期在排的块**；本期取不到才退回全场（并在文案里说清是哪一批）。
        List<MiningUnitLedger.Row> rows;
        string basis;
        try
        {
            var store = new MonthlyUnitLedgerStore();
            string month = PeriodKey();
            if (!store.TryLoadBase(out var all, out _) || all is not { Count: > 0 }) return 0;

            // ⚠ **几何只在基表里**：期次文件（2026-08.csv）是量的中间格式，没有 Cx/Cy/Cz。
            //   直接拿期次行算质心 ⇒ 坐标全 0 ⇒ 一个点都补不上，而"没说话"看着像"本来就有"。
            //   所以：**清单取本期、几何取基表**，按单元号取交集。
            if (month.Length > 0 && store.TryLoad(month, out var cur, out _) && cur is { Count: > 0 })
            {
                var ids = new HashSet<string>(cur.Where(r => r != null && !string.IsNullOrWhiteSpace(r.UnitId))
                                                 .Select(r => r.UnitId.Trim()), StringComparer.OrdinalIgnoreCase);
                var hit = all.Where(r => r != null && ids.Contains((r.UnitId ?? "").Trim())).ToList();
                if (hit.Count > 0) { rows = hit; basis = $"本期（{month}）在排的块"; }
                else { rows = all; basis = "全场（本期的块在基表里对不上号，退回全场）"; }
            }
            else { rows = all; basis = "全场（本期台账取不到，退回基表）"; }
        }
        catch { return 0; }

        SinkFromLedgerResult built;
        try { built = SinkFromLedgerBuilder.Build(rows); }
        catch { return 0; }
        if (!built.Ok) return 0;

        var byName = built.Sinks.Where(x => x.HasPoint)
                          .ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        if (byName.Count == 0) return 0;

        List<SinkProfile> profiles;
        try { profiles = EquipmentDataContext.SinkProfiles.All().Where(p => p != null).ToList(); }
        catch { profiles = new List<SinkProfile>(); }
        var profOf = new Dictionary<string, SinkProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
            if (!string.IsNullOrWhiteSpace(p.SinkId)) profOf[p.SinkId.Trim()] = p;

        int filled = 0, snapped = 0;
        var farFromRoad = new List<string>();
        foreach (var d in have)
        {
            string name = (d.Name ?? "").Trim();
            if (!byName.TryGetValue(name, out var sk)) continue;
            if (string.IsNullOrWhiteSpace(d.DumpId)) continue;

            profOf.TryGetValue(d.DumpId.Trim(), out var prof);
            // ★ 已经有坐标的**分两种**（2026-08-20 晚，同一个坑的下一层）：
            //   · **人录/实测的** —— 一个字都不碰。
            //   · **上一版自己派生的** —— 要允许改进（这一版会把点吸到路面上）。
            //     不分开判的话，"已有坐标就跳过"会把自己派生的那个点永久冻住，
            //     后面所有改进都落不到它身上，而界面上一切正常。
            //   分辨靠备注里的派生标记：没有标记就是人的，有标记就是我们写的。
            //   ⚠ 认标记要**连旧版写的也认**：标记是这一版才加的，而库里那条备注是上一版写的、
            //     没有标记 —— 只认新标记的话，上一版自己派生的点会被当成"人录的"永远冻住。
            //     旧版那句话里恒有「不是实测卸点位置」，拿它当旧标记。
            string note0 = prof?.Note ?? "";
            bool derivedBefore = note0.Contains(DerivedMark, StringComparison.Ordinal)
                              || note0.Contains(LegacyDerivedMark, StringComparison.Ordinal);
            bool hasPoint = prof != null && (Math.Abs(prof.X) > 1e-9 || Math.Abs(prof.Y) > 1e-9);
            if (hasPoint && !derivedBefore) continue;

            prof ??= new SinkProfile
            {
                SinkId = d.DumpId.Trim(),
                SinkKind = name.Contains("内排") ? "InternalDump" : "ExternalDump",
                Status = "active",
            };

            // ★ **吸到路面上**（TP 组）：卡车卸在块体靠路的那一侧，不是块体中心。
            //   中心落在场内部，离路可能几百米 —— 吸不上就整条腿落回兜底运距。
            //   实测：点从"全场重心"换成"在排块重心"后路网腿 16→8，少的那些不是没有路，是点不在路上。
            var snap = TipPointSnapper.Snap(sk.X, sk.Y, sk.Z);
            prof.X = snap.Ok ? snap.X : Math.Round(sk.X, 3);
            prof.Y = snap.Ok ? snap.Y : Math.Round(sk.Y, 3);
            prof.Z = snap.Ok ? snap.Z : Math.Round(sk.Z, 3);
            if (snap.Ok) snapped++;
            else if (!double.IsInfinity(snap.DistanceM)) farFromRoad.Add($"{name}（离路 {snap.DistanceM:0} m）");
            if (sk.WorkLineLengthM > 1e-6 && prof.WorkLineLengthM <= 1e-6)
                prof.WorkLineLengthM = sk.WorkLineLengthM;      // 只补空的，人填过的不动
            // 备注只留一条派生说明（否则每跑一次就多接一句，几轮之后没人读得下去）
            string keep = (prof.Note ?? "");
            int mark = keep.IndexOf(DerivedMark, StringComparison.Ordinal);
            if (mark >= 0) keep = keep[..mark].TrimEnd('；', ';', ' ');
            prof.Note = (keep.Length > 0 ? keep + "；" : "")
                      + DerivedMark + $"按{basis}的块体质心"
                      + (snap.Ok ? $"、并吸到路面（离路 {snap.DistanceM:0} m）" : "、未吸上路面")
                      + " —— 不是实测卸点位置";

            try { EquipmentDataContext.SinkProfiles.Upsert(prof); filled++; }
            catch { /* 写不进去就算了，下一轮再补；不拖垮装配 */ }
        }

        if (filled > 0)
        {
            try { SinkRegistryLoader.Invalidate(); } catch { }
            if (farFromRoad.Count > 0)
                LastNotes.Add($"◆ {farFromRoad.Count} 个卸点**离路网太远、没吸上**（{string.Join("、", farFromRoad.Take(3))}）—— "
                            + $"超过 {TipPointSnapper.MaxSnapM:0} m 就不硬吸：吸到几百米外的一条路上，"
                            + "运距会算得又准又假。这几个的运距仍走兜底，要么补路网、要么录真实卸点坐标。");
            LastNotes.Add($"· 给 {filled} 个排土场补了**派生代表点与工作线长**（按{basis}的块体质心，"
                        + $"其中 {snapped} 个已**吸到路面**上）。"
                        + "它不是实测的卸点位置，只是让运距能走路网 —— 要精确就到「去向台账」录真实卸点坐标。");
        }
        return filled;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? "";
        int i = m.IndexOf('\n');
        if (i > 0) m = m[..i];
        return m.Length > 60 ? m[..60] + "…" : m;
    }
}
