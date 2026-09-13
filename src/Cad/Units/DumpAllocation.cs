// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/DumpAllocation.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>采排配对策略 —— <b>派生轴之一</b>（第 4 条轴，与 N / 剥离节奏 / 采煤节奏 正交）。</summary>
public enum PairingStrategy
{
    /// <summary>运输功最小：每笔都往最近的合法去向送。对标 <c>FlowAssigner</c> 的 <c>min Σ(Q×L_eq)</c>。</summary>
    MinHaul = 0,
    /// <summary>内排优先：能进内排就进内排（省运距、省外排库容），内排满了再外排。</summary>
    InternalFirst = 1,
    /// <summary>库容均衡：往剩余占比最高的去向送，避免某个排土场先排满、后期无处可去。</summary>
    LevelCapacity = 2,
}

/// <summary>
/// 一个<b>排土位置</b>（= 排土场某一级台阶上的一条带）。
/// <para>直接对应 <c>DumpStripPlanner.Cell</c>（口径 D1–D6）：
/// 那边已经把排土场切成带库容与质心的位置清单了，这里<b>不重新造几何</b>，只做"哪个月往哪儿排多少"。</para>
/// </summary>
public sealed class DumpSlot
{
    /// <summary>去向名（排土场/堆场）。</summary>
    public string DumpName = "";
    /// <summary>台阶级。<b>自下而上</b>承接：同一去向同一时刻只有最低的未满级在接收。</summary>
    public int    Level;
    /// <summary>同级内的顺序（幅号×带号推出来）。</summary>
    public int    Order;
    /// <summary>库容，口径 = <b>占容方 V容</b>（D6）。</summary>
    public double CapacityM3;
    /// <summary>质心（运距用）。</summary>
    public double Cx, Cy, Cz;
    /// <summary>是否内排（有启用时机）。</summary>
    public bool   IsInternal;
    /// <summary>第几个月起可用（1 = 期初就可用）。内排要等采空区形成 —— 时空约束第 1 条。</summary>
    public int    AvailableFromMonth = 1;
    /// <summary>到采场的等效运距（km）。</summary>
    public double HaulKm;

    internal double Used;                       // 已占（V容）
    internal double Remain => Math.Max(0, CapacityM3 - Used);
}

/// <summary>逐层间标签的物料参数。<b>不在本模块内建物料表</b> —— 谁拥有物料目录谁填，杜绝三份同值表各改各的。</summary>
public sealed class GapMaterial
{
    /// <summary>可读名（覆岩/层间/夹矸…）。</summary>
    public string Name = "";
    /// <summary>
    /// 物料码，与下游目录对齐（<c>topsoil / weathered / rock / interburden / coal / lowgrade</c>）。
    /// 空 = 下游按 <see cref="Name"/> 自行匹配。导出契约带着它走，下游拿它去查自己的物料表并<b>对账</b>。
    /// </summary>
    public string Code = "";
    /// <summary>原位密度 t/m³ —— 运输功按吨量算，不按方量。</summary>
    public double Density = 2.5;
    /// <summary>残余膨胀系数 Kr：<b>排土场库容按 V容 = V实 × Kr 扣</b>（不是松方 Ks）。</summary>
    public double Kr = 1.15;
    /// <summary>允许去向名（空 = 不限）。「表土只能进表土堆场」这类<b>硬约束</b>写这里。</summary>
    public string[] AllowedDumps = Array.Empty<string>();
}

public sealed class DumpAllocationInput
{
    public List<DumpSlot>   Slots = new();
    /// <summary>逐层间标签的物料参数，索引 = <see cref="GapCode"/> 的 g。</summary>
    public GapMaterial[]    Materials = Array.Empty<GapMaterial>();
    public PairingStrategy  Strategy = PairingStrategy.MinHaul;

    /// <summary>
    /// <b>逐月运距</b>（月, 位置）→ km。null = 用 <see cref="DumpSlot.HaulKm"/> 那个静态值。
    /// <para>坑越挖越深、排土场越堆越高，O-D 运距逐月在变。静态运距会让"运输功最小"
    /// 在头几个月挑对、后几个月挑错。外循环（<see cref="CoupledMinePlanner"/>）从几何反算它。</para>
    /// </summary>
    public Func<int, DumpSlot, double>? HaulProvider;

