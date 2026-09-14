using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 逐三角面拾取 —— 「删除三角面」等"点选某一个三角面"的命令共用（原版 DeleteMeshFacesCommandState::PickFaceAt 的托管等价）。
/// <list type="bullet">
/// <item><b>2D 俯视</b>：光标 XY 落在哪个三角的平面投影内；多个三角叠在同一 XY(闭合体有顶有底)时取 Z 最高的那个 —— 俯视看得见的那层。</item>
/// <item><b>3D</b>：把顶点按当前相机投到屏幕，屏幕点落在哪个三角的投影内；重叠取深度最前者。原版走射线求交，
///   投影法在透视下与之等价(同一像素的射线穿过三角 ⇔ 像素落在三角投影内)，而顶点只投一次、逐帧悬停也扛得住。</item>
/// </list>
/// 两条路都在三角的 2D 包围盒上铺一层格网桶：悬停时鼠标每动一下只查落点所在的那个桶, 不扫全网
/// (早前 <c>FindTriangleAt</c> 逐三角线性扫, 只用在点击那一下; 现在每次指针移动都要判, 十万三角的网扫全表就拖不动光标)。
/// 3D 的投影 + 桶按 <paramref name="viewStamp"/>(相机状态 + 视口尺寸)缓存, 相机一动才重投。
/// </summary>
public sealed class MeshFacePick
{
    private readonly MeshEntity _m;
    private Buckets? _xy;            // 2D: 世界 XY 桶(随网建一次)
    private object? _stamp;          // 3D: 投影缓存对应的视图戳
    private double[]? _sx, _sy, _sz; // 3D: 顶点屏幕坐标 + 深度
    private bool[]? _sok;            // 3D: 顶点在相机前方(投得出来)
    private Buckets? _scr;           // 3D: 屏幕 XY 桶

    public MeshFacePick(MeshEntity m) { _m = m; }

    public MeshEntity Mesh => _m;

    /// <summary>2D：世界 XY → 三角索引(重叠取最高 Z)；没命中返回 -1。</summary>
    public int FindAtXY(double x, double y)
    {
        if (_m.Tris.Count == 0) return -1;
        if (_xy == null)
        {
            int nv = _m.Verts.Count;
            var vx = new double[nv]; var vy = new double[nv]; var vz = new double[nv]; var ok = new bool[nv];
            for (int i = 0; i < nv; i++) { var v = _m.Verts[i]; vx[i] = v.x; vy[i] = v.y; vz[i] = v.z; ok[i] = true; }
            _xy = new Buckets(_m.Tris, vx, vy, vz, ok);
        }
        return _xy.Find(x, y, nearestIsMax: true);
    }

    /// <summary>
    /// 3D：屏幕点 → 三角索引(重叠取深度最前)；没命中返回 -1。
    /// <paramref name="viewStamp"/> 变了(相机动了 / 视口改尺寸)才重新投影，否则复用上次的投影与桶。
    /// </summary>
    public int FindAtScreen(double sx, double sy, object viewStamp,
                            Func<double, double, double, (double sx, double sy, double depth)?> project)
    {
        if (_m.Tris.Count == 0) return -1;
        if (_scr == null || _stamp == null || !_stamp.Equals(viewStamp))
        {
            int nv = _m.Verts.Count;
            _sx = new double[nv]; _sy = new double[nv]; _sz = new double[nv]; _sok = new bool[nv];
            double elev = _m.Elevation;
            for (int i = 0; i < nv; i++)
            {
                var v = _m.Verts[i];
                var s = project(v.x, v.y, v.z + elev);
                if (s == null) continue;
                _sx[i] = s.Value.sx; _sy[i] = s.Value.sy; _sz[i] = s.Value.depth; _sok[i] = true;
            }
            _scr = new Buckets(_m.Tris, _sx, _sy, _sz, _sok);
            _stamp = viewStamp;
        }
        return _scr.Find(sx, sy, nearestIsMax: false);   // NDC 深度越小越近
    }

