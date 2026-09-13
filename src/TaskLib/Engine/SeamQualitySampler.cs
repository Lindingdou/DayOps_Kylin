// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/SeamQualitySampler.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;                   // EquipmentDataContext
using PitMine3D.Kylin.Data.Entities;          // Borehole / CoalSample
using PitMine3D.Kylin.TaskLib.Domain;                        // CoalQuality

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  按位置取煤质 —— 「这个煤面采出来的煤，灰分热值硫水是多少」。
//
//  ══ 为什么按位置 ══
//  煤质数据本来就是**空间**的：钻孔在某个坐标上、某一层取了样，测出灰分/热值/硫/水。
//  而作业面的煤质此前只有一条来源：人在「作业面台账」上手填（`FaceQualityDraft`）。
//  没人填 ⇒ 该面无煤质目标 ⇒ **配煤约束整条不跑**，而计划照出、报表照有 ——
//  「按综合灰分重分配采出量」那一步静默变成空操作。
//  钻孔就在图上，面也在图上，中间缺的只是一次按距离的取值。
//
//  ══ 六条口径（Q 组）══
//  **Q1 只取同一煤层的样。** 跨层取样是把另一层的煤质安到这一层上 ——
//      四个数看着都正常，而两层的灰分可以差一倍。层号对不上就是取不到。
//  **Q2 四项齐全才交给引擎。** 与 `FaceQualityDraft` 同一条纪律：半份硬凑时没填的项变成 0，
//      配煤校核里「灰分 ≤ 0+容差」几乎恒假、「热值 ≥ 0−容差」恒真 ——
//      **一个半真半假的约束比没有约束更难查**。
//  **Q3 距离要有上限。** 最近的孔在三公里外时，那个煤质不代表这个面。
//      超限就说取不到，**不是取一个远处的值** —— 取回来的四个数一样正常。
//  **Q4 取原煤不取精煤**（`AdRaw` 而不是 `AdClean`）。采出来的是原煤，
//      洗选之后的灰分系统性更低；取错会让整盘配煤看着都达标。
//  **Q5 报出取样依据**：用了几个孔、最近多远、怎么加权。
//      三个月后有人问「这个面的灰分哪来的」，答案得在库里。
//  **Q6 反距离加权，且权重要有下限保护。** 面正好落在某个孔上时距离为 0，
//      1/d 会是无穷 —— 夹一个最小距离，否则那一个孔会吃掉全部权重（这本身没错），
//      但 NaN 会顺着算下去污染四项全部。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次按位置取煤质的结果。</summary>
public sealed class SeamQualityPick
{
    /// <summary>四项齐全时非空；否则 null（<b>不交半份给引擎</b>）。</summary>
    public CoalQuality? Quality;
    /// <summary>取样依据（用了几个孔 / 最近多远 / 加权方式）。空 = 没取到。</summary>
    public string Provenance = "";
    /// <summary>为什么没取到（取到时为空）。</summary>
    public string Why = "";
    /// <summary>参与加权的孔数。</summary>
    public int HoleCount;
    /// <summary>最近的孔有多远（m）。<b>NaN = 一个孔都没有</b>。</summary>
    public double NearestM = double.NaN;

    public bool Ok => Quality != null;
}

/// <summary>按位置从钻孔煤质样取一个作业面的煤质。永不抛。</summary>
public static class SeamQualitySampler
{
    /// <summary>取样半径缺省 1500m（Q3）。超出这个距离的孔不代表这个面。</summary>
    public const double DefaultRadiusM = 1500;

    /// <summary>最多用几个孔加权。太多会把远处的孔也拉进来，冲淡近孔。</summary>
    public const int DefaultMaxHoles = 5;

    /// <summary>权重的最小距离（m，Q6）—— 面正好落在孔上时防 1/0。</summary>
    private const double MinDistM = 1.0;

    /// <summary>一个带坐标的样（离线判据喂算例用）。</summary>
    public readonly record struct HoleSample(
        string HoleId, double X, double Y,
        double? AshPct, double? CalorificMJkg, double? SulfurPct, double? MoisturePct)
    {
        /// <summary>四项齐全（Q2）。</summary>
        public bool Complete => AshPct.HasValue && CalorificMJkg.HasValue
                             && SulfurPct.HasValue && MoisturePct.HasValue;
    }