    /// <summary>
    /// <b>内排的累计占容上限</b>（m³ 占容方，下标 = 月份，长度 ≥ T+1）。空 = 不卡。
    /// <para>物理含义：<b>坑还没挖出来就没法往里回填</b>。内排容量的真上限不是排土场设计库容，
    /// 而是<b>已经形成的采空区</b>。只卡剥离总量是不够的 —— 那只管"能剥多少"，
    /// 不管"剥出来的往哪儿放"，结果会排出往还不存在的空间里排土的计划。</para>
    /// </summary>
    public double[] InternalCumCapM3 = Array.Empty<double>();
}

/// <summary>一笔物料流：(期, 源标签, 物料, 实方, 汇, 运距) —— 与 PlanLib 的 <c>PlanFlow</c> 六元组同构。</summary>
public sealed class DumpFlow
{
    public int    Month;
    public int    Gap;                  // 源：层间标签
    public string MaterialName = "";
    public string MaterialCode = "";    // 与下游物料目录对齐的码
    public double Density, Kr;          // 实际用的 ρ / Kr —— 下游拿它和自己的目录对账，别让两边各算各的
    public string DumpName = "";
    public int    Level;                // 汇：排土场台阶级（0 = 最先承接的最下一级）
    /// <summary>汇的位置质心（世界坐标）—— 三维层体按它长，运距按它算。</summary>
    public double Dx, Dy, Dz;
    public double InSituM3;             // V实（采场挖出来的）
    public double DumpM3;               // V容（排土场实际占掉的）= V实 × Kr
    public double TonnageT;             // 吨量 = V实 × ρ
    public double HaulKm;
    public double TransportWorkTKm => TonnageT * HaulKm;
    public bool   IsInternal;
}

public sealed class DumpMonthRow
{
    public int    Month;
    public double InSituM3, DumpM3, TonnageT, TransportWorkTKm;
    public double InternalRatePct;      // 内排率（按实方）
    public double UnplacedM3;           // 排不下的实方 —— 必须显式，不许摊平
    public readonly List<DumpFlow> Flows = new();
}

public sealed class DumpAllocationResult
{
    public bool   Success;
    public string Error = "";
    public readonly List<DumpMonthRow> Months = new();
    public readonly List<string> Warnings = new();
    public readonly Dictionary<string, double> RemainByDump = new();   // 期末剩余库容（V容）

    public double TotalInSituM3, TotalTransportWorkTKm, TotalUnplacedM3;
    public double OverallInternalRatePct;
    /// <summary>能力/库容够不够 —— 一方排不下就是 false，不许"基本排得下"。</summary>
    public bool   AllPlaced => TotalUnplacedM3 <= 1e-6;

