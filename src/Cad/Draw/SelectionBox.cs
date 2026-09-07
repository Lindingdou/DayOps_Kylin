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
        foreach (var (i, j) in me.Edges)
        {
            var p = me.Verts[i]; var q = me.Verts[j];
            if (SegRect(p.x, p.y, q.x, q.y, minX, minY, maxX, maxY)) return true;
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

    private static bool In(double x, double y, double minX, double minY, double maxX, double maxY)
        => x >= minX && x <= maxX && y >= minY && y <= maxY;

    private static bool SegRect(double x0, double y0, double x1, double y1, double minX, double minY, double maxX, double maxY)
        => LineMath.SegmentsIntersect(x0, y0, x1, y1, minX, minY, maxX, minY)   // 下
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, maxX, minY, maxX, maxY)   // 右
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, maxX, maxY, minX, maxY)   // 上
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, minX, maxY, minX, minY);  // 左
}
