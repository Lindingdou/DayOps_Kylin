using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 【工作帮范围】在采场 / 排土场范围**里面**再套的一层更小的边界 —— 本期实际在哪儿干。
///
/// <para><b>它解决什么</b>：采矿模型此前按<b>整个</b>采场范围铺开（实测全场 2312 个单元、
/// 岩 1146 条带），排土模型按整个排土场范围切格。可现场一期只在<b>工作帮</b>那一片推进，
/// 剩下的都是端帮/底帮/暂不动的区域。范围不收窄，排产就得在两千多个单元里挑几十个。</para>
///
/// <para><b>层级是硬的</b>：采场/排土场范围决定<b>归属</b>（那一行台账写哪个采场），
/// 工作帮范围决定<b>本期干哪儿</b>。所以两个环<b>都要满足</b>，而不是拿工作帮环替掉母环 ——
/// 替掉之后采场名就只能靠猜，而"这个单元属于哪个采场"是台账的身份列。</para>
///
/// <para><b>为什么诊断必须单独做一遍</b>：工作帮环画错地方（圈到隔壁采场、圈在范围外）时，
/// 两侧的表现都是<b>"一条都没生成"</b>，而现有报错说的是"没有露头带 / 没配出一幅" ——
/// 人会照着那句话去调坡度、面积、台阶高，怎么调都没用，因为根因在范围。
/// 所以这里先把两个环的关系量出来，让"圈错了"这件事<b>在生成之前</b>就说得出来。</para>
///
/// <para>粒度上不做新口径：<b>裁剪仍在各自既有的那一处</b>（采场按三角形心、排土按格质心），
/// 本类只负责"环合不合法"与"环和母范围什么关系"。</para>
/// </summary>
public static class WorkSlopeRange
{
    /// <summary>母范围内采样点里落在工作帮内的比例达到这个数，就认为"等于没收窄"。</summary>
    public const double CoversAllThreshold = 0.995;

    /// <summary>工作帮环内采样点里落在母范围内的比例低于这个数，就认为"基本不在母范围里"。</summary>
    public const double OutsideThreshold = 0.02;

    /// <summary>诊断一维网格的边数（母范围包围盒按它切格采样）。</summary>
    private const int SampleGrid = 120;

    /// <summary>与母范围的关系。</summary>
    public enum Relation
    {
        /// <summary>没给工作帮范围 —— 走母范围全铺（旧行为）。</summary>
        NotGiven,
        /// <summary>环本身不合法（顶点太少 / 退化成线 / 坐标有非数）。</summary>
        Invalid,
        /// <summary>基本不在母范围里 —— <b>多半是圈错了地方</b>。</summary>
        Outside,
        /// <summary>覆盖了整个母范围 —— 等于没收窄。</summary>
        CoversAll,
        /// <summary>正常收窄。</summary>
        Narrows,
    }

    /// <summary>诊断结果。<see cref="Caption"/> 由调用方原样显示，别自己再编一遍。</summary>
    public sealed class Check
    {
        public Relation Relation;
        /// <summary>母范围里有多大比例落在工作帮内（0~1）。<b>NaN = 没量</b>（不是 0）。</summary>
        public double CoverOfParent = double.NaN;
        /// <summary>工作帮范围里有多大比例落在母范围内（0~1）。NaN = 没量。</summary>
        public double InsideParent = double.NaN;
        /// <summary>工作帮环的平面面积（m²，鞋带公式取绝对值）。NaN = 没量。</summary>
        public double AreaM2 = double.NaN;
        public string Why = "";

        /// <summary>能不能拿它去生成。<see cref="Relation.Outside"/> 与 <see cref="Relation.Invalid"/> 不行。</summary>
        public bool Usable => Relation is Relation.NotGiven or Relation.CoversAll or Relation.Narrows;

