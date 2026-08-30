namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 对象捕捉 —— 在导入几何的顶点里找离光标最近、且在容差内的点。
/// verts 为交错 P3_C3（每 6 float 一个顶点），只用位置。纯逻辑，可单测。
/// </summary>
public static class SnapPoints
{
    /// <summary>返回容差 tol 内离 (cx,cy) 最近的顶点 (x,y)；无则 null。</summary>
    public static (double x, double y)? FindNearest(float[] verts, double cx, double cy, double tol)
    {
        if (verts == null || verts.Length < 6 || tol <= 0) return null;
        double best = tol * tol;
        (double x, double y)? found = null;
        for (int i = 0; i + 5 < verts.Length; i += 6)
        {
            double dx = verts[i] - cx, dy = verts[i + 1] - cy;
            double d2 = dx * dx + dy * dy;
            if (d2 <= best) { best = d2; found = (verts[i], verts[i + 1]); }
        }
        return found;
    }
}
