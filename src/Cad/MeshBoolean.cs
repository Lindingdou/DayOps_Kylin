using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网布尔运算（BOOLUNION / BOOLINTER / BOOLDIFF / BOOLCOMP）与刀切闭合实体（CUTBYKNIFE）的托管内核。
///
/// 原版走 C++ 内核（精确谓词 + 重网格），这里用「求交 → 逐三角约束重剖 → 广义缠绕数分类 → 装配」
/// 这条同构路线在托管侧重算，用的都是仓库里已有的稳妥件：
///   ① <see cref="MeshIntersect.TriTriSegment"/> 逐三角对求交段（标准 tri-tri，非变体敏感）；
///   ② <see cref="Delaunay.TriangulateConstrained"/> 把交段当约束边，在三角形自己的平面内重剖；
///   ③ <see cref="WindingNumberTester"/> 广义缠绕数判内外（对小缝隙/轻微非水密比射线法稳）；
///   ④ <see cref="MeshWeld"/> + <see cref="MeshOrient"/> 焊接与统一外法线。
///
/// 已知局限（结果里如实回报，不假装成功）：
///   · 两网**共面重叠**的面片按"A 优先保留、B 侧丢弃"处理，不做共面精确合并；
///   · 输入非闭合时缠绕数分类不可靠 —— 直接判失败并说明，不给一个看似成功的错网格。
/// 纯逻辑、无 UI 依赖、可单测。
/// </summary>
public static class MeshBoolean
{
    public enum Op { Union = 0, Intersection = 1, Difference = 2, Complement = 3 }

    public sealed class Result
    {
        public bool Success;
        public string Error = "";
        public List<(double x, double y, double z)> Verts = new();
        public List<(int a, int b, int c)> Tris = new();
        public int FacesFromA, FacesFromB;   // 结果里分别来自 A / B 的面数
        public int SplitFaces;               // 被交线切开而重剖的原始面数
        public int InputFacesA, InputFacesB;
    }

    public static string OpName(Op op) => op switch
    {
        Op.Union => "并集",
        Op.Intersection => "交集",
        Op.Difference => "差集 (A-B)",
        _ => "补集 (B-A)",
    };