        public string Caption => Relation switch
        {
            Relation.NotGiven => "工作帮范围：**没给** —— 按整个采场/排土场范围铺开（旧口径）。",
            Relation.Invalid  => "◆ 工作帮范围不合法：" + Why + " —— 不拿它裁，也不静默当没给。",
            Relation.Outside  => "◆ 工作帮范围**基本不在这个采场/排土场里**"
                               + (double.IsNaN(InsideParent) ? "" : $"（只有 {InsideParent * 100:0.#}% 落在里面）")
                               + " —— 多半圈错了地方或选错了区域。照它裁会一条都生成不出来，"
                               + "而报错会说成\"没有露头带\"，人只会去调坡度和面积。",
            Relation.CoversAll => "工作帮范围**覆盖了整个母范围**"
                               + (double.IsNaN(AreaM2) ? "" : $"（{AreaM2 / 1e4:0.#} 万m²）")
                               + " —— 等于没收窄，出来的还是全场那一份。",
            _ => $"工作帮范围：{(double.IsNaN(AreaM2) ? "" : $"{AreaM2 / 1e4:0.#} 万m² · ")}"
               + $"占母范围约 {CoverOfParent * 100:0.#}%"
               + (double.IsNaN(InsideParent) || InsideParent > 0.995 ? "" : $"（其中 {InsideParent * 100:0.#}% 在母范围内，其余部分不起作用）"),
        };
    }

    /// <summary>
    /// 环合不合法。<b>顶点少于 3 个、面积退化成 0、含非数坐标</b>三种都不能用来裁 ——
    /// 射线法对它们不会抛，只会给出"点全在外面"这种看着正常的答案。
    /// </summary>
    public static bool IsValidRing(double[]? ringXy, out string why)
    {
        why = "";
        if (ringXy == null || ringXy.Length < 6) { why = $"顶点不足 3 个（拿到 {(ringXy?.Length ?? 0) / 2} 个）"; return false; }
        if (ringXy.Length % 2 != 0) { why = $"坐标个数是奇数（{ringXy.Length}），不是成对的 x,y"; return false; }
        for (int i = 0; i < ringXy.Length; i++)
            if (double.IsNaN(ringXy[i]) || double.IsInfinity(ringXy[i])) { why = "含 NaN / 无穷坐标"; return false; }
        double a = Math.Abs(SignedArea(ringXy));
        if (!(a > 1e-6)) { why = "面积退化成 0（顶点共线或全部重合）"; return false; }
        return true;
    }

    /// <summary>鞋带公式的<b>带符号</b>面积（正 = 逆时针）。</summary>
    public static double SignedArea(double[] ringXy)
    {
        if (ringXy == null || ringXy.Length < 6) return 0;
        int n = ringXy.Length / 2;
        double s = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
            s += (ringXy[j * 2] + ringXy[i * 2]) * (ringXy[j * 2 + 1] - ringXy[i * 2 + 1]);
        return -s * 0.5;
    }

