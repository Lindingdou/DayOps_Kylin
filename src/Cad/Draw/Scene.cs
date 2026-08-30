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

    /// <summary>把自身镶嵌为线段（交错 P3_C3）追加到 o。</summary>
    public abstract void Tessellate(List<float> o);

    protected void Seg(List<float> o, double x0, double y0, double x1, double y1)
    {
        o.Add((float)x0); o.Add((float)y0); o.Add(0); o.Add(Cr); o.Add(Cg); o.Add(Cb);
        o.Add((float)x1); o.Add((float)y1); o.Add(0); o.Add(Cr); o.Add(Cg); o.Add(Cb);
    }
}

public sealed class LineEntity : SceneEntity
{
    public double X0, Y0, X1, Y1;
    public override void Tessellate(List<float> o) => Seg(o, X0, Y0, X1, Y1);
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
}

public sealed class RectEntity : SceneEntity
{
    public double X0, Y0, X1, Y1;
    public override void Tessellate(List<float> o)
    {
        Seg(o, X0, Y0, X1, Y0); Seg(o, X1, Y0, X1, Y1);
        Seg(o, X1, Y1, X0, Y1); Seg(o, X0, Y1, X0, Y0);
    }
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

    public float[] BuildGeometry()
    {
        var o = new List<float>();
        foreach (var e in Entities) e.Tessellate(o);
        return o.ToArray();
    }
}
