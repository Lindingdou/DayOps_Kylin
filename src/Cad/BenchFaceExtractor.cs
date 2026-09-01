using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 从【现状面】切出台阶坡面：按坡度把三角分成"陡=坡面 / 缓=平盘"，陡的按连通域分片，
/// 每片就是一个台阶坡面；再从该片的边界环分出上沿（坡顶线）与下沿（坡底线）。
///
/// 坡面不需要配对：它本身就是一片完整的几何。现状面上陡的地方就是台阶坡面，缓的地方就是平盘，
/// 按坡度一分就出来了。现状面是权威——实际挖成什么样，坡面就是什么样。
/// 比 <see cref="CrestToe"/>(仅出平陡分界的散断棱边)完整: 连通域分片 + 每片台阶高/坡度/面积 +
/// 有序坡顶/坡底线(按走向排序, 可 loft)。忠实原 BlockModelLib.BenchFaceExtractor。纯几何、可单测。
/// </summary>
public static class BenchFaceExtractor
{
    /// <summary>切出来的一片台阶坡面。</summary>
    public sealed class BenchFace
    {
        /// <summary>本片包含的三角在原网中的序号。</summary>
        public List<int> TriIndices = new();

        /// <summary>坡顶线（上沿），世界坐标扁平 [x,y,z,...]。</summary>
        public double[] CrestXyz = Array.Empty<double>();
        /// <summary>坡底线（下沿）。</summary>
        public double[] ToeXyz = Array.Empty<double>();

        /// <summary>本片的高程范围。</summary>
        public double MinZ, MaxZ;
        /// <summary>台阶高（m）= MaxZ − MinZ。</summary>
        public double BenchHeightM => MaxZ - MinZ;

        /// <summary>坡面【真实】面积（三维，m²）。</summary>
        public double AreaM2;
        /// <summary>坡面在平面上的投影面积（m²）。</summary>
        public double PlanAreaM2;

        /// <summary>平均坡度（°），由真实面积与投影面积之比反算：cosα = 投影/真实。</summary>
        public double MeanSlopeDeg
        {
            get
            {
                if (AreaM2 <= 1e-9) return 0;
                double c = Math.Min(1.0, PlanAreaM2 / AreaM2);
                return Math.Acos(c) * 180.0 / Math.PI;
            }
        }

        /// <summary>坡面水平投影宽（m）≈ 台阶高 / tan(坡度)，供与设计值核对。</summary>
        public double FaceRunM
        {
            get
            {
                double a = MeanSlopeDeg;
                if (a <= 0.01 || a >= 89.99) return 0;
                return BenchHeightM / Math.Tan(a * Math.PI / 180.0);
            }
        }

        public int CrestPointCount => CrestXyz.Length / 3;
        public int ToePointCount => ToeXyz.Length / 3;
    }

    public sealed class Options
    {
        /// <summary>判为坡面的最小坡度（°）。缓于此的三角算平盘，不参与。默认 20° 折中，务必按实际调。</summary>
        public double MinSlopeDeg = 20.0;
        /// <summary>一片坡面的最小真实面积（m²），小于此的碎片丢弃。</summary>
        public double MinAreaM2 = 200.0;
        /// <summary>一片坡面的最小台阶高（m），低于此的丢弃（多半是地形起伏不是台阶）。</summary>
        public double MinBenchHeightM = 2.0;
        /// <summary>顶点焊接容差（m）。</summary>
        public double WeldTolM = 0.001;
        /// <summary>采场范围环（扁平 [x0,y0,x1,y1,...]，隐式闭合）。非空时只在环内切坡面。</summary>
        public double[]? ClipRingXy;
        /// <summary>单片坡面的台阶高上限（m）。超过就按高程再切（多级坑壁粘成一片）。≤0 = 不限。</summary>
        public double MaxBenchHeightM = 25.0;
        /// <summary>陡三角集合的【闭运算】轮数：邻居里有 ≥2 个陡的平三角补判为陡（补等高线 TIN 空洞）。0 = 不做。</summary>
        public int CloseIterations = 2;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        /// <summary>切出的坡面，按真实面积降序。</summary>
        public List<BenchFace> Faces = new();
        /// <summary>陡三角数 / 总三角数。</summary>
        public int SteepTris, TotalTris;
        /// <summary>因面积/台阶高不够被丢弃的片数。</summary>
        public int DroppedSmall, DroppedLow;
        public List<string> Warnings = new();
    }

