using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 【排土场按量推进】给定排弃量，反出排土场的推进形态。
///
/// 与采场侧的「刀量切割 / 量驱动开采模板」完全对称：那边是"给量 → 沿工作线逐刀切进去"，
/// 这边是"给量 → 沿排土线逐带堆出来"。两侧同一个思路：**量是输入，形态是输出**。
///
/// 【为什么不是造一套新几何】排土推进是**一维问题**：形态早就由「排土条带」定死了
/// （<see cref="DumpStripPlanner"/> 的 D1–D6：一级台阶壳子 × 分割长度 × 条带宽度，
/// 同级标高不变、逐带首尾相接）。给定量之后要回答的只有一句话 ——「推到第几带」。
/// 所以本类不碰三角网、不调内核，只沿着 dump_strip 台账的**带序**把量吃下去。
/// 形态 = 已吃掉的那些带的并集，是真台阶壳子，不是"坡顶线等距外扩 d"那种近似。
///
/// 【规则】
///   DA1 量→带：沿推进序累加各带库容，累到 ≥ 本期量为止；最后一带**按比例部分填**，
///       不整带算 —— 整带进位会把一年的排弃量夸大半带，几年下来就是一整级台阶。
///   DA2 口径：入口只认【占容方 V容】。给的是采场剥离【实方】时先 ×Kr 换算，
///       换算只在入口做一次（dump_strip.capacity_m3 存的就是 V容，见该表注释）。
///   DA3 推进距离：**按级**报 d级 = 该级本期排入量 / (该级总走向长 × 该级台阶高)。
///       ★ 各幅是**并肩**推进的，推进距离不能把各幅的带宽加起来 —— 两幅各推 1 带 20m，
///         这一级推进的是 20m 不是 40m。判据 DA3_MultiPanel 就是钉这条的（第一版实现正是加起来的）。
///       另给领先幅的推进 <see cref="DumpAdvanceSlice.LeadAdvanceMByLevel"/>（各幅里推得最远的那幅），
///       以及全场对照值 V容/(L排中位×h排中位) —— 对照值只在"一级、一幅、等宽"时才与 d级 相符。
///   DA4 推进次序：默认【并肩推进】—— 同一带号各幅一起推完再进下一带（现场就是整条排土线
///       一带一带往外长，见 DumpStripPlanner 的同名说明）；可选【逐级排满】。
///       "运距最近优先"不在这里 —— 那是采排配对的策略轴，不是排土场自己的推进次序。
///   DA5 排满：带吃完还有余量 → 明确报 <see cref="DumpAdvanceSlice.OverflowM3"/>，
///       不静默截断、不把余量摊回前面的带（借 D5 的纪律）。
///   DA6 形态：已填带即推进后的形态；每带带着自己的坡顶/坡底轨与图上体 handle，
///       调用方要画就照着画，本类不入图。
///   DA7 不改台账：只算不写。是否把"已填"落回 dump_site / 库容账，由调用方决定 ——
///       一个"看一眼"的按钮顺手改掉库容账，是最难查的那种账错。
///   DA8 多期：<see cref="AdvanceSeries"/> 逐期接着上一期的水位往下吃，
///       于是中长远的逐年排弃量能直接反出**逐年排土形态**。
/// </summary>
public static class DumpAdvanceByVolume
{
    /// <summary>推进次序（DA4）。</summary>
    public enum Order
    {
        /// <summary>并肩推进（默认）：同一带号的各幅、各级一起推完，再进下一带。</summary>
        StepAbreast,
        /// <summary>逐级排满：一级的全部带排完再上一级。</summary>
        LevelByLevel,
    }

    /// <summary>一带被填的记录（Fraction 小于 1 = 本期只填了一部分，下期接着填）。</summary>
    public readonly record struct Fill(DumpStrip Strip, double FillM3, double Fraction)
    {
        public bool IsPartial => Fraction < 0.999;
    }

    /// <summary>一期的推进结果。</summary>
    public sealed class DumpAdvanceSlice
    {
        public string PeriodLabel = "";
        /// <summary>本期要排的量（占容方 m³）。</summary>
        public double RequestedM3;
        /// <summary>实际排下的（占容方 m³）。</summary>
        public double PlacedM3;
        /// <summary>排不下的余量（DA5；大于 0 = 这个排土场本期排满了）。</summary>
        public double OverflowM3;
        public bool SiteFull => OverflowM3 > 1e-6;

        public List<Fill> Fills = new();

        /// <summary>本期推进到的位置（级/带；空期为 0）。</summary>
        public int TopLevel, TopStep;
        /// <summary>逐级推进距离 m（DA3：该级本期排入量 ÷ 该级总走向长 ÷ 台阶高 —— 各幅并肩，不相加）。</summary>
        public Dictionary<int, double> AdvanceMByLevel = new();
        /// <summary>逐级【领先幅】的推进 m（各幅里推得最远的那幅；与 AdvanceMByLevel 差得多 = 各幅推得不齐）。</summary>
        public Dictionary<int, double> LeadAdvanceMByLevel = new();
        /// <summary>逐级本期排入量 m³（占容方）。</summary>
        public Dictionary<int, double> PlacedByLevel = new();
        /// <summary>对照值 m：V容/(L排×h排)。只作对照，不是结果（DA3）。</summary>
        public double EquivalentAdvanceM;

