// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ProcessZonePlanner.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;                  // LedgerKind
using PitMine3D.Kylin.Data.Entities;           // ProcessZone（工序码值域只在实体那一层定义）
using PitMine3D.Kylin.TaskLib.Engine;                        // WorkCalendar
using PitMine3D.Kylin.TaskLib.Simulation;                    // UnitPrism

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  工序作业区 —— 本期块段 → 五道工序各自的那块地。
//
//  ══ 这一层要回答的问题 ══
//  「这个月，钻机去哪、放炮时哪片要清场、电铲在哪、卡车在哪卸、推土机在哪推」。
//
//  作业区域那一层（ZoneAutoPlanner / Z 组）回答的是「本月在哪儿作业」——
//  它出的其实**只有采装那一块地**，却挂着"作业面"的名字。
//
//  ══ 全部口径的根（P0）══
//  **同一个作业面、同一个月，五道工序的区域不是同一块地** ——
//  穿孔超前、爆破在其后、采装再后、排土在另一头，它们沿推进方向错开成几条带。
//  把五道工序共用一份占地，等于把"这个月这个面动过的所有地"压成一块：
//  钻机会被派到电铲脚下、警戒区会圈住早就采完的地，而每一块的面积都"看着对"。
//
//  ══ 已定的规则（P 组）══
//  **P1 采装区 = 本期单元占地并集**，与三维单元体同源（Z4）。这一块与 Z 组等价。
//  **P2 穿孔区 = 采装队列上开一个前推 lead 的窗**。队列按台账排产序 Seq 排，
//      以**剩余量**为轴累加（已采的那部分早就爆过了）。窗口 = 累计量 [V_lead, V_total)，
//      前面被切掉的那段是上期打的孔，后面缺的那段落在下期 —— **两段都要报出来**。
//      这是一维问题：在队列上挪一个窗，不是两块地求交。
//  **P3 免爆的面没有穿孔区**。物料优先（MaterialSpec.NeedsBlasting），
//      没有面绑就按台账类型退定并标明。不筛这一条会在免爆面上圈出一块钻机作业区，
//      而所有数字都正常 —— 「免爆」与「算不出台阶高」的月延米都是 0，报表上一模一样。
//  **P4 爆破警戒区 = 穿孔区栅格膨胀 R**，不走等距偏移。R 一般 200~300m 而单元宽只有几十米，
//      这是**大偏移**，正是 ZE6 那笔账（环被里外翻个个儿而面积比和收口守卫都判不出来）的量级。
//  **P5 警戒区按炮次分组，不按作业面**。它天然会盖住相邻的面 —— 这正是它的用处
//      （放炮时哪几个面要撤人）。硬套「分组键 = 作业面」会把它切成几块各自不完整的地。
//      落地口径：膨胀后的**连通块**就是一个炮次的警戒范围，名字带上它盖住的面。
//  **P6 排土卸载带 = 同一对真轨换一个更小的 W 重切**，不是把占地环再切一刀
//      （环在凹弯处会自交，切出来的两半都不成立）。
//  **P7 排土推排带 = 全幅栅格 − 卸载带栅格**，同一片格网上相减，不做多边形布尔（Z5）。
//  **P8 幅宽不够时不切零宽带**：W ≤ w卸 ⇒ 整幅都是卸载带、**没有推排带**，
//      点名报出来。切一条 0.3m 宽的推排带会让人以为推土机有地方站。
//  **P9 运输不出面状区域**。它是线状的（装车点 → 路径 → 卸载点），
//      真正需要地的是两个端点，那是装卸点台账的事，不是这里。
//  **P10 高程与作业区域同一条**（Z7：坡顶线最小二乘平面，夹在源点 Z 区间内）；
//      解不出**拒绝入库**，不补 0。
//  **P11 每一块都记账**：期次 / 工序 / 分组 / 单元清单 / 格距 / Z 出处 / 用了哪个超前期，
//      全写进 note。三个月后有人问"这条边界哪来的"，答案得在库里。
//  **P12 生成不自动入库**（同 Z10）：候选先画在图上，勾了才写。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>工序作业区的生成参数。</summary>
public sealed class ProcessZoneOptions
{
    /// <summary>格距 m；&lt;=0 = 按范围自适应。轮廓精度就是它。</summary>
    public double CellM;

    /// <summary>
    /// 穿爆超前（工日，全局缺省）。<b>硬岩大区爆破与煤层控制爆破的超前期本来就不一样</b>，
    /// 一个全局常数表达不了 —— 面级覆盖走 <see cref="FaceLeadDays"/>。
    /// </summary>
    public double BlastLeadDays = 3;

