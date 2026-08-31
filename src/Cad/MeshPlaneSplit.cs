using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网沿竖直面切分为两片——原 PitMine3D「沿多段线分割三角网」走 C++ 内核, 但**三角形-平面裁剪是标准几何**:
/// 竖直面由 XY 平面内一条切割线(p0→p1)定义(法向 = 线的 XY 垂向); 逐三角按顶点符号距离分左右, 跨界三角在
/// 交点处裁成子三角, 各归其侧。复用托管 (verts, tris) 表示; 纯逻辑、可单测。
/// (直线切割精确; 曲折多段线取首末点连线所在面近似——按弦切, 记录。)
/// </summary>
public static class MeshPlaneSplit
{
    /// <summary>沿过 (x0,y0)→(x1,y1) 的竖直面切分。返回 (左片, 右片)，各为 (verts, tris)。左=符号距离&lt;0 侧。</summary>
    public static ((List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) left,
                   (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) right)
        Split(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris,
              double x0, double y0, double x1, double y1)
    {
        var left = (v: new List<(double x, double y, double z)>(), t: new List<(int a, int b, int c)>());
        var right = (v: new List<(double x, double y, double z)>(), t: new List<(int a, int b, int c)>());
        if (verts == null || tris == null || verts.Count < 3 || tris.Count < 1) return (left, right);

        // 竖直面法向(XY) = 切割线方向的垂向; 符号距离 d(p) = n·((px,py) - p0)。
        double nx = -(y1 - y0), ny = (x1 - x0);
        double len = Math.Sqrt(nx * nx + ny * ny);
        if (len < 1e-12) return (left, right);   // 退化线
        nx /= len; ny /= len;
        double Dist((double x, double y, double z) p) => nx * (p.x - x0) + ny * (p.y - y0);

        // 顶点去重(按侧内独立索引)。
        var lMap = new Dictionary<long, int>(); var rMap = new Dictionary<long, int>();
        int AddL((double x, double y, double z) p) => AddVert(left.v, lMap, p);
        int AddR((double x, double y, double z) p) => AddVert(right.v, rMap, p);

        const double eps = 1e-9;
        foreach (var (ia, ib, ic) in tris)
        {
            var pa = verts[ia]; var pb = verts[ib]; var pc = verts[ic];
            double da = Dist(pa), db = Dist(pb), dc = Dist(pc);
            bool aL = da < -eps, bL = db < -eps, cL = dc < -eps;
            bool aR = da > eps, bR = db > eps, cR = dc > eps;
            // 不跨界(全在一侧或贴面) → 整片归属。贴面(全 |d|<=eps)归左。
            if (!(aR || bR || cR)) { EmitWhole(left, AddL, pa, pb, pc); continue; }
            if (!(aL || bL || cL)) { EmitWhole(right, AddR, pa, pb, pc); continue; }
            // 跨界 → 裁剪。对每条边求与面交点, 分别向左右侧发多边形(再扇形三角化)。
            var lpoly = new List<(double x, double y, double z)>();
            var rpoly = new List<(double x, double y, double z)>();
            ClipEdge(pa, pb, da, db, lpoly, rpoly);
            ClipEdge(pb, pc, db, dc, lpoly, rpoly);
            ClipEdge(pc, pa, dc, da, lpoly, rpoly);
            EmitPoly(left, AddL, lpoly);
            EmitPoly(right, AddR, rpoly);
        }
        return (left, right);
    }

    // 把一个顶点(含到当前侧点)加入子片, 按顶点侧别归入左/右多边形; 边跨面则追加交点到两侧。
    private static void ClipEdge((double x, double y, double z) p0, (double x, double y, double z) p1,
        double d0, double d1, List<(double x, double y, double z)> lpoly, List<(double x, double y, double z)> rpoly)
    {
        const double eps = 1e-9;
        // 起点归属(贴面同时进两侧, 保多边形闭合)。
        if (d0 <= eps) lpoly.Add(p0);
        if (d0 >= -eps) rpoly.Add(p0);
        // 边真正跨面(异号) → 交点进两侧。
        if ((d0 < -eps && d1 > eps) || (d0 > eps && d1 < -eps))
        {
            double t = d0 / (d0 - d1);
            var ip = (p0.x + (p1.x - p0.x) * t, p0.y + (p1.y - p0.y) * t, p0.z + (p1.z - p0.z) * t);
            lpoly.Add(ip); rpoly.Add(ip);
        }
    }

    private static void EmitWhole((List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) side,
        Func<(double x, double y, double z), int> add, (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        => side.t.Add((add(a), add(b), add(c)));

    private static void EmitPoly((List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) side,
        Func<(double x, double y, double z), int> add, List<(double x, double y, double z)> poly)
    {
        if (poly.Count < 3) return;
        int i0 = add(poly[0]);
        for (int i = 1; i + 1 < poly.Count; i++)   // 扇形三角化(凸子多边形, 裁剪产物≤4 边)
            side.t.Add((i0, add(poly[i]), add(poly[i + 1])));
    }

    private static int AddVert(List<(double x, double y, double z)> v, Dictionary<long, int> map, (double x, double y, double z) p)
    {
        long key = Quant(p.x) * 1000003L ^ Quant(p.y) * 9176L ^ Quant(p.z);
        if (map.TryGetValue(key, out int idx) && Near(v[idx], p)) return idx;
        idx = v.Count; v.Add(p); map[key] = idx; return idx;
    }
    private static long Quant(double d) => (long)Math.Round(d * 1e6);
    private static bool Near((double x, double y, double z) a, (double x, double y, double z) b)
        => Math.Abs(a.x - b.x) < 1e-6 && Math.Abs(a.y - b.y) < 1e-6 && Math.Abs(a.z - b.z) < 1e-6;
}
