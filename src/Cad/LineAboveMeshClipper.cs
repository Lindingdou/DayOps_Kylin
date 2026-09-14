using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.SeamOutcrop;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 沿交线裁线（忠实移植原 <c>LineAboveMeshClipper</c> = 内核 <c>PitDesign::ClipLineAboveTerrain</c> 的镜像）：
/// 把一条折线按 <c>d = z_线 − z_面</c> 的零点切开，只留 d≥0（在面之上）的那些段。
///
/// 用在哪：「新建工程位置」排土档拿【排土场坡面】裁【图上旧台阶线】—— 旧线落在排土体之下 = 被埋，该从合成图里去掉。
/// 口径：① 逐段判两端符号，异号才在零点切；② 平行/共线不特殊处理；③ <paramref name="keepUncovered"/> 决定"投影没命中"算在面上还是面下。
/// </summary>
internal static class LineAboveMeshClipper
{
    /// <param name="keepUncovered">面没盖到该点时怎么算：裁旧线 vs 排土面 → true（轮廓外 = 没被埋，留着）；裁排土线 vs 现状面 → false。</param>
    public static List<double[]> Clip(double[] xyz, bool closed, SeamOutcrop.MeshZSampler? mesh, bool keepUncovered)
    {
        var outSegs = new List<double[]>();
        if (xyz == null || xyz.Length < 6) return outSegs;
        int n = xyz.Length / 3;
        if (mesh == null || mesh.IsEmpty)
        {
            outSegs.Add((double[])xyz.Clone());        // 没有面可裁 → 原样一段。绝不当成"全被埋"把线删光
            return outSegs;
        }

        var d = new double[n];
        for (int i = 0; i < n; i++)
            d[i] = mesh.TrySampleZ(xyz[i * 3], xyz[i * 3 + 1], out double mz) ? xyz[i * 3 + 2] - mz : (keepUncovered ? double.MaxValue : double.MinValue);

        var cur = new List<double>();
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < segCount; i++)
        {
            int j = (i + 1) % n;
            bool inA = d[i] >= 0, inB = d[j] >= 0;
            if (inA) PushIfNew(cur, xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);
            if (inA && inB) continue;
            if (inA)
            {
                var p = LerpZero(xyz, i, j, d[i], d[j]);
                PushIfNew(cur, p.x, p.y, p.z);
                if (cur.Count >= 6) outSegs.Add(cur.ToArray());
                cur.Clear();
            }
            else if (inB)
            {
                cur.Clear();
                var p = LerpZero(xyz, i, j, d[i], d[j]);
                PushIfNew(cur, p.x, p.y, p.z);
            }
        }
        if (!closed && d[n - 1] >= 0 && cur.Count > 0) PushIfNew(cur, xyz[(n - 1) * 3], xyz[(n - 1) * 3 + 1], xyz[(n - 1) * 3 + 2]);
        if (cur.Count >= 6) outSegs.Add(cur.ToArray());
        return outSegs;
    }

    private static (double x, double y, double z) LerpZero(double[] xyz, int i, int j, double di, double dj)
    {
        double den = di - dj;
        double t = (double.IsInfinity(den) || double.IsNaN(den) || Math.Abs(den) < 1e-12) ? 0.5 : di / den;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return (xyz[i * 3] + (xyz[j * 3] - xyz[i * 3]) * t, xyz[i * 3 + 1] + (xyz[j * 3 + 1] - xyz[i * 3 + 1]) * t, xyz[i * 3 + 2] + (xyz[j * 3 + 2] - xyz[i * 3 + 2]) * t);
    }

    private static void PushIfNew(List<double> buf, double x, double y, double z)
    {
        int m = buf.Count;
        if (m >= 3 && buf[m - 3] == x && buf[m - 2] == y && buf[m - 1] == z) return;
        buf.Add(x); buf.Add(y); buf.Add(z);
    }
}
