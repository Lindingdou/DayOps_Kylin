using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PitMine3D.Kylin.Cad.SeamOutcrop
{
    /// <summary>
    /// 露头交线细分：沿「现状面 ∩ 顶板」「现状面 ∩ 底板」的交线把现状网切开，再<b>逐面</b>着色。
    ///
    /// 为什么要切：按节点着色的边界只能落在原有顶点上 —— ① 边界是三角内插出来的渐变，锯齿宽度
    /// ≈ 一个三角边长；② 露头带比一个三角还窄时，整条带从三角内部穿过而三个顶点都在带外，那片
    /// 露头一点色都上不去（漏染）。切开之后每个三角要么整片在带内、要么整片在带外，边界就是交线本身。
    ///
    /// 怎么切（marching triangles）：把「顶/底板」看成现状面上的标量场 d(v) = z现状(v) − z板(x,y)，
    /// 交线 = d 的零集。逐三角看三个顶点的 d 符号：同号不切；异号则在跨零的边上线性插出交点、
    /// 把三角拆成 2~3 个。交点按「边」缓存（键取两端点索引的规范序），相邻三角共用同一个新点 →
    /// 无缝、无重复点。多层煤按各自的顶/底板依次切；同一张面被两层共用（上层底板=下层顶板）时
    /// 只切一次（重复切会因采样残差生成碎针三角）。
    ///
    /// 着色：切完按重心判定每个三角属于哪层煤（先到先得），再<b>只在颜色分界处劈开顶点</b>
    /// （同一原顶点被两种颜色的面共用 → 复制一份），使每个三角三顶点同色 = 硬边界、无渐变。
    /// 内部顶点仍然共用，顶点数只沿露头边界增长。
    /// <para><b>忠实逐字移植</b> <c>MineAssLib.SeamOutcrop.SeamOutcropRefiner</c>（仅改命名空间）。</para>
    /// </summary>
    internal static class SeamOutcropRefiner
    {
        /// <summary>顶点色哨兵：交给内核时替换成源面自身的生效色（单色面 = 那一个实体色）。</summary>
        public const uint BaseColorSentinel = 0xFFFFFFFFu;

        /// <summary>零值吸附容差（m）：|d| 小于它就当"顶点正好落在交线上"，避免切出碎针三角。</summary>
        private const double ZeroSnap = 1e-4;

        /// <summary>并行阈值：元素数小于此值时串行（小网上线程调度开销盖过收益）。</summary>
        private const int ParallelThreshold = 20_000;

        /// <summary>一层煤：顶/底板采样器 + 煤色（0x00RRGGBB）。</summary>
        internal readonly struct SeamField
        {
            public readonly MeshZSampler Roof;
            public readonly MeshZSampler Floor;
            public readonly uint Rgb;
            public SeamField(MeshZSampler roof, MeshZSampler floor, uint rgb)
            { Roof = roof; Floor = floor; Rgb = rgb; }
        }

        /// <summary>细分 + 着色结果（可直接送内核建面）。</summary>
        internal sealed class Result
        {
            public double[] Verts = Array.Empty<double>();   // 世界坐标 [x,y,z,...]
            public uint[] Tris = Array.Empty<uint>();        // 三角索引 [i0,i1,i2,...]
            public uint[] Colors = Array.Empty<uint>();      // 逐顶点 0x00RRGGBB（哨兵=源面底色）
            public int CutVerts;      // 交线上插出的点数
            public int SplitTris;     // 切割次数（一个三角被顶板、底板各切一刀记 2 次）
            public int CoalTris;      // 判为露头的三角数（各层合计）
            public int TotalTris;     // 细分后总三角数
            public int[] SeamTris = Array.Empty<int>();   // 各层各自的露头三角数（与入参 seams 同序）

            // ── R9：露头**面积**（2026-08-06）。判据与着色同一处产生，杜绝"图上一个数、报表另一个数" ──
            /// <summary>各层露头面的真实（三维）面积 m²，与入参 seams 同序。</summary>
            public double[] SeamArea3D = Array.Empty<double>();
            /// <summary>各层露头面的水平投影面积 m²（陡坡处明显小于三维面积，两个都给，别替对方）。</summary>
            public double[] SeamAreaXY = Array.Empty<double>();
            /// <summary>逐面归属层序（−1=非露头）。与 <see cref="Tris"/> 同序，供上层提边界环。</summary>
            public int[] FaceSeam = Array.Empty<int>();
        }

        /// <summary>
        /// 沿各层顶/底板交线细分现状网并逐面着色。
        /// <paramref name="verts"/>/<paramref name="tris"/> 为现状面世界坐标与索引（不被修改）。
        /// </summary>
        public static Result Build(double[] verts, int[] tris,
                                   IReadOnlyList<SeamField> seams,
                                   double snapEps, RegionMask? region)
        {
            var r = new Result();
            if (verts == null || tris == null || verts.Length < 9 || tris.Length < 3 || seams == null)
                return r;

            int nv0 = verts.Length / 3;
            var vx = new List<double>(verts.Length + verts.Length / 4);
            vx.AddRange(verts);
            var idx = new List<int>(tris);

            // ── ① 逐张顶/底板切一刀（同一张面只切一次：上层底板=下层顶板时切两遍会生成碎针三角）──
            var cutDone = new List<MeshZSampler>(seams.Count * 2);
            foreach (var s in seams)
            {
                foreach (var field in new[] { s.Floor, s.Roof })
                {
                    if (field == null || field.IsEmpty) continue;
                    if (cutDone.Contains(field)) continue;   // 引用比较：同 handle → 同一个采样器实例
                    cutDone.Add(field);
                    CutByField(vx, ref idx, field, r);
                }
            }
            r.CutVerts = vx.Count / 3 - nv0;

            // 切完定形，落成数组（后面两步都只读，且要并行）
            double[] vArr = vx.ToArray();
            int[] iArr = idx.ToArray();
            vx.Clear(); idx.Clear();   // 尽早还内存：大网上这两块各几十 MB

            // ── ② 逐面判定：重心落在哪层煤的带内（先到先得）→ 该面的颜色 ──
            int nt = iArr.Length / 3;
            var faceColor = new uint[nt];
            r.SeamTris = new int[seams.Count];
            r.SeamArea3D = new double[seams.Count];
            r.SeamAreaXY = new double[seams.Count];
            r.FaceSeam = new int[nt];
            r.CoalTris = ClassifyFaces(vArr, iArr, seams, snapEps, region, faceColor, r);
            r.TotalTris = nt;

            // ── ③ 只在颜色分界处劈开顶点 → 每个三角三顶点同色（硬边界，无渐变）──
            Unweld(vArr, iArr, faceColor, r);
            return r;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ① 交线切割
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按标量场 d(v) = z(v) − sampler(x,y) 的零集切开当前网。
        /// 采不到（板没盖到该 xy）记 NaN，含 NaN 的三角原样保留（那里本来就不是露头）。
        /// </summary>
        private static void CutByField(List<double> vx, ref List<int> idx, MeshZSampler field, Result stat)
        {
            int nv = vx.Count / 3;
            var d = new double[nv];
            double[] vArr = vx.ToArray();   // 采样阶段只读，拷成数组便于并行

            void Fill(int from, int to)
            {
                for (int v = from; v < to; v++)
                {
                    int p = v * 3;
                    d[v] = field.TrySampleZ(vArr[p], vArr[p + 1], out double zs)
                         ? vArr[p + 2] - zs
                         : double.NaN;
                }
            }
            if (nv < ParallelThreshold) Fill(0, nv);
            else Parallel.ForEach(Partitioner.Create(0, nv), rg => Fill(rg.Item1, rg.Item2));

            int ntIn = idx.Count / 3;
            var outIdx = new List<int>(idx.Count + idx.Count / 8);
            var edgePt = new Dictionary<ulong, int>(PackedKeyComparer.Instance);   // 边(规范序) → 交点顶点号，保证相邻三角共用

            for (int t = 0; t < ntIn; t++)
            {
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                double da = d[a], db = d[b], dc = d[c];
                if (double.IsNaN(da) || double.IsNaN(db) || double.IsNaN(dc))
                {
                    outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);
                    continue;
                }

                int sa = Sign(da), sb = Sign(db), sc = Sign(dc);
                int zeros = (sa == 0 ? 1 : 0) + (sb == 0 ? 1 : 0) + (sc == 0 ? 1 : 0);

                // 不需要切：全同号 / 两点以上落在交线上（那条边本身就是交线）
                if (zeros >= 2 || (sa == sb && sb == sc))
                {
                    outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);
                    continue;
                }

                if (zeros == 1)
                {
                    // 一个顶点在交线上：另两点异号时切它们之间那条边 → 2 个三角；同号则不切
                    int p0, p1, p2;   // p0=零点，p1/p2=另两点（保持原绕向 p0→p1→p2）
                    int s1, s2;
                    if (sa == 0) { p0 = a; p1 = b; p2 = c; s1 = sb; s2 = sc; }
                    else if (sb == 0) { p0 = b; p1 = c; p2 = a; s1 = sc; s2 = sa; }
                    else { p0 = c; p1 = a; p2 = b; s1 = sa; s2 = sb; }

                    if (s1 == s2)
                    {
                        outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);
                        continue;
                    }
                    int m = EdgePoint(vx, d, edgePt, p1, p2);
                    outIdx.Add(p0); outIdx.Add(p1); outIdx.Add(m);
                    outIdx.Add(p0); outIdx.Add(m); outIdx.Add(p2);
                    stat.SplitTris++;
                    continue;
                }

                // zeros == 0 且非全同号：必有一点独处一侧 → 切它的两条邻边 → 3 个三角
                int q0, q1, q2;   // q0=独处的那点，绕向 q0→q1→q2
                if (sa != sb && sa != sc) { q0 = a; q1 = b; q2 = c; }
                else if (sb != sa && sb != sc) { q0 = b; q1 = c; q2 = a; }
                else { q0 = c; q1 = a; q2 = b; }

                int m1 = EdgePoint(vx, d, edgePt, q0, q1);   // 在 q0→q1 边上
                int m2 = EdgePoint(vx, d, edgePt, q2, q0);   // 在 q2→q0 边上
                outIdx.Add(q0); outIdx.Add(m1); outIdx.Add(m2);
                outIdx.Add(m1); outIdx.Add(q1); outIdx.Add(q2);
                outIdx.Add(m1); outIdx.Add(q2); outIdx.Add(m2);
                stat.SplitTris++;
            }

            idx = outIdx;
        }

        private static int Sign(double v) => v > ZeroSnap ? 1 : (v < -ZeroSnap ? -1 : 0);

        /// <summary>
        /// 取（或新建）边 (i,j) 上的交点。键用规范序 (min,max)，插值也按规范序算 ——
        /// 相邻两个三角对同一条边算出<b>同一个</b>点，网面无缝、不产生重复点。
        /// </summary>
        private static int EdgePoint(List<double> vx, double[] d, Dictionary<ulong, int> cache, int i, int j)
        {
            int lo = Math.Min(i, j), hi = Math.Max(i, j);
            ulong key = ((ulong)(uint)lo << 32) | (uint)hi;
            if (cache.TryGetValue(key, out int found)) return found;

            double dlo = d[lo], dhi = d[hi];
            double den = dlo - dhi;
            double t = Math.Abs(den) > 1e-300 ? dlo / den : 0.5;   // d=0 处：t = dlo/(dlo-dhi)
            if (!(t > 0.0)) t = 0.0;
            if (!(t < 1.0)) t = 1.0;

            int pl = lo * 3, ph = hi * 3;
            int nv = vx.Count / 3;
            vx.Add(vx[pl]     + (vx[ph]     - vx[pl])     * t);
            vx.Add(vx[pl + 1] + (vx[ph + 1] - vx[pl + 1]) * t);
            vx.Add(vx[pl + 2] + (vx[ph + 2] - vx[pl + 2]) * t);
            cache[key] = nv;
            return nv;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ② 逐面判定
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按重心判定每个三角属于哪层煤（先到先得）；填 <paramref name="perSeam"/> 各层三角数，
        /// 返回合计。重心严格在三角内部，切开之后它落在哪一侧就是整个三角的归属 —— 这正是
        /// 交线细分带来的确定性：不再有"半边在带内"的三角。
        /// </summary>
        private static int ClassifyFaces(double[] vArr, int[] iArr, IReadOnlyList<SeamField> seams,
                                         double snapEps, RegionMask? region, uint[] faceColor, Result res)
        {
            int nt = iArr.Length / 3;
            double eps = Math.Max(0.0, snapEps);
            const double third = 1.0 / 3.0;
            int coal = 0;
            var faceSeam = res.FaceSeam;

            int Run(int from, int to, int[] hits, double[] a3, double[] axy)
            {
                int total = 0;
                for (int t = from; t < to; t++)
                {
                    int pa = iArr[t * 3] * 3, pb = iArr[t * 3 + 1] * 3, pc = iArr[t * 3 + 2] * 3;
                    double x = (vArr[pa] + vArr[pb] + vArr[pc]) * third;
                    double y = (vArr[pa + 1] + vArr[pb + 1] + vArr[pc + 1]) * third;
                    double z = (vArr[pa + 2] + vArr[pb + 2] + vArr[pc + 2]) * third;

                    faceColor[t] = BaseColorSentinel;
                    faceSeam[t] = -1;
                    if (region != null && !region.Contains(x, y)) continue;

                    for (int k = 0; k < seams.Count; k++)
                    {
                        var s = seams[k];
                        if (s.Floor == null || s.Roof == null) continue;
                        if (!s.Floor.TrySampleZ(x, y, out double zf) || z < zf - eps) continue;
                        if (!s.Roof.TrySampleZ(x, y, out double zr) || z > zr + eps) continue;
                        faceColor[t] = s.Rgb;
                        faceSeam[t] = k;
                        hits[k]++;
                        total++;
                        // R9：面积在这里算——判归属和算面积必须是同一处，否则两个数迟早对不上。
                        double ux = vArr[pb] - vArr[pa], uy = vArr[pb + 1] - vArr[pa + 1], uz = vArr[pb + 2] - vArr[pa + 2];
                        double wx = vArr[pc] - vArr[pa], wy = vArr[pc + 1] - vArr[pa + 1], wz = vArr[pc + 2] - vArr[pa + 2];
                        double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz2 = ux * wy - uy * wx;
                        a3[k] += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz2 * cz2);
                        axy[k] += 0.5 * Math.Abs(cz2);            // 叉积 z 分量 = 水平投影的两倍面积
                        break;
                    }
                }
                return total;
            }

            if (nt < ParallelThreshold) return Run(0, nt, res.SeamTris, res.SeamArea3D, res.SeamAreaXY);

            object gate = new();
            Parallel.ForEach(Partitioner.Create(0, nt), rg =>
            {
                var local = new int[seams.Count];          // 分片各记各的，最后一次性并回去
                var la3 = new double[seams.Count];
                var laxy = new double[seams.Count];
                int h = Run(rg.Item1, rg.Item2, local, la3, laxy);
                if (h == 0) return;
                System.Threading.Interlocked.Add(ref coal, h);
                lock (gate)                                 // double 无 Interlocked.Add，面积一起进锁
                    for (int k = 0; k < local.Length; k++)
                    {
                        res.SeamTris[k] += local[k];
                        res.SeamArea3D[k] += la3[k];
                        res.SeamAreaXY[k] += laxy[k];
                    }
            });
            return coal;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ③ 颜色分界处劈点
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 逐面色 → 逐顶点色：同一顶点被多种颜色的面共用时按颜色复制多份，其余共用。
        /// 快路径走"每顶点第一种颜色"的数组命中（绝大多数顶点只有一种颜色），只有边界点进字典。
        /// </summary>
        private static void Unweld(double[] vArr, int[] iArr, uint[] faceColor, Result r)
        {
            int nvOld = vArr.Length / 3;
            int nt = faceColor.Length;

            var firstColor = new uint[nvOld];
            var firstSlot = new int[nvOld];
            for (int i = 0; i < nvOld; i++) firstSlot[i] = -1;
            Dictionary<ulong, int>? extra = null;   // (顶点,颜色) → 新顶点号；只有分界点会用到

            var outVerts = new List<double>(vArr.Length + 3 * 64);
            var outColors = new List<uint>(nvOld + 64);
            var outIdx = new uint[nt * 3];

            int Map(int v, uint color)
            {
                if (firstSlot[v] < 0)
                {
                    int slot = outColors.Count;
                    int p = v * 3;
                    outVerts.Add(vArr[p]); outVerts.Add(vArr[p + 1]); outVerts.Add(vArr[p + 2]);
                    outColors.Add(color);
                    firstSlot[v] = slot; firstColor[v] = color;
                    return slot;
                }
                if (firstColor[v] == color) return firstSlot[v];

                extra ??= new Dictionary<ulong, int>(PackedKeyComparer.Instance);
                ulong key = ((ulong)(uint)v << 32) | color;
                if (extra.TryGetValue(key, out int found)) return found;

                int ns = outColors.Count;
                int q = v * 3;
                outVerts.Add(vArr[q]); outVerts.Add(vArr[q + 1]); outVerts.Add(vArr[q + 2]);
                outColors.Add(color);
                extra[key] = ns;
                return ns;
            }

            for (int t = 0; t < nt; t++)
            {
                uint col = faceColor[t];
                outIdx[t * 3]     = (uint)Map(iArr[t * 3], col);
                outIdx[t * 3 + 1] = (uint)Map(iArr[t * 3 + 1], col);
                outIdx[t * 3 + 2] = (uint)Map(iArr[t * 3 + 2], col);
            }

            r.Verts = outVerts.ToArray();
            r.Colors = outColors.ToArray();
            r.Tris = outIdx;
        }
    }
}
