using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 侧面三角网·弧长拉链放样（忠实移植原 <c>MeshEditLib.Tools.SideSurfaceBuilder.LoftRaw</c>）——
/// 在顶线与底线之间按归一化累计弧长「拉链」缝合成直纹面三角网(开线端点配对/闭环绕向对齐+起点旋转)。
/// 纯几何、可单测。原 Build 包成 PMBI 走内核, 此仅出 (verts, tris)。
/// </summary>
public static class SideSurface
{
    private const double EpsLen2 = 1e-12;

    /// <summary>放样。top/bot=有序 3D 点线; closed=闭环; flip=翻转三角朝向。失败返回空。</summary>
    public static (List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris) Loft(
        IReadOnlyList<(double x, double y, double z)> topLine, IReadOnlyList<(double x, double y, double z)> botLine,
        bool closed, bool flip)
    {
        var verts = new List<(double x, double y, double z)>();
        var tris = new List<(int a, int b, int c)>();
        var top = Dedupe(new List<(double x, double y, double z)>(topLine ?? new List<(double, double, double)>()));
        var bot = Dedupe(new List<(double x, double y, double z)>(botLine ?? new List<(double, double, double)>()));
        if (top.Count < 2 || bot.Count < 2) return (verts, tris);

        Align(top, bot, closed);
        if (closed)
        {
            if (top.Count > 2 && Dist2(top[^1], top[0]) < EpsLen2) top.RemoveAt(top.Count - 1);
            if (bot.Count > 2 && Dist2(bot[^1], bot[0]) < EpsLen2) bot.RemoveAt(bot.Count - 1);
            top.Add(top[0]); bot.Add(bot[0]);
        }
        var tParT = ArcParam(top);
        var tParB = ArcParam(bot);

        int topBase = 0;
        foreach (var p in top) verts.Add(p);
        int botBase = top.Count;
        foreach (var p in bot) verts.Add(p);

        int i = 0, j = 0, lastT = top.Count - 1, lastB = bot.Count - 1;
        while (i < lastT || j < lastB)
        {
            bool canT = i < lastT, canB = j < lastB;
            bool advanceTop;
            if (canT && canB)
            {
                double pT = tParT[i + 1], pB = tParB[j + 1];
                advanceTop = Math.Abs(pT - pB) > 1e-9
                    ? pT < pB
                    : Dist2(top[i + 1], bot[j]) <= Dist2(top[i], bot[j + 1]);
            }
            else advanceTop = canT;

            if (advanceTop) { AddTri(tris, topBase + i, botBase + j, topBase + i + 1, flip); i++; }
            else { AddTri(tris, topBase + i, botBase + j, botBase + j + 1, flip); j++; }
        }
        return (verts, tris);
    }

    private static List<(double x, double y, double z)> Dedupe(List<(double x, double y, double z)> pts)
    {
        var outp = new List<(double x, double y, double z)>(pts.Count);
        foreach (var p in pts)
            if (outp.Count == 0 || Dist2(outp[^1], p) > EpsLen2) outp.Add(p);
        return outp;
    }

    private static void Align(List<(double x, double y, double z)> top, List<(double x, double y, double z)> bot, bool closed)
    {
        if (!closed)
        {
            double straight = Dist2(top[0], bot[0]) + Dist2(top[^1], bot[^1]);
            double reversed = Dist2(top[0], bot[^1]) + Dist2(top[^1], bot[0]);
            if (reversed < straight) bot.Reverse();
            return;
        }
        if (SignedAreaXY(top) * SignedAreaXY(bot) < 0) bot.Reverse();
        int best = 0; double bestD = double.MaxValue;
        for (int k = 0; k < bot.Count; k++)
        {
            double d = Dist2(top[0], bot[k]);
            if (d < bestD) { bestD = d; best = k; }
        }
        if (best > 0)
        {
            var rot = new List<(double x, double y, double z)>(bot.Count);
            for (int k = 0; k < bot.Count; k++) rot.Add(bot[(best + k) % bot.Count]);
            bot.Clear(); bot.AddRange(rot);
        }
    }

    private static double[] ArcParam(List<(double x, double y, double z)> pts)
    {
        var t = new double[pts.Count];
        double acc = 0;
        for (int k = 1; k < pts.Count; k++) { acc += Math.Sqrt(Dist2(pts[k - 1], pts[k])); t[k] = acc; }
        if (acc > 1e-12) for (int k = 0; k < t.Length; k++) t[k] /= acc;
        return t;
    }

    private static double SignedAreaXY(List<(double x, double y, double z)> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            s += a.x * b.y - b.x * a.y;
        }
        return 0.5 * s;
    }

    private static void AddTri(List<(int a, int b, int c)> idx, int a, int b, int c, bool flip)
    {
        if (!flip) idx.Add((a, b, c)); else idx.Add((a, c, b));
    }

    private static double Dist2((double x, double y, double z) a, (double x, double y, double z) b)
    {
        double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx * dx + dy * dy + dz * dz;
    }
}