        /// <summary>本期形态涉及的带数（含部分填的那一带）。</summary>
        public int TouchedCells => Fills.Count;
        /// <summary>本期最大级推进距离 m（报表里"排土场推进了多少"通常指它）。</summary>
        public double MaxAdvanceM => AdvanceMByLevel.Count == 0 ? 0 : AdvanceMByLevel.Values.Max();
    }

    /// <summary>整个推进过程（多期）。</summary>
    public sealed class DumpAdvanceResult
    {
        public bool Ok;
        public string Message = "";
        public List<DumpAdvanceSlice> Slices = new();
        public List<string> Notes = new();

        /// <summary>排土场总库容 / 已填（占容方 m³）。</summary>
        public double TotalCapacityM3, PlacedM3;
        public double RemainingM3 => Math.Max(0, TotalCapacityM3 - PlacedM3);
        public double TotalOverflowM3 => Slices.Sum(s => s.OverflowM3);
    }

    /// <summary>
    /// 单期推进：把 <paramref name="volumeM3"/> 排进 <paramref name="strips"/>，返回本期形态。
    /// </summary>
    /// <param name="strips">一个排土场的潜在排土位置（dump_strip 台账；空 = 没切过条带）。</param>
    /// <param name="volumeM3">本期排弃量。<paramref name="swellKr"/> 大于 1 时按实方进、乘 Kr 换成占容方（DA2）。</param>
    /// <param name="swellKr">残余膨胀系数；小于等于 1 表示给的已经是占容方。</param>
    /// <param name="order">推进次序（DA4）。</param>
    /// <param name="alreadyFilledM3">起始水位（占容方 m³）——多期滚账时上期的累计已填。</param>
    public static DumpAdvanceResult Advance(IReadOnlyList<DumpStrip>? strips, double volumeM3,
        double swellKr = 1.0, Order order = Order.StepAbreast, double alreadyFilledM3 = 0)
        => AdvanceSeries(strips, new[] { ("本期", volumeM3) }, swellKr, order, alreadyFilledM3);

