using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「更新煤层面」完整引擎 —— 逐字移植原 MeshEditLib.ModelUpdate.SurfaceUpdateEngine(全部算法路径):
/// 自动选区 = 观测点影响半径内的顶点(区外不动), 按到最近观测点的距离 smoothstep 羽化(内 1→边界 0),
/// 区内 Z 用估值(NN/MA/IDW/OK/SK/UK)拟合到观测点; 单点=1 观测点(退化 NN)。
/// 相比 <see cref="SurfaceUpdate"/>(默认 IDW 路径) 多出: 观测点 AABB 预剔除 + 并行逐顶点、真正变化顶点 AABB、
/// 片区面积/净体积、网面 XY→Z 采样器 <see cref="MeshSampler2D"/>、影响预览分级格 <see cref="BuildOverlay"/>
/// (原贴到三维面的 PMBI 分级填色带 → 场景里按级着色的采样格 + 片区标注)、更新后三角网组装。
/// 原估值内核 PitMine3D.Estimation(SampleGrid2D/EstimationEngine) 以本文件内 <see cref="SampleGrid2D"/> +
/// <see cref="ComputeEstimate"/>(IDW/NN/MA 直算, OK/SK/UK 走 <see cref="OrdinaryKriging"/>) 等价实现。纯逻辑、可单测。
/// </summary>
public static class SurfaceUpdateEngine
{
    public const double NoData = -999.0;
    public const string PropKey = "高程";

    public sealed class Options
    {
        public string Algorithm = "IDW";       // 多点估值算法; 单点内部退化为 NN
        public double InfluenceRadius = 80.0;   // 影响半径(m): 自动选区范围 + 羽化尺度
        public int MaxSamples = 16;
        public int MinSamples = 1;
        public double IdwPower = 2.0;
    }

    public sealed class Result
    {
        public double[] NewZ = Array.Empty<double>();    // 每顶点新 Z(未受影响=原 Z)
        public double[] Disp = Array.Empty<double>();    // 位移(newZ-oldZ)
        public bool[] Affected = Array.Empty<bool>();    // 羽化权重>0 = 受影响(自动选区)
        public int AffectedVertices;
        public double MaxAbsDisp, MeanAbsDisp, AffectedArea;
        public double MaxDisp, MinDisp;                  // 最大抬升(+) / 最大下沉(-)
        public double NetVolume;                         // 净体积变化 Σ位移×面积(m³,+=抬升)
        public double TotalArea;                         // 目标网总水平投影面积(m²,算占比用)
        public List<ClusterStat> Clusters = new();       // 影响片区(影响圈相互搭接的观测点归一片)
        public double MinX, MinY, MaxX, MaxY;            // 影响 AABB(观测点包围盒外扩半径)
        public bool HasBounds;
        public double AffMinX, AffMinY, AffMaxX, AffMaxY;// 真正发生变化的顶点 AABB(预览取景用, 比上面紧)
        public bool HasAffBounds;
        public string Message = "";
        public bool Any => AffectedVertices > 0;
    }

    /// <summary>影响片区统计(影响圈搭接=距离&lt;2R 的观测点并为一片)。</summary>
    public sealed class ClusterStat
    {
        public double Cx, Cy;            // 片区观测点质心
        public int ObsCount;             // 观测点数
        public int VertCount;            // 受影响顶点数
        public double Area;              // 受影响面积(m²)
        public double MaxAbs;            // 最大|位移|(m)
        public double NetVol;            // 净体积(m³,+=抬升)
    }