    /// <summary>面级超前期覆盖（作业面名 → 工日）。缺省用 <see cref="BlastLeadDays"/>。</summary>
    public Dictionary<string, double> FaceLeadDays { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 爆破警戒半径 m。缺省 300 —— GB6722 对个别飞散物的人员安全距离是 ≥200m，
    /// 露天矿一般取 300m。<b>飞石 / 振动 / 冲击波三个距离不是一个数</b>，
    /// 这里取的是清场用的那个（最大的那个），别拿振动距离往里填。
    /// </summary>
    public double GuardRadiusM = 300;

    /// <summary>排土卸载带宽 m（车长 + 车挡 + 安全余量）。缺省 25。</summary>
    public double TipBandWidthM = 25;

    /// <summary>月作业日数；&lt;=0 = 从 <see cref="WorkCalendar"/> 取（再取不到用兜底常数）。</summary>
    public double MonthWorkdays;

    /// <summary>要生成哪几道工序。缺省全生成。</summary>
    public HashSet<string> Enabled { get; } =
        new(ProcessZone.AllProcesses, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 下一期的块段 —— 穿孔窗口前推后越过本期末尾的那一段在这里取。
    /// <b>null = 取不到</b>，此时那一段会如实报「缺」，不拿本期的顶替。
    /// </summary>
    public PlanScope? NextScope { get; set; }

    public double LeadFor(string faceName)
        => FaceLeadDays.TryGetValue(faceName ?? "", out double d) && d >= 0 ? d : Math.Max(0, BlastLeadDays);
}

/// <summary>一块候选工序作业区。</summary>
public sealed class ProcessZoneProposal
{
    /// <summary>工序码（<see cref="ProcessZone"/> 的值域）。</summary>
    public string Process = "";
    public string Name = "";
    public string GroupKey = "";
    public int PieceIndex = 1, PieceCount = 1;

    public List<ZonePoint> Ring = new();
    /// <summary>Z 出处。<b>空 = 拒绝入库</b>。</summary>
    public string ZSource = "";
    public string Note = "";

    public List<PlanBlock> Blocks = new();

    public double AreaM2, RingAreaM2, CellM;
    /// <summary>膨胀顶到了格网边界 —— 本区被截断（仅警戒区会有）。</summary>
    public bool ClippedAtBorder;

    public double? VolumeM3;
    public string Basis = "";
    public string EquipRole = "";
    public double? LeadDays, GuardRadiusM, BandWidthM;

    public List<string> Issues = new();
    public bool Selected;

    /// <summary>同 (期次, 工序, 名字) 的既有行 —— 入库时换几何，不新增。</summary>
    public ProcessZone? Existing;

    public string ProcessZh => ProcessZone.DisplayName(Process);
    public string ActionText => Existing == null ? "新增" : "改边界";
    public bool IsKeepOut => ProcessZone.IsKeepOut(Process);

    public string Caption =>
        $"{Name}（{ProcessZh}）　{AreaM2 / 1e4:0.##} 万m²　"
      + (VolumeM3.HasValue ? $"{VolumeM3.Value / 1e4:0.##} 万m³·{Basis}　" : "")
      + $"{Blocks.Count} 个单元　{ActionText}";
}

/// <summary>一次工序区规划的结果。</summary>
public sealed class ProcessZonePlanResult
{
    public string Period = "";
    public List<ProcessZoneProposal> Proposals = new();
    public List<string> Notes = new();
    public string Header = "";
    public PlanScope Scope = new();
    /// <summary>本次用的参数（写进 note，三个月后能回答「这条边界哪来的」）。</summary>
    public ProcessZoneOptions Options = new();

    public bool Ok => Proposals.Count > 0;
    public int SelectedCount => Proposals.Count(p => p.Selected);
    public IEnumerable<ProcessZoneProposal> Of(string process)
        => Proposals.Where(p => string.Equals(p.Process, process, StringComparison.OrdinalIgnoreCase));
}

/// <summary>本期块段 → 五道工序各自的候选区域。纯计算，不碰数据库（入库走 <c>ProcessZoneStore</c>）。</summary>
public static class ProcessZonePlanner
{
    /// <summary>碎块阈值：占本组不到这个比例的连通块默认不勾并单独说明（同 Z6）。</summary>
    private const double SliverShare = 0.03;

    // ══════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════

    /// <summary>按期次规划（自动把下一期也装进来，供穿孔窗口前推用）。</summary>
    public static ProcessZonePlanResult Plan(string month, ProcessZoneOptions? opt = null,
                                             IReadOnlyList<ProcessZone>? existing = null,
                                             string? ledgerRoot = null)
    {
        opt ??= new ProcessZoneOptions();
        if (opt.NextScope == null)
        {
            string next = NextMonth(month);
            if (next.Length > 0)
            {
                var ns = ZonePlanSource.Load(next, ledgerRoot);
                if (ns.Ok) opt.NextScope = ns;
            }
        }
        return Plan(ZonePlanSource.Load(month, ledgerRoot), opt, existing);
    }

    /// <summary>块段已装好时用这一版（离线判据喂合成算例走它）。</summary>
    public static ProcessZonePlanResult Plan(PlanScope scope, ProcessZoneOptions? opt = null,
                                             IReadOnlyList<ProcessZone>? existing = null)
    {
        opt ??= new ProcessZoneOptions();
        var res = new ProcessZonePlanResult { Period = scope.Month, Scope = scope, Options = opt };
        res.Notes.AddRange(scope.Notes);
        if (!scope.Ok) { res.Header = scope.Header; return res; }

        double workdays = ResolveWorkdays(scope.Month, opt, res);

        // ── 分组：与作业区域同一条口径（作业面优先，没有面按 采场/排土场+台阶 兜底）──
        //    这里**不许再写一份**分组逻辑：两处分法不同的话，同一个面在两张表里叫两个名字，
        //    而两边各自都"看着对"。
        var groups = scope.Blocks
            .GroupBy(b => (Name: ZoneAutoPlanner.GroupNameOf(b), b.IsDump))
            .OrderByDescending(g => g.Sum(b => b.VolumeM3))
            .ToList();

        // 穿孔区的占地要攒起来 —— 警戒区是在**全矿**的穿孔区上膨胀出来的（P5）
        var drillPolys = new List<double[]>();
        var drillOwner = new List<string>();          // 与 drillPolys 一一对应的面名

        foreach (var g in groups)
        {
            var blocks = g.ToList();
            string name = g.Key.Name;

            if (opt.Enabled.Contains(ProcessZone.ProcLoad))
                EmitFromPolys(res, ProcessZone.ProcLoad, name, blocks,
                              blocks.Select(b => b.FootprintXy).ToList(), opt, null);

            if (!g.Key.IsDump && opt.Enabled.Contains(ProcessZone.ProcDrill))
            {
                var win = DrillWindow(name, blocks, opt, workdays, res);
                if (win.Count > 0)
                {
                    var polys = win.Select(b => b.FootprintXy).ToList();
                    EmitFromPolys(res, ProcessZone.ProcDrill, name, win, polys, opt,
                                  p => p.LeadDays = opt.LeadFor(name));
                    drillPolys.AddRange(polys);
                    for (int i = 0; i < polys.Count; i++) drillOwner.Add(name);
                }
            }

            if (g.Key.IsDump) DumpBands(res, name, blocks, opt);
        }

        if (opt.Enabled.Contains(ProcessZone.ProcBlastGuard))
            GuardZones(res, drillPolys, drillOwner, opt);

        // ── 既有行配对（按 期次 + 工序 + 名字 三键）──
        foreach (var p in res.Proposals)
            p.Existing = existing?.FirstOrDefault(
                e => Eq(e.Period, res.Period) && Eq(e.Process, p.Process) && Eq(e.Name, p.Name));

        Summarize(res, groups.Count, opt);
        return res;
    }

    // ══════════════════════════════════════════════════════════════
    //  P2 / P3：穿孔窗口
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 采装队列上开一个前推 lead 的窗，返回窗内的单元。
    ///
    /// <para><b>为什么是一维的</b>：这个月钻机打的是「这个月末到下个月初要采的料」。
    /// 把面内单元按排产序排成一条队、以剩余量为轴，窗口就是把采装那一段整体往前挪 V_lead。
    /// 挪出去的那一段（队列头 V_lead 的量）是<b>上期</b>打的孔，挪进来的那一段（越过本期末尾的
    /// V_lead）落在<b>下期</b> —— 两段都要如实报，不许拿本期的顶上去。</para>
    ///
    /// <para><b>跨界的那个单元整取</b>：一个单元只能整个进或整个不进（占地是整块地，
    /// 切一半没有对应的真轨）。这条口径要说出来 —— 不说的话，「窗口比采装区大了半个单元」
    /// 会被当成算法出错。</para>
    /// </summary>
    private static List<PlanBlock> DrillWindow(string faceName, List<PlanBlock> blocks,
                                               ProcessZoneOptions opt, double workdays,
                                               ProcessZonePlanResult res)
    {
        var need = blocks.Where(b => b.NeedsBlasting).ToList();
        int skipped = blocks.Count - need.Count;
        if (need.Count == 0)
        {
            if (blocks.Count > 0)
                res.Notes.Add($"· 「{faceName}」**没有穿孔区**：{blocks.Count} 个单元全是免爆的"
                            + (blocks.Any(b => b.BlastFromMaterial)
                                ? "（按作业面的物料码判定）"
                                : "（**没有面绑这些单元**，按台账类型退定的 —— 煤台阶按免爆、岩台阶按需爆破。"
                                + "硬煤要爆破的面请在作业面台账上填物料码）"));
            return new List<PlanBlock>();
        }
        if (skipped > 0)
            res.Notes.Add($"· 「{faceName}」有 {skipped}/{blocks.Count} 个单元免爆，已排除在穿孔区之外。");

        // 排产序为 0 = 台账没排序。整队都没序号时队列退化成任意顺序 ——
        // 那不是「超前期为 0」，是「排不出队」，必须分开说。
        int noSeq = need.Count(b => b.Seq == 0);
        var q = need.OrderBy(b => b.Seq == 0 ? int.MaxValue : b.Seq)
                    .ThenBy(b => b.UnitId, StringComparer.Ordinal)
                    .ToList();

        double lead = opt.LeadFor(faceName);
        double total = q.Sum(b => b.RemainM3);
        if (!(total > 1e-6))
        {
            res.Notes.Add($"◆ 「{faceName}」需爆破的 {q.Count} 个单元剩余量合计为 0 —— "
                        + "要么本期已全部采完、要么台账的量是空的。穿孔区取整队。");
            return q;
        }
        if (!(workdays > 0)) workdays = WorkCalendar.FallbackMonthWorkdays;
        double vLead = total * Math.Clamp(lead, 0, workdays) / workdays;

        var win = new List<PlanBlock>();
        double cum = 0, dropped = 0;
        foreach (var b in q)
        {
            double v = b.RemainM3;
            // 单元占 [cum, cum+v)；与窗口 [vLead, total) 有交叠就整取
            if (cum + v > vLead - 1e-9) win.Add(b); else dropped += v;
            cum += v;
        }

        if (noSeq == need.Count && lead > 1e-9)
            res.Notes.Add($"◆ 「{faceName}」的 {need.Count} 个单元**台账里都没有排产序**（Seq 全为 0）—— "
                        + "队列排不出来，穿孔窗口只能按单元号顺序开，位置不代表真实的采掘顺序。"
                        + "补法：在「采掘单元清单」里排一次序（那一步会写 Seq 列）。");
        else if (noSeq > 0)
            res.Notes.Add($"· 「{faceName}」有 {noSeq}/{need.Count} 个单元没有排产序，已排到队尾。");

        if (lead > 1e-9)
            res.Notes.Add($"· 「{faceName}」穿孔区按超前 {lead:0.#} 工日前推："
                        + $"队头 {dropped / 1e4:0.##} 万m³ 是**上期**打的孔（已排除）；"
                        + $"越过本期末尾的 {vLead / 1e4:0.##} 万m³ 落在**下期**"
                        + (opt.NextScope != null ? "（下期台账已装入，见下期的采装区）"
                                                 : " —— **下一期还没排产，这一段现在圈不出来**")
                        + $"。月作业日按 {workdays:0.#} 天摊。"
                        + "跨窗口边界的单元整取（占地是整块地，切一半没有对应的真轨）。");
        return win;
    }

    // ══════════════════════════════════════════════════════════════
    //  P4 / P5：爆破警戒区
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 全矿的穿孔区膨胀 R → 警戒区。<b>连通块即一个炮次的警戒范围</b>（P5）。
    /// <para>按作业面分组是错的：警戒区天然跨面，而这正是它要回答的问题
    /// （放炮时哪几个面要撤人）。</para>
    /// </summary>
    private static void GuardZones(ProcessZonePlanResult res, List<double[]> drillPolys,
                                   List<string> owners, ProcessZoneOptions opt)
    {
        if (drillPolys.Count == 0)
        {
            if (opt.Enabled.Contains(ProcessZone.ProcDrill))
                res.Notes.Add("· 本期没有任何穿孔区 ⇒ 没有爆破警戒区（全部面免爆，或本期块段都不需爆破）。");
            return;
        }
        double r = Math.Max(0, opt.GuardRadiusM);
        if (!(r > 1e-6))
            res.Notes.Add("◆ 警戒半径为 0 —— 警戒区会与穿孔区完全重合，这几乎肯定是参数没填。");

        // 预留 R：不预留的话膨胀会被格网边界削掉，而削掉之后仍是一条规规矩矩的闭合环。
        var g = ZoneRaster.Grid.Lattice(drillPolys, opt.CellM, r + 4, out string why);
        if (g == null) { res.Notes.Add($"◆ 爆破警戒区建不出格网（{why}）。"); return; }
        g.FillAll(drillPolys);
        var dil = g.Dilate(r);
        var union = dil.Trace();
        if (!union.Ok) { res.Notes.Add("◆ 爆破警戒区膨胀后并不出轮廓。"); return; }

        // 每个连通块盖住了哪几个面（名字要说得出来 —— 清场是按面点人的）
        double total = union.TotalAreaM2;
        for (int i = 0; i < union.Pieces.Count; i++)
        {
            var piece = union.Pieces[i];
            var faces = new List<string>();
            for (int k = 0; k < drillPolys.Count; k++)
            {
                var (cx, cy) = ZoneAutoPlanner.Centroid(drillPolys[k]);
                if (!ZoneAutoPlanner.ContainsXy(piece.Ring, cx, cy)) continue;
                if (!faces.Contains(owners[k], StringComparer.Ordinal)) faces.Add(owners[k]);
            }
            var mine = new List<PlanBlock>();
            foreach (var pr in res.Of(ProcessZone.ProcDrill))
                if (faces.Contains(pr.GroupKey, StringComparer.Ordinal)) mine.AddRange(pr.Blocks);
            mine = mine.Distinct().ToList();

            string label = faces.Count == 0 ? $"炮次{i + 1}"
                         : faces.Count <= 2 ? string.Join("+", faces)
                         : $"{faces[0]}等{faces.Count}个面";
            var prop = new ProcessZoneProposal
            {
                Process = ProcessZone.ProcBlastGuard,
                Name = $"警戒·{label}",
                GroupKey = label,
                PieceIndex = i + 1,
                PieceCount = union.Pieces.Count,
                AreaM2 = piece.CellAreaM2,
                RingAreaM2 = piece.RingAreaM2,
                CellM = union.CellM,
                ClippedAtBorder = dil.ClippedAtBorder,
                Blocks = mine,
                GuardRadiusM = r,
                EquipRole = ProcessZone.EquipRole(ProcessZone.ProcBlastGuard),
                Basis = "",
                VolumeM3 = null,               // 警戒区没有量 —— 给个 0 会被下游 SUM 进工程量
                Selected = piece.CellAreaM2 >= total * SliverShare,
            };
            prop.Issues.Add("**这是禁入区，不是作业区** —— 它说的是「这段时间谁都不许进」，"
                          + "与其余四类的语义相反。别拿它去派设备。");
            if (faces.Count > 1)
                prop.Issues.Add($"本区跨 {faces.Count} 个作业面（{string.Join("、", faces)}）—— "
                              + "放炮时这几个面都要撤人。**这正是警戒区不按面分组的原因**。");
            if (dil.ClippedAtBorder)
                prop.Issues.Add("⚠ 膨胀顶到了格网边界，**本区被截断**。截断后的轮廓仍是一条闭合环，"
                              + "图上看不出来 —— 把格距调小或缩小警戒半径再生成一次。");
            FinishPiece(res, prop, piece, mine, union, opt);
        }
        res.Notes.Add($"· 爆破警戒区：穿孔区外扩 {r:0.#}m（栅格膨胀，不是等距偏移）→ "
                    + $"{union.Pieces.Count} 个连通块 = {union.Pieces.Count} 个炮次范围，"
                    + $"合计 {total / 1e4:0.##} 万m²。**按炮次分，不按作业面**。");
    }

    // ══════════════════════════════════════════════════════════════
    //  P6 / P7 / P8：排土的卸载带与推排带
    // ══════════════════════════════════════════════════════════════

    private static void DumpBands(ProcessZonePlanResult res, string groupName,
                                  List<PlanBlock> blocks, ProcessZoneOptions opt)
    {
        bool wantTip = opt.Enabled.Contains(ProcessZone.ProcDumpTip);
        bool wantDoze = opt.Enabled.Contains(ProcessZone.ProcDumpDoze);
        if (!wantTip && !wantDoze) return;

        double w = Math.Max(0, opt.TipBandWidthM);
        if (!(w > 1e-6)) { res.Notes.Add("◆ 卸载带宽为 0 —— 排土的两条带切不出来。"); return; }

        var tipPolys = new List<double[]>();
        var allPolys = new List<double[]>();
        var tooNarrow = new List<string>();
        var recut = new List<string>();

        foreach (var b in blocks)
        {
            allPolys.Add(b.FootprintXy);
            // P8：幅宽不够 ⇒ 整幅都是卸载带，**没有推排带**。切一条 0.3m 的带
            //     会让人以为推土机有地方站。
            double full = b.WidthM;
            if (full > 1e-6 && full <= w + 1e-6) { tipPolys.Add(b.FootprintXy); tooNarrow.Add(b.UnitId); continue; }

            var band = TipBand(b, w);
            if (band.Length >= 6) tipPolys.Add(band);
            else { tipPolys.Add(b.FootprintXy); recut.Add(b.UnitId); }
        }

        if (tooNarrow.Count > 0)
            res.Notes.Add($"◆ 「{groupName}」有 {tooNarrow.Count} 个排土幅的**推进宽 ≤ 卸载带宽 {w:0.#}m**"
                        + $"（{string.Join("、", tooNarrow.Take(6))}{(tooNarrow.Count > 6 ? " 等" : "")}）—— "
                        + "这些幅**整幅都是卸载带，没有推排带**。不是算法少切了一块："
                        + "幅本来就窄，推土机在这些幅上没有独立的站位。要么把幅加宽，要么接受卡车推土交替作业。");
        if (recut.Count > 0)
            res.Notes.Add($"◆ 「{groupName}」有 {recut.Count} 个排土幅**重切失败**"
                        + $"（{string.Join("、", recut.Take(6))}）—— 已整幅当卸载带处理，"
                        + "这一部分的推排带会偏小。多半是这些幅既没有真轨、台账里也没有走向方位角。");

        if (wantTip)
            EmitFromPolys(res, ProcessZone.ProcDumpTip, groupName, blocks, tipPolys, opt,
                          p => p.BandWidthM = w);

        if (!wantDoze) return;

        // P7：全幅 − 卸载带，**同一片格网**上相减。各自 Union 一次再减是错的
        //     （两次自适应格距不同、原点也不同，减出来的边缘全是锯齿状假空洞）。
        var lat = ZoneRaster.Grid.Lattice(allPolys, opt.CellM, 0, out string why);
        if (lat == null) { res.Notes.Add($"◆ 「{groupName}」推排带建不出格网（{why}）。"); return; }
        var gAll = lat.Blank(); gAll.FillAll(allPolys);
        var gTip = lat.Blank(); gTip.FillAll(tipPolys);
        var gDoze = gAll.Minus(gTip, out string mw);
        if (gDoze == null) { res.Notes.Add($"◆ 「{groupName}」推排带相减失败（{mw}）。"); return; }
        if (!gDoze.Any)
        {
            res.Notes.Add($"· 「{groupName}」**没有推排带**：全部排土幅的推进宽都不超过卸载带宽 {w:0.#}m。");
            return;
        }
        var union = gDoze.Trace();
        EmitPieces(res, ProcessZone.ProcDumpDoze, groupName, blocks, union, opt, false,
                   p => p.BandWidthM = Math.Max(0, blocks.Max(b => b.WidthM) - w));
    }

    /// <summary>
    /// P6：把一个排土幅重切成靠坡顶线那一侧的前缘带。
    /// <para>真轨走 <c>UnitPrism.PlanFootprint</c> 换一个更小的 W；
    /// 盒子退路把质心沿推进方向朝前挪 (w−W)/2 再按 w 摆一片。</para>
    /// </summary>
    private static double[] TipBand(PlanBlock b, double w)
    {
        if (b.RealRail && b.Crest.Length >= 6 && b.Toe.Length >= 6)
        {
            if (UnitPrism.PlanFootprint(b.Crest, b.Toe, w, b.TowardCrest, 0, 1,
                                        out var f, out var r, out _, out _, out _, out _))
                return UnitPrism.PlanRing(f, r);
            return Array.Empty<double>();
        }
        if (!(b.LengthM > 1e-6) || !(b.WidthM > 1e-6)) return Array.Empty<double>();

        // 局部 v 轴（推进方向）= 走向的左垂线，与 BoxPlanFootprint 同一套；
        // 前脸在 v = −W/2，所以带心要挪到 v = (w−W)/2。
        double ex = 1, ey = 0;
        if (b.AzimuthDeg.HasValue)
        {
            double rad = b.AzimuthDeg.Value * Math.PI / 180.0;
            ex = Math.Sin(rad); ey = Math.Cos(rad);
        }
        double nx = -ey, ny = ex, d = (w - b.WidthM) * 0.5;
        if (!UnitPrism.BoxPlanFootprint(b.Cx + d * nx, b.Cy + d * ny, b.LengthM, w, 0, 1, b.AzimuthDeg,
                                        out var bf, out var br, out _)) return Array.Empty<double>();
        return UnitPrism.PlanRing(bf, br);
    }

    // ══════════════════════════════════════════════════════════════
    //  共用：占地 → 候选（分块 / 归块 / 高程 / 记账）
    // ══════════════════════════════════════════════════════════════

    private static void EmitFromPolys(ProcessZonePlanResult res, string process, string groupName,
                                      List<PlanBlock> blocks, List<double[]> polys,
                                      ProcessZoneOptions opt, Action<ProcessZoneProposal>? decorate)
    {
        var union = ZoneRaster.Union(polys, opt.CellM);
        if (!union.Ok)
        {
            res.Notes.Add($"◆ 「{groupName}」的{ProcessZone.DisplayName(process)}区并不出轮廓（{union.Note}）。");
            return;
        }
        EmitPieces(res, process, groupName, blocks, union, opt, false, decorate);
    }

    private static void EmitPieces(ProcessZonePlanResult res, string process, string groupName,
                                   List<PlanBlock> blocks, ZoneRaster.UnionResult union,
                                   ProcessZoneOptions opt, bool clipped,
                                   Action<ProcessZoneProposal>? decorate)
    {
        double total = union.TotalAreaM2;

        // 单元归块：质心落在哪一块里就算哪一块的（都不在算最近的那一块）
        var buckets = new List<List<PlanBlock>>();
        for (int i = 0; i < union.Pieces.Count; i++) buckets.Add(new List<PlanBlock>());
        foreach (var b in blocks)
        {
            var (cx, cy) = ZoneAutoPlanner.Centroid(b.FootprintXy);
            int hit = -1;
            for (int i = 0; i < union.Pieces.Count && hit < 0; i++)
                if (ZoneAutoPlanner.ContainsXy(union.Pieces[i].Ring, cx, cy)) hit = i;
            if (hit < 0) hit = ZoneAutoPlanner.NearestPiece(union.Pieces, cx, cy);
            buckets[hit].Add(b);
        }

        for (int i = 0; i < union.Pieces.Count; i++)
        {
            var piece = union.Pieces[i];
            var mine = buckets[i];
            var prop = new ProcessZoneProposal
            {
                Process = process,
                Name = i == 0 ? groupName : $"{groupName}-{i + 1}",
                GroupKey = groupName,
                PieceIndex = i + 1,
                PieceCount = union.Pieces.Count,
                Blocks = mine,
                AreaM2 = piece.CellAreaM2,
                RingAreaM2 = piece.RingAreaM2,
                CellM = union.CellM,
                ClippedAtBorder = clipped,
                EquipRole = ProcessZone.EquipRole(process),
                Basis = ProcessZone.VolumeBasis(process),
                VolumeM3 = mine.Sum(b => b.VolumeM3),
                Selected = i == 0 && piece.CellAreaM2 >= total * SliverShare,
            };
            decorate?.Invoke(prop);
            if (i > 0)
                prop.Issues.Add($"这是「{groupName}」{prop.ProcessZh}区的第 {i + 1} 块"
                              + $"（本组共 {union.Pieces.Count} 块不相连的地）—— 默认不勾。");
            FinishPiece(res, prop, piece, mine.Count > 0 ? mine : blocks, union, opt);
        }
    }

    /// <summary>高程 + 描边可信度 + 记账，然后收进结果。</summary>
    private static void FinishPiece(ProcessZonePlanResult res, ProcessZoneProposal prop,
                                    ZoneRaster.Piece piece, List<PlanBlock> zBlocks,
                                    ZoneRaster.UnionResult union, ProcessZoneOptions opt)
    {
        // P10：高程与作业区域同一条（Z7）。解不出 ⇒ 拒绝入库，不补 0。
        if (ZoneAutoPlanner.FitRingZ(zBlocks, piece.Ring, out var ring, out string prov))
        { prop.Ring = ring; prop.ZSource = prov; }
        else
        {
            prop.Ring = piece.Ring.Select(p => new ZonePoint(p.X, p.Y, double.NaN)).ToList();
            prop.ZSource = "";
            prop.Selected = false;
            prop.Issues.Add("⚠ 解不出高程 —— **不入库**。写 0 会被下游当成实测高程，层体整体摆到 0 米。");
        }

        if (!piece.Trustworthy)
            prop.Issues.Add($"⚠ 描边不可信：环面积 {piece.RingAreaM2 / 1e4:0.##} 万m² 与栅格面积 "
                          + $"{piece.CellAreaM2 / 1e4:0.##} 万m² 差 "
                          + $"{Math.Abs(piece.RingAreaM2 - piece.CellAreaM2) / Math.Max(1, piece.CellAreaM2) * 100:0}%"
                          + " —— 本块有一格宽的细颈或孔洞，轮廓只能看态势。把格距调小再生成一次。");

        prop.Note = ComposeNote(res.Period, prop, union, opt);
        res.Proposals.Add(prop);
    }

    /// <summary>P11：记账。</summary>
    private static string ComposeNote(string period, ProcessZoneProposal p,
                                      ZoneRaster.UnionResult union, ProcessZoneOptions opt)
    {
        var ids = p.Blocks.Select(b => b.UnitId).Distinct().ToList();
        string list = ids.Count == 0 ? "（无）"
                    : string.Join("、", ids.Take(12)) + (ids.Count > 12 ? $" 等 {ids.Count} 个" : "");
        int real = p.Blocks.Count(b => b.RealRail);
        return $"自动生成 {DateTime.Now:yyyy-MM-dd HH:mm}｜期次 {period}｜工序 {p.ProcessZh}"
             + (p.PieceCount > 1 ? $"｜本组第 {p.PieceIndex}/{p.PieceCount} 块（不相连）" : "")
             + $"｜分组 {p.GroupKey}"
             + $"｜{p.Blocks.Count} 个单元（真轨 {real} · 盒子 {p.Blocks.Count - real}）"
             + $"｜{p.AreaM2 / 1e4:0.##} 万m²"
             + (p.VolumeM3.HasValue ? $"、{p.VolumeM3.Value / 1e4:0.##} 万m³（{p.Basis}）" : "、无量口径")
             + $"｜格距 {union.CellM:0.##}m（轮廓精度即此值）"
             + (p.LeadDays.HasValue ? $"｜穿爆超前 {p.LeadDays.Value:0.#} 工日" : "")
             + (p.GuardRadiusM.HasValue ? $"｜警戒半径 {p.GuardRadiusM.Value:0.#}m" : "")
             + (p.BandWidthM.HasValue ? $"｜带宽 {p.BandWidthM.Value:0.#}m" : "")
             + (p.ClippedAtBorder ? "｜⚠ 膨胀顶到格网边界，本区被截断" : "")
             + $"｜Z：{(p.ZSource.Length > 0 ? p.ZSource : "解不出（拒绝入库）")}"
             + $"｜单元：{list}";
    }

    // ══════════════════════════════════════════════════════════════
    //  小件
    // ══════════════════════════════════════════════════════════════

    private static double ResolveWorkdays(string month, ProcessZoneOptions opt, ProcessZonePlanResult res)
    {
        if (opt.MonthWorkdays > 0) return opt.MonthWorkdays;
        try
        {
            if (DateTime.TryParseExact((month ?? "").Trim() + "-01", "yyyy-MM-dd",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                var info = WorkCalendar.MonthWorkdays(d);
                if (info.Workdays > 0) return info.Workdays;
            }
        }
        catch { }
        res.Notes.Add($"· 月作业日取不到，穿孔窗口按兜底 {WorkCalendar.FallbackMonthWorkdays:0} 天摊 —— "
                    + "超前期换算成量的那一步依赖它，这个数不对，穿孔区的位置就整体偏。");
        return WorkCalendar.FallbackMonthWorkdays;
    }

    private static void Summarize(ProcessZonePlanResult res, int groupCount, ProcessZoneOptions opt)
    {
        var byProc = ProcessZone.AllProcesses
            .Select(p => (Proc: p, N: res.Of(p).Count()))
            .Where(x => x.N > 0)
            .Select(x => $"{ProcessZone.DisplayName(x.Proc)} {x.N}")
            .ToList();
        int add = res.Proposals.Count(p => p.Existing == null);
        res.Header = $"{res.Scope.Header}　→　{groupCount} 组 / {res.Proposals.Count} 块工序区"
                   + (byProc.Count > 0 ? $"（{string.Join(" · ", byProc)}）" : "")
                   + $"　新增 {add} · 改边界 {res.Proposals.Count - add}";

        res.Notes.Add($"· 参数：穿爆超前 {opt.BlastLeadDays:0.#} 工日"
                    + (opt.FaceLeadDays.Count > 0 ? $"（{opt.FaceLeadDays.Count} 个面另有覆盖）" : "")
                    + $"　警戒半径 {opt.GuardRadiusM:0.#}m　卸载带宽 {opt.TipBandWidthM:0.#}m"
                    + $"　格距 {(opt.CellM > 0 ? opt.CellM.ToString("0.##") + "m" : "自适应")}。"
                    + "**这三个数都不在台账里** —— 改了要重新生成一次，库里的 note 记的是生成当时的值。");
        res.Notes.Add("· **运输没有面状区域**（P9）：它是线状的（装车点 → 路径 → 卸载点），"
                    + "真正需要地的是两个端点 —— 那是装卸点台账的事，硬圈成面只会得到一块没人用的地。");

        var noZ = res.Proposals.Count(p => p.ZSource.Length == 0);
        if (noZ > 0)
            res.Notes.Add($"◆ {noZ} 块工序区**解不出高程，不能入库**。写 0 会被下游当成实测高程。");
    }

    private static string NextMonth(string month)
    {
        if (DateTime.TryParseExact((month ?? "").Trim() + "-01", "yyyy-MM-dd",
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return "";
    }

    private static bool Eq(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
