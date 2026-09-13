// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/SinkFromLedgerBuilder.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  采掘单元台账 → 去向台账（排土场那一族）。
//
//  ══ 为什么要有它 ══
//  去向台账原来靠 V004 的两条**种子**撑着（北排土场 12000/8500、内排土场 5000/3200），
//  而真实的排土场清单一直躺在**采掘单元台账**里 —— 排土条带产出的位置行，
//  每一条都带着场名与库容。两边从来没对过账，代价是：
//    · 名字对不上（台账「内排土场1」vs 种子「内排土场」）⇒ 绑定按名字精确配，
//      17 个排土面**整月零入方**，而甘特上只是少几行、不报错；
//    · 库容差 40 倍（真实 1291.1 万m³ vs 种子 17000 万m³）⇒ 库容闸等于没闸。
//
//  ══ 名字为什么天然对齐 ══
//  场名直接取台账里那一列（`Row.Region`），**不做任何清洗、不做任何猜测**。
//  绑定端读的也是这一列 —— 同一个源，就不会差字符。
//
//  ══ 哪些派生得出来，哪些派生不出来 ══
//  · **派生得出来**：场名、设计容量（Σ库容，占容方口径）、台阶高（位置行的中位数）。
//  · **派生不出来**：已填、坐标、通过能力、工作线长、兜底运距、开放时窗。
//    它们是现场的事实，台账里没有。**一律留空/留 0，不给缺省** ——
//    编一个出来会让运距、库容告警、时窗全都看着正常而实际是假的。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>从台账派生出来的一个排土场。</summary>
public sealed class LedgerSink
{
    /// <summary>场名 —— <b>原样取自台账的「采场/排土场」列</b>，绑定端读的是同一列。</summary>
    public string Name = "";
    /// <summary>设计容量（m³，<b>占容方 V容</b>）= 这个场名下所有排土位置的库容之和。</summary>
    public double CapacityM3;
    /// <summary>位置行数（这个场被切成了几个幅·带）。</summary>
    public int SlotCount;
    /// <summary>台阶高（m）—— 位置行的中位数；一个都取不到时为 0（= 没有，不是"平的"）。</summary>
    public double BenchHeightM;

    /// <summary>
    /// 代表点（**按库容加权的质心**，取自台账里各排土位置的 Cx/Cy/Cz）。
    /// <para>
    /// <b>它不是实测的卸点位置</b>，是这个场的重心 —— 但有它，运距就能走路网实算，
    /// 而不是整条落到兜底运距（兜底一变，循环时间、配车数、编组班产跟着一起偏）。
    /// 派生来源要标出来，别让人以为这是测量点。
    /// </para>
    /// <para>三个都为 0 = 台账里那些位置行没有坐标（不是"在原点"）。</para>
    /// </summary>
    public double X, Y, Z;

    /// <summary>有没有算出代表点（X、Y 至少一个非 0；Z=0 是合法标高）。</summary>
    public bool HasPoint => Math.Abs(X) > 1e-9 || Math.Abs(Y) > 1e-9;

    /// <summary>
    /// 工作线长（m）—— <b>按几何推</b>：这一批块体质心两两之间的最远距离。
    /// <para>
    /// 排土条带是沿工作线并排铺开的，两端块体的间距就是这条线的长度（单块时退回块长）。
    /// 它决定同时能摆几台推土机、几个卸点 —— 编组与卸点能力都要用它。
    /// <b>0 = 推不出来</b>（没有坐标），不是"线长为 0"。
    /// </para>
    /// </summary>
    public double WorkLineLengthM;

    /// <summary>代表点与线长是**按哪一批块**算的（本期在排 / 全场）——文案要说清楚。</summary>
    public string PointBasis = "";
}

/// <summary>一次派生的结果。</summary>
public sealed class SinkFromLedgerResult
{
    public List<LedgerSink> Sinks = new();
    public List<string> Notes = new();
    public string Headline = "";
    public bool Ok => Sinks.Count > 0;
}

