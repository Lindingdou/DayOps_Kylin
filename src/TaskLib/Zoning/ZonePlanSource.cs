// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZonePlanSource.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;            // MiningUnitLedger / MonthlyUnitLedgerStore / UnitRailFile
using PitMine3D.Kylin.TaskLib.Domain;                 // MaterialCatalog / MaterialSpec.NeedsBlasting
using PitMine3D.Kylin.TaskLib.Engine;                  // ExploderConfig / ProductionPlanContext / ProjectScope
using PitMine3D.Kylin.TaskLib.Simulation;              // UnitSolidStage.RailIndex / UnitPrism

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  本期计划的「块段」—— 采矿模型给几何、月度计划给范围。
//
//  ── 这一层回答的问题 ──
//  「本月要开采的是哪几块地」。输入两份，各管各的：
//    · **月度台账**（`期次 = 本期` 的那些行）定**范围**：哪些采掘单元排到了本期；
//    · **采矿模型**（真轨 / 台账几何）定**形状**：每个单元在平面上占哪块地。
//  两份缺一不可 —— 只有台账没有几何 = 知道采多少不知道采哪儿；
//  只有模型没有期次 = 把全矿家底当成本月任务（`UnitPlanLink` 的口径② 记过这笔账）。
//
//  ── 已定的口径（Z 组规则，与 UnitPlanLink / UnitSolidStage 对齐）──
//  **Z1 只认本期**：`Period == 期次` 的行才进。没有期次的行是基表里还没排产的单元。
//  **Z2 分组键 = 作业面**：单元归到"绑了它的作业面"（`FaceInput.UnitId == Row.UnitId`），
//      区域名取 `FaceInput.Zone` —— 推演正是按这个名字配源的（`SimPlanScene.NameHit`）。
//  **Z3 没有面绑的单元不丢**：按台账 `Region`（采场名/排土场名）兜底成一组，并**点名列出**。
//      悄悄并进别人的区、或悄悄丢掉，两种都会让"本期作业范围"少一块而界面上看不出来。
//  **Z4 占地与三维单元体同源**：走 <see cref="UnitPrism.PlanFootprint"/>（前脸轨 + 偏 W 的后界轨）；
//      没有真轨退 `质心 + 长宽 + 走向方位角` 的矩形，并标成 <see cref="PlanBlock.RealRail"/>=false。
//      **方位角没有就是没有**（`AzimuthDeg` 为 null）—— 轴对齐盒子，不拿 0° 冒充正南北。
//
//  ── 量的口径 ──
//  煤取 `CoalM3`、岩取 `NetRockM3`（净岩量）、排土取 `DumpCapM3`（占容方）。
//  **不用 `Row.Qty`**：那是报表主量，煤那一档给的是吨，混进 m³ 的求和就是量纲错。
//  与 `UnitPlanLink.UnitVolumeM3` 同一条，改一处要同时改。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>本期计划里的一个块段（= 一个采掘单元 / 一个排土位置 在平面上占的那块地）。</summary>
public sealed class PlanBlock
{
    public string UnitId = "";
    public LedgerKind Kind;
    /// <summary>采场名 / 排土场名（台账 Region 列）。</summary>
    public string Region = "";
    public string Seam = "";

    /// <summary>绑了本单元的作业面名（`FaceInput.Zone`）。<b>空 = 没有面绑它</b>（走 Z3 兜底）。</summary>
    public string FaceZone = "";
    /// <summary>本单元被几个面同时绑（&gt;1 时占地会进每个面的区域，要报出来）。</summary>
    public int FaceCount;

    /// <summary>平面占地环（扁平 [x,y,...]，隐式闭合）。</summary>
    public double[] FootprintXy = Array.Empty<double>();
    /// <summary>拟合区域高程用的点（真轨的坡顶线；盒子则是角点配 ZHi）。扁平 xyz。</summary>
    public double[] TopXyz = Array.Empty<double>();

