using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「图元密集区」范围 —— 导入后定位视图用（纯逻辑，可单测）。
///
/// 为什么需要它：现场 DWG 常带着几件跑到几十上百公里外的孤立图元（图框/图例被放在原点、
/// 早年拼图留下的残迹、误操作拖走的一条线）。按真实包围盒缩放，一张 8km×6km 的采剥平面图
/// 会被压成屏幕上的一个点 —— 用户看到的就是"文件打不开"。实测某 51MB 接续计划：
/// 真实包围盒 636km×4392km，而 99% 的图元其实挤在 7.9km×6.2km 里。
///
/// 做法：按图元中心取分位数剪掉两端极少数离群者；只有当剪完的跨度比原范围小很多倍时才认定
/// "确有远处离群图元"并改用密集区范围，否则原样返回 —— 正常图纸一律不受影响。
/// </summary>
public static class RobustExtent
{
    /// <param name="Bounds">[minX, minY, maxX, maxY] —— 建议缩放到的范围。</param>
    /// <param name="Trimmed">true = 判定存在远处离群图元，Bounds 已改成密集区。</param>
    /// <param name="Outliers">落在 Bounds 之外的图元个数（Trimmed=false 时为 0）。</param>
    public readonly record struct Result(double[] Bounds, bool Trimmed, int Outliers);

    /// <summary>
    /// 取密集区范围。
    /// </summary>
    /// <param name="centers">每个图元一个代表点（各自包围盒中心）。</param>
    /// <param name="raw">真实包围盒 [minX, minY, maxX, maxY]。</param>
    /// <param name="keep">保留的中心比例，默认 99%（两端各剪 0.5%）。</param>
    /// <param name="minShrink">剪后跨度至少要比原跨度小这么多倍才判定为离群，默认 3。</param>
    public static Result Compute(
        IReadOnlyList<(double x, double y)> centers, double[] raw, double keep = 0.99, double minShrink = 3.0)
    {
        if (raw == null || raw.Length < 4) return new Result(new double[] { 0, 0, 0, 0 }, false, 0);
        // 图元太少时分位数没有意义（一张只有几十个图元的图，"1%" 剪掉的可能正是主体）
        if (centers == null || centers.Count < 200) return new Result(raw, false, 0);

        double q = Math.Clamp((1 - keep) / 2, 0, 0.25);
        var xs = new double[centers.Count];
        var ys = new double[centers.Count];
        for (int i = 0; i < centers.Count; i++) { xs[i] = centers[i].x; ys[i] = centers[i].y; }
        Array.Sort(xs); Array.Sort(ys);

        double Q(double[] v, double p) => v[Math.Clamp((int)Math.Round(p * (v.Length - 1)), 0, v.Length - 1)];
        double x0 = Q(xs, q), x1 = Q(xs, 1 - q), y0 = Q(ys, q), y1 = Q(ys, 1 - q);

        double rawW = raw[2] - raw[0], rawH = raw[3] - raw[1];
        double w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return new Result(raw, false, 0);          // 密集区退化成一条线/一个点：不敢剪

        // 只有"真实范围比密集区大出好几倍"才认定有远处离群图元；正常图纸原样返回。
        bool trim = rawW / w >= minShrink || rawH / h >= minShrink;
        if (!trim) return new Result(raw, false, 0);

        // 留 3% 余量，免得密集区边上的图元贴着屏幕边
        double padX = w * 0.03, padY = h * 0.03;
        var box = new[] { x0 - padX, y0 - padY, x1 + padX, y1 + padY };

        int outside = 0;
        foreach (var c in centers)
            if (c.x < box[0] || c.x > box[2] || c.y < box[1] || c.y > box[3]) outside++;

        return new Result(box, true, outside);
    }

    /// <summary>把范围跨度写成便于回显的距离（图纸单位按米算）。</summary>
    public static string Describe(double[] b)
    {
        if (b == null || b.Length < 4) return "—";
        double w = b[2] - b[0], h = b[3] - b[1];
        static string L(double v) => v >= 1000 ? $"{v / 1000:0.#} km" : $"{v:0.#} m";
        return $"{L(w)} × {L(h)}";
    }
}
