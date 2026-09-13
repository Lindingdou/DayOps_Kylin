// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/TaskZoneSplitter.cs（逐行对应；仅命名空间适配）
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Dump;   // DumpStripPlanner.Cell（Kylin 侧已按原版移植，见 Cad/Dump）

// ─────────────────────────────────────────────────────────────────────────────
//  智能区划：把一个采掘单元<b>沿推进方向按量切成若干任务区域</b>。
//
//  ══ 要解决的是什么 ══
//  作业区域那一层给的是**采场级**的大区（一个面一块地）。日计划要的不是它 ——
//  日计划要的是「这一块地，这台设备干这几天」。一个采掘单元一个月推 W 米，
//  排产把这 W 米分给了几台设备的几段工日；**任务区域就是那 W 米上的一段**。
//  一块任务区可以作业多日（一段连续工日 = 一块地），这正是它与「日」不是一一对应的原因。
//
//  ══ 这是一维问题，不是布尔运算 ══
//  单元体是「前脸轨 + 沿推进方向偏 W 的后界轨」竖向拉伸出来的。
//  按量切，切的就是**推进方向上的一个宽度区间 [w0, w1]** —— 用同一对轨换两个偏移量重取，
//  不是把已经围好的占地环再切一刀（环在凹弯处会自交，切出来的两半都不成立）。
//
//  ══ 最容易错的那一条（K1）══
//  **量与宽度不成正比**。只有直轨上才成立：弯轨往外偏时，同样 1 米宽扫过的面积更大
//  （凸侧）或更小（凹侧）。按宽度线性分下去，每一块的面积都对不上它该有的量 ——
//  而每一块画出来都是规规矩矩的带，图上完全看不出来。
//  所以这里按**面积**反求宽度（面积对 w 单调，二分即可），并把它与线性分的差报出来：
//  差得小说明这条轨挺直，差得大说明线性分本来就会错那么多。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>切出来的一块任务区域。</summary>
public sealed class TaskZoneSlice
{
    /// <summary>调用方给的标识（一台设备的一段连续工日、一个班组…随调用方定义）。</summary>
    public string Key = "";
    /// <summary>本块承担的量（原样带回，便于对账）。</summary>
    public double VolumeM3;

    /// <summary>推进方向上的宽度区间（m，从前脸算起）。</summary>
    public double W0, W1;
    public double WidthM => Math.Max(0, W1 - W0);

    /// <summary>平面占地环（扁平 [x,y,...]，隐式闭合）。</summary>
    public double[] RingXy = Array.Empty<double>();
    /// <summary>本块的平面面积（鞋带，m²）。</summary>
    public double AreaM2;

    /// <summary>按<b>线性分宽</b>本该落在哪儿 —— 与 <see cref="W1"/> 的差就是 K1 那条的量级。</summary>
    public double LinearW1;
    public double LinearDeltaM => Math.Abs(W1 - LinearW1);

    public bool Ok => RingXy.Length >= 6 && AreaM2 > 1e-6;
}

/// <summary>一次切分的结果。永不抛。</summary>
public sealed class TaskZoneSplit
{
    public List<TaskZoneSlice> Slices = new();
    public List<string> Notes = new();
    public string Why = "";
    /// <summary>整个单元的平面面积（切完之和应该等于它）。</summary>
    public double TotalAreaM2;
    public bool Ok => Slices.Count > 0;
}

/// <summary>采掘单元 → 任务区域。纯几何 + 量，不碰数据库、不碰 GUI。</summary>
public static class TaskZoneSplitter
{
    /// <summary>二分求宽度的相对精度（面积占比）。</summary>
    private const double AreaTol = 1e-4;
    private const int MaxIter = 60;