    /// <summary>占地来自真轨（false = 台账的长宽盒子，形态为近似）。</summary>
    public bool RealRail;
    /// <summary>
    /// 盒子退路时台账里有没有走向方位角。
    /// <para><b>没有方位角的盒子一律长轴朝东</b> —— 相邻幅会错位断开，并集因此碎成一堆。
    /// 这一条要单独记：它与"没有真轨"是两件事，补法也不同（前者重取一次台账就有）。</para>
    /// </summary>
    public bool HasAzimuth;
    /// <summary>后界轨折回的段数（&gt;0 = 凹弯处占地环自交，栅格并集仍成立，单看这一条环不成立）。</summary>
    public int Folded;

    /// <summary>本单元的量（m³：煤=实方 / 岩=净岩 / 排土=占容方）。</summary>
    public double VolumeM3;
    /// <summary>期初完成度 0~1。</summary>
    public double Done;

    // ── 工序区域要用的三样（见 ProcessZonePlanner）────────────────────────
    //
    // 五道工序的地**不在同一个位置**：穿孔超前、爆破在其后、采装再后、排土在另一头。
    // 沿推进方向错开这件事，靠的就是下面这三样 —— 上面那个 FootprintXy 只够画采装那一块。

    /// <summary>
    /// 台账排产序（<c>MiningUnitLedger.Row.Seq</c>）。<b>穿孔区就是在这条队列上开窗前推的</b>。
    /// <para>0 = 台账没排序。整个面都是 0 时队列退化成"任意顺序"，穿孔区会等于采装区 ——
    /// 那不是"超前期为 0"，是"排不出来"，两者必须分开说。</para>
    /// </summary>
    public int Seq;

    /// <summary>本单元的物料码（从绑着它的作业面取）。空 = 没有面绑它，需爆破按台账类型退定。</summary>
    public string MaterialCode = "";

    /// <summary>
    /// 需不需要先穿孔爆破。<b>false 的面没有穿孔区</b>。
    /// <para>不筛这一条的话，会在免爆面上一本正经地圈出一块钻机作业区 —— 而所有数字都正常。
    /// 「免爆」与「算不出台阶高」的月穿孔延米都是 0，报表上长得一模一样，这里必须分开。</para>
    /// </summary>
    public bool NeedsBlasting;

    /// <summary>本单元是按物料真判出来的需爆破（false = 没有面绑它，按台账类型退定的）。</summary>
    public bool BlastFromMaterial;

    /// <summary>
    /// 真轨：坡顶线 / 坡底线 / 推进宽 / 推进朝向。<b>排土的卸载带与推排带靠它重切</b> ——
    /// 卸载带 = 同一对轨换一个更小的 W，不是把占地环再切一刀。
    /// <para>空 = 这一块是盒子退路，重切走 <see cref="Cx"/>/<see cref="Cy"/> 那一套。</para>
    /// </summary>
    public double[] Crest = Array.Empty<double>();
    public double[] Toe = Array.Empty<double>();
    public double WidthM;
    /// <summary>推进朝坡顶（采场 true / 排土 false）—— 决定后界轨往哪一侧偏。</summary>
    public bool TowardCrest;

    /// <summary>盒子退路重切要用的质心与长宽方位角。</summary>
    public double Cx, Cy, LengthM, ZHi;
    public double? AzimuthDeg;

    public bool IsDump => Kind == LedgerKind.Dump;

    public double RemainM3 => Math.Max(0, VolumeM3 * (1 - Math.Clamp(Done, 0, 1)));

    public string Caption =>
        $"{UnitId}（{MiningUnitLedger.KindToText(Kind)} {Seam}，{VolumeM3 / 1e4:0.##}万m³"
      + (Done > 1e-9 ? $"，已采 {Done * 100:0.#}%" : "") + (RealRail ? "，真轨" : "，盒子") + "）";
}

/// <summary>一次「本期计划块段」的装载结果。永不抛，失败以 <see cref="Notes"/> 表达。</summary>
public sealed class PlanScope
{
    public string Month = "";
    public List<PlanBlock> Blocks = new();
    /// <summary>整批的来源与口径说明（界面直接显示）。</summary>
    public List<string> Notes = new();
    /// <summary>一句话表头。</summary>
    public string Header = "";

