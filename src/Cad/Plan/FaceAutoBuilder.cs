// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/FaceAutoBuilder.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  「按本期单元派生作业面」
//
//  ══ 为什么必须有这个 ══
//  `ShortTermPlan.CreateDefaultFaces()` 写死了两个面：
//    主采面·东 @ 台阶标高 **60 m**　·　辅采面·南 @ **48 m**
//  而这个矿真实的采掘单元在 **1120–1320 m**。
//  归属口径 FA3 要求「面的台阶标高落在单元 [ZLo−2, ZHi+2] 之内」——
//  差着一千多米，**FA3 永远不可能成立**。
//
//  后果不是"归属率低"，是整条下游全断：
//    · 933 个单元一个都配不上作业面 ⇒ 没有工艺参数 ⇒
//    · 月度工序量：采出 0 · 排弃 0 · 穿孔 0 延米 · 炸药 0 t · 爆破 0 次
//    · 设备指派失败（一台可派的挖装设备都没有）
//  而**每一步都不报错**：报告里写的是「106 个单元没归属到作业面」，
//  读起来像"台账没填全"，实际是那两个面本身是样例。
//
//  ══ 派生什么、不派生什么 ══
//  派生（有据可依，从本期单元算得出来）：
//    · 台阶标高 —— 取该组单元**顶板**的代表值（FA4 就是按"贴近顶板"排序的，口径必须一致）
//    · 物料码 —— 煤 / 岩
//    · 份额、备采储量 —— 按该组在本期的量占比（不是拍的）
//  **不派生**（猜了就是编）：
//    · 推进方位 —— 只有工作线/真轨说得清，见 [[longterm-worklines-and-dump-by-truth]]
//    · 去向、运距 —— 去向档案里没配就是没配，排产会按运输功最小自己配
//    · 台阶几何（台阶高/坡面角/采宽/面长）—— 那是 working_face 库表的东西，
//      往这边复制就是第二份，两份迟早不一致而且都不报错
//
//  ══ 一条纪律 ══
//  **派生出来的面不许冒充录入的面**：每个面都带 <see cref="DerivedFace.Origin"/>，
//  名字带「派生」二字，Note 里写清它是从哪几个单元来的。
//  否则下一个人打开「确定开采程序」会以为这些面是有人核过的。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>作业面是哪来的。</summary>
public enum FaceOrigin
{
    /// <summary>人录进来的 / 库表里建过档的。</summary>
    Entered = 0,
    /// <summary>按本期单元派生 —— <b>没有人核过</b>。</summary>
    DerivedFromPeriod = 1,
    /// <summary>写死的样例（<b>标高 60/48 m，对不上任何真实台阶</b>）。</summary>
    Sample = 9,
}

/// <summary>派生出来的一个面（连同它的来历）。</summary>
public sealed class DerivedFace
{
    public WorkingFace Face = new();
    public FaceOrigin Origin = FaceOrigin.DerivedFromPeriod;
    /// <summary>这个面覆盖的单元号。</summary>
    public List<string> UnitIds = new();
    /// <summary>这一组的量（煤按万t、岩按万m³ —— <b>两者不许并成一列</b>）。</summary>
    public double CoalWanT;
    public double RockWanM3;
}

/// <summary>喂给派生器的一条单元（调用方从期次台账装）。</summary>
public sealed class FaceSeedUnit
{
    public string UnitId = "";
    /// <summary>true = 煤，false = 岩。<b>排土位置不进来</b>（它不是作业面）。</summary>
    public bool IsCoal;
    /// <summary>底板 / 顶板标高（m）。</summary>
    public double ZLo, ZHi;
    /// <summary>煤量（t）—— 煤单元用。</summary>
    public double CoalT;
    /// <summary>净岩量（m³）—— 岩单元用。</summary>
    public double RockM3;
    /// <summary>台阶/煤层名（进面名，人能对回图纸）。</summary>
    public string BenchName = "";
}

/// <summary>一次派生的结果。</summary>
public sealed class FaceAutoResult
{
    public List<DerivedFace> Faces = new();
    public List<string> Notes = new();
    /// <summary>界面直接显示的一句话。</summary>
    public string Headline = "";
    /// <summary>没能进任何面的单元（<b>必须点名，不许静默丢</b>）。</summary>
    public List<string> Unplaced = new();

    public bool Ok => Faces.Count > 0;
}