    /// <summary>
    /// 把一个单元按各份的量切成任务区域。
    /// </summary>
    /// <param name="crest">前脸轨（扁平 xyz）。</param>
    /// <param name="toe">坡底线（扁平 xyz）—— 只用来定推进方向的正负。</param>
    /// <param name="widthM">本单元本期的推进宽 W（m）。</param>
    /// <param name="advanceTowardCrest">推进朝坡顶（采场 true / 排土 false）。</param>
    /// <param name="parts">各份 (标识, 量)。<b>顺序即推进先后</b> —— 先干的在前脸那一侧。</param>
    public static TaskZoneSplit Split(double[]? crest, double[]? toe, double widthM,
                                      bool advanceTowardCrest,
                                      IReadOnlyList<(string Key, double VolumeM3)>? parts)
    {
        var res = new TaskZoneSplit();
        if (crest == null || crest.Length < 6) { res.Why = "前脸轨不足 2 点，切不了。"; return res; }
        if (toe == null || toe.Length < 6) { res.Why = "坡底线不足 2 点，定不出推进方向。"; return res; }
        if (!(widthM > 1e-6)) { res.Why = $"推进宽不成立（{widthM:0.###} m）。"; return res; }

        var list = (parts ?? Array.Empty<(string, double)>()).ToList();
        if (list.Count == 0) { res.Why = "没有要切的份。"; return res; }

        // K4：量为 0 的份**不出地**。切一条零宽带出来，图上是一条线，
        //     而「这台设备这几天有活」与「有块地但干 0 方」是两件事。
        var zero = list.Where(p => !(p.VolumeM3 > 1e-9)).Select(p => p.Key).ToList();
        var use = list.Where(p => p.VolumeM3 > 1e-9).ToList();
        if (use.Count == 0) { res.Why = "所有份的量都是 0，切不出任何地。"; return res; }
        if (zero.Count > 0)
            res.Notes.Add($"· {zero.Count} 份的量是 0，**没有给它们地**（{string.Join("、", zero.Take(5))}）—— "
                        + "零宽带在图上是一条线，而「有活」与「有地但干 0 方」是两件事。");

        // 推进方向：AdvanceDirs 返回指坡脚那一版；采场取反（与 UnitPrism 同一口径）
        double[] toeAt;
        (double X, double Y)[] dirs;
        try
        {
            toeAt = DumpStripPlanner.ProjectOnto(crest, toe);
            dirs = DumpStripPlanner.AdvanceDirs(crest, toeAt, Math.Max(widthM, 2.0));
        }
        catch (Exception ex) { res.Why = $"推进方向求解失败（{ex.GetType().Name}）。"; return res; }
        if (advanceTowardCrest)
            for (int i = 0; i < dirs.Length; i++) dirs[i] = (-dirs[i].X, -dirs[i].Y);

        // 面积对 w 的函数（单调不减）。K1 的全部依据就是它不是线性的。
        double AreaAt(double w)
        {
            if (w <= 1e-9) return 0;
            var ring = RingBetween(crest, dirs, 0, w);
            return ring.Length >= 6 ? Math.Abs(Shoelace(ring)) : 0;
        }

        double total = AreaAt(widthM);
        if (!(total > 1e-6)) { res.Why = "整个单元的平面面积算出来是 0。"; return res; }
        res.TotalAreaM2 = total;

        double sumV = use.Sum(p => p.VolumeM3);
        double cumV = 0, w0 = 0;
        double maxLinDelta = 0;

        for (int i = 0; i < use.Count; i++)
        {
            cumV += use[i].VolumeM3;
            // 最后一份直接顶到 W —— 二分留下的那点残差不该变成一条缝
            double w1 = i == use.Count - 1 ? widthM : SolveW(AreaAt, total * cumV / sumV, widthM);
            double linW1 = widthM * cumV / sumV;                 // K1：线性分本该落在哪儿
            maxLinDelta = Math.Max(maxLinDelta, Math.Abs(w1 - linW1));

            var ring = RingBetween(crest, dirs, w0, w1);
            res.Slices.Add(new TaskZoneSlice
            {
                Key = use[i].Key,
                VolumeM3 = use[i].VolumeM3,
                W0 = w0, W1 = w1,
                LinearW1 = linW1,
                RingXy = ring,
                AreaM2 = ring.Length >= 6 ? Math.Abs(Shoelace(ring)) : 0,
            });
            w0 = w1;
        }

        // K2 守恒：各块面积之和 = 整块
        double got = res.Slices.Sum(s => s.AreaM2);
        if (Math.Abs(got - total) > Math.Max(1.0, total * 1e-3))
            res.Notes.Add($"◆ 切完各块面积之和 {got:N0} m²，而整个单元是 {total:N0} m² —— "
                        + "差额说明有地没落进任何一块（或落进了两块）。这是记账漏洞，请报给开发。");

        // K1：把线性分会错多少说出来 —— 差得小说明这条轨挺直，差得大说明线性分本来就会错那么多
        res.Notes.Add(maxLinDelta > 0.5
            ? $"· 按**面积**反求宽度，与「按宽度线性分」最大差 {maxLinDelta:0.##} m —— "
            + "这条轨有明显弯度，线性分会让每一块的面积对不上它该有的量，"
            + "而每一块画出来都是规规矩矩的带，图上看不出来。"
            : $"· 按面积反求宽度，与线性分最大差 {maxLinDelta:0.###} m（这条轨接近直的，两种分法几乎一样）。");

        var bad = res.Slices.Where(s => !s.Ok).Select(s => s.Key).ToList();
        if (bad.Count > 0)
            res.Notes.Add($"◆ {bad.Count} 块围不成面（{string.Join("、", bad.Take(5))}）—— "
                        + "多半是量太小、宽度落到亚米级。这些块**不要拿去派工**。");
        return res;
    }