    /// <summary>本期台账里带期次的行数（进不进得来另说）。</summary>
    public int RowsInMonth;
    /// <summary>有期次、但算不出占地的单元（几何缺失）—— <b>必须列出来</b>，不静默丢。</summary>
    public List<string> NoGeometry = new();
    /// <summary>真轨来源文案（会话内 / 落盘文件 / 一条都没有）。</summary>
    public string RailLabel = "";
    /// <summary>盘子来源文案（读不出来时说清楚，作业面分组会整体退到 Region 兜底）。</summary>
    public string PlanLabel = "";

    public bool Ok => Blocks.Count > 0;
    public int RealRailCount => Blocks.Count(b => b.RealRail);
}

/// <summary>月度计划 + 采矿模型 → 本期块段。</summary>
public static class ZonePlanSource
{
    /// <summary>台账目录里已有的期次（按名升序）。读不到返回空表。</summary>
    /// <param name="ledgerRoot">台账根目录；null = 默认（桌面「采掘单元台账」）。判据用它指到临时目录。</param>
    public static List<string> Months(string? ledgerRoot = null)
    {
        try { return new MonthlyUnitLedgerStore(ledgerRoot).ListMonths(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 默认期次：工程当前工作日期所在月；那一期没有台账就退到台账里**最新的一期**。
    /// <para>退的时候必须让调用方知道退了 —— 否则人以为在看本月，其实在看三月前那一期。</para>
    /// </summary>
    public static string DefaultMonth(out string note, string? ledgerRoot = null)
    {
        note = "";
        string want;
        try { want = ProjectScope.WorkDate.ToString("yyyy-MM", CultureInfo.InvariantCulture); }
        catch { want = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture); }

        var months = Months(ledgerRoot);
        if (months.Contains(want, StringComparer.OrdinalIgnoreCase)) return want;
        if (months.Count == 0)
        {
            note = $"台账目录里一期都没有 —— 「{want}」还没排产。";
            return want;
        }
        string latest = months[^1];
        note = $"当前工作日期在 {want}，但那一期没有月度台账 —— 已退到最新的一期「{latest}」。";
        return latest;
    }

    /// <summary>
    /// 装载一期的块段。
    /// </summary>
    public static PlanScope Load(string month, string? ledgerRoot = null)
    {
        var scope = new PlanScope { Month = month ?? "" };
        if (string.IsNullOrWhiteSpace(month))
        {
            scope.Header = "没有指定期次。";
            return scope;
        }

        // ── ① 月度台账（范围）──
        List<MiningUnitLedger.Row> rows;
        try
        {
            var store = new MonthlyUnitLedgerStore(ledgerRoot);
            if (!store.Exists(month))
            {
                scope.Header = $"{month} 没有月度台账（{store.MonthPath(month)}）";
                scope.Notes.Add("先在「采掘单元清单」里排一期，再回来生成作业区域。"
                              + "没有期次就没有『本期要开采哪几块』这件事 —— 这里不拿基表全量顶替，"
                              + "那等于把全矿家底当成本月任务。");
                return scope;
            }
            store.TryLoad(month, out rows, out var issues);
            foreach (var s in issues) if (!string.IsNullOrWhiteSpace(s)) scope.Notes.Add(s);
        }
        catch (Exception ex)
        {
            scope.Header = $"{month} 的月度台账读取失败（{Short(ex)}）";
            return scope;
        }

        // Z1：只认本期期次的行
        var inMonth = (rows ?? new List<MiningUnitLedger.Row>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.UnitId)
                     && string.Equals((r.Period ?? "").Trim(), month, StringComparison.OrdinalIgnoreCase))
            .ToList();
        scope.RowsInMonth = inMonth.Count;

        if (inMonth.Count == 0)
        {
            scope.Header = $"{month} 的台账里没有排到本期的单元（共 {rows?.Count ?? 0} 行）";
            scope.Notes.Add("台账有行但都没填「期次」—— 那些是基表里还没排产的单元，不算本期作业范围。");
            return scope;
        }

        // ── ② 作业面绑定（分组键）──
        var faceOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // 单元号 → 物料码。穿孔区要靠它筛掉免爆的面（MaterialSpec.NeedsBlasting）。
        var matOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cfg = ProductionPlanContext.Config();
            foreach (var f in cfg.Faces)
            {
                if (!f.HasUnit) continue;
                string id = f.UnitId.Trim();
                string zone = (f.Zone ?? "").Trim();
                if (zone.Length == 0) continue;
                if (!faceOf.TryGetValue(id, out var list)) faceOf[id] = list = new List<string>();
                if (!list.Contains(zone, StringComparer.OrdinalIgnoreCase)) list.Add(zone);
                string mc = (f.MaterialCode ?? "").Trim();
                if (mc.Length > 0 && !matOf.ContainsKey(id)) matOf[id] = mc;
            }
            scope.PlanLabel = faceOf.Count > 0
                ? $"作业面绑定：{faceOf.Count} 个单元号绑到了面（{ProjectScope.Caption}）"
                : $"作业面绑定：**一个面都没绑单元号**（{ProjectScope.Caption}）—— "
                + "全部块段按台账的采场/排土场名兜底分组（在「作业面台账」填「单元号」后可按面分）";
        }
        catch (Exception ex)
        {
            scope.PlanLabel = $"盘子读不出来（{Short(ex)}）—— 全部块段按台账的采场/排土场名兜底分组";
        }

        // ── ③ 真轨（形状）──
        UnitSolidStage.RailIndex rails;
        // 真轨与台账**必须同一个目录**：台账从 A 读、真轨从 B 找 = 一个单元都对不上号 → 全退盒子，而且不报错
        try { rails = UnitSolidStage.RailIndex.Snapshot(ledgerRoot); }
        catch (Exception ex)
        {
            rails = UnitSolidStage.RailIndex.Empty();
            scope.Notes.Add($"◆ 真轨索引取不到（{Short(ex)}）—— 全部退盒子。");
        }
        scope.RailLabel = rails.Caption;

        // ── ④ 逐行算占地 ──
        int box = 0, folded = 0;
        foreach (var r in inMonth)
        {
            double vol = VolumeM3(r);
            var block = new PlanBlock
            {
                UnitId = r.UnitId.Trim(),
                Kind = r.Kind,
                Region = (r.Region ?? "").Trim(),
                Seam = (r.Seam ?? "").Trim(),
                VolumeM3 = vol,
                Done = Math.Clamp(r.Done, 0, 1),
                Seq = r.Seq,
                Cx = r.Cx, Cy = r.Cy, LengthM = r.LengthM,
                ZHi = r.ZHi > r.ZLo ? r.ZHi : (r.ThickM > 1e-6 ? r.Cz + r.ThickM * 0.5 : r.Cz),
                AzimuthDeg = r.AzimuthDeg,
            };
            if (faceOf.TryGetValue(block.UnitId, out var zones) && zones.Count > 0)
            {
                block.FaceZone = zones[0];
                block.FaceCount = zones.Count;
            }
            ResolveBlasting(block, matOf);

            if (!Footprint(r, rails, block))
            {
                scope.NoGeometry.Add(block.UnitId);
                continue;
            }
            if (!block.RealRail) box++;
            if (block.Folded > 0) folded++;

            // 同一个单元被多个面绑：每个面各得一份占地（复制），并在文案里点名
            scope.Blocks.Add(block);
            if (block.FaceCount > 1)
                for (int k = 1; k < zones!.Count; k++)
                    scope.Blocks.Add(Clone(block, zones[k]));
        }

        int coal = scope.Blocks.Count(b => b.Kind == LedgerKind.Coal);
        int rock = scope.Blocks.Count(b => b.Kind == LedgerKind.Rock);
        int dump = scope.Blocks.Count(b => b.IsDump);
        scope.Header = $"{month}：本期 {scope.RowsInMonth} 个单元 → 算出占地 {scope.Blocks.Count} 块"
                     + $"（煤 {coal} · 岩 {rock} · 排土 {dump}）";

        if (scope.NoGeometry.Count > 0)
            scope.Notes.Add($"◆ 有 {scope.NoGeometry.Count} 个本期单元**算不出占地**，没进作业范围："
                          + string.Join("、", scope.NoGeometry.Take(6))
                          + (scope.NoGeometry.Count > 6 ? $" 等 {scope.NoGeometry.Count} 个" : "")
                          + " —— 既没有真轨，台账里的长/宽也不成立。生成的区域**比本期计划小**，"
                          + "重新跑一次采矿模型并从模型取一次台账即可补上。");
        if (box > 0)
            scope.Notes.Add($"· {box}/{scope.Blocks.Count} 块占地是**盒子近似**（没有这个单元的真轨）："
                          + "面积与朝向按台账的长宽方位角摆，**弯带被拉直了**。轮廓当范围看没问题，别拿它量边界。");
        if (folded > 0)
            scope.Notes.Add($"· {folded} 块占地在凹弯处后界轨折回 —— 单块环自交，"
                          + "但并集走的是栅格填充，范围仍然成立。");
        var multi = scope.Blocks.Where(b => b.FaceCount > 1).Select(b => b.UnitId).Distinct().ToList();
        if (multi.Count > 0)
            scope.Notes.Add($"◆ {multi.Count} 个单元被多个作业面同时绑（{string.Join("、", multi.Take(4))}）—— "
                          + "同一块地会出现在每个面的区域里，空间上重叠。要么改作业面台账只留一个，要么接受重叠。");
        return scope;
    }