    /// <summary>切坡面。verts 扁平 [x,y,z,...]，tris 三角索引。任何异常降级为 Ok=false，不抛。</summary>
    public static Result Extract(double[]? verts, int[]? tris, Options? opt = null)
    {
        opt ??= new Options();
        var res = new Result();
        if (verts == null || verts.Length < 9 || tris == null || tris.Length < 3)
        { res.Message = "现状面三角网为空或不合法。"; return res; }

        int nt = tris.Length / 3;
        res.TotalTris = nt;
        double cosMax = Math.Cos(Math.Max(0.0, Math.Min(89.9, opt.MinSlopeDeg)) * Math.PI / 180.0);

        // ── ① 逐三角判陡缓：|nz|/|n| = cos(坡度)。陡 ⟺ cos(坡度) < cos(阈值)。
        double[]? ring = opt.ClipRingXy is { Length: >= 6 } ? opt.ClipRingXy : null;
        int outsideRing = 0;

        var steep = new bool[nt];
        var triArea = new double[nt];
        var triPlanArea = new double[nt];
        for (int t = 0; t < nt; t++)
        {
            int a = tris[t * 3] * 3, b = tris[t * 3 + 1] * 3, c = tris[t * 3 + 2] * 3;
            if (a < 0 || b < 0 || c < 0 || a + 2 >= verts.Length || b + 2 >= verts.Length || c + 2 >= verts.Length)
                continue;

            if (ring != null)
            {
                double mx = (verts[a] + verts[b] + verts[c]) / 3.0;
                double my = (verts[a + 1] + verts[b + 1] + verts[c + 1]) / 3.0;
                if (!InRingXy(ring, mx, my)) { outsideRing++; continue; }
            }

            double ux = verts[b] - verts[a], uy = verts[b + 1] - verts[a + 1], uz = verts[b + 2] - verts[a + 2];
            double vx = verts[c] - verts[a], vy = verts[c + 1] - verts[a + 1], vz = verts[c + 2] - verts[a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) continue;

            triArea[t] = len * 0.5;                    // 叉积模的一半 = 三维面积
            triPlanArea[t] = Math.Abs(nz) * 0.5;       // 法向 z 分量 = 投影面积
            if (Math.Abs(nz) / len < cosMax) { steep[t] = true; res.SteepTris++; }
        }

        if (res.SteepTris == 0)
        {
            res.Message = ring != null && outsideRing > 0
                ? $"采场内 {nt - outsideRing:N0} 个三角里，按 {opt.MinSlopeDeg:0.#}° 一片坡面都没有（另有 {outsideRing:N0} 个在采场外）。阈值定高了。"
                : $"按 {opt.MinSlopeDeg:0.#}° 阈值，{nt:N0} 个三角里一片坡面都没有——阈值定高了，或这张面本来就平。";
            return res;
        }

        int inRange = nt - outsideRing;
        if (inRange > 0 && res.SteepTris > inRange * 0.45)
            res.Warnings.Add($"陡三角占到 {res.SteepTris * 100.0 / inRange:0.#}%——阈值 {opt.MinSlopeDeg:0.#}° 多半太低，自然地形的坡被算成台阶了。调高再看。");

        // ── ② 邻接表(范围内全部三角, 闭运算要看平三角邻居)
        var allEdge = new Dictionary<(int, int), List<int>>();
        var inRangeTri = new bool[nt];
        for (int t = 0; t < nt; t++)
        {
            if (triArea[t] <= 0) continue;
            inRangeTri[t] = true;
            int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
            foreach (var (u, v) in new[] { (i0, i1), (i1, i2), (i2, i0) })
            {
                var key = u < v ? (u, v) : (v, u);
                if (!allEdge.TryGetValue(key, out var lst)) allEdge[key] = lst = new List<int>(2);
                lst.Add(t);
            }
        }

        // ── ①b 闭运算：补掉坡面中间那些三角化产生的平三角（只补邻居 ≥2 个陡的）
        int closed = 0;
        for (int iter = 0; iter < Math.Max(0, opt.CloseIterations); iter++)
        {
            var add = new List<int>();
            for (int t = 0; t < nt; t++)
            {
                if (!inRangeTri[t] || steep[t]) continue;
                int j0 = tris[t * 3], j1 = tris[t * 3 + 1], j2 = tris[t * 3 + 2];
                int steepNb = 0;
                foreach (var (u, v) in new[] { (j0, j1), (j1, j2), (j2, j0) })
                {
                    var key = u < v ? (u, v) : (v, u);
                    if (!allEdge.TryGetValue(key, out var nb)) continue;
                    foreach (int o in nb) if (o != t && steep[o]) { steepNb++; break; }
                }
                if (steepNb >= 2) add.Add(t);
            }
            if (add.Count == 0) break;
            foreach (int t in add) { steep[t] = true; closed++; }
        }
        if (closed > 0)
            res.Warnings.Add($"闭运算补入 {closed:N0} 个平三角（坡面中间由等高线三角化产生的空洞）。");

        var edgeMap = new Dictionary<(int, int), List<int>>();
        void AddEdge(int u, int v, int t)
        {
            var key = u < v ? (u, v) : (v, u);
            if (!edgeMap.TryGetValue(key, out var lst)) edgeMap[key] = lst = new List<int>(2);
            lst.Add(t);
        }
        for (int t = 0; t < nt; t++)
        {
            if (!steep[t]) continue;
            int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
            AddEdge(i0, i1, t); AddEdge(i1, i2, t); AddEdge(i2, i0, t);
        }

        var triZ = new double[nt];
        for (int t = 0; t < nt; t++)
        {
            if (!steep[t]) continue;
            triZ[t] = (verts[tris[t * 3] * 3 + 2] + verts[tris[t * 3 + 1] * 3 + 2] + verts[tris[t * 3 + 2] * 3 + 2]) / 3.0;
        }

        // 陡三角按【共边】连通域分片
        var comp = new int[nt];
        for (int i = 0; i < nt; i++) comp[i] = -1;
        int nComp = 0;
        var stack = new Stack<int>();
        for (int t = 0; t < nt; t++)
        {
            if (!steep[t] || comp[t] >= 0) continue;
            int id = nComp++;
            stack.Push(t);
            comp[t] = id;
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                int j0 = tris[cur * 3], j1 = tris[cur * 3 + 1], j2 = tris[cur * 3 + 2];
                foreach (var (u, v) in new[] { (j0, j1), (j1, j2), (j2, j0) })
                {
                    var key = u < v ? (u, v) : (v, u);
                    if (!edgeMap.TryGetValue(key, out var nb)) continue;
                    foreach (int o in nb)
                        if (comp[o] < 0) { comp[o] = id; stack.Push(o); }
                }
            }
        }

