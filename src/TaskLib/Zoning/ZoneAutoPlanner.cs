// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneAutoPlanner.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;            // LedgerKind

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  作业区域自动规划 —— 本期块段 → 候选作业区域。
//
//  ── 已定的口径（Z 组规则，接着 ZonePlanSource 的 Z1–Z4 往下）──
//  **Z5 并集走栅格**：一组块段的占地并集 = 栅格化 + 连通块 + Moore 描边 + DP 抽稀
//      （<see cref="ZoneRaster"/>），**不做多边形布尔**。轮廓精度 = 格距，格距要报出来。
//  **Z6 不连通就分块出，且只默认选最大那一块**：
//      一个作业面本期的块段分成 N 片不相连的地时，**不求它们的外包轮廓**
//      （那会把中间根本不作业的地一起圈进来）。分成 N 个候选，名字 `面名` / `面名-2` …
//      （`SimPlanScene.NameHit` 是互相包含判据，带后缀照样对得上源）。
//      但**默认只勾最大的一块** —— 两块同名区域都进推演的话，
//      推演会让**每一块各吃下该面的全部量**，总量凭空翻倍。要两块都要，人自己勾并知道这件事。
//  **Z7 高程取自采矿模型，不取地表**：环的 Z = 本块所含单元**坡顶线的最小二乘平面**在该点的值，
//      并夹在源点 Z 的 [min,max] 内（长条带外推会飞）。出处写进 note。
//      模型连 Z 都没有才退现状面；两条都不成立**拒绝入库**（写 0 会被推演当成台账实测高程）。
//  **Z8 类别由块段类型定**：煤/岩 → 采场 `pit`；排土 → 名字含「内排」→ `internal_dump`，否则 `external_dump`。
//  **Z9 入库按名字 upsert**：同名区域**只换几何**（id / 选定 / 类别 / 别处的引用全保住）；
//      没有同名的才新增。**手工圈的区域不会被动到**，除非它正好同名。
//  **Z10 生成不自动入库**：先出候选画在图上，人勾了才写。一键刷掉台账是不可接受的默认行为。
//  **Z11 note 记账**：期次 + 来源（真轨/盒子各几块）+ 单元清单 + 格距 + Z 出处，全写进 note 列。
//      三个月后有人问"这条边界哪来的"，答案得在库里，不能只在当时那个窗口的状态栏上。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一块候选作业区域。</summary>
public sealed class ZoneProposal
{
    /// <summary>区域名（= 作业面名 / 采场名；分块时带 -2、-3 后缀）。</summary>
    public string Name = "";
    /// <summary>分组名（不带分块后缀）。</summary>
    public string GroupName = "";
    public string Category = "mineable";
    /// <summary>分组键是作业面（false = 按台账的采场/排土场名兜底，Z3）。</summary>
    public bool ByFace;

    public List<ZonePoint> Ring = new();
    /// <summary>Z 出处（写进 note 第一行）。</summary>
    public string Provenance = "";
    /// <summary>其余记账（期次/来源/单元清单/格距），写进 note 后续行。</summary>
    public string Note = "";

    /// <summary>本块覆盖的块段。</summary>
    public List<PlanBlock> Blocks = new();

    /// <summary>栅格面积（真实占地，不受描边失真影响）。</summary>
    public double AreaM2;
    /// <summary>描边环的鞋带面积（与上面差得多 = 描边不可信）。</summary>
    public double RingAreaM2;
    public double CellM;

    /// <summary>连通块序（1 = 最大的一块）与本组共几块。</summary>
    public int PieceIndex = 1, PieceCount = 1;

    /// <summary>默认勾不勾（Z6：只有最大那一块默认勾）。</summary>
    public bool Selected;

    /// <summary>同名的既有区域 —— 入库时改它的几何，不新增（Z9）。</summary>
    public ZoneRecord? Existing;

    /// <summary>这一块要人知道的事（描边不可信 / 类别与既有不一致 / 碎块 …）。</summary>
    public List<string> Issues = new();

    public double VolumeM3 => Blocks.Sum(b => b.VolumeM3);
    public bool IsDump => Category is "external_dump" or "internal_dump";
    public string ActionText => Existing == null ? "新增" : "改既有区域的边界";