    /// <param name="worldVerts">目标网世界坐标 [x,y,z,...]</param>
    /// <param name="tris">三角索引</param>
    /// <param name="obs">观测点 (x,y,z)</param>
    public static Result Evaluate(double[] worldVerts, int[] tris,
        IReadOnlyList<(double x, double y, double z)> obs, Options opt)
    {
        int V = worldVerts.Length / 3;
        var res = new Result { NewZ = new double[V], Disp = new double[V], Affected = new bool[V] };
        for (int i = 0; i < V; i++) res.NewZ[i] = worldVerts[i * 3 + 2];

        if (obs.Count == 0) { res.Message = "无观测点"; return res; }

        var ctx = BuildCtx(obs, opt);
        double R = Math.Max(1e-3, opt.InfluenceRadius);

        // 观测点 XY 包围盒外扩 R = 影响 AABB: 盒外顶点不可能受影响 → 廉价拒绝, 不进 FindNearest。
        double oMinX = double.MaxValue, oMinY = double.MaxValue, oMaxX = double.MinValue, oMaxY = double.MinValue;
        foreach (var o in obs)
        {
            if (o.x < oMinX) oMinX = o.x; if (o.x > oMaxX) oMaxX = o.x;
            if (o.y < oMinY) oMinY = o.y; if (o.y > oMaxY) oMaxY = o.y;
        }
        double boxMinX = oMinX - R, boxMaxX = oMaxX + R, boxMinY = oMinY - R, boxMaxY = oMaxY + R;
        res.MinX = boxMinX; res.MaxX = boxMaxX; res.MinY = boxMinY; res.MaxY = boxMaxY; res.HasBounds = true;

        // 逐顶点相互独立 → 并行填 NewZ/Disp/Affected(不在此累加统计, 避免对 double 竞态); 统计随后串行归约。
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, V, po, i =>
        {
            double vx = worldVerts[i * 3], vy = worldVerts[i * 3 + 1], vz = worldVerts[i * 3 + 2];
            if (vx < boxMinX || vx > boxMaxX || vy < boxMinY || vy > boxMaxY) return;   // AABB 预剔除

            var hits = ctx.Grid.FindNearest(vx, vy, R, opt.MaxSamples);
            if (hits.Count == 0) return;                         // 影响半径外 → 不动

            double est = ComputeEstimate(ctx, hits, vx, vy);
            if (double.IsNaN(est) || est <= NoData + 1e-6) return;

            double d = hits[0].dist;                             // 到最近观测(水平)
            double t = Math.Clamp(1.0 - d / R, 0, 1);
            double w = t * t * (3 - 2 * t);                      // smoothstep 羽化
            if (w <= 1e-6) return;

            double newZ = vz + w * (est - vz);
            res.NewZ[i] = newZ; res.Disp[i] = newZ - vz; res.Affected[i] = true;
        });

        // 串行归约统计; 顺带收"真正变了的顶点"AABB → 预览按它取景, 不被离网/远端观测点撑大
        double sumAbs = 0, maxAbs = 0, maxUp = 0, maxDown = 0; int nAff = 0;
        double aMinX = double.MaxValue, aMinY = double.MaxValue, aMaxX = double.MinValue, aMaxY = double.MinValue;
        for (int i = 0; i < V; i++)
        {
            if (!res.Affected[i]) continue;
            double dsp = res.Disp[i], a = Math.Abs(dsp);
            sumAbs += a; if (a > maxAbs) maxAbs = a;
            if (dsp > maxUp) maxUp = dsp; if (dsp < maxDown) maxDown = dsp;
            double vx = worldVerts[i * 3], vy = worldVerts[i * 3 + 1];
            if (vx < aMinX) aMinX = vx; if (vx > aMaxX) aMaxX = vx;
            if (vy < aMinY) aMinY = vy; if (vy > aMaxY) aMaxY = vy;
            nAff++;
        }
        if (nAff > 0 && aMaxX > aMinX && aMaxY > aMinY)
        {
            res.AffMinX = aMinX; res.AffMinY = aMinY; res.AffMaxX = aMaxX; res.AffMaxY = aMaxY;
            res.HasAffBounds = true;
        }