        // ── ③ 每片落成坡面
        var raw = new List<int>[nComp];
        for (int i = 0; i < nComp; i++) raw[i] = new List<int>();
        for (int t = 0; t < nt; t++) if (comp[t] >= 0) raw[comp[t]].Add(t);

        // 超高的片按【自身】高程再切（平盘窄或已被削掉时陡三角一路连下去把整面坑壁吞成一块）
        var buckets = new List<List<int>>();
        double maxH = opt.MaxBenchHeightM;
        int splitCount = 0;
        foreach (var b in raw)
        {
            if (b.Count == 0) continue;
            if (maxH <= 0) { buckets.Add(b); continue; }

            double lo = double.MaxValue, hi = double.MinValue;
            foreach (int t in b) { if (triZ[t] < lo) lo = triZ[t]; if (triZ[t] > hi) hi = triZ[t]; }
            if (hi - lo <= maxH) { buckets.Add(b); continue; }

            var byBand = new Dictionary<int, List<int>>();
            foreach (int t in b)
            {
                int k = (int)Math.Floor((triZ[t] - lo) / maxH);
                if (!byBand.TryGetValue(k, out var lst)) byBand[k] = lst = new List<int>();
                lst.Add(t);
            }
            foreach (var lst in byBand.Values) buckets.Add(lst);
            splitCount++;
        }
        if (splitCount > 0)
            res.Warnings.Add($"{splitCount} 片高差超过 {maxH:0.#}m 的坡面被按高程切开（多级坑壁粘成一片，不是一级台阶）。");