    /// <summary>
    /// 多期推进（DA8）：逐期接着上一期的水位往下吃 —— 中长远的逐年排弃量喂进来，
    /// 反出的就是**逐年排土形态**（每期的 Fills 即该年新增的那部分排土体）。
    /// </summary>
    public static DumpAdvanceResult AdvanceSeries(IReadOnlyList<DumpStrip>? strips,
        IReadOnlyList<(string Label, double VolumeM3)>? periods,
        double swellKr = 1.0, Order order = Order.StepAbreast, double alreadyFilledM3 = 0)
    {
        var r = new DumpAdvanceResult();
        if (strips == null || strips.Count == 0)
        {
            r.Message = "这个排土场还没有潜在排土位置 —— 先用「排土条带」把它切一遍（按量推进是【吃】条带的，不造条带）";
            return r;
        }
        if (periods == null || periods.Count == 0) { r.Message = "没有给排弃量"; return r; }

        double kr = swellKr > 1e-6 ? swellKr : 1.0;
        var seq = Sort(strips, order);
        r.TotalCapacityM3 = seq.Sum(s => s.CapacityM3);

        // 各级的正面长度（Σ各幅走向长）与台阶高 —— DA3 的分母，全场固定，逐期不变
        var frontageByLevel = strips.GroupBy(s => (int)s.LevelIndex).ToDictionary(
            g => g.Key,
            g => g.GroupBy(x => (int)x.PanelIndex).Sum(pg => pg.Max(x => x.StrikeLenM)));
        var benchHByLevel = strips.GroupBy(s => (int)s.LevelIndex).ToDictionary(
            g => g.Key,
            g => g.Select(x => x.BenchHeightM).Where(v => v > 0).DefaultIfEmpty(0).Max());

        // 起始水位：把已填的量先在带序上"走掉"，本期从水位处接着填（DA8）
        int ptr = 0;
        double usedInCell = 0;
        double waterline = Math.Max(0, alreadyFilledM3);
        while (ptr < seq.Count && waterline > 1e-6)
        {
            double cap0 = Math.Max(0, seq[ptr].CapacityM3);
            if (waterline >= cap0 - 1e-9) { waterline -= cap0; ptr++; }
            else { usedInCell = waterline; waterline = 0; }
        }
        r.PlacedM3 = Math.Min(Math.Max(0, alreadyFilledM3), r.TotalCapacityM3);
        if (waterline > 1e-6)
            r.Notes.Add($"起始已填 {alreadyFilledM3:N0} m³ 超过本场总库容 {r.TotalCapacityM3:N0} m³，超出的 {waterline:N0} m³ 不在本场账内");

        foreach (var (label, vol) in periods)
        {
            var slice = new DumpAdvanceSlice { PeriodLabel = label };
            double need = kr > 1.0 ? vol * kr : vol;      // DA2：实方 → 占容方，只换这一次
            slice.RequestedM3 = need;
            var perPanel = new Dictionary<(int Level, int Panel), double>();

            while (need > 1e-6 && ptr < seq.Count)
            {
                var cell = seq[ptr];
                double cap = Math.Max(0, cell.CapacityM3);
                double room = cap - usedInCell;
                if (room <= 1e-9) { ptr++; usedInCell = 0; continue; }

                double take = Math.Min(room, need);       // DA1：最后一带按比例部分填
                double frac = cap > 1e-9 ? take / cap : 0;
                slice.Fills.Add(new Fill(cell, take, frac));

                // DA3：先按（级,幅）攒各幅自己的推进，级层面的 d 在本期结束后统一算 ——
                // 各幅并肩推进，级的推进不是各幅之和
                int lv = (int)cell.LevelIndex;
                var key = (lv, (int)cell.PanelIndex);
                perPanel.TryGetValue(key, out double hadPanel);
                perPanel[key] = hadPanel + cell.StripWidthM * frac;
                slice.PlacedByLevel.TryGetValue(lv, out double hadVol);
                slice.PlacedByLevel[lv] = hadVol + take;
                if (lv >= slice.TopLevel) { slice.TopLevel = lv; slice.TopStep = (int)cell.StepIndex; }

                need -= take; usedInCell += take; slice.PlacedM3 += take;
                if (usedInCell >= cap - 1e-9) { ptr++; usedInCell = 0; }
            }

            slice.OverflowM3 = Math.Max(0, need);         // DA5：排不下就报出来，不摊回去
            slice.EquivalentAdvanceM = EquivalentAdvance(slice.PlacedM3, seq);

            // DA3：级的推进 = 本级排入量 /(本级总走向长 × 台阶高)。分母取【全级】的正面长度，
            // 不只取本期动过的那几幅 —— 只有一幅动了的话，这一级平均就是推进了那么一点点。
            foreach (var (lv, volLv) in slice.PlacedByLevel)
            {
                double frontage = frontageByLevel.TryGetValue(lv, out var fr) ? fr : 0;
                double hLv = benchHByLevel.TryGetValue(lv, out var hh) ? hh : 0;
                slice.AdvanceMByLevel[lv] = frontage > 1e-6 && hLv > 1e-6
                    ? Math.Round(volLv / (frontage * hLv), 4) : 0;
            }
            foreach (var g in perPanel.GroupBy(kv => kv.Key.Level))
                slice.LeadAdvanceMByLevel[g.Key] = g.Max(kv => kv.Value);
            r.PlacedM3 += slice.PlacedM3;
            r.Slices.Add(slice);
        }

        r.Ok = true;
        double over = r.TotalOverflowM3;
        r.Message = over > 1e-6
            ? $"排满：本场总库容 {r.TotalCapacityM3:N0} m³ 已填满，还有 {over:N0} m³ 排不下 —— 需另找去向或扩场"
            : $"已排入 {r.Slices.Sum(s => s.PlacedM3):N0} m³（占容方），剩余库容 {r.RemainingM3:N0} m³";
        return r;
    }

    /// <summary>DA3 的对照值：V容/(L排×h排)。L排 取各带走向长的中位、h排 取台阶高的中位。</summary>
    private static double EquivalentAdvance(double volM3, IReadOnlyList<DumpStrip> seq)
    {
        if (volM3 <= 1e-6 || seq.Count == 0) return 0;
        double l = Median(seq.Select(s => s.StrikeLenM).Where(v => v > 0).ToList());
        double h = Median(seq.Select(s => s.BenchHeightM).Where(v => v > 0).ToList());
        return l > 1e-6 && h > 1e-6 ? volM3 / (l * h) : 0;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>DA4：推进次序。级序 1 = 最上一级，所以"自下而上"是 LevelIndex 降序。</summary>
    public static List<DumpStrip> Sort(IReadOnlyList<DumpStrip> strips, Order order) => order switch
    {
        // 并肩推进：先把所有幅、所有级的第 1 带推完，再进第 2 带（整条排土线一带一带往外长）
        Order.StepAbreast => strips.OrderBy(s => s.StepIndex)
                                   .ThenByDescending(s => s.LevelIndex)
                                   .ThenBy(s => s.PanelIndex).ThenBy(s => s.SubIndex).ToList(),
        // 逐级排满：一级推到边界再上一级
        _ => strips.OrderByDescending(s => s.LevelIndex)
                   .ThenBy(s => s.StepIndex).ThenBy(s => s.PanelIndex).ThenBy(s => s.SubIndex).ToList(),
    };
}
