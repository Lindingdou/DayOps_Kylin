using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 框选判定 —— 窗口选(全含)/交叉选(相交或含)。对实体自镶嵌的每段做矩形包含/相交测试。纯逻辑、可单测。
/// </summary>
public static class SelectionBox
{
    /// <summary>实体是否被选框选中。crossing=false 窗口选(整体在框内)；true 交叉选(任一点在框内或任一段与框相交)。</summary>
    public static bool Match(SceneEntity e, double minX, double minY, double maxX, double maxY, bool crossing)
    {
        if (e is MeshEntity me) return MatchMesh(me, minX, minY, maxX, maxY, crossing);
        var o = new List<float>();
        e.Tessellate(o);
        if (o.Count == 0) return false;

        bool all = true, hit = false;
        for (int i = 0; i + 11 < o.Count; i += 12)
        {
            double x0 = o[i], y0 = o[i + 1], x1 = o[i + 6], y1 = o[i + 7];
            bool in0 = In(x0, y0, minX, minY, maxX, maxY);
            bool in1 = In(x1, y1, minX, minY, maxX, maxY);
            if (!in0 || !in1) all = false;
            if (in0 || in1) hit = true;
            else if (SegRect(x0, y0, x1, y1, minX, minY, maxX, maxY)) hit = true;   // 两端都在外但穿过框
        }
        return crossing ? hit : all;
    }

    /// <summary>
    /// 三角网框选：不依赖显示模式(着色面模式下 Tessellate 不出边线)。窗口选=包围盒全在框内；
    /// 交叉选=包围盒与框相交 且 (任一顶点在框内 或 任一边穿框)。大网先用包围盒粗排斥。
    /// </summary>
    public static bool MatchMesh(MeshEntity me, double minX, double minY, double maxX, double maxY, bool crossing)
    {
        if (me.VertexCount == 0) return false;
        var b = me.Bounds;
        bool boxInside = b.minX >= minX && b.maxX <= maxX && b.minY >= minY && b.maxY <= maxY;
        if (!crossing) return boxInside;
        if (boxInside) return true;
        if (b.maxX < minX || b.minX > maxX || b.maxY < minY || b.minY > maxY) return false;
        foreach (var (x, y, _) in me.Verts) if (In(x, y, minX, minY, maxX, maxY)) return true;
        // 逐三角的三条边(不走 me.Edges——那会为一次判定构建整张去重边表, 大网首次很慢)
        foreach (var (ia, ib, ic) in me.Tris)
        {
            if (ia >= me.Verts.Count || ib >= me.Verts.Count || ic >= me.Verts.Count) continue;
            var p = me.Verts[ia]; var q = me.Verts[ib]; var r = me.Verts[ic];
            if (SegRect(p.x, p.y, q.x, q.y, minX, minY, maxX, maxY)) return true;
            if (SegRect(q.x, q.y, r.x, r.y, minX, minY, maxX, maxY)) return true;
            if (SegRect(r.x, r.y, p.x, p.y, minX, minY, maxX, maxY)) return true;
        }
        // 框整个落在某个三角内部(无顶点/无边穿过)：框中心落在网内也算碰到
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        return me.ContainsXY(cx, cy);
    }

    /// <summary>实体是否被多边形圈选。crossing=false 全含(所有顶点在多边形内)；true 任一顶点在内。</summary>
    public static bool MatchPolygon(SceneEntity e, IReadOnlyList<(double x, double y)> poly, bool crossing)
    {
        if (poly.Count < 3) return false;
        var o = new List<float>();
        if (e is MeshEntity mm) mm.TessellateEdges(o); else e.Tessellate(o);
        if (o.Count == 0) return false;
        bool all = true, any = false;
        for (int i = 0; i + 1 < o.Count; i += 6)
        {
            bool inside = LineMath.PointInPolygon(o[i], o[i + 1], poly);
            if (inside) any = true; else all = false;
        }
        return crossing ? any : all;
    }

    /// <summary>
    /// 屏幕空间框选(3D 视图)：实体边线两端经 project 投到屏幕后做同样的 窗口/交叉 判定；
    /// 三角网走边线(不看显示模式)。project 返回 null 视为在相机后方(不计入)。
    /// </summary>
    public static bool MatchScreen(SceneEntity e, double sx0, double sy0, double sx1, double sy1, bool crossing,
        Func<double, double, double, (double sx, double sy)?> project)
    {
        var o = new List<float>();
        if (e is MeshEntity me) me.TessellateEdges(o); else e.Tessellate(o);
        if (o.Count == 0) return false;
        double minX = Math.Min(sx0, sx1), maxX = Math.Max(sx0, sx1), minY = Math.Min(sy0, sy1), maxY = Math.Max(sy0, sy1);
        bool all = true, hit = false;
        for (int i = 0; i + 11 < o.Count; i += 12)
        {
            var a = project(o[i], o[i + 1], o[i + 2]);
            var b = project(o[i + 6], o[i + 7], o[i + 8]);
            if (a == null || b == null) { all = false; continue; }
            bool in0 = In(a.Value.sx, a.Value.sy, minX, minY, maxX, maxY);
            bool in1 = In(b.Value.sx, b.Value.sy, minX, minY, maxX, maxY);
            if (!in0 || !in1) all = false;
            if (in0 || in1) hit = true;
            else if (SegRect(a.Value.sx, a.Value.sy, b.Value.sx, b.Value.sy, minX, minY, maxX, maxY)) hit = true;
            if (crossing && hit) return true;
            if (!crossing && !all) return false;
        }
        return crossing ? hit : all;
    }