        foreach (var bucket in buckets)
        {
            if (bucket.Count == 0) continue;
            var face = new BenchFace { TriIndices = bucket };

            double area = 0, planArea = 0, mnz = double.MaxValue, mxz = double.MinValue;
            foreach (int t in bucket)
            {
                area += triArea[t];
                planArea += triPlanArea[t];
                for (int k = 0; k < 3; k++)
                {
                    double z = verts[tris[t * 3 + k] * 3 + 2];
                    if (z < mnz) mnz = z;
                    if (z > mxz) mxz = z;
                }
            }
            face.AreaM2 = area; face.PlanAreaM2 = planArea;
            face.MinZ = mnz; face.MaxZ = mxz;

            if (area < opt.MinAreaM2) { res.DroppedSmall++; continue; }
            if (face.BenchHeightM < opt.MinBenchHeightM) { res.DroppedLow++; continue; }

            SplitBoundaryIntoCrestAndToe(verts, tris, bucket, edgeMap, face);
            if (face.CrestPointCount < 2 || face.ToePointCount < 2) continue;   // 分不出上下沿的丢弃

            res.Faces.Add(face);
        }

        if (res.Faces.Count == 0)
        {
            res.Message = $"切出 {buckets.Count} 片陡坡，但没有一片够格"
                        + $"（面积 < {opt.MinAreaM2:0} m² 的 {res.DroppedSmall} 片，台阶高 < {opt.MinBenchHeightM:0.#}m 的 {res.DroppedLow} 片）。放宽下限再试。";
            return res;
        }