    /// <summary>
    /// 一行 → 平面占地。真轨优先，退盒子；两条都不成立返回 false（**不编一个尺寸出来**）。
    /// </summary>
    private static bool Footprint(MiningUnitLedger.Row r, UnitSolidStage.RailIndex rails, PlanBlock block)
    {
        var rail = rails.Find(r);
        if (rail != null)
        {
            var v = rail.Value;
            bool towardCrest = r.Kind != LedgerKind.Dump;   // 采场 d 指坡顶，排土 d 指坡脚
            if (UnitPrism.PlanFootprint(v.Crest, v.Toe, v.WidthM, towardCrest, 0, 1,
                                        out var front, out var rear, out var seg, out _,
                                        out int folded, out _))
            {
                // 真轨留着：排土的卸载带/推排带是**同一对轨换一个更小的 W** 重切出来的，
                // 不是把这个占地环再切一刀（环在凹弯处会自交，切出来的两半都不成立）。
                block.Crest = v.Crest; block.Toe = v.Toe;
                block.WidthM = v.WidthM; block.TowardCrest = towardCrest;
                block.FootprintXy = UnitPrism.PlanRing(front, rear);
                block.TopXyz = TopPoints(seg, v.ConstTopZ);
                block.RealRail = true;
                block.Folded = folded;
                return block.FootprintXy.Length >= 6;
            }
        }

        // 盒子退路：质心 + 长宽 + 走向方位角。**方位角没有就轴对齐**，不拿 0° 冒充。
        double len = r.LengthM, wid = r.WidthM;
        if (!(len > 1e-6) || !(wid > 1e-6)) return false;
        if (!UnitPrism.BoxPlanFootprint(r.Cx, r.Cy, len, wid, 0, 1, r.AzimuthDeg,
                                        out var bf, out var br, out _)) return false;

        block.FootprintXy = UnitPrism.PlanRing(bf, br);
        block.HasAzimuth = r.AzimuthDeg.HasValue;
        double zTop = r.ZHi > r.ZLo ? r.ZHi : (r.ThickM > 1e-6 ? r.Cz + r.ThickM * 0.5 : r.Cz);
        var top = new List<double>();
        for (int i = 0; i + 1 < block.FootprintXy.Length; i += 2)
        { top.Add(block.FootprintXy[i]); top.Add(block.FootprintXy[i + 1]); top.Add(zTop); }
        block.TopXyz = top.ToArray();
        block.RealRail = false;
        return block.FootprintXy.Length >= 6;
    }