    /// <summary>
    /// 屏幕空间点选(3D 视图)：像素容差内离点击最近的实体边线；三角网另按"点击落在某三角投影内"命中(多网重叠取深度最前者)。
    /// 边线命中优先于面命中(便于在面上选线)。
    /// </summary>
    public static SceneEntity? PickScreen(IEnumerable<SceneEntity> entities, double sx, double sy, double tolPx,
        Func<double, double, double, (double sx, double sy, double depth)?> project, Func<string, bool>? canSelect = null)
    {
        SceneEntity? bestEdge = null; double bestD = tolPx;
        SceneEntity? bestFace = null; double bestDepth = double.MaxValue;
        var o = new List<float>();
        foreach (var e in entities)
        {
            if (!e.Visible) continue;
            if (canSelect != null && !canSelect(e.LayerName)) continue;
            o.Clear();
            if (e is MeshEntity me)
            {
                // 面命中：先包围盒投影粗排斥, 再逐三角
                var b = me.Bounds;
                double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue; bool anyCorner = false;
                foreach (var (cx, cy, cz) in new[] { (b.minX, b.minY, b.minZ), (b.maxX, b.minY, b.minZ), (b.maxX, b.maxY, b.minZ), (b.minX, b.maxY, b.minZ), (b.minX, b.minY, b.maxZ), (b.maxX, b.minY, b.maxZ), (b.maxX, b.maxY, b.maxZ), (b.minX, b.maxY, b.maxZ) })
                {
                    var s = project(cx, cy, cz); if (s == null) continue; anyCorner = true;
                    bx0 = Math.Min(bx0, s.Value.sx); bx1 = Math.Max(bx1, s.Value.sx); by0 = Math.Min(by0, s.Value.sy); by1 = Math.Max(by1, s.Value.sy);
                }
                if (!anyCorner || sx < bx0 - tolPx || sx > bx1 + tolPx || sy < by0 - tolPx || sy > by1 + tolPx) continue;
                // 顶点只投影一次(逐三角投影会重复 3 倍, 4 万三角时首次点选明显卡顿)
                int nv = me.Verts.Count;
                var px = new double[nv]; var py = new double[nv]; var pz = new double[nv]; var okv = new bool[nv];
                for (int i = 0; i < nv; i++)
                {
                    var s = project(me.Verts[i].x, me.Verts[i].y, me.Verts[i].z + me.Elevation);
                    if (s == null) continue;
                    px[i] = s.Value.sx; py[i] = s.Value.sy; pz[i] = s.Value.depth; okv[i] = true;
                }
                foreach (var (a, bb, c) in me.Tris)
                {
                    if (a >= nv || bb >= nv || c >= nv || !okv[a] || !okv[bb] || !okv[c]) continue;
                    double ax = px[a], ay = py[a], bx = px[bb], by = py[bb], cx2 = px[c], cy2 = py[c];
                    // 三角屏幕包围盒快速排斥
                    if (sx < Math.Min(ax, Math.Min(bx, cx2)) || sx > Math.Max(ax, Math.Max(bx, cx2)) ||
                        sy < Math.Min(ay, Math.Min(by, cy2)) || sy > Math.Max(ay, Math.Max(by, cy2))) continue;
                    double d = (by - cy2) * (ax - cx2) + (cx2 - bx) * (ay - cy2);
                    if (Math.Abs(d) < 1e-12) continue;
                    double w0 = ((by - cy2) * (sx - cx2) + (cx2 - bx) * (sy - cy2)) / d;
                    double w1 = ((cy2 - ay) * (sx - cx2) + (ax - cx2) * (sy - cy2)) / d;
                    double w2 = 1 - w0 - w1;
                    if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                    double depth = w0 * pz[a] + w1 * pz[bb] + w2 * pz[c];
                    if (depth < bestDepth) { bestDepth = depth; bestFace = me; }
                }
                if (MeshEntity.RenderMode != MeshEntity.DisplayMode.Shaded) me.TessellateEdges(o);   // 有线框显示时边线也可点中
            }
            else e.Tessellate(o);
            for (int i = 0; i + 11 < o.Count; i += 12)
            {
                var a = project(o[i], o[i + 1], o[i + 2]); var b = project(o[i + 6], o[i + 7], o[i + 8]);
                if (a == null || b == null) continue;
                double dd = SegDist(sx, sy, a.Value.sx, a.Value.sy, b.Value.sx, b.Value.sy);
                if (dd <= bestD) { bestD = dd; bestEdge = e; }
            }
        }
        return bestEdge ?? bestFace;
    }

    private static double SegDist(double px, double py, double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-18 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / l2, 0, 1);
        double cx = x0 + t * dx, cy = y0 + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static bool In(double x, double y, double minX, double minY, double maxX, double maxY)
        => x >= minX && x <= maxX && y >= minY && y <= maxY;

    private static bool SegRect(double x0, double y0, double x1, double y1, double minX, double minY, double maxX, double maxY)
        => LineMath.SegmentsIntersect(x0, y0, x1, y1, minX, minY, maxX, minY)   // 下
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, maxX, minY, maxX, maxY)   // 右
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, maxX, maxY, minX, maxY)   // 上
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, minX, maxY, minX, minY);  // 左
}