/// <summary>按本期单元派生作业面骨架。纯计算，永不抛。</summary>
public static class FaceAutoBuilder
{
    /// <summary>
    /// 台阶分档宽度（m）。
    /// <para>同一台阶上的单元顶板会差零点几到几米（分幅、分带），逐个建面就成了几十个一模一样的面。
    /// 按档归并，档宽取一个**明显小于台阶高**的值 —— 12m 台阶用 6m 档，不会把两级台阶并到一起。</para>
    /// </summary>
    public const double DefaultBandM = 6.0;

    /// <summary>
    /// 派生。
    /// </summary>
    /// <param name="units">本期采场类单元（<b>不含排土位置</b>）。</param>
    /// <param name="bandM">台阶分档宽度，见 <see cref="DefaultBandM"/>。</param>
    public static FaceAutoResult Build(IReadOnlyList<FaceSeedUnit>? units, double bandM = DefaultBandM)
    {
        var r = new FaceAutoResult();
        var list = (units ?? Array.Empty<FaceSeedUnit>()).Where(u => u != null).ToList();

        if (list.Count == 0)
        {
            r.Headline = "◆ 本期一个采场单元都没有 —— 派生不出作业面。"
                       + "先在「采掘单元清单」点一次「一键排本月」把期次排出来。";
            return r;
        }
        if (!(bandM > 1e-6))
        {
            r.Headline = $"◆ 台阶分档宽度必须为正（给的是 {bandM:0.##}）—— 没有派生。";
            return r;
        }

        // 顶板落档：同一台阶上的分幅分带归到一个面
        var groups = list
            .Where(u => Finite(u.ZLo) && Finite(u.ZHi))
            .GroupBy(u => (u.IsCoal, Band: (long)Math.Floor(u.ZHi / bandM)))
            .OrderBy(g => g.Key.IsCoal ? 0 : 1)
            .ThenByDescending(g => g.Key.Band)
            .ToList();

        // 标高读不出来的单元不许静默丢
        foreach (var u in list.Where(u => !Finite(u.ZLo) || !Finite(u.ZHi)))
            r.Unplaced.Add(u.UnitId);

        double totCoalT = list.Where(u => u.IsCoal).Sum(u => u.CoalT);
        double totRockM3 = list.Where(u => !u.IsCoal).Sum(u => u.RockM3);

        int order = 1;
        foreach (var g in groups)
        {
            bool coal = g.Key.IsCoal;
            double zTop = g.Average(u => u.ZHi);          // FA4 按"贴近顶板"排序 ⇒ 这里也用顶板口径
            double coalT = g.Where(u => u.IsCoal).Sum(u => u.CoalT);
            double rockM3 = g.Where(u => !u.IsCoal).Sum(u => u.RockM3);

            // 份额按**同物料内**的量占比算 —— 煤的万t 和岩的万m³ 不许放进同一个分母
            double share = coal
                ? (totCoalT > 1e-9 ? coalT / totCoalT * 100 : 0)
                : (totRockM3 > 1e-9 ? rockM3 / totRockM3 * 100 : 0);

            // ★ 台阶高**从单元量出来**（2026-08-19）—— 这是工序量唯一缺的那个输入。
            //
            //   `FaceProcessChain` 的孔网（7×8m）和炸药单耗（0.32kg/m³）都有缺省，
            //   只有 BenchHeightM 缺省是 0；而 CheckComputable 一看它是 0 就判"算不出"，
            //   于是整个面的穿孔延米/孔数/炸药量都是 0 ——
            //   **而那个 0 的意思是「算不出」，不是「不用穿」，两者在报表上长得一模一样。**
            //
            //   台阶高不用猜：它就是 ZHi − ZLo。取中位数而不是均值 ——
            //   一组里混进一个跨两级的厚单元时，均值会被拉高，中位数不会。
            var thicks = g.Select(u => u.ZHi - u.ZLo).Where(t => t > 1e-6).OrderBy(t => t).ToList();
            double benchH = thicks.Count == 0 ? 0 : thicks[thicks.Count / 2];

            string bench = g.Select(u => u.BenchName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string name = $"派生·{(coal ? "煤" : "岩")}{zTop:0}m"
                        + (bench.Length > 0 ? $"·{bench}" : "");

            var f = new WorkingFace
            {
                Name = name,
                BenchElevationM = Math.Round(zTop, 2),
                SharePct = Math.Round(share, 2),
                MaterialCode = coal ? PlanMaterialCatalog.Coal : PlanMaterialCatalog.Rock,
                Order = order++,
                // 煤按万t 记备采储量；岩不是"储量"，这一列留 0 并在 Note 里写明它的量
                AvailableReserveWanT = coal ? Math.Round(coalT / 1e4, 3) : 0,
                // ★ 下面这些**一律不猜**：
                //   推进方位只有工作线/真轨说得清；去向与运距在去向档案里，没配就是没配。
                //   给个"缺省 90°/2.6km"看着完全正常，而它会一路进运输功、进班表、进达成度。
                AdvanceAzimuthDeg = double.NaN,
                DestinationId = "", DestinationName = "", DestinationKindText = "",
                HaulDistanceKm = 0,
                // 工艺链：台阶高从单元量出来；孔网与单耗**用的是缺省值，不是这个矿的实测**
                Process = new FaceProcessChain { BenchHeightM = Math.Round(benchH, 2) },
                Note = $"【按本期单元派生，没有人核过】{g.Count()} 个单元　"
                     + (coal ? $"煤 {coalT / 1e4:0.##} 万t" : $"净岩 {rockM3 / 1e4:0.#} 万m³")
                     + $"　顶板 {g.Min(u => u.ZHi):0.#}~{g.Max(u => u.ZHi):0.#} m"
                     + $"　台阶高 {benchH:0.##}m（= 单元厚度中位数）"
                     + "　◆ 孔网 7×8m / 炸药单耗 0.32kg/m³ 是**缺省值，不是本矿实测**"
                     + "　◆ 推进方位/去向/运距未派生（猜了就是编）",
            };

            r.Faces.Add(new DerivedFace
            {
                Face = f, Origin = FaceOrigin.DerivedFromPeriod,
                UnitIds = g.Select(u => u.UnitId).ToList(),
                CoalWanT = coalT / 1e4, RockWanM3 = rockM3 / 1e4,
            });
        }

        r.Headline = Describe(r, list.Count, bandM, totCoalT, totRockM3);
        return r;
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static string Describe(FaceAutoResult r, int nUnits, double bandM,
                                   double totCoalT, double totRockM3)
    {
        int coalFaces = r.Faces.Count(f => f.Face.MaterialCode == PlanMaterialCatalog.Coal);
        string s = $"按本期 {nUnits} 个采场单元派生出 **{r.Faces.Count} 个作业面**"
                 + $"（煤 {coalFaces} · 岩 {r.Faces.Count - coalFaces}，台阶分档 {bandM:0.#} m）"
                 + $" · 煤 {totCoalT / 1e4:0.##} 万t · 净岩 {totRockM3 / 1e4:0.#} 万m³。";

        s += "　⚠ 这些面**没有人核过**：标高与量是从单元算出来的，"
           + "**推进方位、去向、运距、台阶几何一概没派生** —— 猜出来的那几个数看着完全正常，"
           + "却会一路进运输功、进班表、进达成度。要用于比选方案，得到「确定开采程序」逐个核。";

        if (r.Unplaced.Count > 0)
            s += $"　◆ 另有 {r.Unplaced.Count} 个单元标高读不出来，没进任何面："
               + string.Join("、", r.Unplaced.Take(5))
               + (r.Unplaced.Count > 5 ? " 等" : "") + "。";

        r.Notes.Add("· 台阶标高取该组单元**顶板**的均值 —— 归属口径 FA4 就是按「贴近顶板」排序的，两边必须同口径。");
        r.Notes.Add($"· 台阶分档 {bandM:0.#} m：同一级台阶上的分幅分带归成一个面，"
                  + "档宽刻意小于台阶高（12m 台阶用 6m 档），不会把两级并到一起。");
        r.Notes.Add("· 份额按**同物料内**的量占比算 —— 煤的万t 与岩的万m³ 不进同一个分母。");
        r.Notes.Add("· 岩面的「备采储量(万t)」留 0：岩不是储量，它的量在 Note 里按万m³ 记。");
        r.Notes.Add("· **台阶高从单元量出来**（厚度中位数）—— 这是工序量唯一缺的那个输入。"
                  + "它缺省是 0，而 0 会被判成「算不出」，于是穿孔延米/孔数/炸药量全是 0 —— "
                  + "那个 0 与「不用穿」在报表上长得一模一样。");
        r.Notes.Add("◆ **孔网 7×8m 与炸药单耗 0.32kg/m³ 用的是缺省值，不是本矿实测** —— "
                  + "穿爆量按它算得出来，但那是一般值。要当数用，到「编辑工艺流程」按矿改。");
        return s;
    }
}
