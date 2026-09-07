using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 快速建模 / 地质体建模核（忠实移植原 <c>MeshEditLib.Tools.QuickModelBuilder</c>，去掉 PMBI 打包 → 直接出 (verts, tris)）：
///   · <see cref="Build"/>（采样路径）：顶板等高线 + 底板等高线 + 一条闭合边界 → 规则网格上插顶/底 Z(IDW / 普通克里金)
///     → 网格三角化并裁剪到闭合边界 → 顶面 + 底面(反向) + 沿开放边拼侧墙 → 天然水密闭合体；
///   · <see cref="BuildFromMeshesDirect"/>（直接组装）：顶/底板三角网原三角满精度 → 提取真实边界环并按绕向分外轮廓/内洞、
///     按 PCA 细长比 + 横跨落差识别断层缝 → 外轮廓↔外轮廓、断层缝↔断层缝就近配对放样侧壁(<see cref="SideSurface.Loft"/>)
///     → 未配对环耳切 + 约束 Delaunay 翻转封盖 → 焊接成一个实体并做水密自检。
/// 健壮性：任何退化只填 <see cref="BuildResult.Error"/> 返回，绝不抛。纯几何、可单测。
/// </summary>
public static class QuickModelSampler
{
    public const string LayerName = "快速建模体";

    public enum Interp { Idw, Kriging }

    public sealed class BuildResult
    {
        public List<(double x, double y, double z)> Verts = new();
        public List<(int a, int b, int c)> Tris = new();
        public int TopContours, BotContours;
        public int TopSamples, BotSamples;
        public double TopMeanZ, BotMeanZ;
        public int SurfVerts;      // 单面(裁剪后)顶点数
        public int SurfTris;       // 单面三角数
        public int WallTris;       // 侧墙三角数
        public int TotalTris;      // 体总三角数
        public int OpenEdges;      // 最终体的开放边(应为 0 = 水密)
        public int NonManifoldEdges; // 最终体的非流形边(被 >2 三角共用,应为 0)
        public int TopLoops, BotLoops; // 直接组装:顶/底板面提取到的边界环数
        public int WallLoops;      // 直接组装:匹配成对、放样出侧壁的环数
        public int CappedLoops;    // 直接组装:未配对但已三角化封盖的环数
        public int FaultLoops;     // 直接组装:识别为断层缝、放样成开口断层沟的环数
        public int OpenLoops;      // 直接组装:连封盖也退化失败的环数(应为 0)
        public string LayerName = string.Empty;
        public bool Watertight => OpenEdges == 0 && NonManifoldEdges == 0 && TotalTris > 0;
        public string Error = string.Empty;
        public bool Ok => Tris.Count > 0 && string.IsNullOrEmpty(Error);
    }

    private readonly record struct P2(double X, double Y);
    private readonly record struct S3(double X, double Y, double Z);

    /// <param name="topContours">顶板等高线:每条 = 一根多段线的扁平 [x,y,z,...]</param>
    /// <param name="botContours">底板等高线</param>
    /// <param name="boundaryXyz">闭合边界多段线扁平 [x,y,z,...]</param>
    /// <param name="method">插值方法</param>
    /// <param name="gridStep">网格步长(m);≤0 自动按边界范围取</param>
    /// <param name="flipNormals">翻转所有法线朝向</param>
    public static BuildResult Build(
        IReadOnlyList<double[]> topContours, IReadOnlyList<double[]> botContours,
        double[] boundaryXyz, Interp method, double gridStep, bool flipNormals)
    {
        var result = new BuildResult { LayerName = LayerName };
        try
        {
            // 1. 散点
            var topS = GatherSamples(topContours);
            var botS = GatherSamples(botContours);
            result.TopContours = topContours?.Count ?? 0;
            result.BotContours = botContours?.Count ?? 0;
            result.TopSamples = topS.Count;
            result.BotSamples = botS.Count;
            if (topS.Count < 3 || botS.Count < 3)
            {
                result.Error = $"顶/底板等高线顶点不足(顶 {topS.Count}、底 {botS.Count},各需 ≥ 3)。";
                return result;
            }
            result.TopMeanZ = topS.Average(s => s.Z);
            result.BotMeanZ = botS.Average(s => s.Z);

            // 2. 边界环(XY),保证 CCW(内部在每条边左侧),算 bbox
            var bnd = GatherLoop(boundaryXyz);
            if (bnd.Count < 3) { result.Error = "闭合边界顶点不足(需 ≥ 3)。"; return result; }
            if (SignedArea(bnd) < 0) bnd.Reverse();
            double minX = bnd.Min(p => p.X), maxX = bnd.Max(p => p.X);
            double minY = bnd.Min(p => p.Y), maxY = bnd.Max(p => p.Y);
            double spanX = maxX - minX, spanY = maxY - minY;
            if (spanX < 1e-6 || spanY < 1e-6) { result.Error = "闭合边界范围退化(近似一条线)。"; return result; }

            // 3. 步长(安全上限,避免网格爆炸)
            double step = gridStep;
            if (step <= 0) step = Math.Max(spanX, spanY) / 100.0;
            while ((spanX / step + 2) * (spanY / step + 2) > 4_000_000) step *= 1.5;

            // 4. 插值闭包(顶/底各一)
            Func<double, double, double> topZ = MakeInterp(method, topS, spanX, spanY);
            Func<double, double, double> botZ = MakeInterp(method, botS, spanX, spanY);

            // 5. 网格节点 + 角点 PIP 缓存
            int nx = (int)Math.Ceiling(spanX / step);
            int ny = (int)Math.Ceiling(spanY / step);
            P2 Node(int i, int j) => new P2(minX + i * step, minY + j * step);
            var inside = new bool[nx + 1, ny + 1];
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= ny; j++)
                {
                    var p = Node(i, j);
                    inside[i, j] = PointInPolygon(bnd, p.X, p.Y);
                }

            // 6. 逐格 2 三角,裁剪到边界 → XY 三角汤
            var soup = new List<P2>();
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    P2 a = Node(i, j), b = Node(i + 1, j), c = Node(i + 1, j + 1), d = Node(i, j + 1);
                    bool ia = inside[i, j], ib = inside[i + 1, j], ic = inside[i + 1, j + 1], id = inside[i, j + 1];
                    ClipTri(a, b, c, ia, ib, ic, bnd, soup);
                    ClipTri(a, c, d, ia, ic, id, bnd, soup);
                }
            if (soup.Count < 3) { result.Error = "边界范围内没有可建面的网格(边界过小或步长过大?)。"; return result; }

