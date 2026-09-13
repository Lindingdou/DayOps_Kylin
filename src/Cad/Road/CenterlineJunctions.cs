// 忠实移植自原 PitMine3D Modules/RoadLib/Network/CenterlineJunctions.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>交点是怎么来的 —— 与 <see cref="RoadGraphBuilder"/> 打节点的四条规则一一对应。</summary>
public enum JunctionKind
{
    /// <summary>X 十字：两条中线的段在平面内部真相交（建网在此打断成同一交点）。</summary>
    Cross = 0,
    /// <summary>T 丁字：一条中线的端点落在另一条身上（建网把被穿过的那条在垂足打断）。</summary>
    Tee = 1,
    /// <summary>半腰焊：两条中线不相交，但最近处贴到容差内、且都落在各自半腰（建网在两脚中点打断）。</summary>
    MidWeld = 2,
    /// <summary>接缝：两条中线的端点碰在一起（建网靠端点吸附并成一个节点）。</summary>
    Seam = 3,
}

/// <summary>图上一个可捕捉的交点。</summary>
public sealed class CenterlineJunction
{
    public JunctionKind Kind { get; init; }

    /// <summary>交点平面位置（捕捉就吸到这里）。</summary>
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>两条线各自在该处的标高（<see cref="LineA"/> / <see cref="LineB"/> 的）。</summary>
    public double ZA { get; init; }
    public double ZB { get; init; }

    /// <summary>参与这个交点的两条中线在输入表里的下标（A &lt; B）。就近合并后是其中<b>代表</b>的那一对。</summary>
    public int LineA { get; init; }
    public int LineB { get; init; }

    /// <summary>
    /// 在这一点上碰头的中线条数（= 建网在此打出的节点的**度**）。就近合并时累计，最少 2。
    ///
    /// <b>为什么必须有这个数</b>：现场那张网是提取器打断出来的，路口在数据里的样子
    /// 不是"两条线交叉"，而是"三条以上中线的端点碰在一起"——量过 1080 条中线：
    /// X 形只有 1 处，其余 1173 处全是端点碰端点。只按几何规则分类的话，
    /// 真路口和"一条直路被打断留下的缝"会长得一模一样（同 [[road-topology-classification]]：
    /// 度 2 是接缝、不是路口）。捕捉表要是不分这两种，用户想吸路口、十有八九吸到一条缝上。
    /// </summary>
    public int LineCount { get; internal set; } = 2;

    /// <summary>
    /// 这是不是<b>真路口</b>：X 形 / T 形 / 半腰焊天然是（建网在此把某条线断开、度必 ≥3），
    /// 端点碰头那种要碰头的中线 ≥3 条才算，正好两条只是一条路被打断留下的<b>接缝</b>。
    /// </summary>
    public bool IsRealJunction => Kind != JunctionKind.Seam || LineCount >= 3;

    /// <summary>两线在该处的平面缝宽 m：X 十字恒 0，T/半腰焊/接缝是真实缝宽。</summary>
    public double GapM { get; init; }

    /// <summary>
    /// 立交：两线在此平面相交但高差超闸门 —— <b>建网不会在这儿打断</b>，
    /// 捕捉过去只拿到一个几何交点，并不会因此连通。故必须打标并由调用方明说，
    /// 不能默默当路口用，也不能悄悄从表里滤掉（滤掉等于回答"这里没有交点"，而真相是"这里的交点连不上"）。
    /// </summary>
    public bool GradeSeparated { get; init; }

    public double DzM => Math.Abs(ZA - ZB);

    /// <summary>
    /// 该交点给出的标高：平交取两线中值（同建网规范化交点口径）；
    /// 立交取<b>离点击标高近的那一层</b> —— 视口取点的 Z 不可信时（图上没有可命中的三角网 → Z≈0）
    /// 这条恒取低层，所以调用方要把"取的是哪一层"回显出来，别让人以为吸到了上层。
    /// </summary>
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

/// <summary>一张图上算出来的全部交点 + 分类计数（回显用）。</summary>
public sealed class CenterlineJunctionSet
{
    public IReadOnlyList<CenterlineJunction> All { get; init; } = Array.Empty<CenterlineJunction>();

    public int CrossCount { get; init; }
    public int TeeCount { get; init; }
    public int MidWeldCount { get; init; }

    /// <summary>≥3 条中线端点碰头的汇合路口（真实网里路口主要长这样）。</summary>
    public int ConfluenceCount { get; init; }

    /// <summary>正好两条中线端点碰头 —— 一条路被打断留下的缝，<b>不是路口</b>。</summary>
    public int SeamCount { get; init; }

    /// <summary>真路口合计（X 形 + T 形 + 半腰焊 + 汇合口，不含接缝）。</summary>
    public int JunctionCount => CrossCount + TeeCount + MidWeldCount + ConfluenceCount;

