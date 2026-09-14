using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>尖灭处理的结果：截断后的线 + 记账。</summary>
public sealed class PinchResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    /// <summary>截断后的线（保留起点这一侧）。</summary>
    public List<(double x, double y)> Line { get; } = new();
    /// <summary>尖灭点（截断处）。</summary>
    public double PinchX, PinchY;
    /// <summary>截掉的长度 m。</summary>
    public double CutLengthM;
    /// <summary>保留的长度 m。</summary>
    public double KeptLengthM;
    public string Note = "";
}

/// <summary>
/// 处理尖灭（原版 <c>HandlePinchDialog</c> + 内核尖灭事件的托管等价）。
///
/// <para>
/// <b>手动</b>：拾取尖灭点 → 该台阶线在离尖灭点最近处<b>截断</b>，尖灭之外的那一段不再画。
/// <b>煤层</b>：给顶板 / 底板面，沿线采厚度 = z顶 − z底，厚度掉到 <c>minThick</c> 以下的第一处即尖灭点，
/// 从那里截断 —— 煤厚 → 0 的地方台阶尖灭于点。
/// </para>
/// <para>
/// <b>★ 截断的是"从起点数过去第一处"</b>，不是"厚度最小处"：一条线两头都出露、中间尖灭时，
/// 取最小处会把两段都留下、中间断掉，那不是一条台阶线了。要处理另一头，把线反过来再来一次。
/// </para>
/// <para>
/// <b>不强制贯通</b>：原版 v2 的"强制贯通"（尖灭处硬连过去）这里不做 —— 贯通出来的那段台阶
/// 在图上看着是台阶，地质上却没有煤，谁也不知道它是编的。
/// </para>
/// </summary>
public static class BenchPinch
{
    /// <summary>手动：在离 (px,py) 最近的位置截断（保留起点侧）。</summary>
    public static PinchResult TruncateAtPoint(IReadOnlyList<(double x, double y)> pts, double px, double py)
    {
        var r = new PinchResult();
        if (pts == null || pts.Count < 2) { r.Error = "线至少 2 点"; return r; }

        // 找最近的线段与投影参数
        int bestSeg = -1; double bestD2 = double.MaxValue, bestT = 0; (double x, double y) bestP = default;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
            double t = len2 < 1e-18 ? 0 : Math.Clamp(((px - a.x) * dx + (py - a.y) * dy) / len2, 0, 1);
            double qx = a.x + dx * t, qy = a.y + dy * t;
            double d2 = (qx - px) * (qx - px) + (qy - py) * (qy - py);
            if (d2 < bestD2) { bestD2 = d2; bestSeg = i; bestT = t; bestP = (qx, qy); }
        }
        if (bestSeg < 0) { r.Error = "找不到最近段"; return r; }

        for (int i = 0; i <= bestSeg; i++) r.Line.Add(pts[i]);
        if (bestT > 1e-9) r.Line.Add(bestP);
        if (r.Line.Count < 2) { r.Error = "尖灭点落在起点上，截断后一段都不剩 —— 请把线反过来（尖灭在另一头）或换点"; r.Line.Clear(); return r; }

        r.PinchX = bestP.x; r.PinchY = bestP.y;
        r.KeptLengthM = Length(r.Line);
        r.CutLengthM = Math.Max(0, Length(pts) - r.KeptLengthM);
        r.Note = $"在离拾取点 {Math.Sqrt(bestD2):0.##} m 处截断：保留 {r.KeptLengthM:0.#} m，截掉 {r.CutLengthM:0.#} m";
        r.Ok = true;
        return r;
    }

    /// <summary>
    /// 煤层：沿线按站距采顶/底板厚度，厚度首次掉到 <paramref name="minThick"/> 以下处截断。
    /// 采不到顶板或底板的站<b>不算尖灭</b>（那是面没盖到，不是煤没了），跳过并计数。
    /// 全线厚度都够 ⇒ 不截、如实说；起点就不够 ⇒ 报错（这条线根本不在煤里）。
    /// </summary>
    public static PinchResult TruncateWhereThin(IReadOnlyList<(double x, double y)> pts,
                                                IRoadZSampler? top, IRoadZSampler? bottom,
                                                double minThick = 0.3, double stepM = 2.0)
    {
        var r = new PinchResult();
        if (pts == null || pts.Count < 2) { r.Error = "线至少 2 点"; return r; }
        if (top == null || bottom == null) { r.Error = "顶板 / 底板面都得有（未指定面）"; return r; }
        stepM = Math.Max(0.1, stepM);

        int missed = 0, sampled = 0;
        double? firstThick = null;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double dx = b.x - a.x, dy = b.y - a.y, seg = Math.Sqrt(dx * dx + dy * dy);
            int sub = Math.Max(1, (int)Math.Ceiling(seg / stepM));
            for (int m = (i == 0 ? 0 : 1); m <= sub; m++)
            {
                double t = (double)m / sub;
                double x = a.x + dx * t, y = a.y + dy * t;
                if (!top.TrySample(x, y, out double zt) || !bottom.TrySample(x, y, out double zb)) { missed++; continue; }
                sampled++;
                double thick = zt - zb;
                firstThick ??= thick;
                if (thick < minThick)
                {
                    if (i == 0 && m == 0) { r.Error = $"起点处煤厚就只有 {thick:0.##} m（< {minThick:0.##}）—— 这条线根本不在煤里，无从截"; return r; }
                    for (int k = 0; k <= i; k++) r.Line.Add(pts[k]);
                    if (t > 1e-9) r.Line.Add((x, y));
                    r.PinchX = x; r.PinchY = y;
                    r.KeptLengthM = Length(r.Line);
                    r.CutLengthM = Math.Max(0, Length(pts) - r.KeptLengthM);
                    r.Note = $"煤厚在此降到 {thick:0.##} m（< {minThick:0.##}）⇒ 尖灭于点：保留 {r.KeptLengthM:0.#} m，截掉 {r.CutLengthM:0.#} m"
                          + (missed > 0 ? $"；{missed} 站顶/底板没盖到（不算尖灭，跳过）" : "");
                    r.Ok = true;
                    return r;
                }
            }
        }
        if (sampled == 0) { r.Error = "全线都采不到顶板 / 底板 —— 这条线不在两张面的范围内"; return r; }
        r.Line.AddRange(pts);
        r.KeptLengthM = Length(pts);
        r.Note = $"全线煤厚都 ≥ {minThick:0.##} m（{sampled} 站），没有尖灭，不截" + (missed > 0 ? $"；{missed} 站顶/底板没盖到" : "");
        r.Ok = true;
        return r;
    }

    private static double Length(IReadOnlyList<(double x, double y)> p)
    {
        double s = 0;
        for (int i = 0; i + 1 < p.Count; i++) s += Math.Sqrt((p[i + 1].x - p[i].x) * (p[i + 1].x - p[i].x) + (p[i + 1].y - p[i].y) * (p[i + 1].y - p[i].y));
        return s;
    }
}
