using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>一级台阶：坡顶线 / 坡脚线（XY 环或开口线）与各自标高。</summary>
public sealed class BenchLevel
{
    public int Index { get; init; }
    public double CrestZ { get; init; }
    public double ToeZ { get; init; }
    public List<(double x, double y)> Crest { get; init; } = new();
    public List<(double x, double y)> Toe { get; init; } = new();
    /// <summary>这一级的实际台阶高（楔形/到标高时可能小于 H）。</summary>
    public double HeightM => CrestZ - ToeZ;
}

/// <summary>台阶生成的产物：逐级线 + 坡面网 + 平盘网。</summary>
public sealed class BenchBuildResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public List<BenchLevel> Levels { get; } = new();
    public List<(double x, double y, double z)> FaceVerts { get; } = new();
    public List<(int a, int b, int c)> FaceTris { get; } = new();
    public List<(double x, double y, double z)> BermVerts { get; } = new();
    public List<(int a, int b, int c)> BermTris { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>总下降/上升高度 m。</summary>
    public double TotalDropM => Levels.Sum(l => l.HeightM);
}

/// <summary>
/// 台阶几何生成（托管，替代原版内核 <c>IPitDesignCapability.LocalWedge / ExpandBench</c> 的那一段）。
///
/// <para>
/// 一级台阶 = 坡顶线 → 按 <c>H / tan α</c> 水平偏移得坡脚线（低 H）→ 再按平盘宽 W 偏移得下一级坡顶线（同标高）。
/// 坡面网铺在 坡顶→坡脚 之间，平盘网铺在 坡脚→下一级坡顶 之间。
/// </para>
/// <para>
/// <b>采场 / 排土场语义</b>：采场向下是<b>向内</b>收（境界往里挖），排土场向下是<b>向外</b>放（堆体往外放坡）。
/// 闭合线按有向面积定内外；开口线按行进方向的左/右侧。
/// </para>
/// <para>
/// <b>退化就停</b>：偏移到相邻边平行、或环缩到面积不再递减，就停在上一级并记账 —— 不硬凑一级歪掉的台阶。
/// </para>
/// </summary>
public static class BenchBuilder
{
    /// <summary>开口线按定距 d 向一侧偏移（side = +1 行进方向左侧 / −1 右侧），拐点取相邻偏移边交点（平行时取偏移点）。</summary>
    public static List<(double x, double y)>? OffsetOpen(IReadOnlyList<(double x, double y)> pts, double d, int side)
    {
        int n = pts.Count;
        if (n < 2 || d <= 0) return null;
        double sgn = side >= 0 ? 1.0 : -1.0;
        var off = new (double ax, double ay, double bx, double by)[n - 1];
        for (int i = 0; i + 1 < n; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) return null;
            double nx = sgn * (-dy) / len, ny = sgn * dx / len;
            off[i] = (a.x + nx * d, a.y + ny * d, b.x + nx * d, b.y + ny * d);
        }
        var res = new List<(double x, double y)>(n);
        res.Add((off[0].ax, off[0].ay));
        for (int i = 1; i + 1 < n; i++)
        {
            var e0 = off[i - 1]; var e1 = off[i];
            var ip = LineMath.IntersectInfinite(e0.ax, e0.ay, e0.bx, e0.by, e1.ax, e1.ay, e1.bx, e1.by);
            res.Add(ip ?? (e1.ax, e1.ay));
        }
        res.Add((off[^1].bx, off[^1].by));
        return res;
    }

    /// <summary>
    /// 闭合环偏移：inward = true 往里，false 往外。退化（相邻边平行、或往里收到环翻转）返回 null。
    /// <para><c>BenchLines.OffsetClosed</c> 按有向面积归一化后<b>永远往里</b>（反转点序也一样），
    /// 所以往外不能靠反转点序，得自己带符号偏移。</para>
    /// </summary>
    public static List<(double x, double y)>? OffsetRing(IReadOnlyList<(double x, double y)> ring, double d, bool inward)
    {
        int n = ring.Count;
        if (n < 3 || d <= 0) return null;
        double area = BenchLines.SignedArea(ring);
        if (Math.Abs(area) < 1e-12) return null;
        double sgn = (area > 0 ? 1.0 : -1.0) * (inward ? 1.0 : -1.0);

        var off = new (double ax, double ay, double bx, double by)[n];
        for (int i = 0; i < n; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) return null;
            double nx = sgn * (-dy) / len, ny = sgn * dx / len;
            off[i] = (a.x + nx * d, a.y + ny * d, b.x + nx * d, b.y + ny * d);
        }
        var res = new List<(double x, double y)>(n);
        for (int i = 0; i < n; i++)
        {
            var e0 = off[(i - 1 + n) % n]; var e1 = off[i];
            var ip = LineMath.IntersectInfinite(e0.ax, e0.ay, e0.bx, e0.by, e1.ax, e1.ay, e1.bx, e1.by);
            if (ip == null) return null;
            res.Add(ip.Value);
        }
        // 往里收过头会把环翻过来（有向面积变号）—— 那不是一级台阶，是退化
        double area2 = BenchLines.SignedArea(res);
        if (inward && (Math.Sign(area2) != Math.Sign(area) || Math.Abs(area2) >= Math.Abs(area))) return null;
        if (!inward && Math.Sign(area2) != Math.Sign(area)) return null;
        return res;
    }

    /// <summary>
    /// 从一条基线逐级生成台阶。
    /// </summary>
    /// <param name="baseXy">基线（坡顶线）平面形态。</param>
    /// <param name="closed">是否闭合环。</param>
    /// <param name="z0">基线标高。</param>
    /// <param name="benchH">台阶高 H。</param>
    /// <param name="faceAngleDeg">坡面角 α。</param>
    /// <param name="bermW">平盘宽 W。</param>
    /// <param name="levels">最多几级。</param>
    /// <param name="downward">向下（采场挖 / 排土场放）还是向上。</param>
    /// <param name="isDump">排土场语义：向下往<b>外</b>放；采场向下往<b>内</b>收。</param>
    /// <param name="side">开口线用：放坡（坡脚）在行进方向的 +1 左 / −1 右（闭合线忽略；开口线没有内外可言，由人指定）。</param>
    /// <param name="taper">楔形：台阶高沿线从 H 线性收到 0（弧长收口）。</param>
    /// <param name="stopZ">到标高：总高差截到 |z0 − stopZ|，末级可能不满一级；null = 不截。</param>
    /// <param name="stopAtGround">采样面（排土场"一路推到现状面"）：某级坡脚已低于面则截到面并停；null = 不看面。</param>
    public static BenchBuildResult Build(IReadOnlyList<(double x, double y)> baseXy, bool closed, double z0,
                                         double benchH, double faceAngleDeg, double bermW, int levels,
                                         bool downward, bool isDump, int side = 1,
                                         bool taper = false, double? stopZ = null, IRoadZSampler? stopAtGround = null)
    {
        var res = new BenchBuildResult();
        if (baseXy == null || baseXy.Count < (closed ? 3 : 2)) { res.Error = closed ? "闭合线至少 3 点" : "开口线至少 2 点"; return res; }
        if (benchH <= 1e-6) { res.Error = "台阶高 H 必须 > 0"; return res; }
        if (faceAngleDeg <= 1 || faceAngleDeg >= 89.9) { res.Error = "坡面角 α 须在 (1°, 90°)"; return res; }
        if (bermW < 0) { res.Error = "平盘宽 W 不能为负"; return res; }
        if (levels < 1) { res.Error = "至少 1 级"; return res; }

        double faceRun = benchH / Math.Tan(faceAngleDeg * Math.PI / 180.0);   // 一级坡面的水平投影
        double dir = downward ? -1 : 1;
        // 向下：采场往内收、排土场往外放；向上则相反
        bool inward = downward ? !isDump : isDump;
        double remain = stopZ.HasValue ? Math.Abs(z0 - stopZ.Value) : double.PositiveInfinity;
        if (stopZ.HasValue && remain < 1e-6) { res.Error = "到标高与基线标高相同，没有高差可放"; return res; }

        var crest = new List<(double x, double y)>(baseXy);
        double zc = z0;
        for (int k = 0; k < levels; k++)
        {
            double h = Math.Min(benchH, remain);
            if (h < 1e-6) { res.Notes.Add($"第 {k + 1} 级：已到指定标高，停。"); break; }
            double run = h / Math.Tan(faceAngleDeg * Math.PI / 180.0);

            var toe = closed ? OffsetRing(crest, run, inward) : OffsetOpen(crest, run, side);
            if (toe == null) { res.Notes.Add($"第 {k + 1} 级：坡脚线偏移退化（相邻边平行或环太小），停在第 {k} 级。"); break; }
            if (closed && Math.Abs(BenchLines.SignedArea(toe)) < 1e-9) { res.Notes.Add($"第 {k + 1} 级：环收缩到零，停。"); break; }

            double zt = zc + dir * h;

            // 排土场"一路推到现状面"：坡脚已经低过面 ⇒ 截到面、这是最后一级
            bool last = false;
            if (stopAtGround != null && downward && SampleAvg(stopAtGround, toe, out double g) && zt <= g + 1e-6)
            {
                double hh = Math.Max(0, zc - g);
                if (hh < 0.05) { res.Notes.Add($"第 {k + 1} 级：坡顶已在现状面上，停。"); break; }
                zt = g; last = true;
                run = hh / Math.Tan(faceAngleDeg * Math.PI / 180.0);
                toe = closed ? OffsetRing(crest, run, inward) : OffsetOpen(crest, run, side);
                if (toe == null) break;
                res.Notes.Add($"第 {k + 1} 级：坡脚落到现状面（标高 {g:0.#}），本级实高 {hh:0.#} m。");
            }

            var lvl = new BenchLevel { Index = k + 1, CrestZ = zc, ToeZ = zt, Crest = crest, Toe = toe };
            res.Levels.Add(lvl);
            AddStrip(res.FaceVerts, res.FaceTris, crest, zc, toe, zt, closed, taper ? (k, levels) : null);

            remain -= h;
            if (last || remain < 1e-6) break;

            // 平盘：坡脚 → 下一级坡顶（同标高）
            if (k + 1 < levels && bermW > 1e-9)
            {
                var next = closed ? OffsetRing(toe, bermW, inward) : OffsetOpen(toe, bermW, side);
                if (next == null) { res.Notes.Add($"第 {k + 1} 级平盘：偏移退化，停在本级坡脚。"); break; }
                AddStrip(res.BermVerts, res.BermTris, toe, zt, next, zt, closed, null);
                crest = next;
            }
            else crest = toe;
            zc = zt;
        }

        if (res.Levels.Count == 0) { res.Error = res.Notes.Count > 0 ? res.Notes[0] : "一级都没生成"; return res; }
        res.Ok = true;
        return res;
    }

    private static bool SampleAvg(IRoadZSampler g, IReadOnlyList<(double x, double y)> pts, out double z)
    {
        double sum = 0; int n = 0;
        foreach (var p in pts) if (g.TrySample(p.x, p.y, out double zz)) { sum += zz; n++; }
        z = n > 0 ? sum / n : 0;
        return n > 0;
    }

    /// <summary>两条同点数线之间铺一条带（楔形时下线标高沿弧长从 zb 收回到 za）。</summary>
    private static void AddStrip(List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris,
                                 IReadOnlyList<(double x, double y)> a, double za,
                                 IReadOnlyList<(double x, double y)> b, double zb, bool closed, (int k, int levels)? taper)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 2) return;
        int b0 = verts.Count;
        for (int i = 0; i < n; i++)
        {
            double t = n > 1 ? (double)i / (n - 1) : 0;
            double zbi = taper != null ? za + (zb - za) * (1 - t) : zb;   // 楔形：末端收到与上线同高
            verts.Add((a[i].x, a[i].y, za));
            verts.Add((b[i].x, b[i].y, zbi));
        }
        int m = closed ? n : n - 1;
        for (int i = 0; i < m; i++)
        {
            int j = (i + 1) % n;
            int a0 = b0 + i * 2, b1 = b0 + i * 2 + 1, a1 = b0 + j * 2, b2 = b0 + j * 2 + 1;
            tris.Add((a0, b1, a1));
            tris.Add((b1, b2, a1));
        }
    }

    /// <summary>
    /// 排土场放坡的标高口径：画的二维堆顶线与图上已有台阶线求交，向下推 ⇒ 跟坡脚线交、取交点标高的 max 起算。
    /// <b>一个交点都没有 = 拦下</b>，不替用户编一个标高（编出来的坡照样生成、照样好看，只是整体错在没人会去查的地方）。
    /// </summary>
    /// <param name="drawn">手画堆顶线（闭合，XY）。</param>
    /// <param name="existing">图上可见的台阶线：(点串, 标高)。</param>
    public static (bool ok, double z, int hits, string why) ResolveDumpCrestZ(
        IReadOnlyList<(double x, double y)> drawn, IEnumerable<(IReadOnlyList<(double x, double y)> pts, double z)> existing)
    {
        if (drawn == null || drawn.Count < 3) return (false, 0, 0, "堆顶线至少 3 点");
        double best = double.NegativeInfinity; int hits = 0;
        foreach (var (pts, z) in existing)
        {
            if (pts == null || pts.Count < 2) continue;
            bool cross = false;
            for (int i = 0; i < drawn.Count && !cross; i++)
            {
                var a = drawn[i]; var b = drawn[(i + 1) % drawn.Count];
                for (int j = 0; j + 1 < pts.Count && !cross; j++)
                    if (LineMath.SegmentsIntersect(a.x, a.y, b.x, b.y, pts[j].x, pts[j].y, pts[j + 1].x, pts[j + 1].y)) cross = true;
            }
            if (!cross) continue;
            hits++;
            if (z > best) best = z;
        }
        if (hits == 0) return (false, 0, 0, "堆顶线与图上任何台阶线都不相交 —— 起算标高无从确定，不替你编一个。请把线画到跨过已有坡脚线，或先关掉不该参与的图层。");
        return (true, best, hits, $"与 {hits} 条台阶线相交，取交点标高 max = {best:0.#} 起算");
    }
}