    /// <summary>其中判为立交的个数（建网不会在此连通）。</summary>
    public int GradeSeparatedCount { get; init; }

    /// <summary>被就近合并掉的重复交点数（三线共点会算出好几个几乎同位的点）。</summary>
    public int MergedCount { get; init; }

    public int Count => All.Count;

    public string Summary =>
        $"路口 {JunctionCount}（X形 {CrossCount}/T形 {TeeCount}/半腰焊 {MidWeldCount}/汇合 {ConfluenceCount}）"
        + $" + 接缝 {SeamCount}"
        + (GradeSeparatedCount > 0 ? $"，其中立交 {GradeSeparatedCount}（建网不在此连通）" : "");

    /// <summary>
    /// 离 (<paramref name="px"/>,<paramref name="py"/>) 最近的可捕捉点（<b>只按平面判距</b>：
    /// 视口取点的 Z 本就可能是 0，掺进来会让"点得挺准却吸不上"）。
    /// <paramref name="radiusM"/> 外一律返回 null —— 点在空处不能顺手吸到几百米外那个路口。
    ///
    /// <b>半径内先挑真路口，没有真路口才退给接缝</b>（不是一律取最近的那个）。
    /// 现场那张网上接缝有一千多个、彼此才隔几米，纯"取最近"等于每次都吸到一条缝上，
    /// 想吸的那个路口明明就在旁边却轮不到它 —— 这条优先级正是为此。
    /// 退到接缝时调用方要照实回显"吸的是接缝"，别让人以为吸到了路口。
    /// </summary>
    public CenterlineJunction? Nearest(double px, double py, double radiusM, out double distM)
    {
        distM = double.NaN;
        if (!(radiusM > 0)) return null;
        double r2 = radiusM * radiusM;

        CenterlineJunction? bestJ = null, bestS = null;
        double bestJ2 = double.MaxValue, bestS2 = double.MaxValue;
        foreach (var j in All)     // 严格 <：并列取靠前那个，两次点同一处必得同一点
        {
            double dx = j.X - px, dy = j.Y - py;
            double d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;                     // 半径含边界：恰好落在半径上算命中
            if (j.IsRealJunction) { if (d2 < bestJ2) { bestJ2 = d2; bestJ = j; } }
            else if (d2 < bestS2) { bestS2 = d2; bestS = j; }
        }
        var pick = bestJ ?? bestS;
        if (pick is null) return null;
        distM = Math.Sqrt(bestJ != null ? bestJ2 : bestS2);
        return pick;
    }
}

/// <summary>
/// 「捕捉交点」的纯几何底子：把一批中心线里<b>建网会打出节点的那些位置</b>算成一张可捕捉的点表。
///
/// <b>口径就是建网口径</b>（<see cref="RoadGraphBuilder"/> noding 的四条规则），一条不多一条不少：
/// <list type="bullet">
///   <item><b>X 十字</b>：两段在平面内部真相交（端点相交让给接缝那条，同建网）。</item>
///   <item><b>T 丁字</b>：一条线的端点落在另一条身上 ≤ 容差，且垂足不在对方端点附近。</item>
///   <item><b>半腰焊</b>：两段不相交、最近处 ≤ 容差且两脚都在各自半腰（建网 R-N3 那条）。</item>
///   <item><b>接缝</b>：两条线的端点碰在一起 ≤ 容差（建网靠端点吸附并成同一个节点）。</item>
/// </list>
/// 于是"能吸上的点" = "吸上去建网真会在此打节点的点"，用户不用猜捕捉到的位置算不算路口。
/// 立交是唯一的例外，见 <see cref="CenterlineJunction.GradeSeparated"/>。
///
/// 纯几何、不碰引擎：可离线单测，也可被别的取点交互复用。
/// </summary>
public static class CenterlineJunctions
{
    /// <summary>贴合容差 m：与会话图 <c>RoadGraphBuilder</c> 的 snapToleranceM 同值。</summary>
    public const double DefaultContactTolM = 5.0;

    /// <summary>立交判定高差 m：同 <c>RoadGraphBuilder</c> 的 gradeSeparationM 默认。</summary>
    public const double DefaultGradeSeparationM = 4.0;

    /// <summary>
    /// 交点就近合并半径 m 的<b>缺省记号</b>：&lt;0 = 跟随贴合容差。
    ///
    /// 为什么跟随而不是写死一个小数：建网就是把容差内的端点<b>并成同一个节点</b>，
    /// 合并半径比容差小的话，一个路口上四条支线的端点（彼此相距 3~8m）会被算成四个捕捉点，
    /// 而建网那边它们只是一个节点 —— 捕捉表就不再等于节点表了。真实网上量过：
    /// 合并半径 2m 时 1174 个点、彼此中位间距只有 3.4m，全是同一批路口被拆开的碎点。
    /// </summary>
    public const double DefaultMergeTolM = -1.0;