            // 7. 焊接 XY 三角汤 → 索引化(verts/tris)
            WeldXY(soup, step * 1e-3, out var verts, out var tris);
            result.SurfVerts = verts.Count;
            result.SurfTris = tris.Count / 3;

            // 8. 单面开放边(= 裁剪后的轮廓环,用来拼侧墙)
            var openEdges = CollectOpenEdges(tris);

            // 9. 双 Z 升维 + 组装闭合体
            int N = verts.Count;
            var V = new List<(double x, double y, double z)>(2 * N);
            foreach (var p in verts) V.Add((p.X, p.Y, topZ(p.X, p.Y)));   // [0,N) 顶面
            foreach (var p in verts) V.Add((p.X, p.Y, botZ(p.X, p.Y)));   // [N,2N) 底面

            var idx = new List<int>(tris.Count * 2 + openEdges.Count * 6);
            for (int t = 0; t + 2 < tris.Count; t += 3) AddTri(idx, tris[t], tris[t + 1], tris[t + 2], flipNormals);                 // 顶面(朝上)
            for (int t = 0; t + 2 < tris.Count; t += 3) AddTri(idx, N + tris[t], N + tris[t + 2], N + tris[t + 1], flipNormals);     // 底面(反向)
            foreach (var (i, j) in openEdges)   // 侧墙:每条开放边 (i,j) → 四边形 (top_i,top_j,bot_j,bot_i) 两三角
            {
                AddTri(idx, i, j, N + j, flipNormals);
                AddTri(idx, i, N + j, N + i, flipNormals);
            }
            result.WallTris = openEdges.Count * 2;
            result.TotalTris = idx.Count / 3;

