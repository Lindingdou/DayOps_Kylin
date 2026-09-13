// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/RegionAdvanceAzimuth.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.TaskLib.Adjust;

// ─────────────────────────────────────────────────────────────────────────────
//  推进方位角 —— 让轮廓沿**真实推进方向**平行推进，而不是全周等距内缩。
//
//  ── 数从哪来 ──
//  采掘单元台账（`MiningUnitLedger.Row`）每一行都带质心 (Cx,Cy) 与月内推进序 Seq。
//  把落在某块区域轮廓内的单元按 Seq 排开，**前半段质心 → 后半段质心**这个向量
//  就是那块地实际的推进走位。这不是估的，是排产自己给出的顺序。
//
//  ── 为什么不用台账里的 AzimuthDeg ──
//  那一列是**走向**（单元长轴朝向），不是推进方向，两者差 90°；而且它的角度约定
//  （数学角还是罗盘角、正北起还是正东起）本包没有独立证据，猜错就是整体转 90°，
//  而画面上看起来仍然"像那么回事"。走位向量是在世界 XY 里直接 atan2 出来的，
//  与 <see cref="RingOffset.Offset"/> 的 azimuth 约定（0°=+X，90°=+Y，逆时针正）
//  是同一套，不存在换算。**宁可少用一列，也不引入一个验不了的约定。**
//
//  ── 解不出就不用 ──
//  单元不足 2 个 / 全落在区域外 / 走位长度退化 ⇒ Resolved=false，
//  调用方退回全周等距并如实说明。不拿区域长轴之类的几何猜一个方向出来。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一块区域的推进方位解算结果。</summary>
public sealed class AdvanceAzimuthInfo
{
    /// <summary>解出来了（false ⇒ 调用方必须退回全周等距）。</summary>
    public bool Resolved { get; set; }
    /// <summary>推进方位角（度；0=+X，90=+Y，逆时针正 —— 与 RingOffset 同一套）。</summary>
    public double AzimuthDeg { get; set; }
    /// <summary>落在该区域内的单元数。</summary>
    public int UnitCount { get; set; }
    /// <summary>走位向量长度 m（太短说明这些单元挤在一起，方向不可信）。</summary>
    public double TravelM { get; set; }
    /// <summary>用哪一档定的序。<see cref="RegionAdvanceAzimuth.OrderKey.ModelBand"/> 说明推进序没人排过。</summary>
    public RegionAdvanceAzimuth.OrderKey OrderedBy { get; set; } = RegionAdvanceAzimuth.OrderKey.PlannedSeq;
    /// <summary>来源 / 降级说明，界面直接显示。</summary>
    public string SourceLabel { get; set; } = "";

    public static AdvanceAzimuthInfo No(string why) => new() { Resolved = false, SourceLabel = why };
}

public static class RegionAdvanceAzimuth
{
    /// <summary>走位向量短于这个长度就不认（单元挤在一堆，方位是噪声）。</summary>
    public const double MinTravelM = 20.0;
    /// <summary>至少要这么多个单元才谈得上"顺序"。</summary>
    public const int MinUnits = 2;