    public string Caption =>
        $"{Name}（{ZoneStore.CategoryZh(Category)}）　{AreaM2 / 1e4:0.##} 万m²　"
      + $"{Blocks.Count} 个单元 / {VolumeM3 / 1e4:0.##} 万m³　{ActionText}";
}

/// <summary>一次自动规划的结果。</summary>
public sealed class ZonePlanResult
{
    public string Month = "";
    public List<ZoneProposal> Proposals = new();
    public List<string> Notes = new();
    public string Header = "";
    public PlanScope Scope = new();

    public bool Ok => Proposals.Count > 0;
    public int SelectedCount => Proposals.Count(p => p.Selected);
}

/// <summary>本期块段 → 候选作业区域。纯计算，不碰数据库（入库走 <see cref="Apply"/>）。</summary>
public static class ZoneAutoPlanner
{
    /// <summary>碎块阈值：占本组不到这个比例的连通块，一律默认不勾并单独说明。</summary>
    private const double SliverShare = 0.03;

    /// <summary>
    /// 规划一期。
    /// </summary>
    /// <param name="month">期次（yyyy-MM）。</param>
    /// <param name="existing">台账里已有的区域（用来判"改既有"还是"新增"）。</param>
    /// <param name="cellM">格距；&lt;=0 = 自适应。</param>
    /// <param name="ledgerRoot">台账根目录；null = 默认。判据用它指到临时目录。</param>
    public static ZonePlanResult Plan(string month, IReadOnlyList<ZoneRecord>? existing, double cellM = 0,
                                      string? ledgerRoot = null)
        => Plan(ZonePlanSource.Load(month, ledgerRoot), existing, cellM);

    /// <summary>规划（块段已经装好时用这一版，便于离线判据喂合成算例）。</summary>
    public static ZonePlanResult Plan(PlanScope scope, IReadOnlyList<ZoneRecord>? existing, double cellM = 0)
    {
        var res = new ZonePlanResult { Month = scope.Month, Scope = scope };
        res.Notes.AddRange(scope.Notes);

        if (!scope.Ok)
        {
            res.Header = scope.Header;
            return res;
        }

        // ── 分组：作业面优先（Z2），没有面就按采场/排土场名兜底（Z3）──
        var groups = scope.Blocks
            .GroupBy(b => (Name: GroupNameOf(b), b.IsDump))
            .OrderByDescending(g => g.Sum(b => b.VolumeM3))
            .ToList();

        int byFace = 0, byRegion = 0;
        foreach (var g in groups)
        {
            bool fromFace = g.Any(b => b.FaceZone.Length > 0);
            if (fromFace) byFace++; else byRegion++;
            BuildGroup(res, g.Key.Name, fromFace, g.ToList(), existing, cellM);
        }

        int newCount = res.Proposals.Count(p => p.Existing == null);
        res.Header = $"{scope.Header}　→　{groups.Count} 组 / {res.Proposals.Count} 块候选"
                   + $"（新增 {newCount} · 改既有 {res.Proposals.Count - newCount}）";

        if (byRegion > 0)
            res.Notes.Add($"◆ {byRegion}/{groups.Count} 组是**按「采场/排土场 + 台阶」兜底**分的："
                        + "这些单元没有任何作业面绑着它们（作业面台账的「单元号」列是空的，或号对不上）。"
                        + "带上台阶是因为一个作业面同一时间只在一个台阶上作业 —— 只按采场并的话，"
                        + "几十个台阶的地会叠成一组，而它们在平面上本就互不相接。"
                        + "兜底出来的区域名对不对得上推演的源/汇要另看 —— 下面每一行的「推演」列会当场判。");

        // ── 碎成一堆 = 有根因，不是"这块地本来就碎" ──
        var shattered = res.Proposals.Where(p => p.PieceCount >= 6)
                                     .Select(p => p.GroupName).Distinct().ToList();
        if (shattered.Count > 0)
        {
            int worst = res.Proposals.Max(p => p.PieceCount);
            res.Notes.Add($"◆ 有 {shattered.Count} 组被切成 6 块以上不相连的地（最多一组 {worst} 块）—— "
                        + "**这基本不是地真的碎**，先查两条：① 这些单元有没有真轨（没有真轨时占地按"
                        + "「质心 + 长宽」摆，本来沿走向搭边的相邻幅会断开）；② 是不是整个采场被并成了一组"
                        + "（作业面没绑单元号时会这样）。两条都不成立，才考虑把格距调大让相邻块连上。");
        }

        int noAz = scope.Blocks.Count(b => !b.RealRail && !b.HasAzimuth);
        if (noAz > 0)
            res.Notes.Add($"◆ {noAz} 块占地**既没有真轨、台账里也没有走向方位角** ⇒ 盒子一律长轴朝东，"
                        + "**朝向不代表采掘带走向**，相邻幅会因此错位、断开。"
                        + "补法：跑一次「采矿模型」，再到「采掘单元清单」里「从采矿模型取」一次 —— "
                        + $"那一步会把真轨落到台账目录（{PitMine3D.Kylin.UnitLedger.UnitRailFile.FileName}），台账也会带上方位角。");
        if (byFace > 0)
            res.Notes.Add($"· {byFace} 组按**作业面**分，区域名直接取作业面名 —— 推演按名字配源，天然对得上。");

        // 记账：候选覆盖的量必须等于本期块段的总量，否则有块段被漏掉了
        double planned = scope.Blocks.Sum(b => b.VolumeM3);
        double covered = res.Proposals.Sum(p => p.VolumeM3);
        if (Math.Abs(planned - covered) > Math.Max(1.0, planned * 1e-6))
            res.Notes.Add($"◆ 候选覆盖 {covered / 1e4:0.##} 万m³，本期块段合计 {planned / 1e4:0.##} 万m³ —— "
                        + "差额说明有块段没有落进任何候选，请把这条报给开发（这是记账漏洞，不是现场问题）。");

        double unselected = res.Proposals.Where(p => !p.Selected).Sum(p => p.AreaM2);
        if (unselected > 0)
            res.Notes.Add($"· 默认没勾的候选合计 {unselected / 1e4:0.##} 万m² —— "
                        + "它们是同一个面的**不相连的次要块段**（Z6：两块同名区域都进推演，"
                        + "推演会让每一块各吃下该面的全部量）。确实要就自己勾上。");
        return res;
    }

