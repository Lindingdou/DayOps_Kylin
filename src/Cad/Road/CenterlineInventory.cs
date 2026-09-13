// 忠实移植自原 PitMine3D Modules/RoadLib/Network/CenterlineInventory.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>「中心线管理」清单里的一行：一条中线自己答得出的那几个量。</summary>
public sealed class CenterlineRow
{
    /// <summary>在输入表里的下标（= 清单里的序号 - 1，删除/选中都按它回指原始条目）。</summary>
    public int Index;

    public double LengthM;
    public int VertexCount;

    public double MinZ, MaxZ;

    /// <summary>逐段纵坡绝对值的最大值 (%)：水平投影为 0 的竖直段记 999（用来一眼抓出退化线）。</summary>
    public double MaxGradePct;

    public double StartX, StartY, StartZ;
    public double EndX, EndY, EndZ;

    /// <summary>首尾重合（闭环，<b>按三维判</b>）：没有端点，故不参与悬空判定。
    /// 只按 XY 判会把"画废了的竖直线"（首尾同 XY、差着几十米标高）误判成闭环，
    /// 于是它两头明明都悬空却一处不报。</summary>
    public bool IsClosed;

    /// <summary>悬空端点数 0/1/2：该端点 <see cref="CenterlineInventory.DefaultSnapTolM"/> 内够不着任何别的中线。</summary>
    public int LooseEnds;

    /// <summary>连通片号（0 = 总长最大的那片）。同片的中线彼此走得通。</summary>
    public int ComponentId;

    /// <summary>本条所属连通片的中线条数（1 = 孤立线）。</summary>
    public int ComponentLineCount;
}

/// <summary>中心线清单的整体统计（窗口页脚回显）。</summary>
public sealed class CenterlineInventoryResult
{
    public IReadOnlyList<CenterlineRow> Rows { get; init; } = Array.Empty<CenterlineRow>();
    public int ComponentCount { get; init; }
    public int LooseEndCount { get; init; }
    public double TotalLengthM { get; init; }
}

/// <summary>
/// 把一批中线折线摊成「一条一行」的清单：长度 / 顶点 / 高程 / 最大纵坡 / 连通片 / 悬空端点。
/// 纯几何，不碰引擎，供「中心线管理」窗口与单测共用。
///
/// <b>连通口径与建网对齐但不含桥接</b>：这里判「贴上了」用的是 <see cref="RoadGraphBuilder"/> noding
/// 的同两条规则 —— ① 端点落到别的线身上 ≤<see cref="DefaultSnapTolM"/>（T 形）；
/// ② 两线在 XY 内部真相交（X 形）。两者都要过标高闸门 <see cref="DefaultGradeSeparationM"/>，
/// 免得把立交的上下层判成同一片。
/// <b>但不做建网那步 25m 缺口桥接</b> —— 桥接是"补一段本来没有的路"，清单是"图上现在有什么"，
/// 混进来就答不了"这条线到底接没接上"。因此本清单的片数 ≥「基础道路网络构建」回显的片数，
/// 差出来的那几片正是靠桥接才连上的（页脚已写明这条）。
/// </summary>
public static class CenterlineInventory
{
    /// <summary>端点吸附容差 (m)：与 <c>TryBuildGraph</c> 传给 RoadGraphBuilder 的 snapToleranceM 同值。</summary>
    public const double DefaultSnapTolM = 5.0;

    /// <summary>立交判定标高差 (m)：XY 贴上但高差超它 → 上下层，不算连通（同 RoadGraphBuilder 默认）。</summary>
    public const double DefaultGradeSeparationM = 4.0;

