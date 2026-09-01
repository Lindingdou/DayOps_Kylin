using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 中线交点分类(捕捉交点的纯几何底子) —— 忠实移植原 RoadLib.Network.CenterlineJunctions。
/// 把一批道路中心线里【建网会打出节点的位置】算成一张可捕捉的点表, 并按四条建网规则分类:
///   X 十字(两段平面内部真相交) · T 丁字(一线端点落在另一线身上) · 半腰焊(两段近贴但两脚在半腰) · 接缝(端点碰端点)。
/// 再就近合并(容差内并成同一节点, 累计度数), 区分【真路口】(X/T/半腰焊/≥3 汇合)与【接缝】(仅两线端点相接=一条路被打断的缝),
/// 并标出【立交】(平面相交但高差超闸门, 建网不在此连通)。
///
/// 区别于 Kylin 既有 <c>RoadNetwork</c> 拓扑报表(只数 度≥3 节点): 本类做四型几何分类 + 立交识别 + 就近合并度数,
/// 供路网设计 QA / 交点捕捉。纯几何、可单测。原用 SegmentGrid 粗筛, 此处内联包围盒预筛(结果一致)。
/// </summary>
public enum JunctionKind { Cross = 0, Tee = 1, MidWeld = 2, Seam = 3 }

/// <summary>图上一个可捕捉的交点。</summary>
public sealed class CenterlineJunction
{
    public JunctionKind Kind { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double ZA { get; init; }
    public double ZB { get; init; }
    public int LineA { get; init; }
    public int LineB { get; init; }
    /// <summary>在这一点碰头的中线条数(=节点的度); 就近合并时累计, 最少 2。</summary>
    public int LineCount { get; internal set; } = 2;
    /// <summary>是否真路口: X/T/半腰焊天然是; 端点碰头需 ≥3 条才算, 正好两条只是接缝。</summary>
    public bool IsRealJunction => Kind != JunctionKind.Seam || LineCount >= 3;
    /// <summary>两线平面缝宽 m: X 十字恒 0; T/半腰焊/接缝是真实缝宽。</summary>
    public double GapM { get; init; }
    /// <summary>立交: 平面相交但高差超闸门 —— 建网不在此打断/连通。</summary>
    public bool GradeSeparated { get; init; }
    public double DzM => Math.Abs(ZA - ZB);

    /// <summary>交点标高: 平交取两线中值; 立交取离点击标高近的那层。</summary>
    public double ZAt(double pickZ)
    {
        if (!GradeSeparated) return (ZA + ZB) * 0.5;
        return Math.Abs(pickZ - ZA) <= Math.Abs(pickZ - ZB) ? ZA : ZB;
    }

    public string KindLabel => Kind switch
    {
        JunctionKind.Cross => "X形交叉",
        JunctionKind.Tee => "T形接点",
        JunctionKind.MidWeld => "半腰焊点",
        _ => LineCount >= 3 ? $"{LineCount}路汇合口" : "接缝",
    };
}

/// <summary>一张图上算出来的全部交点 + 分类计数。</summary>
public sealed class CenterlineJunctionSet
{
    public IReadOnlyList<CenterlineJunction> All { get; init; } = Array.Empty<CenterlineJunction>();
    public int CrossCount { get; init; }
    public int TeeCount { get; init; }
    public int MidWeldCount { get; init; }
    /// <summary>≥3 条中线端点碰头的汇合路口。</summary>
    public int ConfluenceCount { get; init; }
    /// <summary>正好两条中线端点碰头 —— 一条路被打断的缝, 不是路口。</summary>
    public int SeamCount { get; init; }
    /// <summary>真路口合计(X+T+半腰焊+汇合, 不含接缝)。</summary>
    public int JunctionCount => CrossCount + TeeCount + MidWeldCount + ConfluenceCount;
    public int GradeSeparatedCount { get; init; }
    /// <summary>被就近合并掉的重复交点数。</summary>
    public int MergedCount { get; init; }
    public int Count => All.Count;

    public string Summary =>
        $"路口 {JunctionCount}（X形 {CrossCount}/T形 {TeeCount}/半腰焊 {MidWeldCount}/汇合 {ConfluenceCount}）"
        + $" + 接缝 {SeamCount}"
        + (GradeSeparatedCount > 0 ? $"，其中立交 {GradeSeparatedCount}（建网不在此连通）" : "");