    private const double TEps = 1e-6;

    public static CenterlineJunctionSet Build(IReadOnlyList<double[]>? lines,
                                              double contactTolM = DefaultContactTolM,
                                              double gradeSeparationM = DefaultGradeSeparationM,
                                              double mergeTolM = DefaultMergeTolM)
    {
        if (lines == null || lines.Count == 0) return new CenterlineJunctionSet();

        double tol = Math.Max(1e-3, contactTolM);
        double zSep = Math.Max(0.0, gradeSeparationM);
        double merge = mergeTolM < 0 ? tol : mergeTolM;    // <0 = 跟随贴合容差（同建网并节点的那把尺）

        var grid = SegmentGrid.Build(lines, tol * 2);
        var cand = new List<CenterlineJunction>();

        // ── ① X 十字 + ③ 半腰焊：逐段对（格索引粗筛，只判 j > i，每对一次）──
        for (int i = 0; i < lines.Count; i++)
        {
            var fi = lines[i];
            if (fi == null || fi.Length < 6) continue;
            for (int a = 0; a + 5 < fi.Length; a += 3)
            {
                double ax = fi[a], ay = fi[a + 1], az = fi[a + 2];
                double bx = fi[a + 3], by = fi[a + 4], bz = fi[a + 5];
                foreach (var (j, b) in grid.QueryBox(Math.Min(ax, bx) - tol, Math.Min(ay, by) - tol,
                                                     Math.Max(ax, bx) + tol, Math.Max(ay, by) + tol))
                {
                    if (j <= i) continue;
                    var fj = lines[j];
                    double cx = fj[b], cy = fj[b + 1], cz = fj[b + 2];
                    double dx = fj[b + 3], dy = fj[b + 4], dz = fj[b + 5];

                    if (SegCross2D(ax, ay, bx, by, cx, cy, dx, dy, out double ta, out double tb))
                    {
                        double za = az + (bz - az) * ta;
                        double zb = cz + (dz - cz) * tb;
                        cand.Add(new CenterlineJunction
                        {
                            Kind = JunctionKind.Cross,
                            X = ax + (bx - ax) * ta,
                            Y = ay + (by - ay) * ta,
                            ZA = za,
                            ZB = zb,
                            LineA = i,
                            LineB = j,
                            GapM = 0.0,
                            GradeSeparated = Math.Abs(za - zb) > zSep,
                        });
                        continue;
                    }

                    // 半腰焊：两段最近处 ≤ tol，且两脚都不在各自折线的首末点附近
                    // （落端点附近的让给 T / 接缝那两条，否则同一处会按三条规则各出一个点）。
                    if (!SegNearest2D(ax, ay, bx, by, cx, cy, dx, dy,
                                      out double sa, out double sb, out double gap)) continue;
                    if (gap > tol) continue;
                    double px = ax + (bx - ax) * sa, py = ay + (by - ay) * sa;
                    double qx = cx + (dx - cx) * sb, qy = cy + (dy - cy) * sb;
                    if (NearPolylineEnd(fi, px, py, tol) || NearPolylineEnd(fj, qx, qy, tol)) continue;
                    double pz = az + (bz - az) * sa, qz = cz + (dz - cz) * sb;
                    cand.Add(new CenterlineJunction
                    {
                        Kind = JunctionKind.MidWeld,
                        X = (px + qx) * 0.5,
                        Y = (py + qy) * 0.5,
                        ZA = pz,
                        ZB = qz,
                        LineA = i,
                        LineB = j,
                        GapM = gap,
                        GradeSeparated = Math.Abs(pz - qz) > zSep,
                    });
                }
            }
        }

        // ── ② T 丁字：每条线两端点投影到别的线身上（每条被投的线只留最近那一脚）──
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
                foreach (var (j, b) in grid.Query(ex, ey, tol))
                {
                    if (j == i) continue;
                    var fj = lines[j];
                    NearestOnSeg(ex, ey, fj, b, out double d, out double fx, out double fy, out double fz);
                    if (d > tol) continue;
                    if (!bestPerLine.TryGetValue(j, out var cur) || d < cur.D)
                        bestPerLine[j] = (d, fx, fy, fz);
                }
                foreach (var kv in bestPerLine)
                {
                    int j = kv.Key;
                    var fj = lines[j];
                    var (d, fx, fy, fz) = kv.Value;
                    // 垂足落在对方端点附近 → 那是接缝（端点吸附），交给 ④，别在这儿再记一遍。
                    if (Near2D(fx, fy, fj[0], fj[1], tol) ||
                        Near2D(fx, fy, fj[fj.Length - 3], fj[fj.Length - 2], tol)) continue;
                    cand.Add(new CenterlineJunction
                    {
                        Kind = JunctionKind.Tee,
                        X = fx,
                        Y = fy,
                        ZA = ez,
                        ZB = fz,
                        LineA = Math.Min(i, j),
                        LineB = Math.Max(i, j),
                        GapM = d,
                        GradeSeparated = Math.Abs(ez - fz) > zSep,
                    });
                }
            }
        }

