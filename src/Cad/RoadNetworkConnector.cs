using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>地形标高采样器（可选）：把补充线铺贴到现状 TIN。忠实原 IRoadZSampler。</summary>
public interface IRoadZSampler
{
    /// <summary>在 (x,y) 采地形标高；命中返回 true 并给出 z。</summary>
    bool TrySample(double x, double y, out double z);
}

/// <summary>道路网连通增强参数。忠实移植原 PointCloudLib.RoadCenterline.RoadConnectOptions。</summary>
public sealed class RoadConnectOptions
{
    /// <summary>端点焊接容差 (m)：两条线端点≤此距离视为同一节点，焊到一起。</summary>
    public double SnapTol = 4.0;
    /// <summary>自动断头连接距离上限 (m)：悬空端点离最近线路≤此则补一段连接线接上。</summary>
    public double ConnectDist = 25.0;
    /// <summary>手动补充线端点连接距离上限 (m)：用户画的补充线意图明确，给更宽容的连接半径。</summary>
    public double ManualConnectDist = 60.0;
    /// <summary>自动桥接坡度上限 (°)：连接段 Δz/水平距 超此判为"跨台阶面"而不连；手动补充线不受此限。</summary>
    public double MaxBridgeSlopeDeg = 14.0;
    /// <summary>地形铺贴采样步距 (m)：把手动补充线加密到此步距再采 TIN 标高（&gt;0 且提供采样器时生效）。</summary>
    public double DrapeSpacing = 5.0;
}

/// <summary>连通增强结果。</summary>
public sealed class RoadConnectResult
{
    public List<double[]> Lines = new();
    public int Welded;        // 焊接合并的端点数
    public int Bridged;       // 新增连接段数
    public int Splits;        // T 形打断处数
    public int ManualMerged;  // 并入的手动补充线条数
    public string Summary = string.Empty;
}