    /// <summary>坡顶线（或整级不变的顶高程）→ 拟合区域高程用的点。</summary>
    private static double[] TopPoints(double[] seg, double? constTopZ)
    {
        if (seg.Length < 3) return Array.Empty<double>();
        if (!constTopZ.HasValue) return seg;
        var v = new double[seg.Length];
        for (int i = 0; i + 2 < seg.Length; i += 3)
        { v[i] = seg[i]; v[i + 1] = seg[i + 1]; v[i + 2] = constTopZ.Value; }
        return v;
    }

    /// <summary>
    /// 需不需要穿孔爆破。<b>物料优先，没有面绑就按台账类型退定并标明</b>。
    ///
    /// <para>为什么不能一律按台账类型判：同一个岩台阶，风化带松软可以直接铲、下面的砂岩要爆破，
    /// 台账的 <c>Kind</c> 两者都是「岩」。物料码才分得开。</para>
    /// <para>为什么退定的那一档要标出来：「这个面免爆」和「不知道这个面爆不爆、按岩当成要爆」
    /// 生成出来的穿孔区一模一样。<see cref="PlanBlock.BlastFromMaterial"/> 就是用来分这两件事的。</para>
    /// </summary>
    private static void ResolveBlasting(PlanBlock block, Dictionary<string, string> matOf)
    {
        if (matOf.TryGetValue(block.UnitId, out var code) && MaterialCatalog.Exists(code))
        {
            block.MaterialCode = code;
            block.NeedsBlasting = MaterialCatalog.Resolve(code).NeedsBlasting;
            block.BlastFromMaterial = true;
            return;
        }
        // 退定：排土不穿爆；岩按需爆破（保守，与 MaterialCatalog.Resolve 未知码的口径一致）；
        // 煤按免爆（露天煤矿的煤台阶绝大多数直接铲装，硬煤要爆破的面请在作业面台账上填物料码）。
        block.NeedsBlasting = block.Kind == LedgerKind.Rock;
        block.BlastFromMaterial = false;
    }

