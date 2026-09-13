// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/CoalSinkAdapter.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;                  // EquipmentDataContext（装卸点台账；未就绪时静默降级）
using PitMine3D.Kylin.Data.Entities;         // LoadUnloadPoint
using PitMine3D.Kylin.Cad.Units;             // CoalSink / MineUnit
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  load_unload_point → UnitPlanEngine.CoalSinks 的适配。
//
//  为什么要有这一层：排土那边有 DumpSlotAdapter 把排土条带转成 DumpSlot，煤这边一直没有对应物。
//  没有出矿点，煤流就落不上运距 —— 而车队能力（FleetCapTKm）是**煤岩共用**一个数，
//  于是"运输功"这道闸只卡住了岩的那一半，煤那一半白送。
//
//  ★ 两种情况必须在文案上分得开（本文件存在的主要理由）：
//    ① 【真点】装卸点台账里录了破碎站/原煤仓/储煤场 —— 运距按真实坐标算。
//    ② 【降级】台账空 / 全被拒收 —— 退回"采场质心"这个**引擎默认位置**。
//       降级不是"差一点"，是**性质不同**：质心在煤单元的正中间，各单元到它的距离
//       是围绕 0 的散布，算出来的煤运输功系统性偏小、且没有任何工程含义。
//       它只用来保证"煤也进了运输功这本账"，绝不能当成运距估计拿去比选方案。
//
//  拒收 (0,0,0)：load_unload_point.x/y/z 是 NOT NULL DEFAULT 0，"没录坐标"和"录在原点"
//  在库里长得一模一样。把 0 当真点会被 NearestNode 吸附到离原点最近的路网节点上，
//  算出一个**看着像真的假运距** —— 这正是 V036 注释里已经踩过的坑，此处照同一口径挡掉。
//
//  取数走 EquipmentDataContext（与 PlanDestinationCatalog 同一入口），全程 try/catch：
//  GeoDataBase 没就绪不许让排产崩，降级并留条即可。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>出矿点的来源 —— 决定结果能不能当运距用。</summary>
public enum CoalSinkSource
{
    /// <summary>表里没有煤单元，本月压根不需要出矿点。</summary>
    NoCoal = 0,

    /// <summary>★ 降级：装卸点台账里没有可用的出矿点，用采场质心兜了一个默认位置。</summary>
    PitCentroid = 1,

    /// <summary>真点：来自 load_unload_point 的真实坐标。</summary>
    Ledger = 2,
}

/// <summary>
/// 一次取数的结果 —— 除了 <see cref="Sinks"/>，把"哪来的、拒收了几个、为什么"一并带出来。
/// <para>只有 <see cref="UsesRealPoints"/> 为真时，煤的运距才是有工程含义的数。</para>
/// </summary>
public sealed class CoalSinkResolution
{
    /// <summary>喂给 <c>UnitPlanInput.CoalSinks</c> 的出矿点。</summary>
    public List<CoalSink> Sinks { get; } = new();

    public CoalSinkSource Source { get; internal set; } = CoalSinkSource.NoCoal;

    /// <summary>面向用户的说明（每一条都是引擎/适配替用户做的决定）。</summary>
    public List<string> Notes { get; } = new();

    /// <summary>被拒收的行 + 原因（逐行，能指到具体是哪个点）。</summary>
    public List<string> Rejected { get; } = new();

    /// <summary>台账总行数 / 卸载点行数 / 子类允许收煤的行数 / 因坐标缺失被拒收的行数。</summary>
    public int LedgerRows { get; internal set; }
    public int UnloadingRows { get; internal set; }
    public int CoalCapableRows { get; internal set; }
    public int RejectedNoCoord { get; internal set; }

    /// <summary>台账读取本身失败时的原因（null = 没失败）。</summary>
    public string? LedgerError { get; internal set; }

    /// <summary>★ 煤的运距是否落在真实点位上。false ⇒ 结果只够"让煤进账"，不够比选。</summary>
    public bool UsesRealPoints => Source == CoalSinkSource.Ledger;

    /// <summary>一句话的来源文案 —— 界面上必须显示它，把真点和降级分开。</summary>
    public string SourceText => Source switch
    {
        CoalSinkSource.Ledger =>
            $"出矿点取自【装卸点台账】真实点位 {Sinks.Count} 个（load_unload_point）",
        CoalSinkSource.PitCentroid =>
            "出矿点用的是【引擎默认位置·采场质心】—— 装卸点台账里没有可用的出矿点。"
          + "这个位置没有工程含义，煤运距会系统性偏小，只够让煤进运输功这本账，不能拿去比选方案。",
        _ => "本月没有煤单元，不需要出矿点。",
    };

