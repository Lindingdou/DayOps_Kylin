using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 曲线平滑（Chaikin 角点切割）—— 每段用 1/4、3/4 两点替换，迭代逼近光滑曲线。
/// 开口保留首末端点；闭合环绕。纯逻辑、可单测。
/// </summary>
public static class PolylineSmooth
{
    public static List<(double x, double y)> Chaikin(IReadOnlyList<(double x, double y)> input, int iterations, bool closed)
    {
        var pts = new List<(double x, double y)>(input);
        if (pts.Count < 2) return pts;
        for (int it = 0; it < iterations; it++)
        {
            var np = new List<(double x, double y)>();
            int n = pts.Count;
            if (!closed) np.Add(pts[0]);
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % n];
                np.Add((p.x + 0.25 * (q.x - p.x), p.y + 0.25 * (q.y - p.y)));
                np.Add((p.x + 0.75 * (q.x - p.x), p.y + 0.75 * (q.y - p.y)));
            }
            if (!closed) np.Add(pts[^1]);
            pts = np;
        }
        return pts;
    }

    /// <summary>
    /// Catmull-Rom 插值样条平滑 —— 生成的曲线**过所有输入点**(区别于 Chaikin 逼近/收缩)，
    /// 每段插 samplesPerSeg 个中间点。开口端点重复端点作控制点。纯逻辑、可单测。
    /// </summary>
    public static List<(double x, double y)> CatmullRom(IReadOnlyList<(double x, double y)> input, int samplesPerSeg, bool closed)
    {
        var res = new List<(double x, double y)>();
        int n = input.Count;
        if (n < 2) { res.AddRange(input); return res; }
        if (samplesPerSeg < 1) samplesPerSeg = 8;

        (double x, double y) P(int i) => closed ? input[((i % n) + n) % n] : input[i < 0 ? 0 : i >= n ? n - 1 : i];
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var p0 = P(i - 1); var p1 = P(i); var p2 = P(i + 1); var p3 = P(i + 2);
            for (int s = 0; s < samplesPerSeg; s++)
            {
                double t = (double)s / samplesPerSeg;
                double t2 = t * t, t3 = t2 * t;
                double x = 0.5 * ((2 * p1.x) + (-p0.x + p2.x) * t + (2 * p0.x - 5 * p1.x + 4 * p2.x - p3.x) * t2 + (-p0.x + 3 * p1.x - 3 * p2.x + p3.x) * t3);
                double y = 0.5 * ((2 * p1.y) + (-p0.y + p2.y) * t + (2 * p0.y - 5 * p1.y + 4 * p2.y - p3.y) * t2 + (-p0.y + 3 * p1.y - 3 * p2.y + p3.y) * t3);
                res.Add((x, y));   // s=0 时即 p1(过数据点)
            }
        }
        if (!closed) res.Add(input[^1]);   // 补末端点
        return res;
    }
}