/// <summary>
/// 道路中心线网连通增强（纯 C#，作用在折线层）。忠实移植原 PointCloudLib.RoadCenterline.RoadNetworkConnector。
/// 三步：① 焊接相距≤SnapTol 的端点到同一节点；② 桥接仍悬空的断头（落线段中部则<b>打断</b>成 T 形节点）；
/// ③ 手动补充线作一等连接源/汇，宽容半径 + 不受坡度限制（可选按 TIN 铺贴）。
/// </summary>
public static class RoadNetworkConnector
{
    private struct V
    {
        public double X, Y, Z;
        public V(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    public static RoadConnectResult Connect(
        IReadOnlyList<double[]> extracted, IReadOnlyList<double[]> manual, RoadConnectOptions opt)
        => Connect(extracted, manual, opt, null);

    /// <param name="sampler">可选地形采样器：非空时把手动补充线按现状地形铺贴（加密+采 Z）。</param>
    public static RoadConnectResult Connect(
        IReadOnlyList<double[]> extracted, IReadOnlyList<double[]> manual, RoadConnectOptions opt,
        IRoadZSampler? sampler)
    {
        opt ??= new RoadConnectOptions();
        var res = new RoadConnectResult();

        var polys = new List<List<V>>();
        var isManual = new List<bool>();

        void Add(double[] flat, bool man)
        {
            if (flat == null || flat.Length < 6) return;
            if (man && sampler != null && opt.DrapeSpacing > 0)
                flat = DrapeFlat(flat, sampler, opt.DrapeSpacing);   // 补充线铺贴到地形
            int n = flat.Length / 3;
            var pl = new List<V>(n);
            for (int i = 0; i < n; i++)
            {
                var v = new V(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
                if (pl.Count == 0 || Sq2(pl[pl.Count - 1], v) > 1e-6) pl.Add(v);   // 去零长重复点
            }
            if (pl.Count < 2) return;
            polys.Add(pl); isManual.Add(man);
            if (man) res.ManualMerged++;
        }

        if (extracted != null) foreach (var f in extracted) Add(f, false);
        if (manual != null) foreach (var f in manual) Add(f, true);

        if (polys.Count == 0) { res.Summary = "连通增强：无可处理线路。"; return res; }

        // ── ① 焊接近失端点 ──
        var welded = new HashSet<(int, int)>();
        res.Welded = WeldEndpoints(polys, opt.SnapTol, welded);

        // ── ② 桥接悬空断头（含 T 形打断），产出最终折线集 ──
        var final = BridgeDeadEnds(polys, isManual, welded, opt, res);

        foreach (var pl in final)
        {
            if (pl.Count < 2) continue;
            var flat = new double[pl.Count * 3];
            for (int i = 0; i < pl.Count; i++) { flat[3 * i] = pl[i].X; flat[3 * i + 1] = pl[i].Y; flat[3 * i + 2] = pl[i].Z; }
            res.Lines.Add(flat);
        }

        res.Summary = $"连通增强：焊接 {res.Welded} 端点 / 新增连接 {res.Bridged} 段 / T形打断 {res.Splits} 处"
                      + (res.ManualMerged > 0 ? $" / 并入手动补充 {res.ManualMerged} 条" : "")
                      + $"；输出 {res.Lines.Count} 条。";
        return res;
    }

    // ── ① 焊接：union-find 把相距≤tol（三维）的端点聚类，每簇吸到质心 ──
    private static int WeldEndpoints(List<List<V>> polys, double tol, HashSet<(int, int)> weldedOut)
    {
        double tol2 = tol * tol;
        var ends = new List<(int pi, int slot, V p)>();
        for (int i = 0; i < polys.Count; i++)
        {
            var pl = polys[i];
            if (Sq2(pl[0], pl[pl.Count - 1]) <= 1e-6) continue;   // 闭环：无端点
            ends.Add((i, 0, pl[0]));
            ends.Add((i, 1, pl[pl.Count - 1]));
        }
        int m = ends.Count;
        if (m < 2) return 0;

        var parent = new int[m];
        for (int i = 0; i < m; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        double cs = Math.Max(tol, 1e-3);
        (int, int) Cell(V p) => ((int)Math.Floor(p.X / cs), (int)Math.Floor(p.Y / cs));
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < m; i++)
        {
            var c = Cell(ends[i].p);
            if (!grid.TryGetValue(c, out var l)) { l = new List<int>(); grid[c] = l; }
            l.Add(i);
        }
        for (int i = 0; i < m; i++)
        {
            var (cx, cy) = Cell(ends[i].p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var l))
                        foreach (int j in l)
                            if (j > i && Sq3(ends[i].p, ends[j].p) <= tol2) Union(i, j);
        }

        var clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < m; i++)
        {
            int r = Find(i);
            if (!clusters.TryGetValue(r, out var l)) { l = new List<int>(); clusters[r] = l; }
            l.Add(i);
        }

        int welded = 0;
        foreach (var kv in clusters)
        {
            var members = kv.Value;
            if (members.Count < 2) continue;
            double sx = 0, sy = 0, sz = 0;
            foreach (int idx in members) { sx += ends[idx].p.X; sy += ends[idx].p.Y; sz += ends[idx].p.Z; }
            var rep = new V(sx / members.Count, sy / members.Count, sz / members.Count);
            foreach (int idx in members)
            {
                var (pi, slot, _) = ends[idx];
                var pl = polys[pi];
                if (slot == 0) pl[0] = rep; else pl[pl.Count - 1] = rep;
                weldedOut.Add((pi, slot));
            }
            welded += members.Count;
        }
        return welded;
    }