    /// <summary>离 (px,py) 最近的可捕捉点(只按平面判距)。半径内先挑真路口, 没有真路口才退给接缝。</summary>
    public CenterlineJunction? Nearest(double px, double py, double radiusM, out double distM)
    {
        distM = double.NaN;
        if (!(radiusM > 0)) return null;
        double r2 = radiusM * radiusM;
        CenterlineJunction? bestJ = null, bestS = null;
        double bestJ2 = double.MaxValue, bestS2 = double.MaxValue;
        foreach (var j in All)
        {
            double dx = j.X - px, dy = j.Y - py;
            double d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;
            if (j.IsRealJunction) { if (d2 < bestJ2) { bestJ2 = d2; bestJ = j; } }
            else if (d2 < bestS2) { bestS2 = d2; bestS = j; }
        }
        var pick = bestJ ?? bestS;
        if (pick is null) return null;
        distM = Math.Sqrt(bestJ != null ? bestJ2 : bestS2);
        return pick;
    }
}

public static class CenterlineJunctions
{
    public const double DefaultContactTolM = 5.0;
    public const double DefaultGradeSeparationM = 4.0;
    /// <summary>就近合并半径缺省记号: &lt;0 = 跟随贴合容差(同建网并节点的那把尺)。</summary>
    public const double DefaultMergeTolM = -1.0;
    private const double TEps = 1e-6;

    /// <summary>lines: 每条中线扁平 [x,y,z, ...](≥2 点)。任何异常都降级返回空集, 不抛。</summary>
    public static CenterlineJunctionSet Build(IReadOnlyList<double[]>? lines,
        double contactTolM = DefaultContactTolM,
        double gradeSeparationM = DefaultGradeSeparationM,
        double mergeTolM = DefaultMergeTolM)
    {
        if (lines == null || lines.Count == 0) return new CenterlineJunctionSet();

        double tol = Math.Max(1e-3, contactTolM);
        double zSep = Math.Max(0.0, gradeSeparationM);
        double merge = mergeTolM < 0 ? tol : mergeTolM;
        var cand = new List<CenterlineJunction>();

        // ── ① X 十字 + ③ 半腰焊(逐段对, 包围盒预筛, 只判 j>i) ──
        for (int i = 0; i < lines.Count; i++)
        {
            var fi = lines[i];
            if (fi == null || fi.Length < 6) continue;
            for (int a = 0; a + 5 < fi.Length; a += 3)
            {
                double ax = fi[a], ay = fi[a + 1], az = fi[a + 2];
                double bx = fi[a + 3], by = fi[a + 4], bz = fi[a + 5];
                double aminx = Math.Min(ax, bx) - tol, amaxx = Math.Max(ax, bx) + tol;
                double aminy = Math.Min(ay, by) - tol, amaxy = Math.Max(ay, by) + tol;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var fj = lines[j];
                    if (fj == null || fj.Length < 6) continue;
                    for (int b = 0; b + 5 < fj.Length; b += 3)
                    {
                        double cx = fj[b], cy = fj[b + 1], cz = fj[b + 2];
                        double dx = fj[b + 3], dy = fj[b + 4], dz = fj[b + 5];
                        if (Math.Max(cx, dx) < aminx || Math.Min(cx, dx) > amaxx ||
                            Math.Max(cy, dy) < aminy || Math.Min(cy, dy) > amaxy) continue;   // 盒不叠

                        if (SegCross2D(ax, ay, bx, by, cx, cy, dx, dy, out double ta, out double tb))
                        {
                            double za = az + (bz - az) * ta;
                            double zb = cz + (dz - cz) * tb;
                            cand.Add(new CenterlineJunction
                            {
                                Kind = JunctionKind.Cross,
                                X = ax + (bx - ax) * ta, Y = ay + (by - ay) * ta,
                                ZA = za, ZB = zb, LineA = i, LineB = j, GapM = 0.0,
                                GradeSeparated = Math.Abs(za - zb) > zSep,
                            });
                            continue;
                        }

                        if (!SegNearest2D(ax, ay, bx, by, cx, cy, dx, dy, out double sa, out double sb, out double gap)) continue;
                        if (gap > tol) continue;
                        double px = ax + (bx - ax) * sa, py = ay + (by - ay) * sa;
                        double qx = cx + (dx - cx) * sb, qy = cy + (dy - cy) * sb;
                        if (NearPolylineEnd(fi, px, py, tol) || NearPolylineEnd(fj, qx, qy, tol)) continue;
                        double pz = az + (bz - az) * sa, qz = cz + (dz - cz) * sb;
                        cand.Add(new CenterlineJunction
                        {
                            Kind = JunctionKind.MidWeld,
                            X = (px + qx) * 0.5, Y = (py + qy) * 0.5,
                            ZA = pz, ZB = qz, LineA = i, LineB = j, GapM = gap,
                            GradeSeparated = Math.Abs(pz - qz) > zSep,
                        });
                    }
                }
            }
        }