        res.Faces.Sort((a, b) => b.AreaM2.CompareTo(a.AreaM2));
        res.Ok = true;
        res.Message = $"切出 {res.Faces.Count} 片台阶坡面（陡三角 {res.SteepTris:N0}/{nt:N0}，阈值 {opt.MinSlopeDeg:0.#}°）："
                    + $"台阶高 {res.Faces.Min(f => f.BenchHeightM):0.##}~{res.Faces.Max(f => f.BenchHeightM):0.##}m，"
                    + $"坡度 {res.Faces.Min(f => f.MeanSlopeDeg):0.#}~{res.Faces.Max(f => f.MeanSlopeDeg):0.#}°。";
        if (res.DroppedSmall + res.DroppedLow > 0)
            res.Warnings.Add($"另丢弃 {res.DroppedSmall} 片过小、{res.DroppedLow} 片过矮的碎片。");
        return res;
    }

    /// <summary>点是否在闭合环内（射线法，XY）。</summary>
    private static bool InRingXy(double[] ring, double x, double y)
    {
        bool inside = false;
        int n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i * 2], yi = ring[i * 2 + 1];
            double xj = ring[j * 2], yj = ring[j * 2 + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 从一片坡面的边界环分出上沿（坡顶线）与下沿（坡底线）。
    /// 边界边 = 本片内只属于一个三角的边; 焊成链后按【走向轴】排序, 高程前/后 35% 分下/上沿,
    /// 中间过渡段(两侧爬升边)排除。倾向 = 各三角法向水平分量合成; 走向 = 与之垂直。
    /// </summary>
    private static void SplitBoundaryIntoCrestAndToe(
        double[] verts, int[] tris, List<int> bucket,
        Dictionary<(int, int), List<int>> edgeMap, BenchFace face)
    {
        var inFace = new HashSet<int>(bucket);

        var boundary = new List<(int A, int B)>();
        var counted = new HashSet<(int, int)>();
        foreach (int t in bucket)
        {
            int j0 = tris[t * 3], j1 = tris[t * 3 + 1], j2 = tris[t * 3 + 2];
            foreach (var (u, v) in new[] { (j0, j1), (j1, j2), (j2, j0) })
            {
                var key = u < v ? (u, v) : (v, u);
                if (!counted.Add(key)) continue;
                if (!edgeMap.TryGetValue(key, out var owners)) continue;
                int inCount = owners.Count(o => inFace.Contains(o));
                if (inCount == 1) boundary.Add((u, v));
            }
        }
        if (boundary.Count < 3) return;

        var adj = new Dictionary<int, List<int>>();
        void Link(int a, int b)
        {
            if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(2);
            l.Add(b);
        }
        foreach (var (a, b) in boundary) { Link(a, b); Link(b, a); }

        var visited = new HashSet<int>();
        List<int>? longest = null;
        foreach (var start in adj.Keys)
        {
            if (visited.Contains(start)) continue;
            var chain = new List<int> { start };
            visited.Add(start);
            int cur = start;
            while (true)
            {
                if (!adj.TryGetValue(cur, out var nbrs)) break;
                int next = -1;
                foreach (int n in nbrs) if (!visited.Contains(n)) { next = n; break; }
                if (next < 0) break;
                visited.Add(next);
                chain.Add(next);
                cur = next;
            }
            if (longest == null || chain.Count > longest.Count) longest = chain;
        }
        if (longest == null || longest.Count < 4) return;

        // 倾向 d = 法向水平分量方向（指向下坡）；走向 s = 与之垂直
        double nx = 0, ny = 0;
        foreach (int t in bucket)
        {
            int a = tris[t * 3] * 3, b = tris[t * 3 + 1] * 3, c = tris[t * 3 + 2] * 3;
            double ux = verts[b] - verts[a], uy = verts[b + 1] - verts[a + 1], uz = verts[b + 2] - verts[a + 2];
            double vx = verts[c] - verts[a], vy = verts[c + 1] - verts[a + 1], vz = verts[c + 2] - verts[a + 2];
            double mx = uy * vz - uz * vy, my = uz * vx - ux * vz, mz = ux * vy - uy * vx;
            if (mz < 0) { mx = -mx; my = -my; }     // 法向统一朝上
            nx += mx; ny += my;
        }
        double nl = Math.Sqrt(nx * nx + ny * ny);
        double dx = nl > 1e-9 ? nx / nl : 1, dy = nl > 1e-9 ? ny / nl : 0;
        double sx = -dy, sy = dx;

        var pts = longest.Select(i => (
            Idx: i,
            Z: verts[i * 3 + 2],
            S: verts[i * 3] * sx + verts[i * 3 + 1] * sy
        )).ToList();

        var zSorted = pts.Select(p => p.Z).OrderBy(z => z).ToList();
        double zLo = zSorted[(int)(zSorted.Count * 0.35)];
        double zHi = zSorted[(int)(zSorted.Count * 0.65)];

        double[] Rail(bool upper)
        {
            var sel = pts.Where(p => upper ? p.Z >= zHi : p.Z <= zLo)
                         .OrderBy(p => p.S)
                         .ToList();
            var a = new double[sel.Count * 3];
            for (int i = 0; i < sel.Count; i++)
            {
                a[i * 3] = verts[sel[i].Idx * 3];
                a[i * 3 + 1] = verts[sel[i].Idx * 3 + 1];
                a[i * 3 + 2] = verts[sel[i].Idx * 3 + 2];
            }
            return a;
        }

        face.CrestXyz = Rail(upper: true);
        face.ToeXyz = Rail(upper: false);
    }
}
