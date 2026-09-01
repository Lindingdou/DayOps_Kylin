using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云区域生长分割（PCL RegionGrowing 式）：逐点 PCA 得法向 + 曲率(最小特征值/特征值和)；
/// 按曲率升序取种子(最平处起)，沿 k 近邻生长——邻点法向夹角 &lt; 平滑阈并入本区，
/// 邻点曲率 &lt; 曲率阈再作新种子继续长(过折棱不再扩)。按【表面光滑度】分割
/// (区别 <see cref="PointCluster"/> Euclidean 只按距离)：台阶面/平盘/坡面在折棱处法向突变而分开。
/// 参数(平滑角/曲率阈)与结果确定对应；标准算法, 与 native PMSG 具体变体可能不同。
/// 复用 <see cref="DepositAutoDetector.JacobiEigen3"/>。纯几何、可单测。
/// </summary>
public static class RegionGrow
{
    public sealed class Result
    {
        /// <summary>每点区号(0..RegionCount-1)；-1 = 未分配/所在区过小被丢。</summary>
        public int[] Label = Array.Empty<int>();
        public int RegionCount;
        /// <summary>各区点数(按区号)。</summary>
        public List<int> RegionSize = new();
    }

    public static Result Segment(IReadOnlyList<(double x, double y, double z)> pts,
        int k = 16, double smoothnessDeg = 15.0, double curvatureThreshold = 0.1, int minSize = 10)
    {
        var res = new Result();
        int n = pts?.Count ?? 0;
        res.Label = new int[Math.Max(0, n)];
        for (int i = 0; i < n; i++) res.Label[i] = -1;
        if (n < 3) return res;
        if (k < 3) k = 3;

        // ── XY 网格(格边≈平均间距×2)加速 k 近邻 ──
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts!) { if (p.x < minX) minX = p.x; if (p.y < minY) minY = p.y; if (p.x > maxX) maxX = p.x; if (p.y > maxY) maxY = p.y; }
        double area = Math.Max((maxX - minX) * (maxY - minY), 1e-9);
        double cell = Math.Max(Math.Sqrt(area / n) * 2, 1e-6);
        var grid = new Dictionary<(long, long), List<int>>(n);
        (long, long) Key(double x, double y) => ((long)Math.Floor((x - minX) / cell), (long)Math.Floor((y - minY) / cell));
        for (int i = 0; i < n; i++)
        {
            var key = Key(pts[i].x, pts[i].y);
            if (!grid.TryGetValue(key, out var l)) { l = new List<int>(); grid[key] = l; }
            l.Add(i);
        }

        // ── 逐点 PCA：法向(最小特征向量, 朝上) + 曲率 + 存 k 近邻(供生长) ──
        var normal = new (double x, double y, double z)[n];
        var curv = new double[n];
        var knn = new int[n][];
        var cand = new List<(double d2, int idx)>();
        double spanCells = Math.Max((maxX - minX), (maxY - minY)) / cell + 1;
        for (int i = 0; i < n; i++)
        {
            var pi = pts[i];
            var (cx, cy) = Key(pi.x, pi.y);
            int ring = 1;
            while (true)
            {
                cand.Clear();
                for (long gx = cx - ring; gx <= cx + ring; gx++)
                    for (long gy = cy - ring; gy <= cy + ring; gy++)
                        if (grid.TryGetValue((gx, gy), out var bucket))
                            foreach (int j in bucket)
                            {
                                double dx = pts[j].x - pi.x, dy = pts[j].y - pi.y, dz = pts[j].z - pi.z;
                                cand.Add((dx * dx + dy * dy + dz * dz, j));
                            }
                if (cand.Count >= k + 1 || ring > spanCells) break;
                ring++;
            }
            cand.Sort((a, b) => a.d2.CompareTo(b.d2));
            int take = Math.Min(k + 1, cand.Count);   // 含自身
            // k 近邻(存, 排自身)
            var nb = new List<int>(take);
            for (int t = 0; t < take; t++) if (cand[t].idx != i) nb.Add(cand[t].idx);
            knn[i] = nb.ToArray();
            // 协方差
            double mx = 0, my = 0, mz = 0;
            for (int t = 0; t < take; t++) { var q = pts[cand[t].idx]; mx += q.x; my += q.y; mz += q.z; }
            mx /= take; my /= take; mz /= take;
            double sxx = 0, syy = 0, szz = 0, sxy = 0, sxz = 0, syz = 0;
            for (int t = 0; t < take; t++)
            {
                var q = pts[cand[t].idx];
                double ex = q.x - mx, ey = q.y - my, ez = q.z - mz;
                sxx += ex * ex; syy += ey * ey; szz += ez * ez; sxy += ex * ey; sxz += ex * ez; syz += ey * ez;
            }
            var cov = new double[3, 3];
            cov[0, 0] = sxx / take; cov[1, 1] = syy / take; cov[2, 2] = szz / take;
            cov[0, 1] = cov[1, 0] = sxy / take; cov[0, 2] = cov[2, 0] = sxz / take; cov[1, 2] = cov[2, 1] = syz / take;
            DepositAutoDetector.JacobiEigen3(cov, out var eval, out var evec);
            int s = 0; if (eval[1] < eval[s]) s = 1; if (eval[2] < eval[s]) s = 2;
            double sum = eval[0] + eval[1] + eval[2];
            curv[i] = sum > 1e-12 ? eval[s] / sum : 0;               // 曲率 = λmin/Σλ
            double nx = evec[s][0], ny = evec[s][1], nz = evec[s][2];
            double nn = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nn < 1e-12) { normal[i] = (0, 0, 1); continue; }
            nx /= nn; ny /= nn; nz /= nn;
            if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }            // 朝上, 使夹角判定一致
            normal[i] = (nx, ny, nz);
        }

        // ── 曲率升序种子；BFS 生长(法向夹角<平滑阈并入; 曲率<阈再作新种子) ──
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => curv[a].CompareTo(curv[b]));
        double cosSmooth = Math.Cos(smoothnessDeg * Math.PI / 180.0);

        var label = res.Label;
        var sizes = new List<int>();
        int region = 0;
        var queue = new Queue<int>();
        foreach (int seed in order)
        {
            if (label[seed] >= 0) continue;
            queue.Clear(); queue.Enqueue(seed); label[seed] = region; int cnt = 1;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                var ncur = normal[cur];
                foreach (int j in knn[cur])
                {
                    if (label[j] >= 0) continue;
                    var nj = normal[j];
                    double dot = ncur.x * nj.x + ncur.y * nj.y + ncur.z * nj.z;
                    if (dot < cosSmooth) continue;                   // 法向夹角 > 平滑阈 → 非同面(折棱), 不并
                    label[j] = region; cnt++;
                    if (curv[j] < curvatureThreshold) queue.Enqueue(j);   // 够平 → 作新种子继续长
                }
            }
            sizes.Add(cnt);
            region++;
        }

        // ── 丢弃过小区, 紧凑重编号 ──
        var remap = new int[region];
        int rc = 0;
        for (int r = 0; r < region; r++) remap[r] = (sizes[r] >= minSize) ? rc++ : -1;
        for (int i = 0; i < n; i++) label[i] = label[i] >= 0 ? remap[label[i]] : -1;
        res.RegionCount = rc;
        res.RegionSize = new List<int>(rc);
        for (int r = 0; r < region; r++) if (remap[r] >= 0) res.RegionSize.Add(sizes[r]);
        return res;
    }
}