/// <summary>采掘单元台账 → 排土场清单。纯计算，不碰库。</summary>
public static class SinkFromLedgerBuilder
{
    /// <summary>
    /// 按场名把排土位置归并成排土场。
    /// </summary>
    /// <param name="rows">台账行（基表或某一期）。<b>只看排土位置</b>，采场单元不参与。</param>
    public static SinkFromLedgerResult Build(IEnumerable<MiningUnitLedger.Row>? rows)
    {
        var res = new SinkFromLedgerResult();
        var all = (rows ?? Array.Empty<MiningUnitLedger.Row>()).Where(r => r != null).ToList();

        var slots = all.Where(r => r.Kind == LedgerKind.Dump).ToList();
        if (slots.Count == 0)
        {
            res.Headline = "◆ 台账里**一个排土位置都没有** —— 派生不出排土场。"
                         + "先跑一次「排土条带」把排土位置生成出来（它才是排土场清单的真源）。";
            return res;
        }

        int noName = 0, noCap = 0;
        foreach (var g in slots.GroupBy(r => (r.Region ?? "").Trim(), StringComparer.Ordinal)
                               .OrderByDescending(g => g.Count()))
        {
            if (g.Key.Length == 0) { noName += g.Count(); continue; }

            // 库容口径 = 占容方 V容（D6）。取不到的行单独计数，**不按 0 算**：
            // 按 0 算会让这个场的容量凭空缩水，而库容告警照常"正常"。
            var caps = g.Select(r => r.DumpCapM3 ?? 0).Where(v => v > 1e-9).ToList();
            noCap += g.Count() - caps.Count;

            var hs = g.Select(r => r.ZHi - r.ZLo).Where(h => h > 1e-6).OrderBy(h => h).ToList();

            // 代表点 = **按库容加权的质心**（大幅的位置说了算；等权会被一堆小幅拉偏）。
            // 只用有坐标的行；一行都没有就留 0（= 没有坐标，不是"在原点"）。
            var pts = g.Where(r => Math.Abs(r.Cx) > 1e-9 || Math.Abs(r.Cy) > 1e-9).ToList();
            double wsum = pts.Sum(r => Math.Max(1e-9, r.DumpCapM3 ?? 1));
            double cx = 0, cy = 0, cz = 0;
            if (pts.Count > 0 && wsum > 1e-9)
            {
                foreach (var r in pts)
                {
                    double w = Math.Max(1e-9, r.DumpCapM3 ?? 1);
                    cx += r.Cx * w; cy += r.Cy * w; cz += r.Cz * w;
                }
                cx /= wsum; cy /= wsum; cz /= wsum;
            }

            // 工作线长 = 这批块体质心两两之间的最远距离（单块时退回块长）
            double span = 0;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double dx = pts[i].Cx - pts[j].Cx, dy = pts[i].Cy - pts[j].Cy;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > span) span = d;
                }
            if (span <= 1e-6 && pts.Count == 1) span = Math.Max(0, pts[0].LengthM);

            res.Sinks.Add(new LedgerSink
            {
                Name = g.Key,
                CapacityM3 = caps.Sum(),
                SlotCount = g.Count(),
                X = cx, Y = cy, Z = cz,
                WorkLineLengthM = Math.Round(span, 1),
                BenchHeightM = hs.Count == 0 ? 0 : hs[hs.Count / 2],
            });
        }

        if (noName > 0)
            res.Notes.Add($"◆ {noName} 个排土位置**没有场名** —— 它们归不到任何排土场，本次不派生。"
                        + "场名是绑定用的钥匙（绑定按名字精确配），空着的话这些位置的量永远进不了去向。");
        if (noCap > 0)
            res.Notes.Add($"◆ {noCap} 个排土位置**没有库容**，没有计入设计容量 —— "
                        + "这不是按 0 算，是这几条根本没这笔数；容量因此偏小，别当成「这个场就这么大」。");

        res.Notes.Add("· 设计容量口径 = **占容方 V容**（Σ 位置库容），与排产扣库容用的是同一本账。");
        res.Notes.Add("◆ **已填 / 坐标 / 通过能力 / 工作线长 / 兜底运距 / 开放时窗派生不出来** —— "
                    + "台账里没有这些，它们是现场的事实。一律留空，**不给缺省值**："
                    + "编一个出来会让运距、库容告警、时窗全都看着正常而实际是假的。"
                    + "已填要录走「盘点修正」；坐标在「☑ 坐标与时窗」那几列补。");

        res.Headline = $"从台账派生出 {res.Sinks.Count} 个排土场："
                     + string.Join("、", res.Sinks.Select(s =>
                           $"{s.Name}（{s.SlotCount} 个位置 · 库容 {s.CapacityM3 / 1e4:0.#} 万m³"
                         + (s.BenchHeightM > 0 ? $" · 台阶高 {s.BenchHeightM:0.#}m" : "") + "）"));
        return res;
    }

    /// <summary>
    /// 场名 → 去向编号。<b>稳定且可逆</b>：同一个场名永远得到同一个 id，重建不会长出重复行。
    /// <para>用 <c>DL-</c> 前缀标明"从台账派生"，与人工建的（<c>D-</c>）分得开。</para>
    /// </summary>
    public static string IdOf(string name)
    {
        string s = (name ?? "").Trim();
        if (s.Length == 0) return "";
        // 场名里可能有中文/符号，取一个稳定哈希做后缀，避免 id 里出现非法字符
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return "DL-" + (h % 100000).ToString("00000");
        }
    }
}