    /// <summary>
    /// 便捷版：直接吃 <see cref="UnitRailFile.RailRow"/>。
    /// </summary>
    public static TaskZoneSplit Split(UnitRailFile.RailRow? rail, bool advanceTowardCrest,
                                      IReadOnlyList<(string Key, double VolumeM3)>? parts)
    {
        if (rail == null || !rail.IsValid)
            return new TaskZoneSplit
            {
                Why = "这个单元没有真轨（或真轨不成立）—— **拒绝切**。"
                    + "拿质心盒子切出来的带朝向是编的，派工按它去现场会找不到地方。",
            };
        return Split(rail.Crest, rail.Toe, rail.WidthM, advanceTowardCrest, parts);
    }

    // ── 小件 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 二分求 w 使 <c>AreaAt(w) == want</c>。面积对 w 单调不减，所以二分一定收敛。
    /// </summary>
    private static double SolveW(Func<double, double> areaAt, double want, double wMax)
    {
        double lo = 0, hi = wMax;
        for (int i = 0; i < MaxIter; i++)
        {
            double mid = (lo + hi) * 0.5;
            double a = areaAt(mid);
            if (Math.Abs(a - want) <= Math.Max(1e-6, want * AreaTol)) return mid;
            if (a < want) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5;
    }

    /// <summary>
    /// 宽度区间 [w0, w1] 的平面环 = 偏 w0 的轨正序 + 偏 w1 的轨倒序。
    /// <para>w0 为 0 时前边界就是前脸轨本身（不去偏 0，<c>OffsetRail</c> 在 0 偏移上会插点）。</para>
    /// </summary>
    private static double[] RingBetween(double[] crest, (double X, double Y)[] dirs, double w0, double w1)
    {
        try
        {
            double[] a = w0 <= 1e-9 ? crest : DumpStripPlanner.OffsetRail(crest, dirs, w0);
            double[] b = DumpStripPlanner.OffsetRail(crest, dirs, w1);
            int na = a.Length / 3, nb = b.Length / 3;
            if (na < 2 || nb < 2) return Array.Empty<double>();

            var ring = new double[(na + nb) * 2];
            int k = 0;
            for (int i = 0; i < na; i++) { ring[k++] = a[i * 3]; ring[k++] = a[i * 3 + 1]; }
            for (int i = nb - 1; i >= 0; i--) { ring[k++] = b[i * 3]; ring[k++] = b[i * 3 + 1]; }
            return ring;
        }
        catch { return Array.Empty<double>(); }
    }

    private static double Shoelace(double[] ring)
    {
        int n = ring.Length / 2;
        if (n < 3) return 0;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            s += ring[i * 2] * ring[j * 2 + 1] - ring[j * 2] * ring[i * 2 + 1];
        }
        return s * 0.5;
    }
}