    /// <summary>
    /// 分组名。作业面优先（Z2）；没有面绑时按 <b>采场/排土场 + 台阶</b> 兜底（Z3）。
    ///
    /// <para><b>为什么兜底要带上台阶</b>：真台账里 <c>Region</c> 一栏全是同一个「采场1」——
    /// 按它整体并，一个采场几十个台阶的地会叠成一组，而各台阶在平面上本就互不相接，
    /// 结果是<b>一组切成 93 块不相连的地</b>（2026-08-11 拿 2026-08 真台账实测）。
    /// 那种清单没法用，而且掩盖了真正的问题。一个作业面同一时间只在一个台阶上作业，
    /// 所以「采场+台阶」才是没有面绑时最接近"一个作业面"的键。</para>
    /// </summary>
    internal static string GroupNameOf(PlanBlock b)
    {
        if (b.FaceZone.Length > 0) return b.FaceZone;
        string region = b.Region.Length > 0 ? b.Region : b.IsDump ? "排土场" : "采场";
        return b.Seam.Length > 0 ? region + "·" + b.Seam : region;
    }

    private static void BuildGroup(ZonePlanResult res, string groupName, bool byFace,
                                   List<PlanBlock> blocks, IReadOnlyList<ZoneRecord>? existing, double cellM)
    {
        var union = ZoneRaster.Union(blocks.Select(b => b.FootprintXy).ToList(), cellM);
        if (!union.Ok)
        {
            res.Notes.Add($"◆「{groupName}」的 {blocks.Count} 个块段并不出轮廓（{union.Note}）—— 本组没有候选。");
            return;
        }

        string category = CategoryOf(blocks, groupName, out string catNote);
        double total = union.TotalAreaM2;

        // 块段归块：质心落在哪一块里就算哪一块的（都不在就算最近的那一块）
        var buckets = new List<List<PlanBlock>>();
        for (int i = 0; i < union.Pieces.Count; i++) buckets.Add(new List<PlanBlock>());
        foreach (var b in blocks)
        {
            var (cx, cy) = Centroid(b.FootprintXy);
            int hit = -1;
            for (int i = 0; i < union.Pieces.Count && hit < 0; i++)
                if (ContainsXy(union.Pieces[i].Ring, cx, cy)) hit = i;
            if (hit < 0) hit = NearestPiece(union.Pieces, cx, cy);
            buckets[hit].Add(b);
        }

        for (int i = 0; i < union.Pieces.Count; i++)
        {
            var piece = union.Pieces[i];
            var mine = buckets[i];
            string name = i == 0 ? groupName : $"{groupName}-{i + 1}";

            var prop = new ZoneProposal
            {
                Name = name,
                GroupName = groupName,
                Category = category,
                ByFace = byFace,
                Blocks = mine,
                AreaM2 = piece.CellAreaM2,
                RingAreaM2 = piece.RingAreaM2,
                CellM = union.CellM,
                PieceIndex = i + 1,
                PieceCount = union.Pieces.Count,
                Selected = i == 0 && piece.CellAreaM2 >= total * SliverShare,
            };

            // Z7：高程取自采矿模型的坡顶线拟合面
            if (!ResolveZ(prop, mine.Count > 0 ? mine : blocks, piece.Ring))
            {
                prop.Selected = false;
                prop.Issues.Add("⚠ 解不出高程 —— **不入库**。写 0 会被推演当成台账实测高程，层体整体摆到 0 米。");
            }

            if (!piece.Trustworthy)
                prop.Issues.Add($"⚠ 描边不可信：环面积 {piece.RingAreaM2 / 1e4:0.##} 万m² 与栅格面积 "
                              + $"{piece.CellAreaM2 / 1e4:0.##} 万m² 差 "
                              + $"{Math.Abs(piece.RingAreaM2 - piece.CellAreaM2) / Math.Max(1, piece.CellAreaM2) * 100:0}%"
                              + " —— 本块有一格宽的细颈或孔洞，轮廓只能看态势。把格距调小再生成一次。");
            if (i > 0)
                prop.Issues.Add($"这是「{groupName}」的第 {i + 1} 块（本组共 {union.Pieces.Count} 块不相连的地）。"
                              + "两块同名区域都进推演时，**每一块各吃下该面的全部量** —— 所以默认不勾。");
            if (piece.CellAreaM2 < total * SliverShare && union.Pieces.Count > 1)
                prop.Issues.Add($"碎块：只占本组 {piece.CellAreaM2 / Math.Max(1, total) * 100:0.#}% 的面积。");
            if (catNote.Length > 0) prop.Issues.Add(catNote);

            // Z9：同名的既有区域改几何，不新增
            prop.Existing = existing?.FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prop.Existing != null && !string.Equals(prop.Existing.Category, category, StringComparison.Ordinal))
                prop.Issues.Add($"既有区域「{name}」的类别是「{ZoneStore.CategoryZh(prop.Existing.Category)}」，"
                              + $"本期块段判出来是「{ZoneStore.CategoryZh(category)}」—— "
                              + "**只换几何不动类别**（类别决定推进极性，改它要人自己确认）。");

            prop.Note = ComposeNote(res.Month, prop, union);
            res.Proposals.Add(prop);
        }
    }

    /// <summary>Z8：类别由块段类型定。</summary>
    private static string CategoryOf(List<PlanBlock> blocks, string groupName, out string note)
    {
        note = "";
        if (!blocks.Any(b => b.IsDump)) return "pit";

        // 排土：先看台账的排土场名，再看分组名。都看不出内外排时**说出来**，不闷头当外排。
        string probe = string.Join(" ", blocks.Select(b => b.Region).Distinct()) + " " + groupName;
        if (probe.Contains("内排", StringComparison.Ordinal)) return "internal_dump";
        if (probe.Contains("外排", StringComparison.Ordinal)) return "external_dump";
        note = $"排土类但名字里既没有「内排」也没有「外排」（{groupName}）—— 暂按**外排土场**，"
             + "内外排的运距与容量口径不同，请在左下「类别」里核一下。";
        return "external_dump";
    }

    /// <summary>
    /// Z7：环的 Z = 块段坡顶线的最小二乘平面，夹在源点 Z 的 [min,max] 内。
    /// <para>夹是必须的：一条 800m 的弯带拟合出来的平面，外推到轮廓拐角处能飞出几十米。</para>
    /// </summary>
    private static bool ResolveZ(ZoneProposal prop, List<PlanBlock> blocks, List<(double X, double Y)> ring)
    {
        bool ok = FitRingZ(blocks, ring, out var outRing, out string prov);
        prop.Ring = outRing; prop.Provenance = prov;
        return ok;
    }

    /// <summary>
    /// Z7 的算法本体，<b>作业区域与工序作业区共用这一份</b>。
    /// <para>两处各写一遍的话，一边改了夹紧口径另一边没改，图上两块地的高程会慢慢分家 ——
    /// 而两边各自都"看着对"。</para>
    /// </summary>
    internal static bool FitRingZ(IReadOnlyList<PlanBlock> blocks, List<(double X, double Y)> ring,
                                  out List<ZonePoint> outRing, out string provenance)
    {
        outRing = new List<ZonePoint>(); provenance = "";
        var pts = new List<double>();
        foreach (var b in blocks)
            for (int i = 0; i + 2 < b.TopXyz.Length; i += 3)
                if (!double.IsNaN(b.TopXyz[i + 2])) { pts.Add(b.TopXyz[i]); pts.Add(b.TopXyz[i + 1]); pts.Add(b.TopXyz[i + 2]); }

        int n = pts.Count / 3;
        if (n == 0)
        {
            // 模型侧一个 Z 都没有 —— 退现状面（与手工圈画同一条路），仍解不出就拒绝
            var xy = ring.Select(p => (p.X, p.Y)).ToList();
            var zr = ZoneElevation.Resolve(xy, null);
            if (!zr.Ok) { provenance = ""; return false; }
            outRing = zr.Ring;
            provenance = "（模型侧无高程）" + zr.Provenance;
            return true;
        }

        double minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double z = pts[i * 3 + 2];
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        FitPlane(pts, n, out double a, out double b2, out double c);
        int clamped = 0;
        outRing = new List<ZonePoint>(ring.Count);
        foreach (var p in ring)
        {
            double z = a * p.X + b2 * p.Y + c;
            if (double.IsNaN(z) || double.IsInfinity(z)) z = (minZ + maxZ) * 0.5;
            if (z < minZ) { z = minZ; clamped++; }
            else if (z > maxZ) { z = maxZ; clamped++; }
            outRing.Add(new ZonePoint(p.X, p.Y, z));
        }

        provenance = $"采矿模型坡顶线拟合平面（{blocks.Count} 个单元 / {n} 点，"
                        + $"{minZ:0.##}~{maxZ:0.##}m"
                        + (clamped > 0 ? $"；{clamped}/{ring.Count} 个轮廓点外推超界已夹回" : "") + "）";
        return true;
    }

    /// <summary>z = a·x + b·y + c 最小二乘；退化则取均值平面。</summary>
    private static void FitPlane(List<double> pts, int n, out double a, out double b, out double c)
    {
        // 平移到质心再解：本矿坐标 X≈62万 / Y≈438万，直接堆 Sxx 会把有效位吃光
        double mx = 0, my = 0, mz = 0;
        for (int i = 0; i < n; i++) { mx += pts[i * 3]; my += pts[i * 3 + 1]; mz += pts[i * 3 + 2]; }
        mx /= n; my /= n; mz /= n;

        double Sxx = 0, Sxy = 0, Syy = 0, Sxz = 0, Syz = 0;
        for (int i = 0; i < n; i++)
        {
            double x = pts[i * 3] - mx, y = pts[i * 3 + 1] - my, z = pts[i * 3 + 2] - mz;
            Sxx += x * x; Sxy += x * y; Syy += y * y; Sxz += x * z; Syz += y * z;
        }
        double det = Sxx * Syy - Sxy * Sxy;
        if (Math.Abs(det) < 1e-9 || n < 3)
        {
            a = 0; b = 0; c = mz;                      // 退化（共线/单点）→ 水平面
            return;
        }
        a = (Sxz * Syy - Syz * Sxy) / det;
        b = (Syz * Sxx - Sxz * Sxy) / det;
        c = mz - a * mx - b * my;
    }

    /// <summary>Z11：记账写进 note。</summary>
    private static string ComposeNote(string month, ZoneProposal p, ZoneRaster.UnionResult union)
    {
        int real = p.Blocks.Count(b => b.RealRail);
        var ids = p.Blocks.Select(b => b.UnitId).Distinct().ToList();
        string list = string.Join("、", ids.Take(12)) + (ids.Count > 12 ? $" 等 {ids.Count} 个" : "");
        return $"自动生成 {DateTime.Now:yyyy-MM-dd HH:mm}｜期次 {month}｜"
             + $"{(p.ByFace ? "按作业面" : "按采场/排土场名兜底")}分组"
             + (p.PieceCount > 1 ? $"｜本组第 {p.PieceIndex}/{p.PieceCount} 块（不相连）" : "")
             + $"｜{p.Blocks.Count} 个单元（真轨 {real} · 盒子 {p.Blocks.Count - real}）"
             + $"｜{p.AreaM2 / 1e4:0.##} 万m²、{p.VolumeM3 / 1e4:0.##} 万m³"
             + $"｜格距 {union.CellM:0.##}m（轮廓精度即此值）"
             + $"｜单元：{list}";
    }

    // ══════════════════════════════════════════════════════════════
    //  入库
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 把勾选的候选写进台账。同名的改几何（Z9），没有同名的新增。
    /// <para><b>逐块独立</b>：一块失败不影响其余，失败原因逐条带回来 ——
    /// "入库了 5 块" 与 "本该入库 7 块" 是两件事，只报前者就是骗人。</para>
    /// </summary>
    public static string Apply(IEnumerable<ZoneProposal>? chosen, out int inserted, out int updated,
                               out List<string> failures)
    {
        inserted = 0; updated = 0;
        failures = new List<string>();
        var list = (chosen ?? Enumerable.Empty<ZoneProposal>()).Where(p => p != null).ToList();
        if (list.Count == 0) return "没有勾选任何候选区域。";

        foreach (var p in list)
        {
            if (p.Ring.Count < 3) { failures.Add($"{p.Name}：只有 {p.Ring.Count} 个顶点"); continue; }
            if (p.Provenance.Length == 0) { failures.Add($"{p.Name}：解不出高程，拒绝入库"); continue; }

            if (p.Existing != null)
            {
                if (ZoneStore.UpdateRing(p.Existing, p.Ring, p.Provenance, p.Note)) updated++;
                else failures.Add($"{p.Name}：{ZoneStore.LastLabel}");
            }
            else
            {
                long id = ZoneStore.Insert(p.Name, p.Category, p.Ring, p.Provenance, p.Note);
                if (id != 0) inserted++;
                else failures.Add($"{p.Name}：{ZoneStore.LastLabel}");
            }
        }

        string msg = $"已入库：新增 {inserted} 块 · 改既有边界 {updated} 块";
        if (failures.Count > 0) msg += $"　◆ {failures.Count} 块没入库：{string.Join("；", failures.Take(3))}"
                                     + (failures.Count > 3 ? " …" : "");
        return msg;
    }

    // ── 小几何件 ─────────────────────────────────────────────────

    internal static (double X, double Y) Centroid(double[] xy)
    {
        int n = xy.Length / 2;
        if (n == 0) return (0, 0);
        double sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { sx += xy[i * 2]; sy += xy[i * 2 + 1]; }
        return (sx / n, sy / n);
    }

    internal static bool ContainsXy(List<(double X, double Y)> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring[i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi + double.Epsilon) + xi)
                inside = !inside;
        }
        return inside;
    }

    internal static int NearestPiece(List<ZoneRaster.Piece> pieces, double x, double y)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < pieces.Count; i++)
        {
            foreach (var p in pieces[i].Ring)
            {
                double d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        return best;
    }
}