    /// <summary>
    /// 取样（<b>离线判据喂算例走这一版</b>）。
    /// </summary>
    /// <param name="x">面的代表点（面积质心）。</param>
    /// <param name="samples">已按煤层筛过的、带坐标的样。</param>
    /// <param name="radiusM">取样半径；&lt;=0 用缺省。</param>
    public static SeamQualityPick PickFrom(double x, double y,
                                           IReadOnlyList<HoleSample>? samples,
                                           double radiusM = 0, int maxHoles = 0)
    {
        var res = new SeamQualityPick();
        double r = radiusM > 0 ? radiusM : DefaultRadiusM;
        int k = maxHoles > 0 ? maxHoles : DefaultMaxHoles;

        var all = (samples ?? Array.Empty<HoleSample>()).ToList();
        if (all.Count == 0) { res.Why = "这一层一个煤质样都没有。"; return res; }

        // Q2：**先筛四项齐全的**。半份样不该参与加权 —— 缺哪一项就在哪一项上拉低样本量，
        //     而四个数出来仍然是四个数，看不出其中一项只用了一个孔。
        var usable = all.Where(s => s.Complete).ToList();
        int partial = all.Count - usable.Count;
        if (usable.Count == 0)
        {
            res.Why = $"这一层有 {all.Count} 个样，但**没有一个四项齐全**"
                    + "（灰分/热值/硫/水缺一不可）—— 半份不交给引擎：缺的项会变成 0，"
                    + "配煤校核里「灰分 ≤ 0+容差」几乎恒假、「热值 ≥ 0−容差」恒真，"
                    + "一个半真半假的约束比没有约束更难查。";
            return res;
        }

        var near = usable
            .Select(s => (S: s, D: Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y))))
            .OrderBy(t => t.D)
            .ToList();

        res.NearestM = near[0].D;

        // Q3：超出半径就是取不到，不取一个远处的值
        var inR = near.Where(t => t.D <= r).Take(k).ToList();
        if (inR.Count == 0)
        {
            res.Why = $"最近的煤质孔在 {near[0].D:N0} m 外（取样半径 {r:N0} m）—— "
                    + "**取不到**。那么远的孔不代表这个面的煤质，"
                    + "而取回来的四个数一样正常，没人看得出来。"
                    + "补法：把半径调大（并知道自己在放宽什么），或在这一带补孔/补样。";
            return res;
        }

        // Q6：反距离加权，距离夹一个下限防 1/0
        double wSum = 0, ash = 0, cv = 0, s2 = 0, mo = 0;
        foreach (var (s, d) in inR)
        {
            double w = 1.0 / Math.Max(MinDistM, d);
            wSum += w;
            ash += w * s.AshPct!.Value;
            cv += w * s.CalorificMJkg!.Value;
            s2 += w * s.SulfurPct!.Value;
            mo += w * s.MoisturePct!.Value;
        }
        if (!(wSum > 0)) { res.Why = "权重和为 0（坐标异常）。"; return res; }

        res.Quality = new CoalQuality
        {
            AshPct = ash / wSum,
            CalorificMJkg = cv / wSum,
            SulfurPct = s2 / wSum,
            MoisturePct = mo / wSum,
        };
        res.HoleCount = inR.Count;
        res.Provenance = $"按位置取自钻孔煤质样：{inR.Count} 个孔反距离加权"
                       + $"（最近 {inR[0].D:N0} m · 最远 {inR[^1].D:N0} m · 半径 {r:N0} m）"
                       + $"，孔号 {string.Join("、", inR.Select(t => t.S.HoleId).Take(5))}"
                       + (partial > 0 ? $"；另有 {partial} 个样四项不全，未参与" : "")
                       + "；取**原煤**口径（Ad/Mad/St,d，不是精煤）";
        return res;
    }

    /// <summary>
    /// 按煤层 + 位置取样（自己去读库）。
    /// </summary>
    /// <param name="seamCode">煤层号（Q1：<b>只取同一层</b>）。</param>
    public static SeamQualityPick Pick(string? seamCode, double x, double y,
                                       double radiusM = 0, int maxHoles = 0)
    {
        var res = new SeamQualityPick();
        string seam = (seamCode ?? "").Trim();
        if (seam.Length == 0)
        {
            res.Why = "这个面没有煤层号 —— **不跨层取样**：另一层的煤质安到这一层上，"
                    + "四个数看着都正常，而两层的灰分可以差一倍。";
            return res;
        }

        try
        {
            var xy = new Dictionary<long, (double X, double Y, string Id)>();
            foreach (var b in EquipmentDataContext.Borehole.All())
                if (b != null) xy[b.Id] = (b.X, b.Y, b.HoleId ?? b.Id.ToString());

            var list = new List<HoleSample>();
            foreach (var s in EquipmentDataContext.CoalQuality.SamplesBySeam(seam))
            {
                if (s == null || !xy.TryGetValue(s.BoreholeId, out var p)) continue;
                // Q4：取**原煤**（Raw），不取精煤（Clean）—— 采出来的是原煤
                list.Add(new HoleSample(p.Id, p.X, p.Y, s.AdRaw, s.QgrD, s.StdRaw, s.MadRaw));
            }
            return PickFrom(x, y, list, radiusM, maxHoles);
        }
        catch (Exception ex)
        {
            res.Why = $"煤质台账读不到（{ex.GetType().Name}）。";
            return res;
        }
    }
}