    /// <summary>
    /// 自洽校核（五类：守恒 / 有序 / 非有限 / 范围与引用 / 完备性）。
    ///
    /// <para><b>为什么配对结果也要自己校</b>：<see cref="ScheduleDeriver"/> 是<b>直接拿这个对象打分</b>的
    /// （运输功、内排率两维），**根本不经过 `MinePlanExport.Validate`**。
    /// 配对一歪，比选表就照着歪的数排名 —— 而契约层的校核在那时还没发生。</para>
    ///
    /// <para>其中两条是<b>踩过的坑的回归网</b>：
    /// ① <b>剩余库容不许为负</b>（Kr 为负时 <c>slot.Used += take*Kr</c> 让已占变负 ⇒「库容越排越多」，全程"成功"）；
    /// ② <b>运输功不许是 ±∞</b>（投不到推进轴的位置 <c>SlotU = −∞</c> 曾让运距变 ∞，
    /// 「运输功最小」在一堆 ∞ 里挑不出东西，而 `ToJson` 直接抛）。</para>
    /// </summary>
    public List<string> Validate(double tolPct = 0.5)
    {
        var bad = new List<string>();
        if (!Success) { bad.Add("配对本身失败：" + Error); return bad; }

        static double Rel(double a, double b)
            => Math.Abs(a) < 1e-9 && Math.Abs(b) < 1e-9 ? 0
             : Math.Abs(a - b) / Math.Max(1e-9, Math.Max(Math.Abs(a), Math.Abs(b))) * 100;
        static bool Bad(double d) => double.IsNaN(d) || double.IsInfinity(d);

        // ③ 非有限（先查：后面全是比大小，与 NaN 比恒 false，不先挡住会让其余判据集体失灵）
        foreach (var (nm, v) in new[]
        {
            ("总实方", TotalInSituM3), ("总运输功", TotalTransportWorkTKm),
            ("总排不下", TotalUnplacedM3), ("总内排率", OverallInternalRatePct),
        })
            if (Bad(v)) bad.Add($"{nm} = {v} —— ±∞/NaN 不是工程量");
        foreach (var f in Months.SelectMany(m => m.Flows))
            if (Bad(f.HaulKm) || Bad(f.InSituM3) || Bad(f.DumpM3) || Bad(f.TonnageT))
            { bad.Add($"第{f.Month}月 {f.MaterialName}→{f.DumpName} 有非有限值（运距 {f.HaulKm}）"); break; }

        // ① 守恒：月行 = 其流之和；汇总 = 月行之和；逐笔 Kr/ρ 换算对得上
        double sIn = 0, sWork = 0, sUnp = 0;
        foreach (var m in Months)
        {
            double fIn = m.Flows.Sum(f => f.InSituM3), fDump = m.Flows.Sum(f => f.DumpM3);
            double fTon = m.Flows.Sum(f => f.TonnageT);
            if (Rel(fIn, m.InSituM3) > tolPct) bad.Add($"第{m.Month}月实方 {m.InSituM3:0.##} ≠ 逐笔流之和 {fIn:0.##}");
            if (Rel(fDump, m.DumpM3) > tolPct) bad.Add($"第{m.Month}月占容 {m.DumpM3:0.##} ≠ 逐笔流之和 {fDump:0.##}");
            if (Rel(fTon, m.TonnageT) > tolPct) bad.Add($"第{m.Month}月吨量 {m.TonnageT:0.##} ≠ 逐笔流之和 {fTon:0.##}");
            sIn += m.InSituM3; sWork += m.TransportWorkTKm; sUnp += m.UnplacedM3;
        }
        if (Rel(sIn, TotalInSituM3) > tolPct) bad.Add($"总实方 {TotalInSituM3:0.##} ≠ 逐月之和 {sIn:0.##}");
        if (Rel(sWork, TotalTransportWorkTKm) > tolPct) bad.Add($"总运输功 {TotalTransportWorkTKm:0.##} ≠ 逐月之和 {sWork:0.##}");
        if (Rel(sUnp, TotalUnplacedM3) > tolPct) bad.Add($"总排不下 {TotalUnplacedM3:0.##} ≠ 逐月之和 {sUnp:0.##}");
        foreach (var f in Months.SelectMany(m => m.Flows))
        {
            if (f.Kr > 0 && Rel(f.InSituM3 * f.Kr, f.DumpM3) > tolPct)
            { bad.Add($"第{f.Month}月 {f.MaterialName} 占容 ≠ 实方×Kr"); break; }
            if (f.Density > 0 && Rel(f.InSituM3 * f.Density, f.TonnageT) > tolPct)
            { bad.Add($"第{f.Month}月 {f.MaterialName} 吨量 ≠ 实方×ρ"); break; }
        }

        // ② 有序 + ⑤ 完备：月序号严格递增、无缺口
        for (int i = 1; i < Months.Count; i++)
            if (Months[i].Month <= Months[i - 1].Month)
            { bad.Add($"月序号没递增：第{Months[i - 1].Month}月之后是第{Months[i].Month}月"); break; }
        if (Months.Count > 0)
        {
            int lo = Months[0].Month, hi = Months[^1].Month;
            if (hi - lo + 1 != Months.Count)
                bad.Add($"月份不连续：{lo}..{hi} 应有 {hi - lo + 1} 个月，实际 {Months.Count} 个");
        }

        // ④ 范围与引用
        foreach (var m in Months)
        {
            if (m.InternalRatePct is < -1e-9 or > 100.0000001)
            { bad.Add($"第{m.Month}月内排率 {m.InternalRatePct:0.0}% 越界"); break; }
            if (m.UnplacedM3 < -1e-9) { bad.Add($"第{m.Month}月排不下为负"); break; }
        }
        foreach (var f in Months.SelectMany(m => m.Flows))
        {
            if (f.Kr is > 0 and < 1) { bad.Add($"第{f.Month}月 {f.MaterialName} 的 Kr={f.Kr:0.###} < 1 —— 岩石破碎只会膨胀"); break; }
            if (f.InSituM3 < -1e-9) { bad.Add($"第{f.Month}月 {f.MaterialName} 实方为负"); break; }
        }
        // ★ 剩余库容为负 = 「越排越多」—— Kr 为负时真出过，而且全程"成功"
        foreach (var kv in RemainByDump.Where(kv => kv.Value < -1e-6))
        { bad.Add($"去向「{kv.Key}」剩余库容 {kv.Value / 1e4:0.##}万m³ 为负 —— 库容被排成负数了"); break; }

        return bad;
    }
}