    /// <summary>
    /// 解一块区域的推进方位。<paramref name="rows"/> 为 null 时从采掘单元台账目录现读。
    /// 永不抛。
    /// </summary>
    public static AdvanceAzimuthInfo Resolve(SimRegion? region, IReadOnlyList<MiningUnitLedger.Row>? rows = null)
    {
        if (region == null || region.Ring.Count < 3)
            return AdvanceAzimuthInfo.No("区域没有可用轮廓 ⇒ 判不了推进方位，按全周等距。");

        if (region.Synthetic)
            return AdvanceAzimuthInfo.No(
                "本区轮廓是**示意图形**（坐标是本包生成的），拿台账单元去套它没有意义 ⇒ 按全周等距。");

        IReadOnlyList<MiningUnitLedger.Row> all;
        try { all = rows ?? LoadLedger(); }
        catch { return AdvanceAzimuthInfo.No("采掘单元台账读取失败 ⇒ 判不了推进方位，按全周等距。"); }

        if (all.Count == 0)
            return AdvanceAzimuthInfo.No(
                "采掘单元台账里一行都没有 ⇒ 判不了推进方位，按全周等距。"
                + "（在「短期进度计划」里排一次采掘单元即可。）");

        // ── 落在本区轮廓内的单元 ──
        //  用几何包含，不用名字 —— 作业面名与区域名对不上是常态，而质心是绝对坐标，不会漂。
        var inside = all.Where(r => r != null && InRing(region.Ring, r.Cx, r.Cy)).ToList();
        if (inside.Count < MinUnits)
            return AdvanceAzimuthInfo.No(
                $"落在本区轮廓内的采掘单元只有 {inside.Count} 个（需要 ≥{MinUnits}）⇒ 判不了推进方位，按全周等距。"
                + $"（台账共 {all.Count} 个单元，质心都不在这块轮廓里 —— 多半是两者不在同一套坐标系。）");

        // ── 定序 ──
        //  ⚠ Seq（推进序）是**人填/算法填**的计划列：FromStrips / FromDumpCells 都不设它。
        //    新算出来的台账里整表 Seq 全是 0，此时按 Seq 排序会退化成 CSV 行序 ——
        //    再拿那个偶然顺序算方位，就是给出一个假的方向，而画面上看着完全正常。
        //    所以定序键**必须真的有变化**才认，否则宁可不解。
        var (ordered, orderBy) = Order(inside);
        if (ordered == null)
            return AdvanceAzimuthInfo.No(
                $"本区内这 {inside.Count} 个单元的**推进序全都一样**（推进序是人填/算法填的计划列，"
                + "模型新算出来的台账整表都是 0）⇒ 排不出先后，也就推不出方位 ⇒ 按全周等距。"
                + "在「采掘单元台账」里把推进序排一遍即可启用平行推进。"
                + "（**不退用带号序**：真台账 A/B 实测两者能差 125°，带号是几何生成序不是推进顺序。）");

        // 前半段质心 → 后半段质心。比"首个 → 末个"稳：单个单元的质心受幅宽影响大，
        // 取半程均值把这种抖动摊掉。
        int half = ordered.Count / 2;
        var a = Centroid(ordered.Take(half).ToList());
        var b = Centroid(ordered.Skip(ordered.Count - half).ToList());

        double dx = b.X - a.X, dy = b.Y - a.Y;
        double travel = Math.Sqrt(dx * dx + dy * dy);

        if (travel < MinTravelM)
            return AdvanceAzimuthInfo.No(
                $"这 {inside.Count} 个单元的走位只有 {travel:0.#} m（阈值 {MinTravelM:0} m）——"
                + "它们基本挤在一处，推不出方向 ⇒ 按全周等距。");

        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (deg < 0) deg += 360;

        return new AdvanceAzimuthInfo
        {
            Resolved = true,
            AzimuthDeg = deg,
            UnitCount = inside.Count,
            TravelM = travel,
            OrderedBy = orderBy,
            SourceLabel = $"推进方位 {deg:0.#}°（0°=东、90°=北、逆时针正）"
                        + $"：由本区内 {inside.Count} 个采掘单元按「{OrderLabel(orderBy)}」的走位反算，"
                        + $"半程质心位移 {travel:0.#} m。台账的走向列（AzimuthDeg）**未参与** —— "
                        + "那是长轴朝向不是推进方向，且角度约定本包无独立证据。",
        };
    }

    /// <summary>
    /// 定序依据。<b>现在只有一档</b> —— 人排的推进序。
    ///
    /// <para><b>曾经有过第二档「模型带号/幅号」，被真台账 A/B 证伪后删掉了</b>
    /// （<c>RealAzimuthDiagnosticTests</c>，2026-08-11）：同一份台账上两档给出的方位
    /// 2016-08 差 25.4°、<b>2026-08 差 125.2°</b>。带号是几何生成序，真实推进会跳带、会折返，
    /// 两者根本不是一回事。留着它等于在"推进序没人排"时把轮廓推向一个错方向，
    /// 而画面上照样平行推进、看着完全正常 —— 这比不推进糟得多。</para>
    /// </summary>
    public enum OrderKey
    {
        /// <summary>人排的推进序（<c>Seq</c>）。这是唯一被真数据支持的定序依据。</summary>
        PlannedSeq,
    }

