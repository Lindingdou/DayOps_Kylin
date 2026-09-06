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

    /// <summary>返回去掉与 (ex,ey) 重合(容差 eps)顶点后的副本 —— 夹点拖拽时排除"被拖的那一个点"，
    /// 否则会捕捉到自己原位置导致鼠标粘住(原版 SnapContext.excludePoint)；同实体其他顶点仍可捕捉。</summary>
    public static float[] Exclude(float[] verts, double ex, double ey, double eps = 1e-6)
    {
        if (verts == null || verts.Length < 6) return verts ?? System.Array.Empty<float>();
        var o = new System.Collections.Generic.List<float>(verts.Length);
        for (int i = 0; i + 5 < verts.Length; i += 6)
        {
            double dx = verts[i] - ex, dy = verts[i + 1] - ey;
            if (dx * dx + dy * dy <= eps * eps) continue;
            for (int k = 0; k < 6; k++) o.Add(verts[i + k]);
        }
        return o.ToArray();
    }
}