    /// <summary>A ⊕ B。两网都必须是闭合体。</summary>
    public static Result Compute(
        IReadOnlyList<(double x, double y, double z)> va, IReadOnlyList<(int a, int b, int c)> ta,
        IReadOnlyList<(double x, double y, double z)> vb, IReadOnlyList<(int a, int b, int c)> tb,
        Op op)
    {
        var r = new Result { InputFacesA = ta?.Count ?? 0, InputFacesB = tb?.Count ?? 0 };
        if (va == null || ta == null || vb == null || tb == null || ta.Count < 4 || tb.Count < 4)
        { r.Error = "两个输入都必须是有效三角网（各 ≥4 面）"; return r; }
        var da = MeshDiagnose.Analyze(va, ta);
        var db = MeshDiagnose.Analyze(vb, tb);
        if (!da.IsClosed || !db.IsClosed)
        {
            r.Error = $"布尔运算要求两个输入都是闭合体（A {(da.IsClosed ? "闭合" : $"开放, 开放边 {da.BoundaryEdges}")}"
                    + $"；B {(db.IsClosed ? "闭合" : $"开放, 开放边 {db.BoundaryEdges}")}）。"
                    + "可先用「修复拓扑关系」补洞，或用「固化成体」把面焊成体。";
            return r;
        }

        var A = Outward(va, ta);
        var B = Outward(vb, tb);
        var wnA = BuildTester(A.v, A.t);
        var wnB = BuildTester(B.v, B.t);
        if (wnA == null || wnB == null) { r.Error = "构建缠绕数测试器失败（顶点/索引异常）"; return r; }

        // 两侧都用同一套交段端点：TriTriSegment 的两个方向算出的端点会差最后几位，
        // 差这几位就焊不到一起 → 接缝裂开、结果不水密。所以固定按 (A三角, B三角) 的顺序求交，
        // B 侧只是把参数掉个个儿传进去，拿到的是**逐位相同**的端点。
        var pieceA = Split(A.v, A.t, B.v, B.t, selfIsFirst: true, out int splitA);
        var pieceB = Split(B.v, B.t, A.v, A.t, selfIsFirst: false, out int splitB);
        r.SplitFaces = splitA + splitB;

        // 分类：sub-triangle 的质心落在对方体内 / 体外。落在对方表面上(缠绕数 ≈ 0.5)算共面重叠。
        bool wantAInside, wantBInside, flipA, flipB;
        switch (op)
        {
            case Op.Union: wantAInside = false; wantBInside = false; flipA = false; flipB = false; break;
            case Op.Intersection: wantAInside = true; wantBInside = true; flipA = false; flipB = false; break;
            case Op.Difference: wantAInside = false; wantBInside = true; flipA = false; flipB = true; break;
            default: wantAInside = true; wantBInside = false; flipA = true; flipB = false; break;   // Complement = B - A
        }

        var outTris = new List<((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)>();
        foreach (var t in pieceA)
        {
            var side = Classify(wnB, t);
            if (side == Side.OnSurface) continue;                       // 共面重叠：A 侧保留一份, 见下
            if ((side == Side.Inside) != wantAInside) continue;
            outTris.Add(flipA ? (t.a, t.c, t.b) : t); r.FacesFromA++;
        }
        // 共面重叠的面只从 A 取一份（B 侧同位置的面丢掉），避免结果里出现重面
        foreach (var t in pieceA)
            if (Classify(wnB, t) == Side.OnSurface && KeepCoplanar(op))
            { outTris.Add(flipA ? (t.a, t.c, t.b) : t); r.FacesFromA++; }
        foreach (var t in pieceB)
        {
            var side = Classify(wnA, t);
            if (side == Side.OnSurface) continue;
            if ((side == Side.Inside) != wantBInside) continue;
            outTris.Add(flipB ? (t.a, t.c, t.b) : t); r.FacesFromB++;
        }

        if (outTris.Count == 0)
        {
            r.Error = op switch
            {
                Op.Intersection => "两体不相交，交集为空",
                Op.Difference => "A 完全被 B 吞掉，差集为空",
                Op.Complement => "B 完全被 A 吞掉，补集为空",
                _ => "结果为空",
            };
            return r;
        }

        var (rv, rt) = Assemble(outTris);
        r.Verts = rv; r.Tris = rt;
        r.Success = rt.Count > 0;
        if (!r.Success) r.Error = "装配后无有效三角面";
        return r;
    }

    /// <summary>
    /// 刀切闭合实体（CUTBYKNIFE）：用开放面(刀)切闭合体，保留刀下方或上方一侧，并用刀在体内的部分封盖成新闭合体。
    /// </summary>
    public static Result CutByKnife(
        IReadOnlyList<(double x, double y, double z)> vs, IReadOnlyList<(int a, int b, int c)> ts,   // 实体
        IReadOnlyList<(double x, double y, double z)> vk, IReadOnlyList<(int a, int b, int c)> tk,   // 刀(开放面)
        bool keepBelow)
    {
        var r = new Result { InputFacesA = ts?.Count ?? 0, InputFacesB = tk?.Count ?? 0 };
        if (vs == null || ts == null || vk == null || tk == null || ts.Count < 4 || tk.Count < 1)
        { r.Error = "需要一个闭合实体 + 一个开放刀面"; return r; }
        var ds = MeshDiagnose.Analyze(vs, ts);
        if (!ds.IsClosed) { r.Error = $"被切对象不是闭合体（开放边 {ds.BoundaryEdges}），请先「修复拓扑关系」"; return r; }

        var S = Outward(vs, ts);
        var wnS = BuildTester(S.v, S.t);
        if (wnS == null) { r.Error = "构建缠绕数测试器失败"; return r; }

        // 刀面按 XY 采样成高程场：判"上/下"用的是同一 XY 处刀面的 Z（与原版"刀下方/刀上方"语义一致）
        var knifeGrid = BuildKnifeGrid(vk, tk);

        var pieceS = Split(S.v, S.t, vk, tk, selfIsFirst: true, out int splitS);
        var pieceK = Split(vk, tk, S.v, S.t, selfIsFirst: false, out int splitK);
        r.SplitFaces = splitS + splitK;

        var outTris = new List<((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)>();
        foreach (var t in pieceS)
        {
            var c = Centroid(t);
            double? kz = knifeGrid.Sample(c.x, c.y);
            bool below = kz == null || c.z <= kz.Value;   // 刀覆盖不到的地方按"留下"处理（原版同样只切刀覆盖范围）
            if (below == keepBelow) { outTris.Add(t); r.FacesFromA++; }
        }
        // 刀在实体内部的部分作封盖；法线朝向要与保留侧相反(保留下方 → 盖朝上)
        foreach (var t in pieceK)
        {
            var c = Centroid(t);
            if (!wnS.IsInsideClosed(c.x, c.y, c.z)) continue;
            var n = Normal(t);
            bool upward = n.z >= 0;
            bool need = keepBelow;                        // 留下方 → 盖的法线朝上
            outTris.Add(upward == need ? t : (t.a, t.c, t.b));
            r.FacesFromB++;
        }
        if (r.FacesFromB == 0) { r.Error = "刀面没有落在实体内部的部分 —— 刀未真正切到该实体"; return r; }
        if (r.FacesFromA == 0) { r.Error = "保留侧为空 —— 换一侧或换一把刀"; return r; }

        var (rv, rt) = Assemble(outTris);
        r.Verts = rv; r.Tris = rt;
        r.Success = rt.Count > 0;
        if (!r.Success) r.Error = "装配后无有效三角面";
        return r;
    }

    // ══════════════════════════ 内部：切分 / 分类 / 装配 ══════════════════════════

    private enum Side { Inside, Outside, OnSurface }

    private static Side Classify(WindingNumberTester wn,
        ((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c) t)
    {
        var c = Centroid(t);
        if (c.x < wn.MinX || c.x > wn.MaxX || c.y < wn.MinY || c.y > wn.MaxY || c.z < wn.MinZ || c.z > wn.MaxZ)
            return Side.Outside;
        double w = wn.Winding(c.x, c.y, c.z);
        if (Math.Abs(w - 0.5) < 0.2) return Side.OnSurface;   // 落在对方表面上 → 共面重叠
        return w > 0.5 ? Side.Inside : Side.Outside;
    }

    /// <summary>共面重叠的面在哪些运算里该保留（并集/交集保留一份；差集/补集里它是被减掉的界面，丢弃）。</summary>
    private static bool KeepCoplanar(Op op) => op is Op.Union or Op.Intersection;

    /// <summary>
    /// 把 self 的每个三角形按与 other 的交段切开：无交段的原样保留，有交段的投到自身平面做约束剖分。
    /// </summary>
    private static List<((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)> Split(
        IReadOnlyList<(double x, double y, double z)> vSelf, IReadOnlyList<(int a, int b, int c)> tSelf,
        IReadOnlyList<(double x, double y, double z)> vOther, IReadOnlyList<(int a, int b, int c)> tOther,
        bool selfIsFirst, out int splitFaces)
    {
        splitFaces = 0;
        var outTris = new List<((double x, double y, double z), (double x, double y, double z), (double x, double y, double z))>(tSelf.Count);
        var grid = new TriGridIndex(vOther, tOther);
        var segs = new List<((double x, double y, double z) A, (double x, double y, double z) B)>();

        foreach (var (ia, ib, ic) in tSelf)
        {
            if (ia >= vSelf.Count || ib >= vSelf.Count || ic >= vSelf.Count) continue;
            var a = vSelf[ia]; var b = vSelf[ib]; var c = vSelf[ic];
            segs.Clear();
            foreach (int j in grid.Candidates(a, b, c))
            {
                var (ja, jb, jc) = tOther[j];
                if (ja >= vOther.Count || jb >= vOther.Count || jc >= vOther.Count) continue;
                bool hit = selfIsFirst
                    ? MeshIntersect.TriTriSegment(a, b, c, vOther[ja], vOther[jb], vOther[jc], out var s)
                    : MeshIntersect.TriTriSegment(vOther[ja], vOther[jb], vOther[jc], a, b, c, out s);
                if (hit) segs.Add((s.A, s.B));
            }
            if (segs.Count == 0) { outTris.Add((a, b, c)); continue; }
            int before = outTris.Count;
            if (!Retriangulate(a, b, c, segs, outTris)) { outTris.Add((a, b, c)); continue; }
            if (outTris.Count > before + 1) splitFaces++;
        }
        return outTris;
    }

    /// <summary>三角形 + 若干平面内交段 → 约束重剖（投到三角形自身平面，剖完再映回 3D，绕向随原三角）。</summary>
    private static bool Retriangulate(
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c,
        List<((double x, double y, double z) A, (double x, double y, double z) B)> segs,
        List<((double x, double y, double z), (double x, double y, double z), (double x, double y, double z))> outTris)
    {
        var n = Cross(Sub(b, a), Sub(c, a));
        double nl = Math.Sqrt(Dot(n, n));
        if (nl < 1e-18) return false;
        // 投影：丢掉法线最大的那个分量（该方向上三角形退化成一条线，另两轴保序不塌）
        int drop = Math.Abs(n.x) >= Math.Abs(n.y) && Math.Abs(n.x) >= Math.Abs(n.z) ? 0
                 : Math.Abs(n.y) >= Math.Abs(n.z) ? 1 : 2;
        (double u, double v) P2((double x, double y, double z) p)
            => drop == 0 ? (p.y, p.z) : drop == 1 ? (p.x, p.z) : (p.x, p.y);

        var a2 = P2(a); var b2 = P2(b); var c2 = P2(c);
        double scale = Math.Max(Math.Max(Dist2(a2, b2), Dist2(b2, c2)), Dist2(c2, a2));
        scale = Math.Sqrt(Math.Max(scale, 1e-30));
        double snap = scale * 1e-7;
        double snap2 = snap * snap;

        var pts2 = new List<(double u, double v)> { a2, b2, c2 };
        var pts3 = new List<(double x, double y, double z)> { a, b, c };
        int Add((double x, double y, double z) p)
        {
            var q = P2(p);
            for (int i = 0; i < pts2.Count; i++) if (Dist2(pts2[i], q) <= snap2) return i;
            pts2.Add(q); pts3.Add(p); return pts2.Count - 1;
        }
        var cons = new List<(int u, int v)>();
        foreach (var s in segs)
        {
            int i0 = Add(s.A), i1 = Add(s.B);
            if (i0 != i1) cons.Add((i0, i1));
        }
        if (pts2.Count <= 3) return false;   // 交段全塌到三角形顶点上：等于没切

        var tris = Delaunay.TriangulateConstrained(pts2, cons);
        if (tris.Count == 0) return false;

        // 剖分覆盖的是点集凸包 = 原三角形；逐片按原三角法线摆正绕向
        int added = 0;
        foreach (var (i, j, k) in tris)
        {
            if (i >= pts3.Count || j >= pts3.Count || k >= pts3.Count) continue;
            var p = pts3[i]; var q = pts3[j]; var s = pts3[k];
            var tn = Cross(Sub(q, p), Sub(s, p));
            if (Dot(tn, n) < 0) (q, s) = (s, q);
            if (Math.Sqrt(Dot(Cross(Sub(q, p), Sub(s, p)), Cross(Sub(q, p), Sub(s, p)))) < nl * 1e-12) continue;
            outTris.Add((p, q, s)); added++;
        }
        return added > 0;
    }

    /// <summary>散三角 → 焊接成共享顶点的网格，并统一外法线。</summary>
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Assemble(
        List<((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)> tris)
    {
        var verts = new List<(double x, double y, double z)>(tris.Count * 3);
        var idx = new List<(int a, int b, int c)>(tris.Count);
        foreach (var t in tris)
        {
            verts.Add(t.a); verts.Add(t.b); verts.Add(t.c);
            idx.Add((verts.Count - 3, verts.Count - 2, verts.Count - 1));
        }
        double diag = Diagonal(verts);
        // 容差要够合上"同一个交点被两侧各算一遍"的最后几位误差, 又不能大到吃掉真实的细小特征
        var w = MeshWeld.Weld(verts, idx, diag > 0 ? diag * 1e-9 : 1e-12, dropDuplicateTris: true);
        // 交线端点常常压在邻居三角形的共用边上, 邻居没被穿过就不会插这个点 → 边对不上、网裂着。
        // 焊接管不了(顶点本来就重合), 得专门把这种 T 型接缝补掉, 结果才成得了闭合体。
        var (tv, tt, _) = MeshTJunction.Fix(w.Verts, w.Tris, diag > 0 ? diag * 1e-9 : 1e-12);
        var w2 = MeshWeld.Weld(tv, tt, diag > 0 ? diag * 1e-9 : 1e-12, dropDuplicateTris: true);
        return (w2.Verts, w2.Tris);
    }

    /// <summary>统一成外法线（有向体积为正）。缠绕数的正负号依赖它。</summary>
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Outward(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var oriented = MeshOrient.MakeConsistent(v, t);
        if (MeshOrient.SignedVolume6(v, oriented) < 0)
            for (int i = 0; i < oriented.Count; i++) oriented[i] = (oriented[i].a, oriented[i].c, oriented[i].b);
        return (new List<(double x, double y, double z)>(v), oriented);
    }

    private static WindingNumberTester? BuildTester(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var fv = new double[v.Count * 3];
        for (int i = 0; i < v.Count; i++) { fv[i * 3] = v[i].x; fv[i * 3 + 1] = v[i].y; fv[i * 3 + 2] = v[i].z; }
        var ft = new int[t.Count * 3];
        for (int i = 0; i < t.Count; i++) { ft[i * 3] = t[i].a; ft[i * 3 + 1] = t[i].b; ft[i * 3 + 2] = t[i].c; }
        try { return new WindingNumberTester(fv, ft); } catch { return null; }
    }

    /// <summary>刀面的 XY 高程场（取该 XY 处刀面 Z 最高的三角，同"实时曲面坐标"的取法）。</summary>
    private static SurfaceVolume.TriGrid BuildKnifeGrid(
        IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
    {
        var fv = new double[v.Count * 3];
        for (int i = 0; i < v.Count; i++) { fv[i * 3] = v[i].x; fv[i * 3 + 1] = v[i].y; fv[i * 3 + 2] = v[i].z; }
        var ft = new int[t.Count * 3];
        for (int i = 0; i < t.Count; i++) { ft[i * 3] = t[i].a; ft[i * 3 + 1] = t[i].b; ft[i * 3 + 2] = t[i].c; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in v) { minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y); maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y); }
        double cell = Math.Max(Math.Max(maxX - minX, maxY - minY) / 200, 1e-9);
        return new SurfaceVolume.TriGrid(fv, ft, cell);
    }

    // 三角 AABB 的均匀格索引：布尔逐三角求交若两两硬碰是 O(nA·nB)，加格后只看邻近格。
    private sealed class TriGridIndex
    {
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private readonly double _cell;
        private readonly bool _empty;

        public TriGridIndex(IReadOnlyList<(double x, double y, double z)> v, IReadOnlyList<(int a, int b, int c)> t)
        {
            if (v.Count == 0 || t.Count == 0) { _empty = true; _cell = 1; return; }
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in v)
            {
                minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y); minZ = Math.Min(minZ, p.z);
                maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y); maxZ = Math.Max(maxZ, p.z);
            }
            double span = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
            // 目标：每格平均装几个三角 → 格边约等于"包围盒 / 三角数的立方根"
            _cell = Math.Max(span / Math.Max(4, (int)Math.Cbrt(t.Count) * 2), span * 1e-6);
            if (_cell <= 0 || double.IsNaN(_cell)) { _empty = true; _cell = 1; return; }
            for (int j = 0; j < t.Count; j++)
            {
                var (ia, ib, ic) = t[j];
                if (ia >= v.Count || ib >= v.Count || ic >= v.Count) continue;
                foreach (var key in Keys(v[ia], v[ib], v[ic]))
                {
                    if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                    list.Add(j);
                }
            }
        }

        private IEnumerable<(int, int, int)> Keys(
            (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        {
            int x0 = (int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x)) / _cell), x1 = (int)Math.Floor(Math.Max(a.x, Math.Max(b.x, c.x)) / _cell);
            int y0 = (int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y)) / _cell), y1 = (int)Math.Floor(Math.Max(a.y, Math.Max(b.y, c.y)) / _cell);
            int z0 = (int)Math.Floor(Math.Min(a.z, Math.Min(b.z, c.z)) / _cell), z1 = (int)Math.Floor(Math.Max(a.z, Math.Max(b.z, c.z)) / _cell);
            // 单个三角跨太多格时退化保护：直接不入格(由候选集合的兜底扫描覆盖)
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 4096) yield break;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        yield return (x, y, z);
        }

        public IEnumerable<int> Candidates(
            (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        {
            if (_empty) yield break;
            var seen = new HashSet<int>();
            foreach (var key in Keys(a, b, c))
                if (_cells.TryGetValue(key, out var list))
                    foreach (int j in list) if (seen.Add(j)) yield return j;
        }
    }

    // ── 小几何件 ──
    private static (double x, double y, double z) Centroid(
        ((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c) t)
        => ((t.a.x + t.b.x + t.c.x) / 3, (t.a.y + t.b.y + t.c.y) / 3, (t.a.z + t.b.z + t.c.z) / 3);

    private static (double x, double y, double z) Normal(
        ((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c) t)
        => Cross(Sub(t.b, t.a), Sub(t.c, t.a));

    private static double Diagonal(IReadOnlyList<(double x, double y, double z)> v)
    {
        if (v.Count == 0) return 0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in v)
        {
            minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y); minZ = Math.Min(minZ, p.z);
            maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y); maxZ = Math.Max(maxZ, p.z);
        }
        return Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
    }

    private static double Dist2((double u, double v) a, (double u, double v) b)
        => (a.u - b.u) * (a.u - b.u) + (a.v - b.v) * (a.v - b.v);
    private static (double x, double y, double z) Sub((double x, double y, double z) a, (double x, double y, double z) b) => (a.x - b.x, a.y - b.y, a.z - b.z);
    private static (double x, double y, double z) Cross((double x, double y, double z) a, (double x, double y, double z) b) => (a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    private static double Dot((double x, double y, double z) a, (double x, double y, double z) b) => a.x * b.x + a.y * b.y + a.z * b.z;
}