    internal static string OrderLabel(OrderKey k) => "人排的月内推进序（台账 Seq 列）";

    /// <summary>
    /// 定序。<c>Seq</c> 必须**真的有变化**才认 —— 它是人填/算法填的计划列，
    /// 模型新算出来的台账整表都是 0，此时按它排序会退化成 CSV 行序。
    /// 排不出先后就返回 null，让调用方拒绝解算（退全周等距）。
    /// </summary>
    private static (List<MiningUnitLedger.Row>? Ordered, OrderKey Key) Order(List<MiningUnitLedger.Row> rows)
    {
        if (rows.Select(r => r.Seq).Distinct().Count() > 1)
            return (rows.OrderBy(r => r.Seq).ToList(), OrderKey.PlannedSeq);
        return (null, OrderKey.PlannedSeq);
    }

    /// <summary>
    /// 读**最新那一个月**的台账行。
    ///
    /// <para><b>刻意不合并所有月份。</b>台账目录里可能躺着<b>两次不同排产</b>的结果 ——
    /// 实测这台机器上 <c>2016-08</c> 与 <c>2026-08</c> 各 190+ 行、单元号几乎不重合，
    /// 那是两个方案而不是相邻两期。把它们排成一条时间轴，推出来的"走位"是从
    /// 方案 A 的位置指向方案 B 的位置，跟推进毫无关系，而画面上完全看不出来。
    /// 同一条纪律见 <c>SimBuilder.BuildPeriodic</c> 的 onlyPeriod 过滤。</para>
    /// </summary>
    private static List<MiningUnitLedger.Row> LoadLedger()
    {
        var list = new List<MiningUnitLedger.Row>();
        var store = new MonthlyUnitLedgerStore();
        string? latest = store.ListMonths().OrderBy(x => x, StringComparer.Ordinal).LastOrDefault();
        if (latest == null) return list;

        if (!store.TryLoad(latest, out var rows, out _) || rows == null) return list;
        foreach (var r in rows)
        {
            if (r == null) continue;
            if (string.IsNullOrWhiteSpace(r.Period)) r.Period = latest;
            list.Add(r);
        }
        return list;
    }

    private static (double X, double Y) Centroid(List<MiningUnitLedger.Row> rows)
    {
        if (rows.Count == 0) return (0, 0);
        // 按体积加权：一个 20 万方的幅和一个 2 万方的幅对「推到哪了」的贡献不是一回事。
        // 取不到体积（老格式行）就退回不加权 —— 不加权也是对的，只是稍粗。
        double w = rows.Sum(VolOf);
        if (w <= 1e-6) return (rows.Average(r => r.Cx), rows.Average(r => r.Cy));
        return (rows.Sum(r => r.Cx * VolOf(r)) / w,
                rows.Sum(r => r.Cy * VolOf(r)) / w);
    }

    /// <summary>
    /// 一行的体积口径 m³。三类台账各填各的列（采煤 CoalM3 / 剥岩 GrossM3 / 排土 DumpCapM3），
    /// 都空时退流向合计。<b>不碰 Qty</b> —— 那一列煤是吨、岩是方，混在一起加权就是把吨当方用。
    /// </summary>
    private static double VolOf(MiningUnitLedger.Row r)
    {
        double v = r.GrossM3 ?? r.CoalM3 ?? r.DumpCapM3 ?? 0;
        if (v <= 1e-6) v = r.FlowSumM3;
        return Math.Max(0, v);
    }

    /// <summary>点在闭合环内（射线法）。边界上算内。</summary>
    internal static bool InRing(IReadOnlyList<SimPoint> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring[i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
            if ((yi > y) != (yj > y) &&
                x < (xj - xi) * (y - yi) / (yj - yi + (Math.Abs(yj - yi) < 1e-15 ? 1e-15 : 0)) + xi)
                inside = !inside;
        }
        return inside;
    }
}