        // ── ② T 丁字(每条线两端点投到别线身上, 每条被投线只留最近脚) ──
        var bestPerLine = new Dictionary<int, (double D, double X, double Y, double Z)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var fi = lines[i];
            if (fi == null || fi.Length < 6) continue;
            for (int slot = 0; slot < 2; slot++)
            {
                int e = slot == 0 ? 0 : fi.Length - 3;
                double ex = fi[e], ey = fi[e + 1], ez = fi[e + 2];
                bestPerLine.Clear();
                for (int j = 0; j < lines.Count; j++)
                {
                    if (j == i) continue;
                    var fj = lines[j];
                    if (fj == null || fj.Length < 6) continue;
                    for (int b = 0; b + 5 < fj.Length; b += 3)
                    {
                        NearestOnSeg(ex, ey, fj, b, out double d, out double fx, out double fy, out double fz);
                        if (d > tol) continue;
                        if (!bestPerLine.TryGetValue(j, out var cur) || d < cur.D)
                            bestPerLine[j] = (d, fx, fy, fz);
                    }
                }
                foreach (var kv in bestPerLine)
                {
                    int j = kv.Key;
                    var fj = lines[j];
                    var (d, fx, fy, fz) = kv.Value;
                    if (Near2D(fx, fy, fj[0], fj[1], tol) ||
                        Near2D(fx, fy, fj[fj.Length - 3], fj[fj.Length - 2], tol)) continue;   // 落对方端点 → 交给接缝
                    cand.Add(new CenterlineJunction
                    {
                        Kind = JunctionKind.Tee,
                        X = fx, Y = fy, ZA = ez, ZB = fz,
                        LineA = Math.Min(i, j), LineB = Math.Max(i, j), GapM = d,
                        GradeSeparated = Math.Abs(ez - fz) > zSep,
                    });
                }
            }
        }