            // 10. 水密自检(最终闭合体的开放边应为 0)
            result.OpenEdges = CountOpenEdges(idx);
            result.Verts = V;
            for (int t = 0; t + 2 < idx.Count; t += 3) result.Tris.Add((idx[t], idx[t + 1], idx[t + 2]));
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"快速建模异常:{ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// 地质体建模·直接组装(不采样):顶板面 + 底板面 → 封闭地质体。
    ///   1. 顶面 = 顶板原三角(原样,满精度)、底面 = 底板原三角(反向法线);
    ///   2. 提取两面各自的开放边界环(外轮廓 + 内部 nodata 空洞边缘),并按绕向分外轮廓/内洞、按细长比+横跨落差分断层缝;
    ///   3. 外轮廓↔外轮廓、断层缝↔断层缝 顶↔底按质心就近 + 面积相近配对,逐对放样(外墙 / 开口断层沟);
    ///   4. nodata 空洞 + 未配对环一律用环自身顶点三角化封盖(<see cref="TriangulateCap"/>)→ 与源网边界焊得上、水密由构造保证;
    ///   5. 顶面 + 底面 + 各侧壁 + 各封盖焊接成一个实体(容差 1e-3), 附水密自检。
    /// 前提:源网绕向一致(TIN 天然满足)。表面精度 = 源网精度,零重采样。
    /// </summary>
    public static BuildResult BuildFromMeshesDirect(
        double[] topVerts, int[] topTris, double[] botVerts, int[] botTris,
        bool flipNormals, string? layerName = null)
    {
        var result = new BuildResult();
        try
        {
            if (topVerts == null || topTris == null || topVerts.Length < 9 || topTris.Length < 3 ||
                botVerts == null || botTris == null || botVerts.Length < 9 || botTris.Length < 3)
            { result.Error = "顶/底板三角网为空或退化。"; return result; }

            result.TopSamples = topVerts.Length / 3;
            result.BotSamples = botVerts.Length / 3;
            result.TopMeanZ = MeshMeanZ(topVerts);
            result.BotMeanZ = MeshMeanZ(botVerts);

            var topLoops = ExtractLoops(topVerts, topTris);
            var botLoops = ExtractLoops(botVerts, botTris);
            result.TopLoops = topLoops.Count;
            result.BotLoops = botLoops.Count;
            if (topLoops.Count == 0 || botLoops.Count == 0)
            { result.Error = "顶/底板面无开放边界环(网格已封闭或退化),无法放样侧壁。"; return result; }

            // 侧壁配对阈值:质心距 < 0.30×全局对角线才算同一特征(避免误配)
            double gdiag = MeshDiagXY(topVerts);
            double matchTol = 0.30 * (gdiag > 1e-6 ? gdiag : 1.0);
            double matchTol2 = matchTol * matchTol;

            var tMeta = new (double cx, double cy, double area)[topLoops.Count];
            var bMeta = new (double cx, double cy, double area)[botLoops.Count];
            for (int k = 0; k < topLoops.Count; k++) tMeta[k] = LoopMetaXY(topLoops[k]);
            for (int k = 0; k < botLoops.Count; k++) bMeta[k] = LoopMetaXY(botLoops[k]);

            var topOuter = ClassifyOuterLoops(topLoops);
            var botOuter = ClassifyOuterLoops(botLoops);
            var topFault = new bool[topLoops.Count];
            var botFault = new bool[botLoops.Count];
            for (int k = 0; k < topLoops.Count; k++) topFault[k] = !topOuter[k] && IsFaultSlit(topLoops[k]);
            for (int k = 0; k < botLoops.Count; k++) botFault[k] = !botOuter[k] && IsFaultSlit(botLoops[k]);

            var meshes = new List<(double[] verts, int[] indices)>
            {
                (topVerts, topTris),                        // 顶面原样
                (botVerts, ReverseWinding(botTris)),        // 底面反向(法线朝下)
            };

            // 顶面外轮廓 + 断层缝按面积从大到小,贪心配最近底面同类环,逐对放样
            var usedBot = new bool[botLoops.Count];
            var topPaired = new bool[topLoops.Count];
            var order = new List<int>(topLoops.Count);
            for (int k = 0; k < topLoops.Count; k++) if (topOuter[k] || topFault[k]) order.Add(k);
            order.Sort((a, b) => tMeta[b].area.CompareTo(tMeta[a].area));

            int wallLoops = 0, wallTris = 0, faultLoops = 0;
            foreach (int ti in order)
            {
                bool tiFault = topFault[ti];
                int best = -1; double bestScore = double.MaxValue;
                for (int bi = 0; bi < botLoops.Count; bi++)
                {
                    if (usedBot[bi]) continue;
                    if (tiFault ? !botFault[bi] : !botOuter[bi]) continue;   // 同类配对
                    double dx = tMeta[ti].cx - bMeta[bi].cx, dy = tMeta[ti].cy - bMeta[bi].cy;
                    double d2 = dx * dx + dy * dy;
                    if (d2 > matchTol2) continue;
                    double amax = Math.Max(tMeta[ti].area, Math.Max(bMeta[bi].area, 1.0));
                    double areaPen = Math.Abs(tMeta[ti].area - bMeta[bi].area) / amax;
                    double score = Math.Sqrt(d2) + areaPen * matchTol;
                    if (score < bestScore) { bestScore = score; best = bi; }
                }
                if (best < 0) continue;   // 无可配底环 → 留待封盖
                var (wv, wt) = SideSurface.Loft(ToTuples(topLoops[ti]), ToTuples(botLoops[best]), closed: true, flip: flipNormals);
                if (wt.Count >= 1)
                {
                    usedBot[best] = true;
                    topPaired[ti] = true;
                    meshes.Add((Flat(wv), Flat(wt)));
                    if (tiFault) faultLoops++; else wallLoops++;
                    wallTris += wt.Count;
                }
            }

            // 收口:未配对的边界环一律贴地形封盖
            int cappedLoops = 0, capTris = 0, openLoops = 0;
            for (int ti = 0; ti < topLoops.Count; ti++)
            {
                if (topPaired[ti]) continue;
                if (TriangulateCap(topLoops[ti], normalUp: !flipNormals, out var cv, out var ct))
                { meshes.Add((cv, ct)); cappedLoops++; capTris += ct.Length / 3; }
                else openLoops++;
            }
            for (int bi = 0; bi < botLoops.Count; bi++)
            {
                if (usedBot[bi]) continue;
                if (TriangulateCap(botLoops[bi], normalUp: flipNormals, out var cv, out var ct))
                { meshes.Add((cv, ct)); cappedLoops++; capTris += ct.Length / 3; }
                else openLoops++;
            }

            result.WallLoops = wallLoops;
            result.WallTris = wallTris + capTris;
            result.CappedLoops = cappedLoops;
            result.FaultLoops = faultLoops;
            result.OpenLoops = openLoops;

            // 焊接成体(原 SolidifyBuilder: 容差 1e-3 焊接 + 边关联数诊断)
            var parts = new List<(IReadOnlyList<(double x, double y, double z)>, IReadOnlyList<(int a, int b, int c)>)>();
            foreach (var (v, t) in meshes) parts.Add((ToTuples(v), ToTriTuples(t)));
            var (cv2, ct2) = MeshWeld.Concat(parts);
            var w = MeshWeld.Weld(cv2, ct2, 1e-3, dropDuplicateTris: false);
            if (w.OutputTris == 0) { result.Error = "焊接后无有效三角形(网格退化或全部重合)。"; return result; }
            var d = MeshDiagnose.Analyze(w.Verts, w.Tris);

            result.LayerName = string.IsNullOrWhiteSpace(layerName) ? LayerName : layerName!;
            result.Verts = w.Verts;
            result.Tris = w.Tris;
            result.SurfTris = (topTris.Length + botTris.Length) / 3;
            result.TotalTris = w.OutputTris;
            result.OpenEdges = d.BoundaryEdges;
            result.NonManifoldEdges = d.NonManifoldEdges;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"地质体直接组装异常:{ex.Message}";
            return result;
        }
    }

