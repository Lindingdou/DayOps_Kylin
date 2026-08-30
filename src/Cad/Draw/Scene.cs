using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 托管绘制场景 —— Home 绘制/编辑命令的内存实体模型（内核到位前的托管重实现）。
/// 每个实体自我镶嵌为 P3_C3 线段；Scene 汇总为一段几何交 GL 渲染。纯逻辑，可单测。
/// </summary>
public abstract class SceneEntity
{
    public float Cr = 0.86f, Cg = 0.9f, Cb = 0.6f;   // 绘制实体默认色（浅黄绿，区别于导入）
    public string LayerName = "0";                    // 所属图层

    /// <summary>把自身镶嵌为线段（交错 P3_C3）追加到 o。</summary>
    public abstract void Tessellate(List<float> o);

    protected void Seg(List<float> o, double x0, double y0, double x1, double y1)
    {
        o.Add((float)x0); o.Add((float)y0); o.Add(0); o.Add(Cr); o.Add(Cg); o.Add(Cb);
        o.Add((float)x1); o.Add((float)y1); o.Add(0); o.Add(Cr); o.Add(Cg); o.Add(Cb);
    }

    /// <summary>点 (px,py) 到本实体几何的最近距离（拾取用；对自身镶嵌的每段求点到线段距离取最小）。</summary>
    public double DistanceTo(double px, double py)
    {
        var o = new List<float>();
        Tessellate(o);
        double best = double.MaxValue;
        for (int i = 0; i + 11 < o.Count; i += 12)
        {
            double d = SegDist(px, py, o[i], o[i + 1], o[i + 6], o[i + 7]);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>点到线段距离。</summary>
    protected static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    /// <summary>应用仿射变换，返回变换后的新实体（移动/旋转/缩放/镜像）。</summary>
    public abstract SceneEntity Apply(Affine2 m);

    /// <summary>偏移：向点击 (px,py) 一侧平行偏移，返回新实体；不支持返回 null。</summary>
    public virtual SceneEntity? Offset(double px, double py) => null;

    /// <summary>分解：拆成更基本的实体（矩形/多段线 → 直线）；不可分解返回 null。</summary>
    public virtual List<SceneEntity>? Explode() => null;

    /// <summary>把本实体颜色复制给 e 并返回（变换保留颜色）。</summary>
    protected T Colored<T>(T e) where T : SceneEntity { e.Cr = Cr; e.Cg = Cg; e.Cb = Cb; return e; }
}

/// <summary>2D 仿射变换：x' = A·x + C·y + E，y' = B·x + D·y + F。</summary>
public readonly struct Affine2
{
    public readonly double A, B, C, D, E, F;
    public Affine2(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }

    public (double x, double y) Map(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    /// <summary>尺度幅度（√|det|）：均匀缩放 s → s；旋转/镜像 → 1。</summary>
    public double ScaleMag => Math.Sqrt(Math.Abs(A * D - B * C));
    /// <summary>是否保持轴对齐（无旋转/错切）。</summary>
    public bool IsAxisAligned => Math.Abs(B) < 1e-9 && Math.Abs(C) < 1e-9;

    public static Affine2 Translate(double dx, double dy) => new(1, 0, 0, 1, dx, dy);
    public static Affine2 Scale(double s, double cx, double cy) => new(s, 0, 0, s, cx - s * cx, cy - s * cy);

    public static Affine2 Rotate(double ang, double cx, double cy)
    {
        double c = Math.Cos(ang), s = Math.Sin(ang);
        return new(c, s, -s, c, cx - c * cx + s * cy, cy - s * cx - c * cy);
    }

    public static Affine2 MirrorLine(double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Translate(0, 0);
        double a = (dx * dx - dy * dy) / len2, b = 2 * dx * dy / len2;
        return new(a, b, b, -a, x0 - a * x0 - b * y0, y0 - b * x0 + a * y0);
    }
}

public sealed class LineEntity : SceneEntity
{
    public double X0, Y0, X1, Y1;
    public override void Tessellate(List<float> o) => Seg(o, X0, Y0, X1, Y1);
    public override SceneEntity Apply(Affine2 m)
    {
        var (x0, y0) = m.Map(X0, Y0); var (x1, y1) = m.Map(X1, Y1);
        return Colored(new LineEntity { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
    }
    public override SceneEntity? Offset(double px, double py)
    {
        double dx = X1 - X0, dy = Y1 - Y0, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return null;
        double nx = -dy / len, ny = dx / len;               // 单位法线
        double d = (px - X0) * nx + (py - Y0) * ny;         // 点击到直线的带符号法向距离
        return Colored(new LineEntity { X0 = X0 + nx * d, Y0 = Y0 + ny * d, X1 = X1 + nx * d, Y1 = Y1 + ny * d });
    }
}

public sealed class CircleEntity : SceneEntity
{
    public double Cx, Cy, Radius;
    public int Segments = 64;
    public override void Tessellate(List<float> o)
    {
        double px = Cx + Radius, py = Cy;
        for (int i = 1; i <= Segments; i++)
        {
            double t = 2 * Math.PI * i / Segments;
            double x = Cx + Radius * Math.Cos(t), y = Cy + Radius * Math.Sin(t);
            Seg(o, px, py, x, y); px = x; py = y;
        }
    }
    public override SceneEntity Apply(Affine2 m)
    {
        var (cx, cy) = m.Map(Cx, Cy);
        return Colored(new CircleEntity { Cx = cx, Cy = cy, Radius = Radius * m.ScaleMag, Segments = Segments });
    }
    public override SceneEntity? Offset(double px, double py)
    {
        double r = Math.Sqrt((px - Cx) * (px - Cx) + (py - Cy) * (py - Cy));
        return r < 1e-6 ? null : Colored(new CircleEntity { Cx = Cx, Cy = Cy, Radius = r, Segments = Segments });
    }
}

public sealed class RectEntity : SceneEntity
{
    public double X0, Y0, X1, Y1;
    public override void Tessellate(List<float> o)
    {
        Seg(o, X0, Y0, X1, Y0); Seg(o, X1, Y0, X1, Y1);
        Seg(o, X1, Y1, X0, Y1); Seg(o, X0, Y1, X0, Y0);
    }
    public override SceneEntity Apply(Affine2 m)
    {
        if (m.IsAxisAligned)
        {
            var (x0, y0) = m.Map(X0, Y0); var (x1, y1) = m.Map(X1, Y1);
            return Colored(new RectEntity { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
        }
        // 旋转/镜像后不再轴对齐 → 转闭合多段线（4 角）
        var pl = new PolylineEntity { Closed = true };
        pl.Points.Add(m.Map(X0, Y0)); pl.Points.Add(m.Map(X1, Y0));
        pl.Points.Add(m.Map(X1, Y1)); pl.Points.Add(m.Map(X0, Y1));
        return Colored(pl);
    }
    public override SceneEntity? Offset(double px, double py)
    {
        double minX = Math.Min(X0, X1), maxX = Math.Max(X0, X1), minY = Math.Min(Y0, Y1), maxY = Math.Max(Y0, Y1);
        bool inside = px > minX && px < maxX && py > minY && py < maxY;
        double d = DistanceTo(px, py);
        double off = inside ? -d : d;
        return Colored(new RectEntity { X0 = minX - off, Y0 = minY - off, X1 = maxX + off, Y1 = maxY + off });
    }
    public override List<SceneEntity>? Explode() => new()
    {
        Colored(new LineEntity { X0 = X0, Y0 = Y0, X1 = X1, Y1 = Y0 }),
        Colored(new LineEntity { X0 = X1, Y0 = Y0, X1 = X1, Y1 = Y1 }),
        Colored(new LineEntity { X0 = X1, Y0 = Y1, X1 = X0, Y1 = Y1 }),
        Colored(new LineEntity { X0 = X0, Y0 = Y1, X1 = X0, Y1 = Y0 })
    };
}

public sealed class PointEntity : SceneEntity
{
    public double X, Y;
    public double Size = 0.5;
    public override void Tessellate(List<float> o)
    {
        Seg(o, X - Size, Y, X + Size, Y);
        Seg(o, X, Y - Size, X, Y + Size);
    }
    public override SceneEntity Apply(Affine2 m)
    {
        var (x, y) = m.Map(X, Y);
        return Colored(new PointEntity { X = x, Y = y, Size = Size });
    }
}

public sealed class ArcEntity : SceneEntity
{
    public double X1, Y1, X2, Y2, X3, Y3;   // 起点 / 圆弧上一点 / 端点
    public int Segments = 48;
    public override void Tessellate(List<float> o)
    {
        var cc = ArcMath.Circumcircle(X1, Y1, X2, Y2, X3, Y3);
        if (cc == null) { Seg(o, X1, Y1, X3, Y3); return; }   // 三点共线 → 退化为直线
        var (cx, cy, r) = cc.Value;
        double a1 = Math.Atan2(Y1 - cy, X1 - cx);
        double am = Math.Atan2(Y2 - cy, X2 - cx);
        double a3 = Math.Atan2(Y3 - cy, X3 - cx);
        double sweep = Norm(a3 - a1);
        double mid = Norm(am - a1);
        double total = mid <= sweep ? sweep : sweep - 2 * Math.PI;   // 经中点的扫向
        double px = cx + r * Math.Cos(a1), py = cy + r * Math.Sin(a1);
        for (int i = 1; i <= Segments; i++)
        {
            double t = a1 + total * i / Segments;
            double x = cx + r * Math.Cos(t), y = cy + r * Math.Sin(t);
            Seg(o, px, py, x, y); px = x; py = y;
        }
    }
    private static double Norm(double a) { while (a < 0) a += 2 * Math.PI; while (a >= 2 * Math.PI) a -= 2 * Math.PI; return a; }
    public override SceneEntity Apply(Affine2 m)
    {
        var (x1, y1) = m.Map(X1, Y1); var (x2, y2) = m.Map(X2, Y2); var (x3, y3) = m.Map(X3, Y3);
        return Colored(new ArcEntity { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X3 = x3, Y3 = y3, Segments = Segments });
    }
}

public sealed class PolylineEntity : SceneEntity
{
    public List<(double x, double y)> Points = new();
    public bool Closed;
    public override void Tessellate(List<float> o)
    {
        for (int i = 0; i + 1 < Points.Count; i++)
            Seg(o, Points[i].x, Points[i].y, Points[i + 1].x, Points[i + 1].y);
        if (Closed && Points.Count > 1)
            Seg(o, Points[^1].x, Points[^1].y, Points[0].x, Points[0].y);
    }
    public override SceneEntity Apply(Affine2 m)
    {
        var pl = new PolylineEntity { Closed = Closed };
        foreach (var p in Points) pl.Points.Add(m.Map(p.x, p.y));
        return Colored(pl);
    }
    public override List<SceneEntity>? Explode()
    {
        var list = new List<SceneEntity>();
        for (int i = 0; i + 1 < Points.Count; i++)
            list.Add(Colored(new LineEntity { X0 = Points[i].x, Y0 = Points[i].y, X1 = Points[i + 1].x, Y1 = Points[i + 1].y }));
        if (Closed && Points.Count > 1)
            list.Add(Colored(new LineEntity { X0 = Points[^1].x, Y0 = Points[^1].y, X1 = Points[0].x, Y1 = Points[0].y }));
        return list.Count > 0 ? list : null;
    }
}

/// <summary>正多边形：中心 + 外接半径 + 边数 + 首顶点方向角。</summary>
public sealed class PolygonEntity : SceneEntity
{
    public double Cx, Cy, Radius, Rotation;   // Rotation = 首顶点相对 +X 的角
    public int Sides = 6;

    private IEnumerable<(double x, double y)> Vertices()
    {
        for (int i = 0; i < Sides; i++)
        {
            double a = Rotation + 2 * Math.PI * i / Sides;
            yield return (Cx + Radius * Math.Cos(a), Cy + Radius * Math.Sin(a));
        }
    }
    public override void Tessellate(List<float> o)
    {
        if (Sides < 3 || Radius < 1e-9) return;
        double px = Cx + Radius * Math.Cos(Rotation), py = Cy + Radius * Math.Sin(Rotation);
        for (int i = 1; i <= Sides; i++)
        {
            double a = Rotation + 2 * Math.PI * i / Sides;   // i=Sides → 回到首顶点，闭合
            double x = Cx + Radius * Math.Cos(a), y = Cy + Radius * Math.Sin(a);
            Seg(o, px, py, x, y); px = x; py = y;
        }
    }
    public override SceneEntity Apply(Affine2 m)
    {
        if (m.A * m.D - m.B * m.C > 0)   // 平移/旋转/缩放 → 保持正多边形
        {
            var (cx, cy) = m.Map(Cx, Cy);
            return Colored(new PolygonEntity { Cx = cx, Cy = cy, Radius = Radius * m.ScaleMag, Sides = Sides, Rotation = Rotation + Math.Atan2(m.B, m.A) });
        }
        var pl = new PolylineEntity { Closed = true };   // 镜像 → 闭合多段线
        foreach (var v in Vertices()) pl.Points.Add(m.Map(v.x, v.y));
        return Colored(pl);
    }
    public override List<SceneEntity>? Explode()
    {
        if (Sides < 3) return null;
        var vs = new List<(double x, double y)>(Vertices());
        var list = new List<SceneEntity>();
        for (int i = 0; i < vs.Count; i++)
        {
            var a = vs[i]; var b = vs[(i + 1) % vs.Count];
            list.Add(Colored(new LineEntity { X0 = a.x, Y0 = a.y, X1 = b.x, Y1 = b.y }));
        }
        return list;
    }
}

/// <summary>圆弧几何辅助（三点外接圆），供 ArcEntity/ArcTool，可单测。</summary>
public static class ArcMath
{
    public static (double cx, double cy, double r)? Circumcircle(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        double d = 2 * (x1 * (y2 - y3) + x2 * (y3 - y1) + x3 * (y1 - y2));
        if (Math.Abs(d) < 1e-12) return null;   // 共线
        double s1 = x1 * x1 + y1 * y1, s2 = x2 * x2 + y2 * y2, s3 = x3 * x3 + y3 * y3;
        double ux = (s1 * (y2 - y3) + s2 * (y3 - y1) + s3 * (y1 - y2)) / d;
        double uy = (s1 * (x3 - x2) + s2 * (x1 - x3) + s3 * (x2 - x1)) / d;
        double r = Math.Sqrt((x1 - ux) * (x1 - ux) + (y1 - uy) * (y1 - uy));
        return (ux, uy, r);
    }
}

/// <summary>直线相交（无限延长），供修剪/延伸。纯逻辑、可单测。</summary>
public static class LineMath
{
    public static (double x, double y)? IntersectInfinite(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1x = ax1 - ax0, d1y = ay1 - ay0, d2x = bx1 - bx0, d2y = by1 - by0;
        double denom = d1x * d2y - d1y * d2x;
        if (Math.Abs(denom) < 1e-12) return null;   // 平行
        double t = ((bx0 - ax0) * d2y - (by0 - ay0) * d2x) / denom;
        return (ax0 + t * d1x, ay0 + t * d1y);
    }
}

public sealed class Scene
{
    public List<SceneEntity> Entities { get; } = new();
    public int Count => Entities.Count;

    public void Add(SceneEntity e) => Entities.Add(e);
    public void Clear() => Entities.Clear();
    public bool RemoveLast()
    {
        if (Entities.Count == 0) return false;
        Entities.RemoveAt(Entities.Count - 1);
        return true;
    }

    /// <summary>拾取：容差 tol 内离 (x,y) 最近的实体；无则 null。</summary>
    public SceneEntity? Pick(double x, double y, double tol)
    {
        SceneEntity? best = null;
        double bestD = tol;
        foreach (var e in Entities)
        {
            double d = e.DistanceTo(x, y);
            if (d <= bestD) { bestD = d; best = e; }
        }
        return best;
    }

    public bool Remove(SceneEntity e) => Entities.Remove(e);

    public void Replace(SceneEntity oldE, SceneEntity newE)
    {
        int i = Entities.IndexOf(oldE);
        if (i >= 0) Entities[i] = newE; else Entities.Add(newE);
    }

    public float[] BuildGeometry()
    {
        var o = new List<float>();
        foreach (var e in Entities) e.Tessellate(o);
        return o.ToArray();
    }
}