        // ── ④ 接缝(端点碰端点, 空间哈希) ──
        var ends = new List<(int Line, double X, double Y, double Z)>(lines.Count * 2);
        for (int i = 0; i < lines.Count; i++)
        {
            var f = lines[i];
            if (f == null || f.Length < 6) continue;
            ends.Add((i, f[0], f[1], f[2]));
            ends.Add((i, f[f.Length - 3], f[f.Length - 2], f[f.Length - 1]));
        }
        var endHash = new Dictionary<(int, int), List<int>>();
        double ecs = Math.Max(tol, 1.0) * 2;
        for (int k = 0; k < ends.Count; k++)
        {
            var key = ((int)Math.Floor(ends[k].X / ecs), (int)Math.Floor(ends[k].Y / ecs));
            if (!endHash.TryGetValue(key, out var l)) { l = new List<int>(); endHash[key] = l; }
            l.Add(k);
        }
        for (int k = 0; k < ends.Count; k++)
        {
            var (li, ex, ey, ez) = ends[k];
            int cx0 = (int)Math.Floor((ex - tol) / ecs), cx1 = (int)Math.Floor((ex + tol) / ecs);
            int cy0 = (int)Math.Floor((ey - tol) / ecs), cy1 = (int)Math.Floor((ey + tol) / ecs);
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    if (!endHash.TryGetValue((cx, cy), out var bucket)) continue;
                    foreach (int m in bucket)
                    {
                        var (lj, fx, fy, fz) = ends[m];
                        if (lj <= li) continue;
                        double d = Math.Sqrt((fx - ex) * (fx - ex) + (fy - ey) * (fy - ey));
                        if (d > tol) continue;
                        cand.Add(new CenterlineJunction
                        {
                            Kind = JunctionKind.Seam,
                            X = (ex + fx) * 0.5, Y = (ey + fy) * 0.5,
                            ZA = ez, ZB = fz, LineA = li, LineB = lj, GapM = d,
                            GradeSeparated = Math.Abs(ez - fz) > zSep,
                        });
                    }
                }
        }

        // ── ⑤ 就近合并 + 定序(全序保"同输入同点必得同点") ──
        cand.Sort((p, q) =>
        {
            int c = ((int)p.Kind).CompareTo((int)q.Kind);
            if (c != 0) return c;
            c = p.LineA.CompareTo(q.LineA); if (c != 0) return c;
            c = p.LineB.CompareTo(q.LineB); if (c != 0) return c;
            c = p.X.CompareTo(q.X); if (c != 0) return c;
            return p.Y.CompareTo(q.Y);
        });

        var kept = new List<CenterlineJunction>();
        var keptLines = new List<HashSet<int>>();
        var keptHash = new Dictionary<(int, int), List<int>>();
        double mcs = Math.Max(merge, 0.5) * 2;
        int merged = 0;
        foreach (var j in cand)
        {
            int host = -1;
            int cx0 = (int)Math.Floor((j.X - merge) / mcs), cx1 = (int)Math.Floor((j.X + merge) / mcs);
            int cy0 = (int)Math.Floor((j.Y - merge) / mcs), cy1 = (int)Math.Floor((j.Y + merge) / mcs);
            for (int cx = cx0; cx <= cx1 && host < 0; cx++)
                for (int cy = cy0; cy <= cy1 && host < 0; cy++)
                {
                    if (!keptHash.TryGetValue((cx, cy), out var bucket)) continue;
                    foreach (int idx in bucket)
                    {
                        var o = kept[idx];
                        double dx = o.X - j.X, dy = o.Y - j.Y;
                        if (dx * dx + dy * dy <= merge * merge) { host = idx; break; }
                    }
                }
            if (host >= 0)
            {
                keptLines[host].Add(j.LineA);
                keptLines[host].Add(j.LineB);
                merged++;
                continue;
            }
            var key = ((int)Math.Floor(j.X / mcs), (int)Math.Floor(j.Y / mcs));
            if (!keptHash.TryGetValue(key, out var kl)) { kl = new List<int>(); keptHash[key] = kl; }
            kl.Add(kept.Count);
            kept.Add(j);
            keptLines.Add(new HashSet<int> { j.LineA, j.LineB });
        }

        int nx = 0, nt = 0, nw = 0, nc = 0, ns = 0, ngs = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            var j = kept[i];
            j.LineCount = keptLines[i].Count;
            switch (j.Kind)
            {
                case JunctionKind.Cross: nx++; break;
                case JunctionKind.Tee: nt++; break;
                case JunctionKind.MidWeld: nw++; break;
                default: if (j.LineCount >= 3) nc++; else ns++; break;
            }
            if (j.GradeSeparated) ngs++;
        }

        return new CenterlineJunctionSet
        {
            All = kept,
            CrossCount = nx, TeeCount = nt, MidWeldCount = nw,
            ConfluenceCount = nc, SeamCount = ns, GradeSeparatedCount = ngs, MergedCount = merged,
        };
    }

    // ── 几何原语 ───────────────────────────────────────────
    private static bool SegCross2D(double p1x, double p1y, double p2x, double p2y,
        double q1x, double q1y, double q2x, double q2y, out double ta, out double tb)
    {
        ta = tb = 0;
        double rx = p2x - p1x, ry = p2y - p1y;
        double sx = q2x - q1x, sy = q2y - q1y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return false;
        double qpx = q1x - p1x, qpy = q1y - p1y;
        ta = (qpx * sy - qpy * sx) / den;
        tb = (qpx * ry - qpy * rx) / den;
        return ta > TEps && ta < 1 - TEps && tb > TEps && tb < 1 - TEps;
    }

    private static bool SegNearest2D(double p1x, double p1y, double p2x, double p2y,
        double q1x, double q1y, double q2x, double q2y, out double sa, out double sb, out double dist)
    {
        Project(q1x, q1y, p1x, p1y, p2x, p2y, out double t1, out double d1);
        Project(q2x, q2y, p1x, p1y, p2x, p2y, out double t2, out double d2);
        Project(p1x, p1y, q1x, q1y, q2x, q2y, out double t3, out double d3);
        Project(p2x, p2y, q1x, q1y, q2x, q2y, out double t4, out double d4);
        dist = d1; sa = t1; sb = 0;
        if (d2 < dist) { dist = d2; sa = t2; sb = 1; }
        if (d3 < dist) { dist = d3; sa = 0; sb = t3; }
        if (d4 < dist) { dist = d4; sa = 1; sb = t4; }
        return true;

        static void Project(double px, double py, double ax, double ay, double bx, double by, out double t, out double d)
        {
            double abx = bx - ax, aby = by - ay;
            double l2 = abx * abx + aby * aby;
            t = l2 < 1e-12 ? 0.0 : ((px - ax) * abx + (py - ay) * aby) / l2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = px - (ax + abx * t), dy = py - (ay + aby * t);
            d = Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private static void NearestOnSeg(double px, double py, double[] f, int si,
        out double dist, out double fx, out double fy, out double fz)
    {
        double ax = f[si], ay = f[si + 1], az = f[si + 2];
        double bx = f[si + 3], by = f[si + 4], bz = f[si + 5];
        double dx = bx - ax, dy = by - ay;
        double l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : ((px - ax) * dx + (py - ay) * dy) / l2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        fx = ax + dx * t; fy = ay + dy * t; fz = az + (bz - az) * t;
        dist = Math.Sqrt((px - fx) * (px - fx) + (py - fy) * (py - fy));
    }

    private static bool Near2D(double ax, double ay, double bx, double by, double tol)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy <= tol * tol;
    }

    private static bool NearPolylineEnd(double[] f, double px, double py, double tol)
        => Near2D(px, py, f[0], f[1], tol) || Near2D(px, py, f[f.Length - 3], f[f.Length - 2], tol);
}