        // ── 观测点分片区: 影响圈搭接(距离<2R)并为一片(并查集) ──
        int nObs = obs.Count;
        var parent = new int[nObs];
        for (int i = 0; i < nObs; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        for (int i = 0; i < nObs; i++)
            for (int j = i + 1; j < nObs; j++)
            {
                double ddx = obs[i].x - obs[j].x, ddy = obs[i].y - obs[j].y;
                if (ddx * ddx + ddy * ddy < 4 * R * R) parent[Find(i)] = Find(j);
            }
        var rootToCluster = new Dictionary<int, int>();
        var obsCluster = new int[nObs];
        for (int i = 0; i < nObs; i++)
        {
            int r = Find(i);
            if (!rootToCluster.TryGetValue(r, out int c))
            { c = res.Clusters.Count; rootToCluster[r] = c; res.Clusters.Add(new ClusterStat()); }
            obsCluster[i] = c;
            var cs = res.Clusters[c];
            cs.ObsCount++; cs.Cx += obs[i].x; cs.Cy += obs[i].y;
        }
        foreach (var cs in res.Clusters) { cs.Cx /= cs.ObsCount; cs.Cy /= cs.ObsCount; }

        // 受影响顶点 → 最近观测点所在片区
        var vertCluster = new int[V];
        for (int i = 0; i < V; i++)
        {
            vertCluster[i] = -1;
            if (!res.Affected[i]) continue;
            var h1 = ctx.Grid.FindNearest(worldVerts[i * 3], worldVerts[i * 3 + 1], R, 1);
            if (h1.Count == 0) continue;
            int c = obsCluster[h1[0].index];
            vertCluster[i] = c;
            var cs = res.Clusters[c];
            cs.VertCount++;
            double a = Math.Abs(res.Disp[i]); if (a > cs.MaxAbs) cs.MaxAbs = a;
        }

        // 面积/体积: 受影响三角形按"第一个受影响角点"归片; 体积=面积×三角平均位移(未受影响角=0)
        double area = 0, totalArea = 0, netVol = 0;
        int F = tris.Length / 3;
        for (int f = 0; f < F; f++)
        {
            int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
            if (a < 0 || b < 0 || c < 0 || a >= V || b >= V || c >= V) continue;
            double triArea = TriAreaXY(worldVerts, a, b, c);
            totalArea += triArea;
            if (!res.Affected[a] && !res.Affected[b] && !res.Affected[c]) continue;
            area += triArea;
            double meanDisp = (res.Disp[a] + res.Disp[b] + res.Disp[c]) / 3.0;
            double vol = triArea * meanDisp;
            netVol += vol;
            int cl = res.Affected[a] ? vertCluster[a] : (res.Affected[b] ? vertCluster[b] : vertCluster[c]);
            if (cl >= 0) { var cs = res.Clusters[cl]; cs.Area += triArea; cs.NetVol += vol; }
        }

        res.AffectedVertices = nAff;
        res.MaxAbsDisp = maxAbs;
        res.MeanAbsDisp = nAff > 0 ? sumAbs / nAff : 0;
        res.MaxDisp = maxUp; res.MinDisp = maxDown;
        res.AffectedArea = area;
        res.TotalArea = totalArea;
        res.NetVolume = netVol;
        res.Clusters.RemoveAll(cs => cs.VertCount == 0 && cs.Area <= 0);   // 半径内无网面的空片区不报
        res.Message = nAff > 0
            ? $"自动选区：受影响 {nAff} 顶点 / 面积 {area:F0} m²；位移 最大 {maxAbs:F2} m、平均 {res.MeanAbsDisp:F2} m"
            : "影响半径内无有效估值（观测点太远或不足），可调大影响半径";
        return res;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  估值上下文(Evaluate 与 BuildOverlay 共用): 样本水平化 + 空间索引 + 任务配置(原 PitMine3D.Estimation 等价)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>估值上下文: 观测样本(水平化, Z 置 0 使近邻距离只算水平)+ 格桶索引 + 算法配置 + 克里金预备。</summary>
    public sealed class EstimationCtx
    {
        public IReadOnlyList<(double x, double y, double z)> Obs = Array.Empty<(double, double, double)>();
        public SampleGrid2D Grid = null!;
        public string Algorithm = "IDW";
        public double SearchRadius, IdwPower = 2;
        public int MaxSamples = 16, MinSamples = 1;
        public double GlobalMean;
        public List<OrdinaryKriging.ControlPoint>? Cps;      // 克里金控制点(OK/SK/UK)
        public OrdinaryKriging.Variogram? Vg;                 // 全局变差函数(拟合一次, 免逐点重拟)
    }

    public static EstimationCtx BuildCtx(IReadOnlyList<(double x, double y, double z)> obs, Options opt)
    {
        var ctx = new EstimationCtx
        {
            Obs = obs,
            Grid = new SampleGrid2D(obs),
            Algorithm = (obs.Count == 1 ? "NN" : (opt.Algorithm ?? "IDW")).ToUpperInvariant(),   // 单点退化 NN
            SearchRadius = opt.InfluenceRadius,
            MaxSamples = opt.MaxSamples,
            MinSamples = Math.Max(1, opt.MinSamples),
            IdwPower = opt.IdwPower,
            GlobalMean = obs.Count > 0 ? obs.Average(o => o.z) : 0,
        };
        if (ctx.Algorithm is "OK" or "SK" or "UK")
        {
            ctx.Cps = obs.Select(o => new OrdinaryKriging.ControlPoint(o.x, o.y, 0, o.z)).ToList();
            if (ctx.Cps.Count >= 2) ctx.Vg = OrdinaryKriging.FitVariogram(ctx.Cps);
        }
        return ctx;
    }

    /// <summary>
    /// 在 (x,y) 估高程(原 EstimationEngine.ComputeEstimate): hits 为半径内按距升序的近邻。
    /// NN=最近邻; MA=近 k 等权均值; IDW=Σ(z/dᵖ)/Σ(1/dᵖ)(命中观测点直接取值); OK/SK/UK 走克里金(无支撑 → NoData)。
    /// 近邻不足 MinSamples → NoData。
    /// </summary>
    public static double ComputeEstimate(EstimationCtx ctx, IReadOnlyList<(int index, double dist)> hits, double x, double y)
    {
        if (hits.Count < ctx.MinSamples || hits.Count == 0) return NoData;
        switch (ctx.Algorithm)
        {
            case "NN": return ctx.Obs[hits[0].index].z;
            case "MA":
            {
                double s = 0; foreach (var h in hits) s += ctx.Obs[h.index].z; return s / hits.Count;
            }
            case "OK":
                return OrdinaryKriging.EstimateAt(ctx.Cps!, x, y, 0, ctx.MaxSamples, ctx.SearchRadius, ctx.Vg)?.est ?? NoData;
            case "SK":
                return OrdinaryKriging.EstimateSimpleAt(ctx.Cps!, x, y, 0, ctx.GlobalMean, ctx.MaxSamples, ctx.SearchRadius, ctx.Vg)?.est ?? NoData;
            case "UK":
                return OrdinaryKriging.EstimateUniversalAt(ctx.Cps!, x, y, 0, ctx.MaxSamples, ctx.SearchRadius, ctx.Vg)?.est ?? NoData;
            default:   // IDW
            {
                double num = 0, den = 0;
                foreach (var h in hits)
                {
                    double z = ctx.Obs[h.index].z;
                    if (h.dist < 1e-9) return z;                    // 落在观测点上
                    double wgt = 1.0 / Math.Pow(h.dist, ctx.IdwPower);
                    num += wgt * z; den += wgt;
                }
                return den > 1e-12 ? num / den : ctx.Obs[hits[0].index].z;
            }
        }
    }

    /// <summary>观测点平面格桶索引(原 EstimationAlgorithms.SampleGrid2D): 半径内最近 k 个, 按距升序。</summary>
    public sealed class SampleGrid2D
    {
        private readonly IReadOnlyList<(double x, double y, double z)> _pts;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private readonly double _cs, _x0, _y0;

        public SampleGrid2D(IReadOnlyList<(double x, double y, double z)> pts, double cellSize = 0)
        {
            _pts = pts;
            double xmn = double.MaxValue, ymn = double.MaxValue, xmx = double.MinValue, ymx = double.MinValue;
            foreach (var p in pts) { if (p.x < xmn) xmn = p.x; if (p.x > xmx) xmx = p.x; if (p.y < ymn) ymn = p.y; if (p.y > ymx) ymx = p.y; }
            if (pts.Count == 0) { xmn = ymn = 0; xmx = ymx = 1; }
            _x0 = xmn; _y0 = ymn;
            _cs = cellSize > 0 ? cellSize : Math.Max(1.0, ((xmx - xmn) + (ymx - ymn)) * 0.5 / Math.Max(1.0, Math.Sqrt(Math.Max(1, pts.Count))));
            for (int i = 0; i < pts.Count; i++)
            {
                var k = Key(pts[i].x, pts[i].y);
                if (!_cells.TryGetValue(k, out var l)) { l = new List<int>(); _cells[k] = l; }
                l.Add(i);
            }
        }

        private (int, int) Key(double x, double y) => ((int)Math.Floor((x - _x0) / _cs), (int)Math.Floor((y - _y0) / _cs));

        /// <summary>半径 radius 内最近 k 个样本 (索引, 水平距), 按距升序; 无 → 空表。</summary>
        public List<(int index, double dist)> FindNearest(double x, double y, double radius, int k)
        {
            var found = new List<(int index, double dist)>();
            if (_pts.Count == 0 || k <= 0) return found;
            double r2 = radius * radius;
            int span = (int)Math.Ceiling(radius / _cs);
            var (cx, cy) = Key(x, y);
            for (int gx = cx - span; gx <= cx + span; gx++)
                for (int gy = cy - span; gy <= cy + span; gy++)
                {
                    if (!_cells.TryGetValue((gx, gy), out var l)) continue;
                    foreach (int i in l)
                    {
                        double dx = _pts[i].x - x, dy = _pts[i].y - y, d2 = dx * dx + dy * dy;
                        if (d2 <= r2) found.Add((i, Math.Sqrt(d2)));
                    }
                }
            found.Sort((a, b) => a.dist.CompareTo(b.dist));
            if (found.Count > k) found.RemoveRange(k, found.Count - k);
            return found;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  网面 XY→Z 采样器(格桶+重心插值)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>网面 XY→Z 采样器(格桶+重心插值; 参照 PointCloudLib/MeshZSampler 成熟样板)。供残差列/预览逐格取网面高程。</summary>
    public sealed class MeshSampler2D
    {
        private readonly double[] _v;
        private readonly int[] _t;
        private readonly Dictionary<(int, int), List<int>> _grid = new();
        private readonly double _cs, _x0, _y0;

        private MeshSampler2D(double[] v, int[] t, double cs, double x0, double y0)
        { _v = v; _t = t; _cs = cs; _x0 = x0; _y0 = y0; }

        public static MeshSampler2D? Build(double[] verts, int[] tris)
        {
            if (verts == null || tris == null || verts.Length < 9 || tris.Length < 3) return null;
            int nt = tris.Length / 3, nv = verts.Length / 3;
            double xmn = double.MaxValue, ymn = double.MaxValue, xmx = double.MinValue, ymx = double.MinValue;
            for (int i = 0; i < nv; i++)
            {
                double x = verts[3 * i], y = verts[3 * i + 1];
                if (x < xmn) xmn = x; if (x > xmx) xmx = x;
                if (y < ymn) ymn = y; if (y > ymx) ymx = y;
            }
            if (xmx <= xmn || ymx <= ymn) return null;

            // 格尺寸 ≈ 平均三角尺度(每三角注册到其包围盒覆盖的格 → 含点的三角必在查询格里)
            double cs = Math.Max(1.0, ((xmx - xmn) + (ymx - ymn)) * 0.5 / Math.Max(1.0, Math.Sqrt(nt)));
            var s = new MeshSampler2D(verts, tris, cs, xmn, ymn);
            for (int t = 0; t < nt; t++)
            {
                int i0 = tris[3 * t], i1 = tris[3 * t + 1], i2 = tris[3 * t + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= nv || i1 >= nv || i2 >= nv) continue;
                double ax = verts[3 * i0], ay = verts[3 * i0 + 1];
                double bx = verts[3 * i1], by = verts[3 * i1 + 1];
                double cx = verts[3 * i2], cy = verts[3 * i2 + 1];
                int gx0 = (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) - xmn) / cs);
                int gx1 = (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - xmn) / cs);
                int gy0 = (int)Math.Floor((Math.Min(ay, Math.Min(by, cy)) - ymn) / cs);
                int gy1 = (int)Math.Floor((Math.Max(ay, Math.Max(by, cy)) - ymn) / cs);
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        var k = (gx, gy);
                        if (!s._grid.TryGetValue(k, out var l)) { l = new List<int>(); s._grid[k] = l; }
                        l.Add(t);
                    }
            }
            return s;
        }