    /// <summary>来源 + 拒收 + 全部留条，拼成界面状态栏能直接贴的一段。</summary>
    public string Describe()
    {
        var sb = new List<string> { SourceText };
        if (LedgerError != null) sb.Add($"◆ 装卸点台账读取失败：{LedgerError}");
        sb.AddRange(Notes.Select(n => "· " + n));
        sb.AddRange(Rejected.Select(n => "⚠ " + n));
        return string.Join("\n", sb);
    }
}

/// <summary>
/// 装卸点台账 → 出矿点。<see cref="Resolve"/> 读库；<see cref="ResolveFrom"/> 是同一套判定的
/// **纯函数版**（不碰数据库），判据与将来的测试都打在它上面。
/// </summary>
public static class CoalSinkAdapter
{
    /// <summary>出矿点码前缀。与 <c>PlanDestinationCatalog</c> 的 <c>LU-{id}</c> 对齐（PlanLib 侧口径）。</summary>
    public const string CodePrefix = "LU-";

    /// <summary>
    /// 坐标"等于 0"的判据阈值（m）。矿区平面坐标量级在 1e5~1e6，任何真实点都不可能落在这个邻域里，
    /// 所以它只会命中"没录"这一种情况。
    /// </summary>
    public const double CoordEps = 1e-6;

