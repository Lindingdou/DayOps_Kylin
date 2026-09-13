// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanFixture.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// UnitPlanEngine 判据用的<b>合成算例</b>。
///
/// <para><b>为什么用合成算例而不是真数据</b>：判据要判的是<b>方向和自洽</b>
/// （"煤多了剥离量不该减"、"占容不该超库容"），不是具体数字。
/// 真数据换一版地质，每一个断言都要重写；而合成算例里压覆关系是<b>我摆出来的</b>，
/// 前驱闭包应该是谁一目了然 —— 判据错了还是引擎错了分得清。</para>
///
/// <para><b>几何摆法</b>（<see cref="Stacked"/>）：每个"柱"= 同一 XY 上下叠三个体
/// 煤 [1000,1008] / 岩1010 [1008,1028] / 岩1030 [1028,1048]。
/// 柱与柱之间 X 向间距 300m、带与带之间 Y 向间距 200m，都大于推进宽 40m 的两倍，
/// 所以<b>压覆边只发生在柱内</b>：Pred[煤] = {岩1010, 岩1030}、Pred[岩1010] = {岩1030}。
/// 这样闭包的量是能手算的。</para>
/// </summary>
public static class UnitPlanFixture
{
    public const double CoalRho = 1.35;
    public const double RockRho = 2.50;
    public const double RockKr = 1.15;

    public const double W = 40.0;          // 推进宽
    public const double RailLen = 200.0;   // 前脸轨长
    public const double PitchX = 300.0;    // 幅间距（> 2W，保证柱间不粘连）
    public const double PitchY = 200.0;    // 带间距

    public static MineUnit Unit(string id, UnitKind kind, string seam, int band, int panel, int panelCount,
                                double x0, double y, double zLo, double zHi, double vol,
                                double done = 0, double strikeLen = -1, double len = RailLen, double w = W)
    {
        return new MineUnit
        {
            UnitId = id,
            Kind = kind,
            SeamCode = seam,
            BandId = band, PanelIndex = panel, PanelCount = Math.Max(1, panelCount),
            Cx = x0 + len * 0.5, Cy = y, Cz = (zLo + zHi) * 0.5,
            ZLo = zLo, ZHi = zHi,
            StrikeLenM = strikeLen > 0 ? strikeLen : len,
            WidthM = w, ThickM = zHi - zLo,
            InSituM3 = vol,
            MaterialIndex = 0,
            DoneFraction = done,
            RailXy = new[] { x0, y, x0 + len, y },
            MinX = x0 - w, MinY = y - w, MaxX = x0 + len + w, MaxY = y + w,
        };
    }

    /// <summary>
    /// 叠柱算例：<paramref name="bands"/> 带 × <paramref name="panels"/> 幅，每柱 煤 + 中岩 + 上岩。
    /// </summary>
    /// <param name="carryOver">前几个（按 带→幅 序）煤幅设成遗留幅（期初完成度 0.4）。</param>
    public static List<MineUnit> Stacked(int bands = 2, int panels = 4,
                                         double coalVol = 5e4, double midVol = 1.2e5, double topVol = 1.6e5,
                                         int carryOver = 0)
    {
        var list = new List<MineUnit>();
        int made = 0;
        for (int b = 1; b <= bands; b++)
            for (int p = 1; p <= panels; p++)
            {
                double x0 = 1000 + (p - 1) * PitchX, y = 500 + b * PitchY;
                double done = made++ < carryOver ? 0.4 : 0.0;
                list.Add(Unit($"煤2-B{b}-P{p}", UnitKind.Coal, "煤2", b, p, panels, x0, y, 1000, 1008, coalVol, done));
                list.Add(Unit($"岩1010-B{b}-P{p}", UnitKind.Rock, "岩1010", b, p, panels, x0, y, 1008, 1028, midVol));
                list.Add(Unit($"岩1030-B{b}-P{p}", UnitKind.Rock, "岩1030", b, p, panels, x0, y, 1028, 1048, topVol));
            }
        return list;
    }

    /// <summary>
    /// <b>平摊算例</b>：多层 × 多带 × 多幅的煤，全部同标高、XY 互不重叠 ⇒ <b>压覆边 0</b>。
    ///
    /// <para>压覆深度全是 0 ⇒ 只有一个深度层 ⇒ 定序完全由 U11 的转场链决定，
    /// 判据看到的就是链本身，中间没有别的东西挡着。</para>
    ///
    /// <para><b>走向长是故意编排的</b>：<c>1000 − (幅−1)×10 − 组序×0.1</c>，
    /// 于是"纯按作业面优先级（长带优先）"排出来的顺序是<b>逐幅横扫</b>——
    /// 每走一步就换一个（层,带）。转场链要是没起作用，一眼就看得出来。</para>
    /// </summary>
    public static List<MineUnit> FlatFaces(int seams = 3, int bands = 2, int panels = 4, double vol = 5e4)
    {
        var list = new List<MineUnit>();
        int gi = 0;
        for (int s = 0; s < seams; s++)
            for (int b = 1; b <= bands; b++)
            {
                gi++;
                for (int p = 1; p <= panels; p++)
                {
                    double x0 = 1000 + (p - 1) * PitchX;
                    double y = 500 + (s * bands + b) * PitchY;
                    double sl = 1000 - (p - 1) * 10 - gi * 0.1;
                    list.Add(Unit($"煤{s}-B{b}-P{p}", UnitKind.Coal, $"煤{s}", b, p, panels,
                                  x0, y, 1000, 1008, vol, 0, sl));
                }
            }
        return list;
    }