    /// <summary>
    /// 2D：世界 XY 矩形框选 → 三角索引集。<paramref name="crossing"/>=false 窗口(三个顶点全在框内才选)，
    /// true 交叉(顶点在框内 / 边与框相交 / 框整个落在三角里, 碰到即选)。AutoCAD 左→右窗口、右→左交叉那一套。
    /// </summary>
    public List<int> FacesInRect(double x0, double y0, double x1, double y1, bool crossing)
    {
        if (_m.Tris.Count == 0) return new List<int>();
        FindAtXY(x0, y0);   // 确保 2D 桶已建
        return _xy!.InRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1), crossing);
    }

    /// <summary>3D：屏幕矩形框选 → 三角索引集(投影落在框内的三角, 被挡住的背面也算, 同 CAD 窗选)。</summary>
    public List<int> FacesInScreenRect(double sx0, double sy0, double sx1, double sy1, bool crossing, object viewStamp,
                                       Func<double, double, double, (double sx, double sy, double depth)?> project)
    {
        if (_m.Tris.Count == 0) return new List<int>();
        FindAtScreen(sx0, sy0, viewStamp, project);   // 确保投影 + 屏幕桶按当前视图建好
        return _scr!.InRect(Math.Min(sx0, sx1), Math.Min(sy0, sy1), Math.Max(sx0, sx1), Math.Max(sy0, sy1), crossing);
    }

    /// <summary>三角的三维真面积(悬停提示用)；索引越界返回 0。</summary>
    public double Area(int ti)
    {
        if (ti < 0 || ti >= _m.Tris.Count) return 0;
        var (a, b, c) = _m.Tris[ti];
        if (a >= _m.Verts.Count || b >= _m.Verts.Count || c >= _m.Verts.Count) return 0;
        var p = _m.Verts[a]; var q = _m.Verts[b]; var r = _m.Verts[c];
        double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
    }

    /// <summary>三角形心(世界坐标, 含 Elevation)；索引越界返回 null。</summary>
    public (double x, double y, double z)? Centroid(int ti)
    {
        if (ti < 0 || ti >= _m.Tris.Count) return null;
        var (a, b, c) = _m.Tris[ti];
        if (a >= _m.Verts.Count || b >= _m.Verts.Count || c >= _m.Verts.Count) return null;
        var p = _m.Verts[a]; var q = _m.Verts[b]; var r = _m.Verts[c];
        return ((p.x + q.x + r.x) / 3, (p.y + q.y + r.y) / 3, (p.z + q.z + r.z) / 3 + _m.Elevation);
    }

    /// <summary>
    /// 三角在某个 2D 坐标系(世界 XY 或屏幕 XY)下的格网桶：按三角 2D 包围盒登记到覆盖的格子里，
    /// 查询只看落点那一格。格数 ≈ √(三角数/2) 一边, 桶里平均几个三角。
    /// </summary>
    private sealed class Buckets
    {
        private readonly List<(int a, int b, int c)> _tris;
        private readonly double[] _x, _y, _d; private readonly bool[] _ok;
        private readonly double _x0, _y0, _cs; private readonly int _nx, _ny;
        private readonly List<int>?[] _cells;

        public Buckets(List<(int a, int b, int c)> tris, double[] x, double[] y, double[] d, bool[] ok)
        {
            _tris = tris; _x = x; _y = y; _d = d; _ok = ok;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < x.Length; i++)
            {
                if (!ok[i]) continue;
                if (x[i] < x0) x0 = x[i]; if (x[i] > x1) x1 = x[i];
                if (y[i] < y0) y0 = y[i]; if (y[i] > y1) y1 = y[i];
            }
            if (x0 > x1) { x0 = y0 = 0; x1 = y1 = 1; }   // 一个顶点都投不出来: 空桶
            int side = Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(1, tris.Count) / 2.0)), 1, 512);
            double span = Math.Max(x1 - x0, y1 - y0);
            _cs = span > 0 ? span / side : 1;
            _x0 = x0; _y0 = y0;
            _nx = Math.Max(1, (int)Math.Floor((x1 - x0) / _cs) + 1);
            _ny = Math.Max(1, (int)Math.Floor((y1 - y0) / _cs) + 1);
            _cells = new List<int>?[_nx * _ny];
            int nv = x.Length;
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c) = tris[t];
                if (a >= nv || b >= nv || c >= nv || !ok[a] || !ok[b] || !ok[c]) continue;
                double tx0 = Math.Min(x[a], Math.Min(x[b], x[c])), tx1 = Math.Max(x[a], Math.Max(x[b], x[c]));
                double ty0 = Math.Min(y[a], Math.Min(y[b], y[c])), ty1 = Math.Max(y[a], Math.Max(y[b], y[c]));
                int i0 = Math.Max(0, Idx(tx0, _x0)), i1 = Math.Min(_nx - 1, Idx(tx1, _x0));
                int j0 = Math.Max(0, Idx(ty0, _y0)), j1 = Math.Min(_ny - 1, Idx(ty1, _y0));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        (_cells[j * _nx + i] ??= new List<int>()).Add(t);
            }
        }

        private int Idx(double v, double o) => (int)Math.Floor((v - o) / _cs);

        /// <summary>矩形框选：只扫框覆盖到的那些格子；窗口 = 三顶点全在框内，交叉 = 顶点在框内 / 边与框边相交 / 框角落在三角内。</summary>
        public List<int> InRect(double x0, double y0, double x1, double y1, bool crossing)
        {
            var res = new List<int>();
            int i0 = Math.Max(0, Idx(x0, _x0)), i1 = Math.Min(_nx - 1, Idx(x1, _x0));
            int j0 = Math.Max(0, Idx(y0, _y0)), j1 = Math.Min(_ny - 1, Idx(y1, _y0));
            if (i0 > i1 || j0 > j1) return res;
            var seen = new HashSet<int>();
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var bk = _cells[j * _nx + i];
                    if (bk == null) continue;
                    foreach (var t in bk)
                    {
                        if (!seen.Add(t)) continue;
                        var (a, b, c) = _tris[t];
                        bool ia = In(_x[a], _y[a]), ib = In(_x[b], _y[b]), ic = In(_x[c], _y[c]);
                        if (ia && ib && ic) { res.Add(t); continue; }
                        if (!crossing) continue;
                        if (ia || ib || ic
                            || EdgeHitsRect(_x[a], _y[a], _x[b], _y[b]) || EdgeHitsRect(_x[b], _y[b], _x[c], _y[c]) || EdgeHitsRect(_x[c], _y[c], _x[a], _y[a])
                            || InTri(x0, y0, a, b, c))   // 框整个落在一个大三角里: 边不相交、顶点也不在框里, 只有框角在三角内
                            res.Add(t);
                    }
                }
            res.Sort();
            return res;

            bool In(double x, double y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
            bool EdgeHitsRect(double ax, double ay, double bx, double by)
                => LineMath.SegmentsIntersect(ax, ay, bx, by, x0, y0, x1, y0) || LineMath.SegmentsIntersect(ax, ay, bx, by, x1, y0, x1, y1)
                || LineMath.SegmentsIntersect(ax, ay, bx, by, x1, y1, x0, y1) || LineMath.SegmentsIntersect(ax, ay, bx, by, x0, y1, x0, y0);
        }

        private bool InTri(double px, double py, int a, int b, int c)
        {
            double ax = _x[a], ay = _y[a], bx = _x[b], by = _y[b], cx = _x[c], cy = _y[c];
            double det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(det) < 1e-15) return false;
            double w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / det;
            double w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / det;
            return w0 >= -1e-9 && w1 >= -1e-9 && 1 - w0 - w1 >= -1e-9;
        }

        /// <summary>落点所在三角；重叠时 <paramref name="nearestIsMax"/>=true 取 d 最大(2D 取最高 Z)、false 取 d 最小(3D 取深度最前)。</summary>
        public int Find(double px, double py, bool nearestIsMax)
        {
            int i = Idx(px, _x0), j = Idx(py, _y0);
            if (i < 0 || j < 0 || i >= _nx || j >= _ny) return -1;
            var bk = _cells[j * _nx + i];
            if (bk == null) return -1;
            int best = -1; double bestD = nearestIsMax ? double.NegativeInfinity : double.PositiveInfinity;
            foreach (var t in bk)
            {
                var (a, b, c) = _tris[t];
                double ax = _x[a], ay = _y[a], bx = _x[b], by = _y[b], cx = _x[c], cy = _y[c];
                if (px < Math.Min(ax, Math.Min(bx, cx)) || px > Math.Max(ax, Math.Max(bx, cx)) ||
                    py < Math.Min(ay, Math.Min(by, cy)) || py > Math.Max(ay, Math.Max(by, cy))) continue;
                double det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(det) < 1e-15) continue;   // 退化(或 3D 里侧着看成一条线)的三角不参与
                double w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / det;
                double w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / det;
                double w2 = 1 - w0 - w1;
                if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                double d = w0 * _d[a] + w1 * _d[b] + w2 * _d[c];
                if (nearestIsMax ? d > bestD : d < bestD) { bestD = d; best = t; }
            }
            return best;
        }
    }
}