    /// <summary>
    /// 坐标可用判据。<b>只看 X/Y 两个零</b>，不要求 Z 非零 ——
    /// 决定运距的是平面位置；而"三个都是 0 才算没录"会放过 (0, 0, 1185) 这种只补了标高的半成品行，
    /// 那种行同样会被吸附到原点附近，算出假运距。
    /// </summary>
    public static bool HasUsableCoord(double x, double y, double z)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return false;
        if (double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z)) return false;
        return Math.Abs(x) > CoordEps || Math.Abs(y) > CoordEps;
    }

    /// <summary>
    /// 卸载点 → 去向类型。<b>名字优先，其次 unload_sub 码</b>，都认不出交给 <see cref="PlanSinkKinds.FromText"/>。
    /// <para>名字优先是有由头的：RoadLib「装卸点设置」的子类下拉<b>没有原煤仓</b>，
    /// 现场把点命名成"原煤仓"、子类却只能落 <c>dump</c>。码优先会把它判成排土场 ——
    /// 煤进不了排土场，这个点就从出矿点里凭空消失了。此处与 TaskLib.SinkRegistryLoader.UnloadKindOf 同序。</para>
    /// </summary>
    public static PlanSinkKind KindOf(LoadUnloadPoint p)
    {
        string name = (p.Name ?? "").Trim();
        string sub = (p.UnloadSub ?? "").Trim();

        // ① 原煤仓：名字里含「煤仓」/「silo」的，不管子类填的什么都按原煤仓算。
        if (name.Contains("煤仓", StringComparison.Ordinal)
            || name.Contains("silo", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.Silo;
        if (sub.Equals("silo", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.Silo;

        // ② 破碎站
        if (sub.Equals("crusher", StringComparison.OrdinalIgnoreCase)
            || name.Contains("破碎", StringComparison.Ordinal)) return PlanSinkKind.Crusher;

        // ③ 储煤场 / 堆场（配矿缓冲、低质煤暂存）
        if (sub.Equals("stockpile", StringComparison.OrdinalIgnoreCase)
            || name.Contains("堆场", StringComparison.Ordinal)
            || name.Contains("储煤", StringComparison.Ordinal)) return PlanSinkKind.Stockpile;

        // ④ sub=='dump' 或没填 —— 按名字判内排/表土/外排。
        return PlanSinkKinds.FromText(name);
    }

    /// <summary>
    /// 该去向类型能不能收煤。<b>判据来自物料目录</b>（<c>PlanMaterialCatalog</c> 里煤的 AllowedSinks
    /// = 破碎站/原煤仓/储煤场），不在这里另写一份 —— 两份"煤能去哪"迟早漂。
    /// </summary>
    public static bool AcceptsCoal(PlanSinkKind kind)
        => PlanMaterialCatalog.Resolve(PlanMaterialCatalog.Coal).Accepts(kind);

    /// <summary>
    /// 读装卸点台账，装配出矿点。台账没有可用真点则退回采场质心（并在结果里说清）。
    /// </summary>
    /// <param name="units">采掘单元（煤 + 岩混在一起，内部自己筛煤）。</param>
    /// <param name="monthlyOperatingHours">
    /// 月作业小时。用于把 throughput_tph（t/h）折成 CoalSink.CapacityT（t/月）。
    /// <b>默认 0 = 不折算、按"通过能力不限"处理</b> —— 作业小时是现场工作历的量，
    /// 这里没有它，编一个（比如按 720 满月）会凭空造出一道能力闸。
    /// </param>
    public static CoalSinkResolution Resolve(IEnumerable<MineUnit>? units, double monthlyOperatingHours = 0)
    {
        List<LoadUnloadPoint> rows = new();
        string? err = null;
        try
        {
            foreach (var p in EquipmentDataContext.LoadUnloadPoints.All())
                if (p != null) rows.Add(p);
        }
        catch (Exception ex)
        {
            // GeoDataBase 没就绪 / 表不存在 —— 降级，不许让排产崩。
            err = ex.Message;
            rows.Clear();
        }

        var r = ResolveFrom(rows, units, monthlyOperatingHours);
        r.LedgerError = err;
        return r;
    }

    /// <summary>
    /// <see cref="Resolve"/> 的纯函数版：给定台账行，装配出矿点。不碰数据库，判据打在这里。
    /// </summary>
    public static CoalSinkResolution ResolveFrom(
        IReadOnlyList<LoadUnloadPoint> rows,
        IEnumerable<MineUnit>? units,
        double monthlyOperatingHours = 0)
    {
        var r = new CoalSinkResolution();
        rows ??= Array.Empty<LoadUnloadPoint>();
        r.LedgerRows = rows.Count;

        var coal = (units ?? Enumerable.Empty<MineUnit>()).Where(u => u != null && u.IsCoal).ToList();
        if (coal.Count == 0)
        {
            r.Source = CoalSinkSource.NoCoal;
            r.Notes.Add("表里没有煤单元 —— 不装配出矿点（煤流为空，不影响岩的运输功）。");
            return r;
        }

        // ── ① 台账里挑出能收煤的真点 ──
        bool hoursGiven = monthlyOperatingHours > 0 && !double.IsNaN(monthlyOperatingHours)
                                                   && !double.IsInfinity(monthlyOperatingHours);
        int tphSeenButNoHours = 0;

        foreach (var p in rows)
        {
            if (p == null) continue;

            // kind 取值域是 loading | unloading（V017 + 全部读取方一致）。源点不是出矿点。
            if (!string.Equals((p.Kind ?? "").Trim(), "unloading", StringComparison.OrdinalIgnoreCase))
                continue;
            r.UnloadingRows++;

            var kind = KindOf(p);
            if (!AcceptsCoal(kind)) continue;      // 排土场/表土堆场不收煤 —— 不是拒收，是本来就不该来
            r.CoalCapableRows++;

            string label = string.IsNullOrWhiteSpace(p.Name) ? $"卸载点{p.Id}" : p.Name.Trim();

            // ★ 坐标缺失一律拒收：不能拿 (0,0,0) 当真点，否则会算出看着像真的假运距。
            if (!HasUsableCoord(p.X, p.Y, p.Z))
            {
                r.RejectedNoCoord++;
                r.Rejected.Add($"「{label}」（{kind.Label()}）没有坐标（x/y 都是 0），已拒收 —— "
                             + "请在「出矿点录入」里拾取位置后再排产。");
                continue;
            }

            double capT = 0;
            if (p.ThroughputTph > 0)
            {
                if (hoursGiven) capT = p.ThroughputTph * monthlyOperatingHours;
                else tphSeenButNoHours++;
            }

            r.Sinks.Add(new CoalSink
            {
                Name = label,
                Code = CodePrefix + p.Id,
                Cx = p.X, Cy = p.Y, Cz = p.Z,
                CapacityT = capT,
                // HaulKm 有意留 0：它是"没坐标时"的静态兜底，而这里的点全都有真坐标，
                // 运距该由 UnitPlanInput.Haul（真路网）或引擎的几何兜底算，不该再塞一个手填数进去。
                HaulKm = 0,
            });
        }

        // ── ② 有真点就用真点 ──
        if (r.Sinks.Count > 0)
        {
            r.Source = CoalSinkSource.Ledger;
            r.Notes.Add($"装卸点台账 {r.LedgerRows} 行：卸载点 {r.UnloadingRows} 个，"
                      + $"其中能收煤的 {r.CoalCapableRows} 个，坐标可用的 {r.Sinks.Count} 个已作出矿点。");
            if (tphSeenButNoHours > 0)
                r.Notes.Add($"有 {tphSeenButNoHours} 个点录了通过能力(t/h)，但没有给月作业小时，"
                          + "本次按【通过能力不限】处理（没折算，不是折成 0）。");
            if (hoursGiven)
                r.Notes.Add($"月通过能力按 t/h × {monthlyOperatingHours:0.#} h/月 折算。");
            return r;
        }

        // ── ③ 降级：采场质心 ──
        r.Source = CoalSinkSource.PitCentroid;
        r.Sinks.Add(new CoalSink
        {
            Name = "破碎站（默认位置）",
            Code = "CR-DEFAULT",
            Cx = coal.Average(u => u.Cx),
            Cy = coal.Average(u => u.Cy),
            Cz = coal.Average(u => u.Cz),
            CapacityT = 0,
            HaulKm = 0,
        });

        if (r.LedgerRows == 0)
            r.Notes.Add("装卸点台账（load_unload_point）是空表。");
        else if (r.CoalCapableRows == 0)
            r.Notes.Add($"装卸点台账 {r.LedgerRows} 行里没有能收煤的卸载点"
                      + $"（卸载点 {r.UnloadingRows} 个，子类都不是破碎站/原煤仓/储煤场）。");
        else
            r.Notes.Add($"能收煤的卸载点有 {r.CoalCapableRows} 个，但坐标全都没录，已全部拒收。");

        r.Notes.Add("要用真实运距，请在「出矿点录入」里录破碎站/原煤仓/储煤场并拾取坐标。");
        return r;
    }

    /// <summary>自动填的煤卸点，来源标记以它开头（人手指的不会有这个前缀）。</summary>
    public const string AutoTag = "装卸点台账";

    /// <summary>
    /// 要不要把台账解出来的出矿点写进**共享的煤卸点**（`CoalSinkPoint`，三维模拟读的就是它）。
    ///
    /// <para><b>补的是一处两边不一致</b>：排产侧走「装卸点台账 → 采场质心」两级兜底，
    /// 而模拟侧只认那个共享点 ⇒ 台账里明明有破碎站，模拟里煤流却一条都不画
    /// （真台账实测：191 笔全是岩流）。</para>
    ///
    /// <para><b>两条硬规矩</b>：
    /// ① <b>只在台账真点时写</b>（<see cref="CoalSinkResolution.UsesRealPoints"/>）——
    ///    采场质心那一档绝不写：它在煤单元正中间，拿它画运输线是画一条不存在的路；
    /// ② <b>绝不覆盖人在图上指过的点</b>，只在"没有"或"上次也是自动填的"时才写。</para>
    ///
    /// <para>抽成纯函数是有意的：这条规则要能<b>脱 GUI 判</b>——
    /// 留在窗口后台里，"会不会覆盖人指的点"就只能靠点界面试。</para>
    /// </summary>
    public static bool TryAutoFill(CoalSinkResolution? res, PitMine3D.Kylin.UnitLedger.CoalSinkPoint? existing,
                                   out PitMine3D.Kylin.UnitLedger.CoalSinkPoint? auto, out string why)
        => TryAutoFill(res?.UsesRealPoints == true, res?.Sinks, existing, out auto, out why);

    /// <summary>
    /// 同上，但<b>只吃规则真正需要的两个量</b>：是不是台账真点、点在哪。
    /// <para><b>为什么要有这个重载</b>：<see cref="CoalSinkResolution.Source"/> 是 <c>internal set</c>，
    /// 判据造不出"降级"那一档的算例。为了判据去开 <c>InternalsVisibleTo</c> 是本末倒置 ——
    /// 一条规则该依赖的是<b>它用到的量</b>，不是那个装它的壳。</para>
    /// </summary>
    public static bool TryAutoFill(bool usesRealPoints, IReadOnlyList<CoalSink>? sinks,
                                   PitMine3D.Kylin.UnitLedger.CoalSinkPoint? existing,
                                   out PitMine3D.Kylin.UnitLedger.CoalSinkPoint? auto, out string why)
    {
        auto = null;
        if (!usesRealPoints || sinks == null || sinks.Count == 0)
        { why = "台账里没有可用的真出矿点（或已降级到采场质心）—— 不写共享煤卸点。"; return false; }

        if (existing is { IsPicked: true } && !existing.SourceNote.StartsWith(AutoTag, StringComparison.Ordinal))
        { why = $"已有【图上指定】的煤卸点「{existing.Name}」—— 人手指的优先，不覆盖。"; return false; }

        var s0 = sinks[0];
        auto = new PitMine3D.Kylin.UnitLedger.CoalSinkPoint
        {
            X = s0.Cx, Y = s0.Cy, Z = s0.Cz,
            Name = s0.Name.Length > 0 ? s0.Name : "出矿点",
            PickedAt = DateTime.Now,
            SourceNote = $"{AutoTag}（自动取，非图上指定）",
        };
        why = $"煤卸点按【装卸点台账】自动填为「{auto.Name}」({auto.X:0}, {auto.Y:0}) —— "
            + "三维模拟的煤运输线用的就是这个点。要改用别的位置，点「指煤卸点…」在图上指一次（手指的不会被覆盖）。";
        return true;
    }
}
