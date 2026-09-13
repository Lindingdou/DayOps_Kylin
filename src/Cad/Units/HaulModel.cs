// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/HaulModel.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>
/// <b>等效运距</b>（含坡度折算）—— 接真路网之前的几何口径。
///
/// <para><b>为什么不能只算平距</b>：露天矿的运输功大头在<b>提升</b>。
/// <b>内排通常是下排</b>（重车下坡，几乎白送）、<b>外排常要爬升到排土场顶</b>。
/// 只算平距的话，一个平距 1.2km 的内排位和一个平距 1.2km 的外排位看起来一样贵 ——
/// 于是「内排优先」和「运输功最小」两条策略在<b>该分开的地方分不开</b>，
/// 而内排率恰恰是这套设计里最重要的决策变量之一。</para>
///
/// <para><b>口径</b>：<c>等效运距 = 平距×迂回系数 + 提升当量</c>，
/// 提升当量按上/下坡分别折算 —— 上坡重车耗油耗时，下坡省力但受制动与安全限速约束，两者<b>不对称</b>。</para>
///
/// <para><b>⚠ 这两个当量系数是工程经验区间的中值，必须现场核准</b>（写进验收标准的待定项）。
/// 接上 <c>RoadLib</c> 的真路网（`HaulResolver` 的 Dijkstra）之后，本类整体作废 ——
/// 那时运距由真实纵坡逐段算，不再需要当量。</para>
/// </summary>
public sealed class HaulModel
{
    /// <summary>路网迂回系数：直线平距 → 实际行车平距。缺路网时的兜底。</summary>
    public double Tortuosity = 1.3;

    /// <summary>
    /// <b>上坡</b>提升当量（m 平距 / m 提升）。重车爬坡：1 m 提升 ≈ 这么多米平距的代价。
    /// 〔待现场核准〕经验区间 8~15，取中值 12。
    /// </summary>
    public double UphillEquivalent = 12.0;

    /// <summary>
    /// <b>下坡</b>下降当量（m 平距 / m 下降）。重车下坡省力，但受制动与安全限速约束，<b>不是 0</b>。
    /// 〔待现场核准〕经验区间 2~5，取中值 3。
    ///
    /// <para><b>允许为负 = 减免</b>：`RoadLib` 的车型缺省值（<c>TruckProfile.DownhillEquivK</c>）
    /// 把下坡当<b>折减</b>算，与这里的"当代价"正负号相反。两套都在算同一件事，
    /// 以哪套为准要现场核准 —— 但<b>核准之后得填得进来</b>。
    /// 上一版这里被 <c>Math.Max(0, …)</c> 夹住，填负数会<b>静默变成 0</b>：
    /// 那等于"下坡免费"，既不是本模块的口径也不是路网缺省值的口径，是第三种谁也没选的口径。</para>
    /// </summary>
    public double DownhillEquivalent = 3.0;

    /// <summary>
    /// 减免的下限：等效运距不得低于平距的这个倍数（默认 0.5，与 <c>RoadLib.HaulMetrics</c> 同）。
    /// <para>下坡给减免时，落差够大就能把等效运距<b>压到 0 甚至为负</b> ——
    /// 那会让"越远越便宜"，排土配对会去抢最深的那个位置。所以必须有底。</para>
    /// </summary>
    public double MinEquivalentFraction = 0.5;

    /// <summary>
    /// 等效运距（km）。<paramref name="planKm"/> = 平面距离（km，未乘迂回系数）；
    /// <paramref name="liftM"/> = 目的地标高 − 源标高（m，正 = 要爬升）。
    /// </summary>
    public double EquivalentKm(double planKm, double liftM)
    {
        double flat = Math.Max(0, planKm) * Math.Max(1.0, Tortuosity);
        // 上坡只可能是代价（负的上坡当量没有物理含义），照旧夹住；
        // 下坡**允许为负**（减免），否则路网缺省值那套口径根本填不进来。
        double lift = liftM >= 0
            ? liftM * Math.Max(0, UphillEquivalent)
            : -liftM * DownhillEquivalent;
        double km = flat + lift / 1000.0;
        // 减免有底：落差够大时它能把等效运距压到 0 甚至为负 —— 那会变成"越远越便宜"，
        // 配对就会去抢最深的那个位置。与 RoadLib.HaulMetrics 同一个兜法（那边夹 0.5×L）。
        return Math.Max(flat * Math.Max(0, Math.Min(1.0, MinEquivalentFraction)), km);
    }

    /// <summary>
    /// 一行说明（报表里标出来运距是怎么来的）。
    /// <para><b>这是几何兜底口径</b>：真路网接上以后它并不作废 —— 路网上解不出来的那些
    /// O-D（源/汇落不到节点、不可达）照旧走这条路，所以两套口径会<b>同时存在</b>，
    /// 命中率由 <c>CoupledPlanResult.HaulNote</c> 报出来。</para>
    /// </summary>
    public string Text()
        => $"几何兜底口径：等效运距 = 平距×{Tortuosity:0.##} + 提升当量"
         + $"（上坡 {UphillEquivalent:0.#} / 下坡 {DownhillEquivalent:0.#} m平距每m，两个系数〔待现场核准〕）";
}