    /// <summary>
    /// 点在环内（射线法）。<b>两侧共用这一份</b> —— 采场与排土各写一套的后果是
    /// 边界上的格一边算进一边算出，而两边的汇总都"看上去正常"。
    /// 环不必首尾闭合（隐式闭合）。
    /// </summary>
    public static bool Contains(double[]? ringXy, double x, double y)
    {
        if (ringXy == null || ringXy.Length < 6) return true;       // 没给范围 = 不限制
        bool inside = false;
        int n = ringXy.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ringXy[i * 2], yi = ringXy[i * 2 + 1];
            double xj = ringXy[j * 2], yj = ringXy[j * 2 + 1];
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 量一遍工作帮环与母范围的关系。<paramref name="parentRingXy"/> 为空时只校验合法性
    /// （母范围本来就可以不给 —— 那时"圈错地方"无从判起，不许假报）。
    /// </summary>
    public static Check Diagnose(double[]? workRingXy, double[]? parentRingXy)
    {
        var c = new Check();
        if (workRingXy is not { Length: >= 6 }) { c.Relation = Relation.NotGiven; return c; }
        if (!IsValidRing(workRingXy, out string why)) { c.Relation = Relation.Invalid; c.Why = why; return c; }

        c.AreaM2 = Math.Abs(SignedArea(workRingXy));

        if (parentRingXy is not { Length: >= 6 } || !IsValidRing(parentRingXy, out _))
        {
            // 母范围没给或不合法 ⇒ 只能说"给了工作帮范围"，比例一律留 NaN 而不是 0：
            // 0 会被读成"完全不重叠"，那是一个没量过的结论。
            c.Relation = Relation.Narrows;
            return c;
        }

        // 母范围包围盒里撒点：母内点里有多少落在工作帮内 / 工作帮内点里有多少落在母内。
        // 用采样而不是多边形求交：这里要的只是"完全不相交"和"几乎全覆盖"两个极端，
        // 为它引一套裁剪库不划算，而采样法在这两个极端上很稳。
        Bounds(parentRingXy, out double x0, out double y0, out double x1, out double y1);
        Bounds(workRingXy, out double wx0, out double wy0, out double wx1, out double wy1);
        double gx0 = Math.Min(x0, wx0), gy0 = Math.Min(y0, wy0);
        double gx1 = Math.Max(x1, wx1), gy1 = Math.Max(y1, wy1);
        double dx = (gx1 - gx0) / SampleGrid, dy = (gy1 - gy0) / SampleGrid;
        if (!(dx > 0) || !(dy > 0)) { c.Relation = Relation.Narrows; return c; }

        long inParent = 0, inParentAndWork = 0, inWork = 0, inWorkAndParent = 0;
        for (int iy = 0; iy < SampleGrid; iy++)
        {
            double y = gy0 + (iy + 0.5) * dy;
            for (int ix = 0; ix < SampleGrid; ix++)
            {
                double x = gx0 + (ix + 0.5) * dx;
                bool p = Contains(parentRingXy, x, y);
                bool w = Contains(workRingXy, x, y);
                if (p) { inParent++; if (w) inParentAndWork++; }
                if (w) { inWork++; if (p) inWorkAndParent++; }
            }
        }

        c.CoverOfParent = inParent > 0 ? (double)inParentAndWork / inParent : double.NaN;
        c.InsideParent = inWork > 0 ? (double)inWorkAndParent / inWork : double.NaN;

        if (inWork == 0)
        {
            // 撒点一个都没落进工作帮 ⇒ 它比一格还小。这不是"在范围外"，是"太小了"，要分开说。
            c.Relation = Relation.Invalid;
            c.Why = $"范围太小，量不出来（面积 {c.AreaM2:0.#} m²，采样格约 {dx:0.#}×{dy:0.#} m）";
            return c;
        }
        if (c.InsideParent < OutsideThreshold) { c.Relation = Relation.Outside; return c; }
        if (c.CoverOfParent >= CoversAllThreshold) { c.Relation = Relation.CoversAll; return c; }
        c.Relation = Relation.Narrows;
        return c;
    }

    private static void Bounds(double[] ring, out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = double.MaxValue; x1 = y1 = double.MinValue;
        for (int i = 0; i + 1 < ring.Length; i += 2)
        {
            if (ring[i] < x0) x0 = ring[i];
            if (ring[i] > x1) x1 = ring[i];
            if (ring[i + 1] < y0) y0 = ring[i + 1];
            if (ring[i + 1] > y1) y1 = ring[i + 1];
        }
    }

    /// <summary>
    /// 把图上拾取的多段线顶点（扁平 xyz）压成环（扁平 xy）。
    /// <para><b>末点与首点重合时去掉末点</b>：闭合多段线首尾同点，留着会在射线法里多一条零长边 ——
    /// 不出错，但面积与采样都白算一遍。</para>
    /// </summary>
    public static double[] RingFromXyz(IReadOnlyList<double>? xyz)
    {
        if (xyz == null || xyz.Count < 9) return Array.Empty<double>();
        int n = xyz.Count / 3;
        var pts = new List<double>(n * 2);
        for (int i = 0; i < n; i++) { pts.Add(xyz[i * 3]); pts.Add(xyz[i * 3 + 1]); }
        if (pts.Count >= 6)
        {
            double fx = pts[0], fy = pts[1], lx = pts[^2], ly = pts[^1];
            if (Math.Abs(fx - lx) < 1e-6 && Math.Abs(fy - ly) < 1e-6) { pts.RemoveAt(pts.Count - 1); pts.RemoveAt(pts.Count - 1); }
        }
        return pts.Count >= 6 ? pts.ToArray() : Array.Empty<double>();
    }

    /// <summary>一行摘要，供状态栏/日志用（数字带单位，别再自己格式化一遍）。</summary>
    public static string Describe(double[]? ringXy)
        => ringXy is not { Length: >= 6 }
            ? "（没给工作帮范围）"
            : string.Format(CultureInfo.InvariantCulture, "{0} 个顶点 · {1:0.#} 万m²",
                            ringXy.Length / 2, Math.Abs(SignedArea(ringXy)) / 1e4);
}
