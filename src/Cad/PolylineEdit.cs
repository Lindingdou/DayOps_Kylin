using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多段线加密纯几何核（对应原 CAD 端 POLYDENSIFY 算子，原走内核, 此为托管重算）：
/// 逐段若长度 &gt; maxStep 则匀分插入内点, 使每子段长 ≤ maxStep。纯函数、可单测。
/// (抽稀/简化见既有 <see cref="PolylineSimplify"/> Douglas-Peucker。)
/// </summary>
public static class PolylineEdit
{
    /// <summary>
    /// 沿源线重采高程：给新点表里的每个点，在源线上找最近的那一段，按投影参数线性插出 z。
    ///
    /// 加密 / 抽稀 / 连接都会换掉点表，三维线的逐点高程必须跟着重算 —— 直接丢掉 z 会把三维线压平，
    /// 按下标搬运又对不上（点数变了）。用"最近段 + 段内线性插值"是这三种改动下都稳的取法：
    /// 加密插的点本来就在原段上（插值即精确）；抽稀留下的点是原顶点子集（取到原值）；
    /// 连接只是首尾相接、点位不动。
    ///
    /// 返回的是**绝对高程**（与 srcZ 同一基准）。写回 PolylineEntity.Zs 时记得减去该实体的 Elevation
    /// —— ZAt = Zs[i] + Elevation，不减就会把标高加两遍。纯函数、可单测。
    /// </summary>
    public static List<double> SampleZAlong(
        IReadOnlyList<(double x, double y)> srcPts, IReadOnlyList<double> srcZ,
        IReadOnlyList<(double x, double y)> dstPts)
    {
        var zs = new List<double>(dstPts?.Count ?? 0);
        if (dstPts == null || dstPts.Count == 0) return zs;
        if (srcPts == null || srcZ == null || srcPts.Count == 0 || srcZ.Count == 0)
        { foreach (var _ in dstPts) zs.Add(0); return zs; }
        if (srcPts.Count == 1) { foreach (var _ in dstPts) zs.Add(srcZ[0]); return zs; }

        foreach (var (x, y) in dstPts)
        {
            double bestD2 = double.MaxValue, bestZ = srcZ[0];
            for (int i = 0; i + 1 < srcPts.Count && i + 1 < srcZ.Count; i++)
            {
                var (ax, ay) = srcPts[i]; var (bx, by) = srcPts[i + 1];
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                double t = len2 > 1e-18 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1) : 0;
                double qx = ax + dx * t, qy = ay + dy * t;
                double d2 = (x - qx) * (x - qx) + (y - qy) * (y - qy);
                if (d2 < bestD2) { bestD2 = d2; bestZ = srcZ[i] + (srcZ[i + 1] - srcZ[i]) * t; }
            }
            zs.Add(bestZ);
        }
        return zs;
    }

    /// <summary>
    /// 绝对高程 → 实体 Zs 的回基：<c>Zs[i] = 绝对Z − 实体标高</c>。
    /// PolylineEntity.ZAt 是 Zs[i] + Elevation，派生新线时若照搬绝对 Z，标高就被加了两遍。
    /// </summary>
    public static List<double> RebaseZ(IReadOnlyList<double> absoluteZ, double elevation)
    {
        var r = new List<double>(absoluteZ?.Count ?? 0);
        if (absoluteZ == null) return r;
        foreach (double z in absoluteZ) r.Add(z - elevation);
        return r;
    }

    /// <summary>
    /// 加密：逐段若长度 &gt; maxStep 则匀分插入内点, 使每子段长 ≤ maxStep。closed 时含闭合段。
    /// 返回新点表(不改输入)。maxStep ≤ 0 或点不足时原样返回。
    /// </summary>
    public static List<(double x, double y)> Densify(IReadOnlyList<(double x, double y)> pts, bool closed, double maxStep)
    {
        var outPts = new List<(double, double)>();
        if (pts == null || pts.Count < 2) { if (pts != null) outPts.AddRange(pts); return outPts; }
        if (maxStep <= 1e-12) { outPts.AddRange(pts); return outPts; }
        int n = pts.Count;
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < segCount; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            outPts.Add(a);
            double dx = b.x - a.x, dy = b.y - a.y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > maxStep)
            {
                int div = (int)Math.Ceiling(len / maxStep);   // 子段数, 每段 ≤ maxStep
                for (int k = 1; k < div; k++)
                {
                    double t = (double)k / div;
                    outPts.Add((a.x + dx * t, a.y + dy * t));
                }
            }
        }
        if (!closed) outPts.Add(pts[n - 1]);   // 开线补末点(闭线末点=起点, 由 Closed 隐含)
        return outPts;
    }
}