        /// <summary>从场景三角网建采样器。</summary>
        public static MeshSampler2D? Build(MeshEntity mesh)
        {
            var (v, t) = mesh.Flatten();
            return Build(v, t);
        }

        public bool TrySample(double px, double py, out double z)
        {
            z = 0;
            int gx = (int)Math.Floor((px - _x0) / _cs), gy = (int)Math.Floor((py - _y0) / _cs);
            if (!_grid.TryGetValue((gx, gy), out var l)) return false;
            foreach (int t in l)
            {
                int i0 = _t[3 * t], i1 = _t[3 * t + 1], i2 = _t[3 * t + 2];
                double ax = _v[3 * i0], ay = _v[3 * i0 + 1], az = _v[3 * i0 + 2];
                double bx = _v[3 * i1], by = _v[3 * i1 + 1], bz = _v[3 * i1 + 2];
                double cx = _v[3 * i2], cy = _v[3 * i2 + 1], cz = _v[3 * i2 + 2];
                double d00 = bx - ax, d01 = cx - ax, d10 = by - ay, d11 = cy - ay;
                double den = d00 * d11 - d01 * d10;
                if (Math.Abs(den) < 1e-12) continue;
                double rx = px - ax, ry = py - ay;
                double u = (d11 * rx - d01 * ry) / den;
                double w = (d00 * ry - d10 * rx) / den;
                if (u < -1e-6 || w < -1e-6 || u + w > 1 + 1e-6) continue;
                z = az + u * (bz - az) + w * (cz - az);
                return true;
            }
            return false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  影响预览: 分级(原三维分级填色带) → 场景着色格
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>一格着色: 平面矩形 [X0,X1]×[Y0,Y1], 网面高程 Z(格心), 分级 Band(±1..±4), 位移 Disp。</summary>
    public readonly record struct OverlayCell(double X0, double Y0, double X1, double Y1, double Z, int Band, double Disp);

    /// <summary>影响预览的构建结果(着色格 + 片区标注 + 图例/状态栏元信息)。</summary>
    public sealed class OverlayBuild
    {
        public List<OverlayCell> CellList = new();
        public List<(double x, double y, double z, string text)> Labels = new();
        public double Step;                              // 分级基数(m); 分级 = step/2step/4step/8step
        public double CellSize;                          // 采样格边长(m)
        public int Cells;                                // 实际着色格数
        public double MinDispVal, MaxDispVal;            // 预览范围内 最大下沉(-)/最大抬升(+)
        public int[] BandCounts = new int[9];            // 各级格数(索引 band+4), 供直方图
        public bool Any => Cells > 0;
        public string Message = "";
    }

    /// <summary>把量值取整到 1/2/5×10ⁿ(比例尺/分级基数用)。</summary>
    public static double Nice125(double v)
    {
        if (v <= 1e-6) return 0.05;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        double n = m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10;
        return n * p;
    }

    /// <summary>分级间隔: 让最大位移落在第 4 级附近, 并取 1/2/5×10ⁿ 的"整数"步长。</summary>
    public static double NiceStep(double maxAbs) => maxAbs <= 1e-6 ? 0.1 : Nice125(maxAbs / 4.0);

    /// <summary>分级下界(m): 倍增分级 0 / s / 2s / 4s / 8s —— 小改动看得见, 大改动不刷屏。</summary>
    public static double BandLow(int band, double step)
    {
        int b = Math.Abs(band);
        return b <= 0 ? 0 : step * (1 << (b - 1));
    }

    /// <summary>位移 → 分级(带正负号): 0=不显著, ±1..±4 = 越界越深。</summary>
    public static int BandOf(double d, double step)
    {
        double a = Math.Abs(d);
        int b = a < step ? 0 : a < 2 * step ? 1 : a < 4 * step ? 2 : a < 8 * step ? 3 : 4;
        return d >= 0 ? b : -b;
    }

    /// <summary>分级配色: band ∈ [-4,4], 负=下沉(蓝, 越深越沉) 正=抬升(红, 越深越抬) 0=变化不显著。</summary>
    public static (byte r, byte g, byte b) BandRgb(int band)
        => band switch
        {
            4 => (0xA5, 0x0F, 0x15),
            3 => (0xE8, 0x3A, 0x2E),
            2 => (0xFC, 0x92, 0x72),
            1 => (0xFD, 0xC4, 0xAC),
            -1 => (0xC6, 0xDB, 0xEF),
            -2 => (0x9E, 0xCA, 0xE1),
            -3 => (0x42, 0x8C, 0xCA),
            -4 => (0x08, 0x51, 0x9C),
            _ => (0xDD, 0xDD, 0xDD),
        };

    /// <summary>
    /// 影响预览: 在目标面范围上按采样格逐格算 位移 = 羽化权重 × (估值 − 网面Z)(与 Evaluate 同一套公式), 按级归桶。
    /// 采样格自己定, 所以着色精度与目标网的节点疏密无关; 只出"够一级"的格(变化不显著的留空, 露出原面)。
    /// maxCells 为格数封顶(原 6 万; 场景逐格画实体时窗口层可调小)。
    /// </summary>
    public static OverlayBuild BuildOverlay(MeshSampler2D sampler,
        IReadOnlyList<(double x, double y, double z)> obs, Options opt, Result res,
        IReadOnlyList<(double x, double y, string text)> labels, int maxCells = 60000)
    {
        var ob = new OverlayBuild();
        if (obs.Count == 0 || !res.Any) { ob.Message = "无受影响区域"; return ob; }

        // 取景 = 真正变了的顶点 AABB(退化到观测点 AABB), 外扩一点点免得贴边裁掉
        double x0 = res.HasAffBounds ? res.AffMinX : res.MinX, x1 = res.HasAffBounds ? res.AffMaxX : res.MaxX;
        double y0 = res.HasAffBounds ? res.AffMinY : res.MinY, y1 = res.HasAffBounds ? res.AffMaxY : res.MaxY;
        double w = x1 - x0, h = y1 - y0;
        if (w <= 1e-6 || h <= 1e-6) { ob.Message = "影响范围过小"; return ob; }
        double R = Math.Max(1e-3, opt.InfluenceRadius);

        // 采样格: 长边 ~150 格, 且不粗于 R/8(一个影响圈至少 8 格); 总格数封顶
        double cell = Math.Min(Math.Max(w, h) / 150.0, R / 8.0);
        int nx = Math.Max(1, (int)Math.Ceiling(w / cell)), ny = Math.Max(1, (int)Math.Ceiling(h / cell));
        if ((long)nx * ny > maxCells)
        {
            double k = Math.Sqrt((double)nx * ny / maxCells);
            cell *= k; nx = Math.Max(1, (int)Math.Ceiling(w / cell)); ny = Math.Max(1, (int)Math.Ceiling(h / cell));
        }
        x0 -= cell; y0 -= cell; nx += 2; ny += 2;                   // 四周各留一格
        ob.CellSize = cell;

        // 分级基数 = 观测点 |Δz| 的中位数(常规修正量级), 分级 s/2s/4s/8s 倍增
        var obsDz = new List<double>(obs.Count);
        foreach (var o in obs)
            if (sampler.TrySample(o.x, o.y, out double mz0)) obsDz.Add(Math.Abs(o.z - mz0));
        obsDz.Sort();
        double step = obsDz.Count > 0 ? Nice125(Math.Max(0.05, obsDz[obsDz.Count / 2])) : NiceStep(res.MaxAbsDisp);
        if (step > res.MaxAbsDisp && res.MaxAbsDisp > 1e-6) step = NiceStep(res.MaxAbsDisp);
        ob.Step = step;

        // ── 逐格点采样: 网面 Z + 位移(离网/半径外 = 无效) ──
        int gw = nx + 1, gh = ny + 1;
        var gz = new double[gw * gh];
        var gd = new double[gw * gh];
        var gok = new bool[gw * gh];
        var ctx = BuildCtx(obs, opt);
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, gh, po, j =>
        {
            double wy = y0 + j * cell;
            for (int i = 0; i < gw; i++)
            {
                double wx = x0 + i * cell;
                if (!sampler.TrySample(wx, wy, out double mz)) continue;      // 离网
                int k = j * gw + i;
                gz[k] = mz; gok[k] = true;
                var hits = ctx.Grid.FindNearest(wx, wy, R, opt.MaxSamples);
                if (hits.Count == 0) continue;
                double est = ComputeEstimate(ctx, hits, wx, wy);
                if (double.IsNaN(est) || est <= NoData + 1e-6) continue;
                double t = Math.Clamp(1.0 - hits[0].dist / R, 0, 1);
                double wt = t * t * (3 - 2 * t);                              // smoothstep(与 Evaluate 一致)
                if (wt > 1e-6) gd[k] = wt * (est - mz);
            }
        });

        double minV = 0, maxV = 0;
        for (int k = 0; k < gd.Length; k++)
            if (gok[k]) { if (gd[k] < minV) minV = gd[k]; if (gd[k] > maxV) maxV = gd[k]; }
        ob.MinDispVal = minV; ob.MaxDispVal = maxV;

        // ── 按级归桶: 四角都在网上才出格(贴合网面边界); 不显著 → 留空 ──
        int cells = 0;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * gw + i, b2 = a + 1, c = a + gw, d = c + 1;
                if (!gok[a] || !gok[b2] || !gok[c] || !gok[d]) continue;
                double disp = (gd[a] + gd[b2] + gd[c] + gd[d]) * 0.25;
                int band = BandOf(disp, step);
                if (band == 0) continue;
                double zc = (gz[a] + gz[b2] + gz[c] + gz[d]) * 0.25;
                ob.CellList.Add(new OverlayCell(x0 + i * cell, y0 + j * cell, x0 + (i + 1) * cell, y0 + (j + 1) * cell, zc, band, disp));
                ob.BandCounts[band + 4]++;
                cells++;
            }
        ob.Cells = cells;
        if (cells == 0)
        {
            ob.Message = $"变化均小于 1 级（{step:0.##}m），无需着色";
            return ob;
        }