    // ── 直接组装辅助 ──────────────────────────────────────────────

    private static List<double[]> ExtractLoops(double[] v, int[] t)
    {
        var loops = MeshBoundaryLoops.Extract(ToTuples(v), ToTriTuples(t));
        var res = new List<double[]>(loops.Count);
        foreach (var l in loops) res.Add(Flat(l));
        return res;
    }

    private static List<(double x, double y, double z)> ToTuples(double[] v)
    {
        var list = new List<(double x, double y, double z)>(v.Length / 3);
        for (int i = 0; i + 2 < v.Length; i += 3) list.Add((v[i], v[i + 1], v[i + 2]));
        return list;
    }

    private static List<(int a, int b, int c)> ToTriTuples(int[] t)
    {
        var list = new List<(int a, int b, int c)>(t.Length / 3);
        for (int i = 0; i + 2 < t.Length; i += 3) list.Add((t[i], t[i + 1], t[i + 2]));
        return list;
    }

    private static double[] Flat(IReadOnlyList<(double x, double y, double z)> v)
    {
        var a = new double[v.Count * 3];
        for (int i = 0; i < v.Count; i++) { a[i * 3] = v[i].x; a[i * 3 + 1] = v[i].y; a[i * 3 + 2] = v[i].z; }
        return a;
    }

    private static int[] Flat(IReadOnlyList<(int a, int b, int c)> t)
    {
        var a = new int[t.Count * 3];
        for (int i = 0; i < t.Count; i++) { a[i * 3] = t[i].a; a[i * 3 + 1] = t[i].b; a[i * 3 + 2] = t[i].c; }
        return a;
    }

    private static double MeshMeanZ(double[] v)
    {
        double s = 0; int n = v.Length / 3;
        for (int i = 0; i < n; i++) s += v[i * 3 + 2];
        return n > 0 ? s / n : 0;
    }