/// <summary>
/// 采排配对：把<b>逐月剥离量</b>（已按层间标签分账）摊到<b>排土位置</b>上。
///
/// <para><b>为什么源的粒度必须是「标高格 × 层间」而不是「作业面」</b>：物料码在层间粒度上才准，
/// 而「表土只能进表土堆场」是硬约束 —— 聚合到作业面就只能取加权平均物料，
/// 那条硬约束在数学上就不成立了。Ks/Kr/是否需爆破也全不同。</para>
///
/// <para><b>三条时空硬约束</b>（对标 TaskLib 的 <c>SpaceTimeValidator</c>）：
/// ① 内排要等采空区形成（<see cref="DumpSlot.AvailableFromMonth"/>）；
/// ② 排土台阶<b>自下而上</b>承接，同一去向同一时刻只有最低的未满级在收；
/// ③ 物料的允许去向是硬的（<see cref="GapMaterial.AllowedDumps"/>）。</para>
///
/// <para><b>三个体积口径别混</b>：采场挖出的是 <b>V实</b>；排土场库容按 <b>V容 = V实×Kr</b> 扣；
/// 运输功按<b>吨量</b>算（吨是三个口径之间唯一守恒的中间量）。</para>
/// </summary>
public static class DumpAllocator
{
    public static DumpAllocationResult Allocate(MonthlyScheduleResult sched, DumpAllocationInput inp)
    {
        var r = new DumpAllocationResult();
        if (sched == null || !sched.Success) { r.Error = "月度采剥接续无效"; return r; }
        if (inp?.Slots == null || inp.Slots.Count == 0) { r.Error = "没有排土位置（先跑「排土条带·潜在排土位置」）"; return r; }
        if (inp.Materials == null || inp.Materials.Length == 0) { r.Error = "没有物料参数（逐层间标签的 ρ/Kr/允许去向）"; return r; }

        // ── 参数体检：非正的 ρ/Kr、负库容一律夹回 + 留条 ────────────────────
        // 不夹会怎样：Kr<0 时 `slot.Used += take*Kr` 让已占变负 ⇒ **库容越排越多**，
        // 而且全程"成功"、每个数看上去都正常。这类静默比崩危险得多。
        var mats = new GapMaterial[inp.Materials.Length];
        for (int g = 0; g < mats.Length; g++)
        {
            var src = inp.Materials[g];
            if (src == null) { mats[g] = null!; continue; }
            double kr = src.Kr, dens = src.Density;
            if (!(kr > 0))
            { r.Warnings.Add($"⚠ {src.Name}：Kr 填的是 {src.Kr:0.###}（须为正），已按 1.15 处理"); kr = 1.15; }
            if (!(dens > 0))
            { r.Warnings.Add($"⚠ {src.Name}：容重填的是 {src.Density:0.###} t/m³（须为正），已按 2.50 处理"); dens = 2.50; }
            mats[g] = ReferenceEquals(kr, src.Kr) && dens == src.Density ? src : new GapMaterial
            {
                Name = src.Name, Code = src.Code, Density = dens, Kr = kr, AllowedDumps = src.AllowedDumps,
            };
        }
        int badCap = inp.Slots.Count(s => s.CapacityM3 < 0);
        if (badCap > 0) r.Warnings.Add($"⚠ {badCap} 个排土位置的库容是负数，已按 0 处理（那些位置排不进东西）");

        foreach (var s in inp.Slots) s.Used = 0;
        // 同一去向内按【级升序 → 同级内 Order】排好：自下而上就是照这个顺序填。
        var byDump = inp.Slots.GroupBy(s => s.DumpName)
                              .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Level).ThenBy(s => s.Order).ToList());

        double totIn = 0, totWork = 0, totInternal = 0, totUnplaced = 0;
        // 内排累计占容（受采空区约束）。它是【跨月累计】的，不是每月一个额度。
        double intUsedDump = 0;
        bool hasIntCap = inp.InternalCumCapM3 is { Length: > 0 };

        foreach (var m in sched.Months)
        {
            var row = new DumpMonthRow { Month = m.Month };
            double intRoomDump = hasIntCap
                ? Math.Max(0, (m.Month < inp.InternalCumCapM3.Length ? inp.InternalCumCapM3[m.Month] : inp.InternalCumCapM3[^1]) - intUsedDump)
                : double.PositiveInfinity;
            var gaps = m.RockByGapM3 ?? Array.Empty<double>();
            for (int g = 0; g < gaps.Length; g++)
            {
                double left = gaps[g];
                if (left <= 1e-6) continue;
                var mat = g < mats.Length ? mats[g] : null;   // 用体检夹过的那份，不用原始输入
                if (mat == null)
                {
                    r.Warnings.Add($"第{m.Month}月 标签{g} 有 {left / 1e4:0.#}万m³ 但没给物料参数 —— 无法配对，如实计为排不下");
                    row.UnplacedM3 += left; continue;
                }

                int guard = 0;
                while (left > 1e-6)
                {
                    if (++guard > inp.Slots.Count + 4) break;
                    var slot = PickSlot(byDump, mat, m.Month, inp.Strategy, inp.HaulProvider, intRoomDump > 1e-6);
                    if (slot == null) break;
                    double haulKm = inp.HaulProvider != null ? inp.HaulProvider(m.Month, slot) : slot.HaulKm;
                    // 库容按 V容 扣：这个位置还能吃下多少【实方】= 剩余占容 ÷ Kr
                    double roomInSitu = slot.Remain / Math.Max(1e-6, mat.Kr);
                    if (slot.IsInternal && !double.IsPositiveInfinity(intRoomDump))
                        roomInSitu = Math.Min(roomInSitu, intRoomDump / Math.Max(1e-6, mat.Kr));   // 采空区还没那么大
                    double take = Math.Min(roomInSitu, left);
                    if (take <= 1e-9) break;
                    slot.Used += take * mat.Kr;
                    if (slot.IsInternal) { intUsedDump += take * mat.Kr; intRoomDump -= take * mat.Kr; }
                    left -= take;

                    var flow = new DumpFlow
                    {
                        Month = m.Month, Gap = g, MaterialName = mat.Name,
                        MaterialCode = mat.Code, Density = mat.Density, Kr = mat.Kr,
                        DumpName = slot.DumpName, Level = slot.Level,
                        Dx = slot.Cx, Dy = slot.Cy, Dz = slot.Cz,
                        InSituM3 = take, DumpM3 = take * mat.Kr, TonnageT = take * mat.Density,
                        HaulKm = haulKm, IsInternal = slot.IsInternal,
                    };
                    row.Flows.Add(flow);
                    row.InSituM3 += take; row.DumpM3 += flow.DumpM3;
                    row.TonnageT += flow.TonnageT; row.TransportWorkTKm += flow.TransportWorkTKm;
                    if (slot.IsInternal) totInternal += take;
                }
                if (left > 1e-6)
                {
                    row.UnplacedM3 += left;
                    r.Warnings.Add($"◆第{m.Month}月 {mat.Name} 尚有 {left / 1e4:0.#}万m³实方"
                                 + $"（{left * mat.Kr / 1e4:0.#}万m³占容）排不下 —— "
                                 + (mat.AllowedDumps.Length > 0 ? $"它只能进 {string.Join("/", mat.AllowedDumps)}；" : "")
                                 + "库容不足或去向未启用");
                }
            }
            row.InternalRatePct = row.InSituM3 > 1e-9
                ? row.Flows.Where(f => f.IsInternal).Sum(f => f.InSituM3) / row.InSituM3 * 100 : 0;
            totIn += row.InSituM3; totWork += row.TransportWorkTKm; totUnplaced += row.UnplacedM3;
            r.Months.Add(row);
        }

        foreach (var kv in byDump) r.RemainByDump[kv.Key] = kv.Value.Sum(s => s.Remain);
        r.TotalInSituM3 = totIn; r.TotalTransportWorkTKm = totWork; r.TotalUnplacedM3 = totUnplaced;
        r.OverallInternalRatePct = totIn > 1e-9 ? totInternal / totIn * 100 : 0;
        r.Success = true;
        return r;
    }

    /// <summary>
    /// 挑一个可用位置。<b>去向</b>按策略选，<b>位置</b>不由策略定 ——
    /// 同一去向内永远是"最低的未满级、级内按顺序"（自下而上，时空约束第 2 条）。
    /// </summary>
    private static DumpSlot? PickSlot(Dictionary<string, List<DumpSlot>> byDump, GapMaterial mat,
                                      int month, PairingStrategy strategy,
                                      Func<int, DumpSlot, double>? haulProvider, bool internalAllowed)
    {
        DumpSlot? best = null; double bestKey = 0;
        foreach (var kv in byDump)
        {
            if (mat.AllowedDumps.Length > 0 && !mat.AllowedDumps.Contains(kv.Key)) continue;   // 允许去向：硬约束
            if (!internalAllowed && kv.Value.Count > 0 && kv.Value[0].IsInternal) continue;    // 采空区已排满
            // 该去向当前的承接面 = 最低的、本月已启用的、还有剩余的那一级
            DumpSlot? head = null;
            int curLevel = int.MinValue;
            foreach (var s in kv.Value)
            {
                if (s.AvailableFromMonth > month) continue;      // 内排未启用
                if (s.Remain <= 1e-9) continue;
                if (head == null) { head = s; curLevel = s.Level; }
                else if (s.Level == curLevel && s.Order < head.Order) head = s;
                if (s.Level > curLevel && head != null) break;   // 已越过承接级，不再看更高的
            }
            if (head == null) continue;

            double hk = haulProvider != null ? haulProvider(month, head) : head.HaulKm;
            double key = strategy switch
            {
                PairingStrategy.InternalFirst => (head.IsInternal ? 0 : 1e6) + hk,     // 内排优先，同类再比运距
                PairingStrategy.LevelCapacity => -kv.Value.Sum(s => s.Remain),         // 剩余最多的先用
                _ => hk,                                                               // 运输功最小
            };
            if (best == null || key < bestKey) { best = head; bestKey = key; }
        }
        return best;
    }

    /// <summary>源—汇流向矩阵 + 库容 + 汇总（命令行 / 报表直接打）。</summary>
    public static string FlowMatrix(DumpAllocationResult r, MonthlyScheduleResult sched)
    {
        if (!r.Success) return "采排配对失败：" + r.Error;
        var sb = new StringBuilder();
        var dumps = r.Months.SelectMany(m => m.Flows).Select(f => f.DumpName).Distinct().OrderBy(x => x).ToList();

        sb.AppendLine("月 |  剥离万m³ | 占容万m³ |  运输功万t·km | 内排% | 排不下万m³ | " + string.Join(" | ", dumps.Select(d => $"{d}万m³")));
        sb.AppendLine(new string('-', 70 + dumps.Count * 12));
        foreach (var m in r.Months)
        {
            var cells = dumps.Select(d => m.Flows.Where(f => f.DumpName == d).Sum(f => f.InSituM3) / 1e4);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,2} | {1,9:0.0} | {2,8:0.0} | {3,12:0.0} | {4,5:0.0} | {5,10:0.0} | ",
                m.Month, m.InSituM3 / 1e4, m.DumpM3 / 1e4, m.TransportWorkTKm / 1e4,
                m.InternalRatePct, m.UnplacedM3 / 1e4)
                + string.Join(" | ", cells.Select(v => $"{v,8:0.0}")));
        }
        sb.AppendLine($"合计: 剥离 {r.TotalInSituM3 / 1e4:0.0}万m³实方 · 运输功 {r.TotalTransportWorkTKm / 1e4:0.0}万t·km"
                    + $" · 内排率 {r.OverallInternalRatePct:0.0}%"
                    + (r.AllPlaced ? " · 全部排下 ✓" : $" · ◆排不下 {r.TotalUnplacedM3 / 1e4:0.0}万m³ ✗"));
        sb.AppendLine("期末剩余库容(万m³占容): " + string.Join(" · ", r.RemainByDump.Select(kv => $"{kv.Key} {kv.Value / 1e4:0.0}")));
        foreach (var w in r.Warnings.Take(8)) sb.AppendLine("  " + w);
        if (r.Warnings.Count > 8) sb.AppendLine($"  …另有 {r.Warnings.Count - 8} 条");
        return sb.ToString();
    }
}