        foreach (var (lx, ly, text) in labels)
        {
            if (!sampler.TrySample(lx, ly, out double lz)) continue;
            ob.Labels.Add((lx, ly, lz, text));
        }
        ob.Message = $"分级带 {cells} 格（格距 {cell:0.#}m）· 分级 {step:0.##}/{2 * step:0.##}/{4 * step:0.##}/{8 * step:0.##}m";
        return ob;
    }

    /// <summary>
    /// 预览着色格 → 场景实体(原 PMBI 每级一张网 → 每格一个按级着色的矩形, 标高=格心网面高程+lift; 片区标注文字)。
    /// 图层由调用方指定(原「更新影响预览」)。
    /// </summary>
    public static List<SceneEntity> OverlayEntities(OverlayBuild ob, string layer, double labelHeight)
    {
        var ents = new List<SceneEntity>(ob.CellList.Count + ob.Labels.Count);
        double lift = Math.Clamp(ob.CellSize * 0.03, 0.05, 0.30);
        foreach (var c in ob.CellList)
        {
            var (r, g, b) = BandRgb(c.Band);
            ents.Add(new RectEntity { X0 = c.X0, Y0 = c.Y0, X1 = c.X1, Y1 = c.Y1, Elevation = c.Z + lift, LayerName = layer, Cr = r / 255f, Cg = g / 255f, Cb = b / 255f });
        }
        foreach (var (x, y, z, text) in ob.Labels)
            ents.Add(new TextEntity { X = x, Y = y, Height = labelHeight, HAlign = 1, VAlign = 2, Text = text, Elevation = z + lift + labelHeight * 0.8, LayerName = layer, Cr = 1f, Cg = 0.63f, Cb = 0f });
        return ents;
    }

    /// <summary>用更新后的 Z 组装新三角网(建新面用; 原 BuildMeshPmbi)。</summary>
    public static MeshEntity BuildUpdatedMesh(double[] worldVerts, double[] newZ, int[] tris, string name)
    {
        int V = worldVerts.Length / 3;
        var verts = new List<(double x, double y, double z)>(V);
        for (int i = 0; i < V; i++) verts.Add((worldVerts[i * 3], worldVerts[i * 3 + 1], i < newZ.Length ? newZ[i] : worldVerts[i * 3 + 2]));
        var tl = new List<(int a, int b, int c)>(tris.Length / 3);
        for (int i = 0; i + 2 < tris.Length; i += 3) tl.Add((tris[i], tris[i + 1], tris[i + 2]));
        return new MeshEntity(name, verts, tl) { Cr = 0x8C / 255f, Cg = 0x9A / 255f, Cb = 0xA8 / 255f };   // 原色 #8C9AA8
    }

    private static double TriAreaXY(double[] v, int a, int b, int c)
    {
        double ax = v[a * 3], ay = v[a * 3 + 1], bx = v[b * 3], by = v[b * 3 + 1], cx = v[c * 3], cy = v[c * 3 + 1];
        return Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) * 0.5;
    }
}