    // XY 包围盒对角线长
    private static double MeshDiagXY(double[] v)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 2 < v.Length; i += 3)
        {
            double x = v[i], y = v[i + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        double dx = maxX - minX, dy = maxY - minY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // 边界环(flat [x,y,z,...])的 XY 质心 + |带符号面积|
    private static (double cx, double cy, double area) LoopMetaXY(double[] loop)
    {
        int n = loop.Length / 3;
        if (n == 0) return (0, 0, 0);
        double sx = 0, sy = 0, a2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = loop[i * 3], y = loop[i * 3 + 1];
            int j = (i + 1) % n;
            double xn = loop[j * 3], yn = loop[j * 3 + 1];
            sx += x; sy += y;
            a2 += x * yn - xn * y;
        }
        return (sx / n, sy / n, Math.Abs(0.5 * a2));
    }

    private static double SignedLoopAreaXY(double[] loop)
    {
        int n = loop.Length / 3;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            s += loop[i * 3] * loop[j * 3 + 1] - loop[j * 3] * loop[i * 3 + 1];
        }
        return 0.5 * s;
    }

    /// <summary>外轮廓/内洞分类(按有向环绕向):取 |带符号面积| 最大的环定"外轮廓符号"→ 同号即外轮廓、异号即内洞。</summary>
    internal static bool[] ClassifyOuterLoops(List<double[]> loops)
    {
        int L = loops.Count;
        var outer = new bool[L];
        if (L == 0) return outer;
        var sa = new double[L];
        for (int k = 0; k < L; k++) sa[k] = SignedLoopAreaXY(loops[k]);
        int big = 0;
        for (int k = 1; k < L; k++) if (Math.Abs(sa[k]) > Math.Abs(sa[big])) big = k;
        int outerSign = Math.Sign(sa[big]); if (outerSign == 0) outerSign = 1;
        for (int k = 0; k < L; k++) outer[k] = Math.Sign(sa[k]) == outerSign;
        return outer;
    }

    private static int[] ReverseWinding(int[] t)
    {
        var r = new int[t.Length];
        for (int f = 0; f + 2 < t.Length; f += 3) { r[f] = t[f]; r[f + 1] = t[f + 2]; r[f + 2] = t[f + 1]; }
        return r;
    }

    /// <summary>
    /// 把一条简单边界环(flat [x,y,z,...])三角化成一张封盖:仅用环自身顶点(不引入新点)。小环走耳切,
    /// 超大环或耳切卡壳退回扇形;再做约束 Delaunay 边翻转提三角形质量。normalUp 控制封盖法线朝向。
    /// </summary>
    internal static bool TriangulateCap(double[] loop, bool normalUp, out double[] verts, out int[] indices)
    {
        verts = loop; indices = Array.Empty<int>();
        int n = loop.Length / 3;
        if (n < 3) return false;

        double area2 = 0;
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; area2 += loop[i * 3] * loop[j * 3 + 1] - loop[j * 3] * loop[i * 3 + 1]; }
        bool loopCcw = area2 > 0;

        var idx = new List<int>((n - 2) * 3);
        bool eared = false;
        if (n <= 2000) eared = EarClip(loop, n, loopCcw, idx);
        if (!eared)
        {
            idx.Clear();
            for (int k = 1; k + 1 < n; k++) { idx.Add(0); idx.Add(k); idx.Add(k + 1); }
        }
        if (idx.Count < 3) return false;

        NormalizeCcw(loop, idx);
        DelaunayFlip(loop, idx);

        if (!normalUp)
            for (int f = 0; f + 2 < idx.Count; f += 3) { (idx[f + 1], idx[f + 2]) = (idx[f + 2], idx[f + 1]); }

        indices = idx.ToArray();
        return true;
    }

    private static void NormalizeCcw(double[] loop, List<int> idx)
    {
        for (int f = 0; f + 2 < idx.Count; f += 3)
        {
            int a = idx[f], b = idx[f + 1], c = idx[f + 2];
            double o = (loop[b * 3] - loop[a * 3]) * (loop[c * 3 + 1] - loop[a * 3 + 1])
                     - (loop[b * 3 + 1] - loop[a * 3 + 1]) * (loop[c * 3] - loop[a * 3]);
            if (o < 0) { idx[f + 1] = c; idx[f + 2] = b; }
        }
    }

    /// <summary>约束 Delaunay 边翻转(Lawson):仅翻违反空圆性且四边形凸的内部边;边界边永不翻。</summary>
    private static void DelaunayFlip(double[] loop, List<int> idx)
    {
        int nTri = idx.Count / 3;
        if (nTri < 2) return;
        double X(int i) => loop[i * 3];
        double Y(int i) => loop[i * 3 + 1];
        long EKey(int a, int b) { int lo = Math.Min(a, b), hi = Math.Max(a, b); return ((long)lo << 32) | (uint)hi; }

        int maxPass = Math.Min(64, nTri * 2 + 8);
        for (int pass = 0; pass < maxPass; pass++)
        {
            var edge = new Dictionary<long, (int t0, int t1)>(idx.Count);
            void AddEdge(int a, int b, int t)
            {
                long k = EKey(a, b);
                edge[k] = edge.TryGetValue(k, out var e) ? (e.t0, t) : (t, -1);
            }
            for (int t = 0; t < nTri; t++)
            {
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                AddEdge(a, b, t); AddEdge(b, c, t); AddEdge(c, a, t);
            }

            bool any = false;
            var touched = new HashSet<int>();
            foreach (var kv in edge)
            {
                var (t0, t1) = kv.Value;
                if (t1 < 0) continue;
                if (touched.Contains(t0) || touched.Contains(t1)) continue;
                int u = (int)(kv.Key >> 32), v = (int)(kv.Key & 0xffffffff);
                int p = ThirdVertex(idx, t0, u, v), q = ThirdVertex(idx, t1, u, v);
                if (p < 0 || q < 0 || p == q) continue;

                int a = u, b = v;
                if (Orient2(X(a), Y(a), X(b), Y(b), X(p), Y(p)) < 0) (a, b) = (b, a);
                double oa = Orient2(X(p), Y(p), X(q), Y(q), X(a), Y(a));
                double ob = Orient2(X(p), Y(p), X(q), Y(q), X(b), Y(b));
                if (oa == 0 || ob == 0 || (oa > 0) == (ob > 0)) continue;
                if (InCircle(X(a), Y(a), X(b), Y(b), X(p), Y(p), X(q), Y(q)) <= 0) continue;

                idx[t0 * 3] = a; idx[t0 * 3 + 1] = p; idx[t0 * 3 + 2] = q;
                idx[t1 * 3] = b; idx[t1 * 3 + 1] = q; idx[t1 * 3 + 2] = p;
                touched.Add(t0); touched.Add(t1);
                any = true;
            }
            if (!any) break;
        }
    }

    private static int ThirdVertex(List<int> idx, int t, int u, int v)
    {
        for (int k = 0; k < 3; k++) { int w = idx[t * 3 + k]; if (w != u && w != v) return w; }
        return -1;
    }
    private static double Orient2(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
    private static double InCircle(double ax, double ay, double bx, double by,
                                   double cx, double cy, double dx, double dy)
    {
        double A = ax - dx, B = ay - dy, C = A * A + B * B;
        double D = bx - dx, E = by - dy, F = D * D + E * E;
        double G = cx - dx, H = cy - dy, I = G * G + H * H;
        return A * (E * I - F * H) - B * (D * I - F * G) + C * (D * H - E * G);
    }

    // ── 断层缝识别 ─────────────────────────────────────────────────
    private const double FaultThrowMin = 3.0;    // 横跨缝最小落差(m)
    private const double FaultAspectMin = 6.0;   // 细长比(长/宽)阈值:断层缝 vs 团块空洞

    // PCA 主轴把环拆成两帮(tip1..tip2 两条弧),返回沿轴参数 t(按顶点序号)。
    private static bool TipSplit(double[] loop, out int[] arcA, out int[] arcB, out double[] tPar)
    {
        arcA = Array.Empty<int>(); arcB = Array.Empty<int>(); tPar = Array.Empty<double>();
        int n = loop.Length / 3;
        if (n < 6) return false;
        double cx = 0, cy = 0;
        for (int i = 0; i < n; i++) { cx += loop[i * 3]; cy += loop[i * 3 + 1]; }
        cx /= n; cy /= n;
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++) { double dx = loop[i * 3] - cx, dy = loop[i * 3 + 1] - cy; sxx += dx * dx; sxy += dx * dy; syy += dy * dy; }
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double ax = Math.Cos(theta), ay = Math.Sin(theta);
        var t = new double[n];
        for (int i = 0; i < n; i++) t[i] = (loop[i * 3] - cx) * ax + (loop[i * 3 + 1] - cy) * ay;
        int tip1 = 0, tip2 = 0;
        for (int i = 1; i < n; i++) { if (t[i] < t[tip1]) tip1 = i; if (t[i] > t[tip2]) tip2 = i; }
        if (tip1 == tip2) return false;
        var a = new List<int>(); int k = tip1; while (true) { a.Add(k); if (k == tip2) break; k = (k + 1) % n; }
        var b = new List<int>(); k = tip2; while (true) { b.Add(k); if (k == tip1) break; k = (k + 1) % n; }
        b.Reverse();
        if (a.Count < 2 || b.Count < 2) return false;
        arcA = a.ToArray(); arcB = b.ToArray(); tPar = t;
        return true;
    }

    /// <summary>断层缝判据:细长(PCA 拆两帮)+ 横跨缝落差够大。</summary>
    internal static bool IsFaultSlit(double[] loop)
    {
        if (!TipSplit(loop, out var A, out var B, out var t)) return false;
        double tmin = t[0], tmax = t[0];
        for (int i = 1; i < t.Length; i++) { if (t[i] < tmin) tmin = t[i]; if (t[i] > tmax) tmax = t[i]; }
        double length = tmax - tmin;
        var (throwMed, widthMed) = FaultCrossThrow(loop, A, B, t);
        if (widthMed < 1e-6) return false;
        return (length / widthMed) >= FaultAspectMin && throwMed >= FaultThrowMin;
    }

    private static (double throwMed, double widthMed) FaultCrossThrow(double[] loop, int[] A, int[] B, double[] t)
    {
        double lo = Math.Max(Math.Min(t[A[0]], t[A[^1]]), Math.Min(t[B[0]], t[B[^1]]));
        double hi = Math.Min(Math.Max(t[A[0]], t[A[^1]]), Math.Max(t[B[0]], t[B[^1]]));
        if (hi <= lo) return (0, 0);
        var throws = new List<double>(); var widths = new List<double>();
        for (int s = 1; s <= 25; s++)
        {
            double tt = lo + (hi - lo) * s / 26.0;
            int ja = FaultNearestByT(A, t, tt), jb = FaultNearestByT(B, t, tt);
            throws.Add(Math.Abs(loop[ja * 3 + 2] - loop[jb * 3 + 2]));
            double dx = loop[ja * 3] - loop[jb * 3], dy = loop[ja * 3 + 1] - loop[jb * 3 + 1];
            widths.Add(Math.Sqrt(dx * dx + dy * dy));
        }
        return (FaultMedian(throws), FaultMedian(widths));
    }
    private static int FaultNearestByT(int[] arc, double[] t, double tt) { int best = arc[0]; double bd = Math.Abs(t[arc[0]] - tt); foreach (int i in arc) { double d = Math.Abs(t[i] - tt); if (d < bd) { bd = d; best = i; } } return best; }
    private static double FaultMedian(List<double> v) { if (v.Count == 0) return 0; v.Sort(); return v[v.Count / 2]; }

    // 简单多边形耳切(XY 投影);卡壳返回 false 交扇形兜底。
    private static bool EarClip(double[] loop, int n, bool ccw, List<int> outIdx)
    {
        var V = new List<int>(n);
        for (int i = 0; i < n; i++) V.Add(i);
        int guard = 0, maxGuard = n * n + 16;
        while (V.Count > 3 && guard++ < maxGuard)
        {
            bool clipped = false;
            int m = V.Count;
            for (int k = 0; k < m; k++)
            {
                int i0 = V[(k + m - 1) % m], i1 = V[k], i2 = V[(k + 1) % m];
                if (IsEar(loop, V, i0, i1, i2, ccw))
                {
                    outIdx.Add(i0); outIdx.Add(i1); outIdx.Add(i2);
                    V.RemoveAt(k); clipped = true; break;
                }
            }
            if (!clipped) return false;
        }
        if (V.Count == 3) { outIdx.Add(V[0]); outIdx.Add(V[1]); outIdx.Add(V[2]); }
        return V.Count == 3;
    }

    private static bool IsEar(double[] loop, List<int> V, int i0, int i1, int i2, bool ccw)
    {
        double ax = loop[i0 * 3], ay = loop[i0 * 3 + 1];
        double bx = loop[i1 * 3], by = loop[i1 * 3 + 1];
        double cx = loop[i2 * 3], cy = loop[i2 * 3 + 1];
        double cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (ccw ? cross <= 0 : cross >= 0) return false;
        foreach (int vi in V)
        {
            if (vi == i0 || vi == i1 || vi == i2) continue;
            if (PointInTriXY(loop[vi * 3], loop[vi * 3 + 1], ax, ay, bx, by, cx, cy)) return false;
        }
        return true;
    }

    private static bool PointInTriXY(double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    // ── 散点 / 边界 ──────────────────────────────────────────────

    private static List<S3> GatherSamples(IReadOnlyList<double[]> contours)
    {
        var list = new List<S3>();
        if (contours == null) return list;
        foreach (var c in contours)
        {
            if (c == null) continue;
            for (int k = 0; k + 2 < c.Length; k += 3) list.Add(new S3(c[k], c[k + 1], c[k + 2]));
        }
        return list;
    }

    private static List<P2> GatherLoop(double[] xyz)
    {
        var list = new List<P2>();
        if (xyz == null) return list;
        for (int k = 0; k + 2 < xyz.Length; k += 3) list.Add(new P2(xyz[k], xyz[k + 1]));
        if (list.Count > 1 && Math.Abs(list[0].X - list[^1].X) < 1e-9 && Math.Abs(list[0].Y - list[^1].Y) < 1e-9)
            list.RemoveAt(list.Count - 1);
        return list;
    }

    // ── 插值 ────────────────────────────────────────────────────

    private static Func<double, double, double> MakeInterp(Interp method, List<S3> samples, double spanX, double spanY)
    {
        if (method == Interp.Kriging)
        {
            // 普通克里金:样本 Z=0 使距离退化为 2D, 高程作估值变量
            var pts = samples.Select(s => new OrdinaryKriging.ControlPoint(s.X, s.Y, 0, s.Z)).ToList();
            double range = Math.Max(spanX, spanY) / 3.0;
            var vg = new OrdinaryKriging.Variogram(0.0, 1.0, range <= 0 ? 1.0 : range);
            double radius = Math.Max(spanX, spanY);
            double fallback = samples.Average(s => s.Z);
            return (x, y) =>
            {
                var r = OrdinaryKriging.EstimateAt(pts, x, y, 0, 16, radius, vg);
                return r.HasValue && !double.IsNaN(r.Value.est) ? r.Value.est : fallback;
            };
        }
        return (x, y) => Idw2D(samples, x, y, power: 2.0, maxK: 12);
    }

    // 2D 反距离权重:取最近 maxK 个样本
    private static double Idw2D(List<S3> samples, double qx, double qy, double power, int maxK)
    {
        Span<double> bestD = stackalloc double[16];
        Span<double> bestZ = stackalloc double[16];
        int cnt = 0;
        int K = Math.Min(maxK, 16);
        for (int i = 0; i < samples.Count; i++)
        {
            double dx = samples[i].X - qx, dy = samples[i].Y - qy;
            double d2 = dx * dx + dy * dy;
            if (d2 < 1e-18) return samples[i].Z;
            if (cnt < K)
            {
                bestD[cnt] = d2; bestZ[cnt] = samples[i].Z; cnt++;
                if (cnt == K) SortByKey(bestD, bestZ, cnt);
            }
            else if (d2 < bestD[K - 1])
            {
                bestD[K - 1] = d2; bestZ[K - 1] = samples[i].Z;
                SortByKey(bestD, bestZ, K);
            }
        }
        if (cnt == 0) return 0;
        double wSum = 0, vSum = 0;
        for (int i = 0; i < cnt; i++)
        {
            double w = 1.0 / Math.Pow(Math.Sqrt(bestD[i]), power);
            wSum += w; vSum += w * bestZ[i];
        }
        return wSum > 0 ? vSum / wSum : bestZ[0];
    }

    private static void SortByKey(Span<double> key, Span<double> val, int n)
    {
        for (int i = 1; i < n; i++)
        {
            double k = key[i], v = val[i];
            int j = i - 1;
            while (j >= 0 && key[j] > k) { key[j + 1] = key[j]; val[j + 1] = val[j]; j--; }
            key[j + 1] = k; val[j + 1] = v;
        }
    }

    // ── 裁剪:逐三角到边界 ───────────────────────────────────────

    private static void ClipTri(P2 a, P2 b, P2 c, bool ia, bool ib, bool ic, List<P2> bnd, List<P2> soup)
    {
        int inCnt = (ia ? 1 : 0) + (ib ? 1 : 0) + (ic ? 1 : 0);
        if (inCnt == 3) { soup.Add(a); soup.Add(b); soup.Add(c); return; }
        if (inCnt == 0)
        {
            double gx = (a.X + b.X + c.X) / 3.0, gy = (a.Y + b.Y + c.Y) / 3.0;
            if (!PointInPolygon(bnd, gx, gy)) return;
        }
        var poly = new List<P2> { a, b, c };
        foreach (var (e0, e1) in CrossingEdges(bnd, a, b, c))
        {
            poly = ClipByHalfPlane(poly, e0, e1);
            if (poly.Count < 3) break;
        }
        if (poly.Count < 3)
        {
            double gx = (a.X + b.X + c.X) / 3.0, gy = (a.Y + b.Y + c.Y) / 3.0;
            if (PointInPolygon(bnd, gx, gy)) { soup.Add(a); soup.Add(b); soup.Add(c); }
            return;
        }
        for (int k = 1; k + 1 < poly.Count; k++) { soup.Add(poly[0]); soup.Add(poly[k]); soup.Add(poly[k + 1]); }
    }

    private static IEnumerable<(P2, P2)> CrossingEdges(List<P2> bnd, P2 a, P2 b, P2 c)
    {
        int n = bnd.Count;
        for (int i = 0; i < n; i++)
        {
            P2 p0 = bnd[i], p1 = bnd[(i + 1) % n];
            if (PointInTri(p0, a, b, c) || PointInTri(p1, a, b, c)
                || SegSeg(p0, p1, a, b) || SegSeg(p0, p1, b, c) || SegSeg(p0, p1, c, a))
                yield return (p0, p1);
        }
    }

    private static List<P2> ClipByHalfPlane(List<P2> poly, P2 e0, P2 e1)
    {
        var outp = new List<P2>(poly.Count + 2);
        double ex = e1.X - e0.X, ey = e1.Y - e0.Y;
        double Side(P2 p) => ex * (p.Y - e0.Y) - ey * (p.X - e0.X);
        for (int i = 0; i < poly.Count; i++)
        {
            P2 cur = poly[i], prev = poly[(i + poly.Count - 1) % poly.Count];
            double sc = Side(cur), sp = Side(prev);
            bool curIn = sc >= -1e-9, prevIn = sp >= -1e-9;
            if (curIn)
            {
                if (!prevIn) outp.Add(LineIntersect(prev, cur, e0, e1));
                outp.Add(cur);
            }
            else if (prevIn) outp.Add(LineIntersect(prev, cur, e0, e1));
        }
        return outp;
    }

    // ── 焊接 / 拓扑 ─────────────────────────────────────────────

    private static void WeldXY(List<P2> soup, double tol, out List<P2> verts, out List<int> tris)
    {
        var vlist = new List<P2>(soup.Count);
        var tlist = new List<int>(soup.Count);
        var map = new Dictionary<(long, long), int>(soup.Count);
        double inv = 1.0 / (tol > 1e-12 ? tol : 1e-12);
        int Vid(P2 p)
        {
            var key = ((long)Math.Round(p.X * inv), (long)Math.Round(p.Y * inv));
            if (map.TryGetValue(key, out int id)) return id;
            id = vlist.Count; vlist.Add(p); map[key] = id; return id;
        }
        for (int t = 0; t + 2 < soup.Count; t += 3)
        {
            int x = Vid(soup[t]), y = Vid(soup[t + 1]), z = Vid(soup[t + 2]);
            if (x == y || y == z || x == z) continue;
            tlist.Add(x); tlist.Add(y); tlist.Add(z);
        }
        verts = vlist; tris = tlist;
    }

    private static List<(int, int)> CollectOpenEdges(List<int> tris)
    {
        var count = new Dictionary<long, int>(tris.Count);
        var dir = new Dictionary<long, (int, int)>(tris.Count);
        void Add(int u, int v)
        {
            long key = Key(u, v);
            count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
            if (!dir.ContainsKey(key)) dir[key] = (u, v);
        }
        for (int t = 0; t + 2 < tris.Count; t += 3) { Add(tris[t], tris[t + 1]); Add(tris[t + 1], tris[t + 2]); Add(tris[t + 2], tris[t]); }
        var open = new List<(int, int)>();
        foreach (var kv in count) if (kv.Value == 1) open.Add(dir[kv.Key]);
        return open;
    }

    private static int CountOpenEdges(List<int> tris)
    {
        var count = new Dictionary<long, int>(tris.Count);
        void Add(int u, int v) { long k = Key(u, v); count[k] = count.TryGetValue(k, out int n) ? n + 1 : 1; }
        for (int t = 0; t + 2 < tris.Count; t += 3) { Add(tris[t], tris[t + 1]); Add(tris[t + 1], tris[t + 2]); Add(tris[t + 2], tris[t]); }
        int open = 0;
        foreach (var n in count.Values) if (n != 2) open++;
        return open;
    }

    private static long Key(int u, int v) { int lo = Math.Min(u, v), hi = Math.Max(u, v); return ((long)lo << 32) | (uint)hi; }

    private static void AddTri(List<int> idx, int a, int b, int c, bool flip)
    {
        idx.Add(a);
        if (!flip) { idx.Add(b); idx.Add(c); } else { idx.Add(c); idx.Add(b); }
    }

    // ── 2D 几何基元 ─────────────────────────────────────────────

    private static double SignedArea(List<P2> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a.X * b.Y - b.X * a.Y; }
        return 0.5 * s;
    }

    private static bool PointInPolygon(List<P2> poly, double x, double y)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
            if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) inside = !inside;
        }
        return inside;
    }

    private static bool PointInTri(P2 p, P2 a, P2 b, P2 c)
    {
        double d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
        bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
        return !(neg && pos);
    }

    private static double Cross(P2 p, P2 a, P2 b) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static bool SegSeg(P2 p1, P2 p2, P2 p3, P2 p4)
    {
        double d1 = Dir(p3, p4, p1), d2 = Dir(p3, p4, p2), d3 = Dir(p1, p2, p3), d4 = Dir(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Dir(P2 a, P2 b, P2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static P2 LineIntersect(P2 p0, P2 p1, P2 e0, P2 e1)
    {
        double a1 = p1.Y - p0.Y, b1 = p0.X - p1.X, c1 = a1 * p0.X + b1 * p0.Y;
        double a2 = e1.Y - e0.Y, b2 = e0.X - e1.X, c2 = a2 * e0.X + b2 * e0.Y;
        double det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < 1e-12) return p1;
        return new P2((b2 * c1 - b1 * c2) / det, (a1 * c2 - a2 * c1) / det);
    }
}