    /// <summary>
    /// 一个排土场：<paramref name="levels"/> 级 × 4 个位置。
    /// <para>位次按 <c>带×1e6 + 幅×100</c> 编（与 <see cref="DumpSlotAdapter"/> 同源），
    /// 4 个位置排成 2 带 × 2 幅 —— 这样「逐带推进」和「逐幅到底」两种排弃顺序<b>确实不同</b>；
    /// 排成 1×4 的话两者答案一样，U12 那条轴看上去"塌了"其实是算例摆得不对。</para>
    /// <para>库容<b>逐位置不同</b>（×(1+k×0.3)），否则「摊平」与「逐带推进」在均等库容下会重合。</para>
    /// </summary>
    public static List<DumpSlot> Dump(string name, int levels, double capBase,
                                      double cx, double cy, double baseZ,
                                      bool isInternal = false, int availableFrom = 1)
    {
        var list = new List<DumpSlot>();
        for (int lv = 0; lv < levels; lv++)
            for (int k = 0; k < 4; k++)
                list.Add(new DumpSlot
                {
                    DumpName = name,
                    Level = lv,                                   // 0 = 最下一级，最先承接
                    Order = (k / 2) * 1000000 + (k % 2) * 100,    // 带 × 幅
                    CapacityM3 = capBase * (1 + k * 0.3),
                    Cx = cx + k * 120, Cy = cy, Cz = baseZ + lv * 20,
                    IsInternal = isInternal,
                    AvailableFromMonth = availableFrom,
                });
        return list;
    }

    public static List<CoalSink> Sinks(double x = 4000, double y = 4000, double z = 1050, double capT = 0)
        => new() { new CoalSink { Name = "破碎站1", Code = "CRUSH1", Cx = x, Cy = y, Cz = z, CapacityT = capT } };

    public static GapMaterial[] Rock1()
        => new[] { new GapMaterial { Name = "岩", Code = "rock", Density = RockRho, Kr = RockKr } };

    /// <summary>
    /// 一份可直接 Solve 的输入（叠柱算例 + 一个外排场 + 一个破碎站）。
    /// <para><paramref name="stripTargetM3"/> = 本月<b>排弃量</b>（m³实方，U13）；
    /// 与 <paramref name="stripRatio"/> <b>两条路线</b>，都不给就只剥必剥闭包。</para>
    /// </summary>
    public static UnitPlanInput Input(List<MineUnit> units, List<DumpSlot> slots,
                                      double coalTargetT, double stripRatio = 0, double stripTargetM3 = 0)
        => new()
        {
            Units = units,
            Slots = slots,
            CoalSinks = Sinks(),
            Materials = Rock1(),
            CoalTargetT = coalTargetT,
            TargetStripRatio = stripRatio,
            StripTargetM3 = stripTargetM3,
            CoalDensity = CoalRho,
            Month = 1,
        };

    /// <summary>
    /// 「只剩一个煤幅可采」的算例：1 带 4 幅，前 3 幅的煤标成<b>已采完</b>。
    /// <para>用途：让<b>煤欠产</b>与<b>必剥闭包不大</b>同时成立 —— U13.3（煤欠了排弃量不许跟着缩）
    /// 必须在这两件事同时成立时才判得动。整份算例都可采的话，煤一欠、闭包就把整个采场拉进必剥，
    /// 目标被下界顶掉，那条判据就<b>空过</b>了。</para>
    /// <para>可采煤 = 1 幅（<c>coalVol × ρ</c>）· 必剥闭包 = 该柱的中岩 + 上岩 ·
    /// 前沿可剥的岩 = 闭包 + 另外 3 柱的上岩（中岩被上岩压着，本月进不了候选）。</para>
    /// </summary>
    public static List<MineUnit> OneCoalLeft(double coalVol = 5e4, double midVol = 1.2e5, double topVol = 1.6e5)
    {
        var list = Stacked(1, 4, coalVol, midVol, topVol);
        foreach (var u in list)
            if (u.IsCoal && u.PanelIndex <= 3) u.DoneFraction = 1.0;
        return list;
    }

    /// <summary>上一个算例里「本月前沿上真正剥得动」的总量（m³实方）—— 判据拿它定位缺口的边界。</summary>
    public static double FrontierRockM3(double midVol = 1.2e5, double topVol = 1.6e5)
        => (midVol + topVol) + 3 * topVol;

    /// <summary>叠柱算例里煤的可采总吨（判据里"目标超上限"要用它定位）。</summary>
    public static double TotalCoalT(IEnumerable<MineUnit> units)
        => units.Where(u => u.IsCoal).Sum(u => u.RemainM3) * CoalRho;

    /// <summary>建图（与引擎默认口径一致：不给推进方向 ⇒ 双侧走廊，偏保守）。</summary>
    public static UnitGraph Graph(IReadOnlyList<MineUnit> units) => UnitGraphBuilder.Build(units, null);
}
