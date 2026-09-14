using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「创建工程位置」⑦ **台阶面**的几何层（忠实移植原 <c>BenchFaceBuilder</c>，纯几何、可脱 GUI）。
///
/// 一句话：把人成对指定的坡顶线 / 坡底线，各自**沿衔接链拼成一条完整链**（原线 → 衔接段 → 下一条原线…），再在两条链之间按弧长逐站配对放样成三角带。
/// 衔接判成 <see cref="EpConnector.Overlapped"/>（两条线本身已经交叉）时不插几何，改为把两侧越过交点的尾巴裁掉 —— 不裁的话放样出来的面自己穿自己。
/// </summary>
public static class BenchFaceBuilder
{
    private const int MaxStations = 2000;
    private const double StationStep = 2.0;

    /// <summary>自 seed 出发，沿连线图往两头把整条链拼出来（连线图已由环路闸保证无环，仍留 visited 兜底）。</summary>
    public static List<(double X, double Y, double Z)> StitchChain(EpPolyline seed, EpScene? scene, IReadOnlyList<EpConnector>? cons, IReadOnlyDictionary<ulong, EpPolyline> byHandle)
    {
        var mid = ToPts(seed.Xyz);
        if (scene == null || mid.Count < 2) return mid;
        var visited = new HashSet<ulong> { seed.Handle };
        var tail = Walk(seed, fromHead: false, scene, cons, byHandle, visited, out double trimTail);
        var head = Walk(seed, fromHead: true, scene, cons, byHandle, visited, out double trimHead);
        if (trimHead > 1e-6) mid = TrimFrom(mid, true, trimHead) ?? mid;
        if (trimTail > 1e-6) mid = TrimFrom(mid, false, trimTail) ?? mid;
        var outp = new List<(double X, double Y, double Z)>(head.Count + mid.Count + tail.Count);
        for (int i = head.Count - 1; i >= 0; i--) outp.Add(head[i]);
        outp.AddRange(mid);
        outp.AddRange(tail);
        return Dedup(outp);
    }

    private static List<(double X, double Y, double Z)> Walk(EpPolyline cur, bool fromHead, EpScene scene, IReadOnlyList<EpConnector>? cons,
                                                             IReadOnlyDictionary<ulong, EpPolyline> byHandle, HashSet<ulong> visited, out double trimSelf)
    {
        var acc = new List<(double X, double Y, double Z)>();
        trimSelf = 0;
        bool first = true;
        while (true)
        {
            var here = EndPoint(cur, fromHead);
            var node = NodeAt(scene, cur.Handle, here);
            if (node == null) break;
            var link = FindLink(scene, node.Id);
            if (link == null) break;
            string otherId = link.TemplateId == node.Id ? link.WallId : link.TemplateId;
            var other = scene.ById(otherId);
            if (other == null) break;
            ulong nextH = HandleOf(other);
            if (nextH == 0 || !byHandle.TryGetValue(nextH, out var next)) break;
            if (!visited.Add(nextH)) break;

            var con = FindConnector(cons, node.Id, otherId);
            double trimNext = 0;
            if (con != null && con.Overlapped)
            {
                double tSelf = con.TemplateId == node.Id ? con.TrimA : con.TrimB;
                double tNext = con.TemplateId == node.Id ? con.TrimB : con.TrimA;
                if (first) trimSelf = tSelf;
                else if (tSelf > 1e-6 && acc.Count >= 2)
                {
                    var cut = TrimFrom(acc, false, tSelf);
                    if (cut != null) { acc.Clear(); acc.AddRange(cut); }
                }
                trimNext = tNext;
            }
            else if (con != null && con.Pts.Count >= 2)
            {
                var cp = new List<(double X, double Y, double Z)>(con.Pts);
                if (Dist2(cp[0], here) > Dist2(cp[cp.Count - 1], here)) cp.Reverse();
                for (int i = 1; i < cp.Count; i++) acc.Add(cp[i]);
            }

            bool nextFromHead = IsHead(next, (other.X, other.Y, other.Z));
            var np = ToPts(next.Xyz);
            if (!nextFromHead) np.Reverse();
            if (trimNext > 1e-6) np = TrimFrom(np, true, trimNext) ?? np;
            for (int i = 0; i < np.Count; i++) acc.Add(np[i]);

            cur = next;
            fromHead = !nextFromHead;
            first = false;
        }
        return acc;
    }

