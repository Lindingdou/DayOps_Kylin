using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>工作线类型（与原版内核 <c>WorkLineAdvanceMode</c> 编码对齐：0 = 直线/平行推进，2 = 扇形/回转推进；1 是已取消的 L 型，留空号位）。</summary>
public enum WorkLineMode : byte { Straight = 0, Fan = 2 }

/// <summary>一条工作线的几何：基线 + 结束位置形态线（虚线）+ 方向箭头（+ 扇形的回转中心）。</summary>
public sealed class WorkLineGeometry
{
    public WorkLineMode Mode { get; init; }
    public double LevelZ { get; init; }
    public List<(double x, double y)> Base { get; } = new();
    /// <summary>结束位置形态线（独立一条，长度形状可与基线不同；默认按推进方向平推 / 绕回转中心转出来）。</summary>
    public List<(double x, double y)> EndLine { get; } = new();
    /// <summary>方向箭头（琥珀色）：一段折线，从基线中点指向推进方向。</summary>
    public List<(double x, double y)> Arrow { get; } = new();
    /// <summary>扇形的回转中心；直线为 null。</summary>
    public (double x, double y)? Pivot { get; init; }
    /// <summary>推进方向（单位向量，直线用；扇形指基线中点处的切向）。</summary>
    public double DirX { get; init; }
    public double DirY { get; init; }
    /// <summary>扇形回转角 °（正 = 逆时针）。</summary>
    public double SweepDeg { get; init; }

    public string ModeLabel => Mode == WorkLineMode.Fan ? "扇形工作线（回转推进）" : "直线工作线（平行推进）";
}

/// <summary>
/// 工作线（移植原版 <c>CreateWorkLineFromPoints</c> 的几何部分）。
///
/// <para>
/// <b>工作线只表征推进方向，不设任何驱动距离</b>（原版口径）。两种类型都由「基线 + 结束位置形态线（虚线）」构成，
/// 扇形再加一个回转中心；推进方向 / 回转角与旋向都由两条线反算。基线一律<b>拍平到台阶水平</b> —— 工作线即台阶线，按定义等高。
/// </para>
/// <para>
/// 结束位置形态线的<b>默认</b>放法：直线 = 沿推进方向平推一个"默认推进距"（基线长的一半，只是个起始位置，人会拖）；
/// 扇形 = 绕回转中心（默认放在基线一端外侧）转默认回转角 30°。它是独立一条线，人拖它的顶点即改收尾形态。
/// </para>
/// </summary>
public static class WorkLineModel
{
    /// <summary>直线默认推进距 = 基线长 × 此系数。</summary>
    public const double DefaultAdvanceRatio = 0.5;
    /// <summary>扇形默认回转角 °。</summary>
    public const double DefaultSweepDeg = 30.0;
    /// <summary>箭头长 = 基线长 × 此系数（最短 5 m）。</summary>
    public const double ArrowRatio = 0.25;

    /// <summary>
    /// 由两点基线建工作线。<paramref name="dirSide"/>：推进方向取基线法向的哪一侧（+1 左 / −1 右）。
    /// 两点重合返回 null（定不出推进方向）。
    /// </summary>
    public static WorkLineGeometry? FromTwoPoints((double x, double y) a, (double x, double y) b, WorkLineMode mode,
                                                 double levelZ, int dirSide = 1, (double x, double y)? pivot = null)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return null;
        double tx = dx / len, ty = dy / len;
        double nx = -ty * (dirSide >= 0 ? 1 : -1), ny = tx * (dirSide >= 0 ? 1 : -1);   // 左法向 = 推进方向

        (double x, double y) mid = ((a.x + b.x) * 0.5, (a.y + b.y) * 0.5);
        double arrowLen = Math.Max(5.0, len * ArrowRatio);

        if (mode == WorkLineMode.Fan)
        {
            // 回转中心默认放在基线 a 端外侧（沿基线反向延一段），使基线绕它转出扇形
            var pv = pivot ?? (a.x - tx * len * 0.25, a.y - ty * len * 0.25);
            // 旋向：绕中心转 +θ 时点沿 (−ry, rx) 走；它与推进方向同向（点积 ≥ 0）就取正角，否则取负角
            double rx0 = a.x - pv.x, ry0 = a.y - pv.y;
            double dot = -ry0 * nx + rx0 * ny;
            double sweep = DefaultSweepDeg * (dot >= 0 ? 1 : -1);
            var g = new WorkLineGeometry { Mode = mode, LevelZ = levelZ, Pivot = pv, DirX = nx, DirY = ny, SweepDeg = sweep };
            g.Base.Add(a); g.Base.Add(b);
            double rad = sweep * Math.PI / 180.0, c = Math.Cos(rad), s = Math.Sin(rad);
            foreach (var p in new[] { a, b })
            {
                double rx = p.x - pv.x, ry = p.y - pv.y;
                g.EndLine.Add((pv.x + rx * c - ry * s, pv.y + rx * s + ry * c));
            }
            AddArrow(g, mid, nx, ny, arrowLen);
            return g;
        }
        else
        {
            double adv = len * DefaultAdvanceRatio;
            var g = new WorkLineGeometry { Mode = mode, LevelZ = levelZ, DirX = nx, DirY = ny };
            g.Base.Add(a); g.Base.Add(b);
            g.EndLine.Add((a.x + nx * adv, a.y + ny * adv));
            g.EndLine.Add((b.x + nx * adv, b.y + ny * adv));
            AddArrow(g, mid, nx, ny, arrowLen);
            return g;
        }
    }

    /// <summary>由已有基线 + 结束线反算推进方向（直线）或回转要素（扇形）—— "两条线反算"那条口径。</summary>
    public static (double dirX, double dirY, double sweepDeg) Infer(IReadOnlyList<(double x, double y)> baseLine,
                                                                    IReadOnlyList<(double x, double y)> endLine,
                                                                    (double x, double y)? pivot)
    {
        if (baseLine.Count < 2 || endLine.Count < 2) return (0, 0, 0);
        (double x, double y) bm = ((baseLine[0].x + baseLine[^1].x) * 0.5, (baseLine[0].y + baseLine[^1].y) * 0.5);
        (double x, double y) em = ((endLine[0].x + endLine[^1].x) * 0.5, (endLine[0].y + endLine[^1].y) * 0.5);
        double dx = em.x - bm.x, dy = em.y - bm.y, len = Math.Sqrt(dx * dx + dy * dy);
        double dirX = len > 1e-9 ? dx / len : 0, dirY = len > 1e-9 ? dy / len : 0;
        if (pivot is not { } pv) return (dirX, dirY, 0);
        double a0 = Math.Atan2(bm.y - pv.y, bm.x - pv.x), a1 = Math.Atan2(em.y - pv.y, em.x - pv.x);
        double sweep = (a1 - a0) * 180.0 / Math.PI;
        while (sweep > 180) sweep -= 360;
        while (sweep < -180) sweep += 360;
        return (dirX, dirY, sweep);
    }

    private static void AddArrow(WorkLineGeometry g, (double x, double y) mid, double nx, double ny, double len)
    {
        var tip = (mid.x + nx * len, mid.y + ny * len);
        double hx = -ny, hy = nx, h = len * 0.25;
        g.Arrow.Add(mid);
        g.Arrow.Add(tip);
        g.Arrow.Add((tip.Item1 - nx * h + hx * h * 0.5, tip.Item2 - ny * h + hy * h * 0.5));
        g.Arrow.Add(tip);
        g.Arrow.Add((tip.Item1 - nx * h - hx * h * 0.5, tip.Item2 - ny * h - hy * h * 0.5));
    }
}