    private static PlanBlock Clone(PlanBlock b, string faceZone) => new()
    {
        UnitId = b.UnitId, Kind = b.Kind, Region = b.Region, Seam = b.Seam,
        FaceZone = faceZone, FaceCount = b.FaceCount,
        FootprintXy = b.FootprintXy, TopXyz = b.TopXyz,
        RealRail = b.RealRail, HasAzimuth = b.HasAzimuth, Folded = b.Folded,
        VolumeM3 = b.VolumeM3, Done = b.Done,
        // ★ 新增字段必须一并复制：漏一个，"一个单元绑两个面"时第二份就是空壳，
        //   而它照样进分组、照样出区域 —— 只是那块地的工序区凭空少了。
        Seq = b.Seq, MaterialCode = b.MaterialCode,
        NeedsBlasting = b.NeedsBlasting, BlastFromMaterial = b.BlastFromMaterial,
        Crest = b.Crest, Toe = b.Toe, WidthM = b.WidthM, TowardCrest = b.TowardCrest,
        Cx = b.Cx, Cy = b.Cy, LengthM = b.LengthM, ZHi = b.ZHi, AzimuthDeg = b.AzimuthDeg,
    };

    /// <summary>
    /// 单元的量（m³）。与 <c>UnitPlanLink.UnitVolumeM3</c> 同一条口径：
    /// <b>不用 <c>Row.Qty</c></b> —— 那是报表主量，煤那一档给的是吨。
    /// </summary>
    private static double VolumeM3(MiningUnitLedger.Row r) => r.Kind switch
    {
        LedgerKind.Coal => r.CoalM3 ?? 0,
        LedgerKind.Rock => r.NetRockM3 ?? 0,
        _ => r.DumpCapM3 ?? 0,
    };

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