    public static CenterlineInventoryResult Build(IReadOnlyList<double[]>? lines,
                                                  double snapTolM = DefaultSnapTolM,
                                                  double gradeSeparationM = DefaultGradeSeparationM)
    {
        var rows = new List<CenterlineRow>();
        if (lines == null || lines.Count == 0)
            return new CenterlineInventoryResult { Rows = rows };

        double tol = Math.Max(1e-3, snapTolM);
        double zSep = Math.Max(0.0, gradeSeparationM);

        // ── ① 逐条的自身量 ──
        for (int i = 0; i < lines.Count; i++)
        {
            var f = lines[i];
            if (f == null || f.Length < 6) continue;
            int n = f.Length / 3;

            double zMin = double.MaxValue, zMax = double.MinValue, maxGrade = 0;
            for (int k = 0; k < n; k++)
            {
                double z = f[3 * k + 2];
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
            }
            for (int s = 0; s + 5 < f.Length; s += 3)
            {
                double dx = f[s + 3] - f[s], dy = f[s + 4] - f[s + 1], dz = f[s + 5] - f[s + 2];
                double run = Math.Sqrt(dx * dx + dy * dy);
                double g = run < 1e-6 ? (Math.Abs(dz) > 1e-6 ? 999.0 : 0.0) : Math.Abs(dz) / run * 100.0;
                if (g > maxGrade) maxGrade = g;
            }

            rows.Add(new CenterlineRow
            {
                Index = i,
                LengthM = CenterlinePick.Length3d(f),
                VertexCount = n,
                MinZ = zMin,
                MaxZ = zMax,
                MaxGradePct = maxGrade,
                StartX = f[0], StartY = f[1], StartZ = f[2],
                EndX = f[f.Length - 3], EndY = f[f.Length - 2], EndZ = f[f.Length - 1],
                IsClosed = Dist2(f[0], f[1], f[2], f[f.Length - 3], f[f.Length - 2], f[f.Length - 1]) <= tol * tol,
            });
        }
        if (rows.Count == 0) return new CenterlineInventoryResult { Rows = rows };

        // ── ② 连通：端点贴线（T）+ 段段相交（X），并查集聚片 ──
        // 段索引与「捕捉交点」共用同一份（SegmentGrid）：两边判的都是"段与段贴上没有"，
        // 各留一份索引早晚漂成两套格边口径 —— 那种漂移不报错，只会让"清单说连着、捕捉说没交点"。
        var grid = SegmentGrid.Build(lines, tol * 2);
        var rowOfLine = new int[lines.Count];      // 索引按**线号**回报，退化线没有行 → -1
        for (int i = 0; i < rowOfLine.Length; i++) rowOfLine[i] = -1;
        for (int r = 0; r < rows.Count; r++) rowOfLine[rows[r].Index] = r;
        var uf = new int[rows.Count];
        for (int i = 0; i < uf.Length; i++) uf[i] = i;
        int Find(int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) uf[ra] = rb; }

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.IsClosed) continue;                      // 闭环没有端点

            for (int slot = 0; slot < 2; slot++)
            {
                double px = slot == 0 ? row.StartX : row.EndX;
                double py = slot == 0 ? row.StartY : row.EndY;
                double pz = slot == 0 ? row.StartZ : row.EndZ;
                bool attached = false;
                foreach (var (lj, si) in grid.Query(px, py, tol))
                {
                    int rj = rowOfLine[lj];
                    if (rj < 0 || rj == r) continue;
                    var g = lines[lj];
                    NearestOnSeg(px, py, g, si, out double d, out double zOnSeg);
                    if (d > tol || Math.Abs(zOnSeg - pz) > zSep) continue;
                    attached = true;
                    Union(r, rj);
                }
                if (!attached) row.LooseEnds++;
            }
        }

        // X 形交叉：段与段在 XY 内部真相交且标高接近 → 同片（建网会在这儿打断成节点）。
        for (int r = 0; r < rows.Count; r++)
        {
            var f = lines[rows[r].Index];
            for (int s = 0; s + 5 < f.Length; s += 3)
            {
                double ax = f[s], ay = f[s + 1], az = f[s + 2];
                double bx = f[s + 3], by = f[s + 4], bz = f[s + 5];
                foreach (var (lj, si) in grid.QueryBox(Math.Min(ax, bx), Math.Min(ay, by),
                                                       Math.Max(ax, bx), Math.Max(ay, by)))
                {
                    int rj = rowOfLine[lj];
                    if (rj < 0 || rj <= r) continue;           // 每对只判一次
                    if (Find(rj) == Find(r)) continue;         // 已同片，省掉相交计算
                    var g = lines[lj];
                    if (!SegCross2D(ax, ay, bx, by, g[si], g[si + 1], g[si + 3], g[si + 4],
                                    out double ta, out double tb)) continue;
                    double za = az + (bz - az) * ta;
                    double zb = g[si + 2] + (g[si + 5] - g[si + 2]) * tb;
                    if (Math.Abs(za - zb) > zSep) continue;    // 立交
                    Union(r, rj);
                }
            }
        }

        // ── ③ 片按总长降序编号（0 = 主干），回填每行 ──
        var byRoot = new Dictionary<int, (double Len, int Count, List<int> Members)>();
        for (int r = 0; r < rows.Count; r++)
        {
            int root = Find(r);
            if (!byRoot.TryGetValue(root, out var agg)) agg = (0, 0, new List<int>());
            agg.Len += rows[r].LengthM;
            agg.Count++;
            agg.Members.Add(r);
            byRoot[root] = agg;
        }
        var ordered = new List<KeyValuePair<int, (double Len, int Count, List<int> Members)>>(byRoot);
        ordered.Sort((a, b) => b.Value.Len.CompareTo(a.Value.Len));
        for (int c = 0; c < ordered.Count; c++)
            foreach (int r in ordered[c].Value.Members)
            {
                rows[r].ComponentId = c;
                rows[r].ComponentLineCount = ordered[c].Value.Count;
            }

        double total = 0;
        int loose = 0;
        foreach (var r in rows) { total += r.LengthM; loose += r.LooseEnds; }

        return new CenterlineInventoryResult
        {
            Rows = rows,
            ComponentCount = ordered.Count,
            LooseEndCount = loose,
            TotalLengthM = total,
        };
    }

    // ── 几何原语 ──────────────────────────────────────────────────────────────

    private static double Dist2(double ax, double ay, double az, double bx, double by, double bz)
    { double dx = ax - bx, dy = ay - by, dz = az - bz; return dx * dx + dy * dy + dz * dz; }

    /// <summary>点到第 <paramref name="si"/> 段（扁平数组下标）的 XY 距离 + 投影处按段插值的 Z。</summary>
    private static void NearestOnSeg(double px, double py, double[] f, int si, out double dist, out double zOnSeg)
    {
        double ax = f[si], ay = f[si + 1], az = f[si + 2];
        double bx = f[si + 3], by = f[si + 4], bz = f[si + 5];
        double dx = bx - ax, dy = by - ay;
        double l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : ((px - ax) * dx + (py - ay) * dy) / l2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double qx = ax + dx * t, qy = ay + dy * t;
        dist = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        zOnSeg = az + (bz - az) * t;
    }

    /// <summary>二维线段"内部"真相交（端点相交交给端点吸附那条，这里刻意排除，同 RoadGraphBuilder）。</summary>
    private static bool SegCross2D(double p1x, double p1y, double p2x, double p2y,
                                   double q1x, double q1y, double q2x, double q2y,
                                   out double ta, out double tb)
    {
        const double eps = 1e-6;
        ta = tb = 0;
        double rx = p2x - p1x, ry = p2y - p1y;
        double sx = q2x - q1x, sy = q2y - q1y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return false;                  // 平行 / 共线
        double qpx = q1x - p1x, qpy = q1y - p1y;
        ta = (qpx * sy - qpy * sx) / den;
        tb = (qpx * ry - qpy * rx) / den;
        return ta > eps && ta < 1 - eps && tb > eps && tb < 1 - eps;
    }

}
