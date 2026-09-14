using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PitMine3D.Kylin.Cad.SeamOutcrop
{
    /// <summary>
    /// 煤层露头逐顶点分类（纯 C#）。露头 = 现状面高程夹在【底板 ≤ 现状 ≤ 顶板】的区域。
    ///
    /// 按「节点着色」：判定现状面每个顶点是否落在某层煤的露头内（可选再裁到可采范围），是则标记该顶点
    /// 用该层煤色；随后由内核克隆现状网、把这些顶点覆盖成煤色（其余保留原色）得一张完整着色面。
    /// 顶/底板未盖到某顶点(nodata)则该顶点不算露头（保留原色）。
    ///
    /// 性能：逐顶点判定彼此独立（每个 v 只被本层写一次），故按区间分块并行；每个顶点先走
    /// 「范围环包围盒 → 顶底板包围盒交集 → 采底板 → 低于底板早退 → 采顶板」的由廉到贵短路链。
    /// <para><b>忠实逐字移植</b> <c>MineAssLib.SeamOutcrop.SeamOutcropEngine</c>（仅改命名空间）。</para>
    /// </summary>
    internal static class SeamOutcropEngine
    {
        /// <summary>并行阈值：顶点数小于此值时串行跑（避免小网上的线程调度开销盖过收益）。</summary>
        private const int ParallelThreshold = 20_000;

        /// <summary>
        /// 标记本层煤的露头顶点：对每个【尚未被其它层认领】的现状顶点，若落在本层顶底板之间
        /// （且在可采范围内，若指定），标记 assigned 并写 packedRgb 到 color。返回本层新标记数。
        /// </summary>
        public static int MarkOutcropVertices(
            double[] terrainVerts, MeshZSampler roof, MeshZSampler floor,
            double snapEps, RegionMask? region, uint packedRgb,
            bool[] assigned, uint[] color, out int noData)
        {
            noData = 0;
            int nv = terrainVerts.Length / 3;
            if (nv < 1 || assigned.Length < nv || color.Length < nv) return 0;

            double eps = Math.Max(0.0, snapEps);
            // 顶/底板 XY 包围盒交集：盒外的现状顶点两面都盖不到，4 次比较即可剔除（等价于旧版两次采样失败）。
            double bx0 = Math.Max(roof.MinX, floor.MinX), bx1 = Math.Min(roof.MaxX, floor.MaxX);
            double by0 = Math.Max(roof.MinY, floor.MinY), by1 = Math.Min(roof.MaxY, floor.MaxY);

            if (nv < ParallelThreshold)
            {
                MarkRange(0, nv, terrainVerts, roof, floor, eps, region, packedRgb, assigned, color,
                          bx0, bx1, by0, by1, out int m0, out int nd0);
                noData = nd0;
                return m0;
            }

            int marked = 0, nodataTotal = 0;
            Parallel.ForEach(Partitioner.Create(0, nv), range =>
            {
                MarkRange(range.Item1, range.Item2, terrainVerts, roof, floor, eps, region, packedRgb,
                          assigned, color, bx0, bx1, by0, by1, out int m, out int nd);
                if (m != 0) Interlocked.Add(ref marked, m);
                if (nd != 0) Interlocked.Add(ref nodataTotal, nd);
            });
            noData = nodataTotal;
            return marked;
        }

        /// <summary>
        /// 漏染估算：本层露头带穿过、但三个顶点都没被任何层认领的三角面数量。
        ///
        /// 按节点着色的固有边界精度 = 现状网三角尺度：露头带比一个三角还窄时，可能整条带子从三角内部
        /// 穿过而三个顶点都不在带内 → 这片露头一点色都上不去。这里用重心采一次做估算（只是量级参考，
        /// 不是精确面积），数值明显不为 0 就说明该按交线重剖分（沿顶/底板与现状面的交线插点再三角化）。
        /// </summary>
        public static int CountUncoveredTriangles(
            double[] terrainVerts, int[] terrainTris, bool[] assigned,
            MeshZSampler roof, MeshZSampler floor, double snapEps, RegionMask? region)
        {
            if (terrainVerts == null || terrainTris == null || assigned == null) return 0;
            int nt = terrainTris.Length / 3;
            int nv = terrainVerts.Length / 3;
            if (nt < 1) return 0;

            double eps = Math.Max(0.0, snapEps);
            const double third = 1.0 / 3.0;

            int Count(int from, int to)
            {
                int miss = 0;
                for (int t = from; t < to; t++)
                {
                    int a = terrainTris[t * 3], b = terrainTris[t * 3 + 1], c = terrainTris[t * 3 + 2];
                    if ((uint)a >= (uint)nv || (uint)b >= (uint)nv || (uint)c >= (uint)nv) continue;
                    if (assigned[a] || assigned[b] || assigned[c]) continue;   // 已有顶点染上色 → 这片显示得出来

                    int pa = a * 3, pb = b * 3, pc = c * 3;
                    double x = (terrainVerts[pa] + terrainVerts[pb] + terrainVerts[pc]) * third;
                    double y = (terrainVerts[pa + 1] + terrainVerts[pb + 1] + terrainVerts[pc + 1]) * third;
                    double z = (terrainVerts[pa + 2] + terrainVerts[pb + 2] + terrainVerts[pc + 2]) * third;

                    if (region != null && !region.Contains(x, y)) continue;
                    if (!floor.TrySampleZ(x, y, out double zf) || z < zf - eps) continue;
                    if (!roof.TrySampleZ(x, y, out double zr) || z > zr + eps) continue;
                    miss++;
                }
                return miss;
            }

            if (nt < ParallelThreshold) return Count(0, nt);

            int total = 0;
            Parallel.ForEach(Partitioner.Create(0, nt), range =>
            {
                int m = Count(range.Item1, range.Item2);
                if (m != 0) Interlocked.Add(ref total, m);
            });
            return total;
        }

        /// <summary>顶点区间 [from, to) 的分类（各区间互不重叠 → 对 assigned/color 的写入天然无竞争）。</summary>
        private static void MarkRange(
            int from, int to, double[] tv, MeshZSampler roof, MeshZSampler floor,
            double eps, RegionMask? region, uint packedRgb, bool[] assigned, uint[] color,
            double bx0, double bx1, double by0, double by1,
            out int marked, out int noData)
        {
            marked = 0; noData = 0;
            for (int v = from; v < to; v++)
            {
                if (assigned[v]) continue;   // 已被前面的层认领
                int p = v * 3;
                double x = tv[p], y = tv[p + 1];

                if (region != null && !region.Contains(x, y)) continue;   // 裁到可采范围
                if (x < bx0 || x > bx1 || y < by0 || y > by1) { noData++; continue; }   // 顶/底板都没盖到

                if (!floor.TrySampleZ(x, y, out double zf)) { noData++; continue; }
                double z = tv[p + 2];
                if (z < zf - eps) continue;                                // 低于底板：不必再采顶板
                if (!roof.TrySampleZ(x, y, out double zr)) { noData++; continue; }
                if (z <= zr + eps)                                         // 底板 ≤ 现状 ≤ 顶板（含 eps 容差）
                {
                    assigned[v] = true;
                    color[v] = packedRgb;
                    marked++;
                }
            }
        }
    }

    /// <summary>
    /// 可采范围掩膜：一组扁平 [x0,y0,x1,y1,...] 隐式闭合环 + 各自包围盒。
    /// 逐顶点射线法是 O(环顶点数) 的重活，先用总包围盒 / 单环包围盒 4 次比较短路，绝大多数点不进射线循环。
    /// 不可变，<see cref="Contains"/> 可并发调用。
    /// </summary>
    internal sealed class RegionMask
    {
        private readonly double[][] _rings;
        private readonly double[] _bb;   // 每环 4 个：minX,minY,maxX,maxY
        private readonly double _minX, _minY, _maxX, _maxY;

        private RegionMask(double[][] rings, double[] bb, double minX, double minY, double maxX, double maxY)
        {
            _rings = rings; _bb = bb;
            _minX = minX; _minY = minY; _maxX = maxX; _maxY = maxY;
        }

        /// <summary>建掩膜；无有效环（null/空/顶点不足）返回 null，上层据此不裁。</summary>
        public static RegionMask? Build(IReadOnlyList<double[]>? rings)
        {
            if (rings == null || rings.Count == 0) return null;
            var keep = new List<double[]>(rings.Count);
            foreach (var r in rings)
                if (r != null && r.Length >= 6) keep.Add(r);
            if (keep.Count == 0) return null;

            var arr = keep.ToArray();
            var bb = new double[arr.Length * 4];
            double gx0 = double.MaxValue, gy0 = double.MaxValue, gx1 = double.MinValue, gy1 = double.MinValue;
            for (int i = 0; i < arr.Length; i++)
            {
                double[] ring = arr[i];
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                for (int k = 0; k + 1 < ring.Length; k += 2)
                {
                    double x = ring[k], y = ring[k + 1];
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                bb[i * 4] = x0; bb[i * 4 + 1] = y0; bb[i * 4 + 2] = x1; bb[i * 4 + 3] = y1;
                if (x0 < gx0) gx0 = x0; if (x1 > gx1) gx1 = x1;
                if (y0 < gy0) gy0 = y0; if (y1 > gy1) gy1 = y1;
            }
            return new RegionMask(arr, bb, gx0, gy0, gx1, gy1);
        }

        /// <summary>点在任一范围环内（射线法）。</summary>
        public bool Contains(double x, double y)
        {
            if (x < _minX || x > _maxX || y < _minY || y > _maxY) return false;
            for (int r = 0; r < _rings.Length; r++)
            {
                int b = r * 4;
                if (x < _bb[b] || x > _bb[b + 2] || y < _bb[b + 1] || y > _bb[b + 3]) continue;
                if (PointInRing(_rings[r], x, y)) return true;
            }
            return false;
        }

        private static bool PointInRing(double[] ring, double x, double y)
        {
            int n = ring.Length / 2;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = ring[i * 2], yi = ring[i * 2 + 1];
                double xj = ring[j * 2], yj = ring[j * 2 + 1];
                bool cross = ((yi > y) != (yj > y)) &&
                             (x < (xj - xi) * (y - yi) / (yj - yi) + xi);
                if (cross) inside = !inside;
            }
            return inside;
        }
    }
}