    // ── ② 桥接 ──
    private static List<List<V>> BridgeDeadEnds(
        List<List<V>> polys, List<bool> isManual, HashSet<(int, int)> connectedEnd,
        RoadConnectOptions opt, RoadConnectResult res)
    {
        double snap = opt.SnapTol, snap2 = snap * snap;
        double maxGapGlobal = Math.Max(opt.ConnectDist, opt.ManualConnectDist);
        double cs = Math.Max(maxGapGlobal, 1.0);

        // 线段空间索引：格 → 该格内的 (折线, 段) 列表
        var grid = new Dictionary<(int, int), List<(int pi, int si)>>();
        for (int pi = 0; pi < polys.Count; pi++)
            for (int si = 0; si + 1 < polys[pi].Count; si++)
            {
                var a = polys[pi][si]; var b = polys[pi][si + 1];
                int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / cs), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / cs);
                int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / cs), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / cs);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cy = y0; cy <= y1; cy++)
                    {
                        var c = (cx, cy);
                        if (!grid.TryGetValue(c, out var l)) { l = new List<(int, int)>(); grid[c] = l; }
                        l.Add((pi, si));
                    }
            }

        var splitReqs = new Dictionary<int, List<(int si, double t, V q)>>();
        var connectors = new List<List<V>>();

        void AddSplit(int pi, int si, double t, V q)
        {
            if (!splitReqs.TryGetValue(pi, out var l)) { l = new List<(int, double, V)>(); splitReqs[pi] = l; }
            l.Add((si, t, q));
        }

        int polyCount = polys.Count;
        for (int pi = 0; pi < polyCount; pi++)
        {
            var pl = polys[pi];
            if (pl.Count < 2) continue;
            if (Sq2(pl[0], pl[pl.Count - 1]) <= 1e-6) continue;   // 闭环无断头

            for (int slot = 0; slot < 2; slot++)
            {
                if (connectedEnd.Contains((pi, slot))) continue;   // 已焊接/已连
                V p = slot == 0 ? pl[0] : pl[pl.Count - 1];
                double maxGap = isManual[pi] ? opt.ManualConnectDist : opt.ConnectDist;

                FindNearest(polys, grid, cs, pi, p, maxGap, out int mj, out int sj, out double t, out V q, out double d);
                if (mj < 0 || d < 1e-6) continue;

                // 坡度闸门（仅自动线；极近的不拦，避免该连的没连）
                if (!isManual[pi] && d > snap)
                {
                    double dz = Math.Abs(q.Z - p.Z);
                    double slopeDeg = Math.Atan2(dz, Math.Max(d, 1e-6)) * 180.0 / Math.PI;
                    if (slopeDeg > opt.MaxBridgeSlopeDeg) continue;
                }

                var tpl = polys[mj];
                bool atStart = Sq2(q, tpl[0]) <= snap2;
                bool atEnd = Sq2(q, tpl[tpl.Count - 1]) <= snap2;
                bool qAtNode = atStart || atEnd;

                if (d <= snap)
                {
                    // 已经贴上：端点对端点 → 视为已连；端点对线段中部 → 打断成 T，吸点对齐
                    if (qAtNode) { connectedEnd.Add((pi, slot)); MarkTargetNode(tpl, mj, atStart, connectedEnd); continue; }
                    if (slot == 0) pl[0] = q; else pl[pl.Count - 1] = q;
                    AddSplit(mj, sj, t, q);
                    connectedEnd.Add((pi, slot));
                    continue;
                }

                // 需要补一段连接线
                V target = qAtNode ? (atStart ? tpl[0] : tpl[tpl.Count - 1]) : q;
                connectors.Add(new List<V> { p, target });
                res.Bridged++;
                if (!qAtNode) AddSplit(mj, sj, t, q);
                connectedEnd.Add((pi, slot));
                if (qAtNode) MarkTargetNode(tpl, mj, atStart, connectedEnd);
            }
        }

        // ── 应用打断 + 组装最终折线集 ──
        var final = new List<List<V>>();
        for (int pi = 0; pi < polys.Count; pi++)
        {
            if (splitReqs.TryGetValue(pi, out var cuts) && cuts.Count > 0)
            {
                var parts = SplitPolyline(polys[pi], cuts);
                res.Splits += cuts.Count;
                final.AddRange(parts);
            }
            else if (polys[pi].Count >= 2) final.Add(polys[pi]);
        }
        final.AddRange(connectors);
        return final;
    }

    private static void MarkTargetNode(List<V> tpl, int mj, bool atStart, HashSet<(int, int)> connectedEnd)
        => connectedEnd.Add((mj, atStart ? 0 : 1));

    // 在最近段集中找离 p 最近的点（XY 投影判距，q.Z 沿段插值）
    private static void FindNearest(
        List<List<V>> polys, Dictionary<(int, int), List<(int pi, int si)>> grid, double cs,
        int selfPi, V p, double maxGap, out int bmj, out int bsj, out double bt, out V bq, out double bd)
    {
        bmj = -1; bsj = -1; bt = 0; bq = default; bd = double.MaxValue;
        double maxGap2 = maxGap * maxGap, best2 = maxGap2;
        int cx = (int)Math.Floor(p.X / cs), cy = (int)Math.Floor(p.Y / cs);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var l)) continue;
                foreach (var (mj, sj) in l)
                {
                    if (mj == selfPi) continue;
                    NearestOnSeg(p, polys[mj][sj], polys[mj][sj + 1], out double t, out V q, out double d2);
                    if (d2 < best2) { best2 = d2; bmj = mj; bsj = sj; bt = t; bq = q; }
                }
            }
        if (bmj >= 0) bd = Math.Sqrt(best2);
    }

    private static void NearestOnSeg(V p, V a, V b, out double t, out V q, out double d2)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double L2 = abx * abx + aby * aby;
        if (L2 < 1e-12) { t = 0; q = a; double dx0 = p.X - a.X, dy0 = p.Y - a.Y; d2 = dx0 * dx0 + dy0 * dy0; return; }
        t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / L2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double qx = a.X + abx * t, qy = a.Y + aby * t, qz = a.Z + (b.Z - a.Z) * t;
        q = new V(qx, qy, qz);
        double ddx = p.X - qx, ddy = p.Y - qy;
        d2 = ddx * ddx + ddy * ddy;
    }

    // 在 cuts 处把折线打断成多段（cuts 携带精确切点 q，保证与连接段端点严格重合）
    private static List<List<V>> SplitPolyline(List<V> v, List<(int si, double t, V q)> cuts)
    {
        cuts.Sort((a, b) => a.si != b.si ? a.si - b.si : a.t.CompareTo(b.t));
        var result = new List<List<V>>();
        var cur = new List<V> { v[0] };
        int ci = 0;
        for (int s = 0; s + 1 < v.Count; s++)
        {
            while (ci < cuts.Count && cuts[ci].si == s)
            {
                var q = cuts[ci].q;
                if (Sq2(cur[cur.Count - 1], q) > 1e-6) cur.Add(q);
                if (cur.Count >= 2) result.Add(cur);
                cur = new List<V> { q };
                ci++;
            }
            if (Sq2(cur[cur.Count - 1], v[s + 1]) > 1e-6) cur.Add(v[s + 1]);
        }
        if (cur.Count >= 2) result.Add(cur);
        return result;
    }

    // 把折线沿程加密到 ~spacing 再采地形标高（命中取地形 Z，未命中保留线性插值 Z）→ 平稳过渡。
    private static double[] DrapeFlat(double[] flat, IRoadZSampler s, double spacing)
    {
        int n = flat.Length / 3;
        if (n < 2 || s == null) return flat;
        double step = Math.Max(0.5, spacing);
        var outp = new List<double>(n * 3 * 2);
        void Push(double x, double y, double zf) { double z = s.TrySample(x, y, out double zz) ? zz : zf; outp.Add(x); outp.Add(y); outp.Add(z); }
        for (int seg = 0; seg + 1 < n; seg++)
        {
            double ax = flat[3 * seg], ay = flat[3 * seg + 1], az = flat[3 * seg + 2];
            double bx = flat[3 * seg + 3], by = flat[3 * seg + 4], bz = flat[3 * seg + 5];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int steps = Math.Max(1, (int)Math.Ceiling(len / step));
            for (int k = 0; k < steps; k++)   // [a,b) 段内点；段末点由下段起点或最终点补
            {
                double f = (double)k / steps;
                Push(ax + (bx - ax) * f, ay + (by - ay) * f, az + (bz - az) * f);
            }
        }
        Push(flat[3 * (n - 1)], flat[3 * (n - 1) + 1], flat[3 * (n - 1) + 2]);   // 终点
        return outp.ToArray();
    }

    private static double Sq2(V a, V b) { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
    private static double Sq3(V a, V b) { double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return dx * dx + dy * dy + dz * dz; }
}