    /// <summary>在坡顶链与坡底链之间放样成三角带（按弧长比例逐站配对；方向先对齐），顶点/索引追加到调用方缓冲。返回面积 m²。</summary>
    public static double Ribbon(List<(double X, double Y, double Z)> crest, List<(double X, double Y, double Z)> toe, List<double> verts, List<int> tris)
    {
        if (crest.Count < 2 || toe.Count < 2) return 0;
        if (Dist2(crest[0], toe[0]) > Dist2(crest[0], toe[toe.Count - 1])) { toe = new List<(double, double, double)>(toe); toe.Reverse(); }
        double lc = Length(crest), lt = Length(toe);
        int n = (int)Math.Ceiling(Math.Max(lc, lt) / StationStep) + 1;
        if (n < 2) n = 2;
        if (n > MaxStations) n = MaxStations;
        int baseIdx = verts.Count / 3;
        for (int i = 0; i < n; i++)
        {
            double u = n == 1 ? 0 : (double)i / (n - 1);
            var pc = At(crest, u * lc);
            var pt = At(toe, u * lt);
            verts.Add(pc.X); verts.Add(pc.Y); verts.Add(pc.Z);
            verts.Add(pt.X); verts.Add(pt.Y); verts.Add(pt.Z);
        }
        double area = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            int c0 = baseIdx + i * 2, t0 = c0 + 1, c1 = c0 + 2, t1 = c0 + 3;
            tris.Add(c0); tris.Add(t0); tris.Add(c1);
            tris.Add(c1); tris.Add(t0); tris.Add(t1);
            area += TriArea(verts, c0, t0, c1) + TriArea(verts, c1, t0, t1);
        }
        return area;
    }

    private static List<(double X, double Y, double Z)> ToPts(double[] xyz)
    {
        var l = new List<(double, double, double)>(xyz.Length / 3);
        for (int i = 0; i + 2 < xyz.Length; i += 3) l.Add((xyz[i], xyz[i + 1], xyz[i + 2]));
        return l;
    }

    private static List<(double X, double Y, double Z)> Dedup(List<(double X, double Y, double Z)> p)
    {
        var o = new List<(double X, double Y, double Z)>(p.Count);
        foreach (var q in p) if (o.Count == 0 || Dist2(o[o.Count - 1], q) > 1e-12) o.Add(q);
        return o;
    }

    private static (double X, double Y, double Z) EndPoint(EpPolyline pl, bool head)
    {
        int n = pl.Xyz.Length / 3;
        int i = head ? 0 : n - 1;
        return (pl.Xyz[i * 3], pl.Xyz[i * 3 + 1], pl.Xyz[i * 3 + 2]);
    }

    private static bool IsHead(EpPolyline pl, (double X, double Y, double Z) p) => Dist2(EndPoint(pl, true), p) <= Dist2(EndPoint(pl, false), p);

    private static EpNode? NodeAt(EpScene s, ulong handle, (double X, double Y, double Z) p)
    {
        EpNode? best = null; double bd = double.MaxValue;
        foreach (var n in s.Nodes)
            foreach (var m in n.Members)
            {
                if (m.Handle != handle) continue;
                double d = Dist2((m.X, m.Y, m.Z), p);
                if (d < bd) { bd = d; best = n; }
            }
        return bd <= 1e-6 ? best : (bd < 1e6 ? best : null);
    }

    private static EpLink? FindLink(EpScene s, string nodeId)
    {
        foreach (var l in s.Links) if (l.TemplateId == nodeId || l.WallId == nodeId) return l;
        return null;
    }

    private static EpConnector? FindConnector(IReadOnlyList<EpConnector>? cons, string a, string b)
    {
        if (cons == null) return null;
        foreach (var c in cons) if ((c.TemplateId == a && c.WallId == b) || (c.TemplateId == b && c.WallId == a)) return c;
        return null;
    }

    private static ulong HandleOf(EpNode n)
    {
        foreach (var m in n.Members) if (m.Handle != 0) return m.Handle;
        return 0;
    }

    private static List<(double X, double Y, double Z)>? TrimFrom(List<(double X, double Y, double Z)> pts, bool fromHead, double dist) => EpConnectorBuilder.TrimFrom(pts, fromHead, dist);

    private static double Length(List<(double X, double Y, double Z)> p)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++) s += Math.Sqrt(Dist2(p[i - 1], p[i]));
        return s;
    }

    private static (double X, double Y, double Z) At(List<(double X, double Y, double Z)> p, double s)
    {
        if (p.Count == 1 || s <= 0) return p[0];
        double acc = 0;
        for (int i = 1; i < p.Count; i++)
        {
            double seg = Math.Sqrt(Dist2(p[i - 1], p[i]));
            if (acc + seg >= s - 1e-12 && seg > 1e-12)
            {
                double f = (s - acc) / seg;
                return (p[i - 1].X + (p[i].X - p[i - 1].X) * f, p[i - 1].Y + (p[i].Y - p[i - 1].Y) * f, p[i - 1].Z + (p[i].Z - p[i - 1].Z) * f);
            }
            acc += seg;
        }
        return p[p.Count - 1];
    }

    private static double Dist2((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static double TriArea(List<double> v, int i0, int i1, int i2)
    {
        double ax = v[i1 * 3] - v[i0 * 3], ay = v[i1 * 3 + 1] - v[i0 * 3 + 1], az = v[i1 * 3 + 2] - v[i0 * 3 + 2];
        double bx = v[i2 * 3] - v[i0 * 3], by = v[i2 * 3 + 1] - v[i0 * 3 + 1], bz = v[i2 * 3 + 2] - v[i0 * 3 + 2];
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }
}