        // ── ④ 接缝：端点碰端点 ──
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
                        if (lj <= li) continue;                       // 每对线只判一次，也排掉自己首尾相接的闭环
                        double d = Math.Sqrt((fx - ex) * (fx - ex) + (fy - ey) * (fy - ey));
                        if (d > tol) continue;
                        cand.Add(new CenterlineJunction
                        {
                            Kind = JunctionKind.Seam,
                            X = (ex + fx) * 0.5,
                            Y = (ey + fy) * 0.5,
                            ZA = ez,
                            ZB = fz,
                            LineA = li,
                            LineB = lj,
                            GapM = d,
                            GradeSeparated = Math.Abs(ez - fz) > zSep,
                        });
                    }
                }
        }

        // ── ⑤ 就近合并 + 定序 ──
        // 排序不是为了好看：捕捉要做到"同一份输入、同一处点击必得同一个点"，
        // 而合并是"先到先得" —— 没有全序的话，线号迭代顺序一变，留下来的代表点就跟着变。
        cand.Sort((p, q) =>
        {
            int c = ((int)p.Kind).CompareTo((int)q.Kind);          // 路口性强的先留（X > T > 半腰焊 > 接缝）
            if (c != 0) return c;
            c = p.LineA.CompareTo(q.LineA); if (c != 0) return c;
            c = p.LineB.CompareTo(q.LineB); if (c != 0) return c;
            c = p.X.CompareTo(q.X); if (c != 0) return c;
            return p.Y.CompareTo(q.Y);
        });

        var kept = new List<CenterlineJunction>();
        var keptLines = new List<HashSet<int>>();          // 每个代表点上碰头的中线集合（= 节点的度）
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
                // 合并掉的候选**不是**扔掉：它带来的那两条线要记进代表点的度里，
                // 否则"四条支线汇一个路口"会退化成"两条线的接缝"，真路口反而被判成缝。
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
            CrossCount = nx,
            TeeCount = nt,
            MidWeldCount = nw,
            ConfluenceCount = nc,
            SeamCount = ns,
            GradeSeparatedCount = ngs,
            MergedCount = merged,
        };
    }

    // ── 几何原语（与 CenterlineInventory / RoadGraphBuilder 同口径）───────────────

    /// <summary>二维线段"内部"真相交（端点相交交给接缝那条，这里刻意排除，同 RoadGraphBuilder）。</summary>
    private static bool SegCross2D(double p1x, double p1y, double p2x, double p2y,
                                   double q1x, double q1y, double q2x, double q2y,
                                   out double ta, out double tb)
    {
        ta = tb = 0;
        double rx = p2x - p1x, ry = p2y - p1y;
        double sx = q2x - q1x, sy = q2y - q1y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return false;                  // 平行 / 共线
        double qpx = q1x - p1x, qpy = q1y - p1y;
        ta = (qpx * sy - qpy * sx) / den;
        tb = (qpx * ry - qpy * rx) / den;
        return ta > TEps && ta < 1 - TEps && tb > TEps && tb < 1 - TEps;
    }

    /// <summary>
    /// 两段在 XY 上的最近点对（参数 + 距离）。两段不相交时最近点必落在<b>某一端点</b>上，
    /// 故四次"端点投影到对段"就是精确解 —— 相交的那种已被 <see cref="SegCross2D"/> 接走。
    /// </summary>
    private static bool SegNearest2D(double p1x, double p1y, double p2x, double p2y,
                                     double q1x, double q1y, double q2x, double q2y,
                                     out double sa, out double sb, out double dist)
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

        static void Project(double px, double py, double ax, double ay, double bx, double by,
                            out double t, out double d)
        {
            double abx = bx - ax, aby = by - ay;
            double l2 = abx * abx + aby * aby;
            t = l2 < 1e-12 ? 0.0 : ((px - ax) * abx + (py - ay) * aby) / l2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = px - (ax + abx * t), dy = py - (ay + aby * t);
            d = Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>点到第 <paramref name="si"/> 段的 XY 距离 + 垂足（含按段插值的 Z）。</summary>
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

    /// <summary>点是否落在折线的<b>首末点</b>附近（⚠ 不是段的端点：折线内部的折点离另一条线 4m 也没人管，同 RoadGraphBuilder 的那条注释）。</summary>
    private static bool NearPolylineEnd(double[] f, double px, double py, double tol)
        => Near2D(px, py, f[0], f[1], tol) || Near2D(px, py, f[f.Length - 3], f[f.Length - 2], tol);
}
