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
    public bool Visible = true;                        // 逐实体隐藏(隐藏对象/结束隐藏)；false=不上屏且不可拾取
    public double[]? Dash;                             // 线型虚线样式(画/空,世界单位); null=实线
    public short LineWeight = -1;                      // 线宽(DXF LineWeightType 值: -1=ByLayer, -3=Default, 0..211=0.01mm); 当前不渲染变宽线, 但 round-trip 保值供下游绘图
    public short Transparency = -1;                    // 透明度(-1=随层 ByLayer, 0..90=百分比); 当前渲染不透明(P3_C3 无 alpha), 但 round-trip 保值供下游/重导出
    public double Elevation;                            // 实体标高(Z, 世界单位): 平面实体默认 0; 等高线/台阶线等按此抬高显示三维。Points 仍存 XY, 此值统一供 z。

    /// <summary>把自身镶嵌为线段（交错 P3_C3）追加到 o。</summary>
    public abstract void Tessellate(List<float> o);

    /// <summary>把自身镶嵌为着色三角面（交错 P3_C3, 每三角 3 顶点）追加到 o。默认无面(仅三角网等面实体覆盖)。</summary>
    public virtual void TessellateFaces(List<float> o) { }

    protected void Seg(List<float> o, double x0, double y0, double x1, double y1)
    {
        float z = (float)Elevation;
        o.Add((float)x0); o.Add((float)y0); o.Add(z); o.Add(Cr); o.Add(Cg); o.Add(Cb);
        o.Add((float)x1); o.Add((float)y1); o.Add(z); o.Add(Cr); o.Add(Cg); o.Add(Cb);
    }

    /// <summary>逐端点真高程的线段(三角网边线用, 不走线型)。</summary>
    protected void Seg3(List<float> o, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        o.Add((float)x0); o.Add((float)y0); o.Add((float)z0); o.Add(Cr); o.Add(Cg); o.Add(Cb);
        o.Add((float)x1); o.Add((float)y1); o.Add((float)z1); o.Add(Cr); o.Add(Cg); o.Add(Cb);
    }

    /// <summary>按线型 Dash 把一段切成虚线子段镶嵌(实线时=单段)。</summary>
    protected void SegD(List<float> o, double x0, double y0, double x1, double y1)
    {
        if (Dash == null || Dash.Length == 0) { Seg(o, x0, y0, x1, y1); return; }
        foreach (var (sx, sy, ex, ey) in DashPattern.Dashes(x0, y0, x1, y1, Dash)) Seg(o, sx, sy, ex, ey);
    }

    /// <summary>点 (px,py) 到本实体几何的最近距离（拾取用；对自身镶嵌的每段求点到线段距离取最小）。</summary>
    public virtual double DistanceTo(double px, double py)
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

    /// <summary>打断：移除两投影点之间的一段，返回剩余实体（可能为空=整体删除）；不支持返回 null。</summary>
    public virtual List<SceneEntity>? Break(double x1, double y1, double x2, double y2) => null;

    /// <summary>夹点位置（端点/中点/圆心/象限/顶点…）；无夹点返回空。</summary>
    public virtual List<(double x, double y)> Grips() => new();

    /// <summary>把第 i 个夹点移到 (nx,ny)，返回修改后的新实体；不支持返回 null。</summary>
    public virtual SceneEntity? MoveGrip(int i, double nx, double ny) => null;

    /// <summary>把本实体颜色复制给 e 并返回（变换保留颜色）。</summary>
    /// <summary>把源实体的全部非几何样式(色/线型/线宽/可见/图层)拷到本实体。
    /// 单一枢纽: 变换深拷(Colored)与各处手工构造新实体(简化/平滑/裁剪)都经此, 加样式字段只改这一处。</summary>
    public void CopyStyleFrom(SceneEntity s)
    {
        Cr = s.Cr; Cg = s.Cg; Cb = s.Cb; Dash = s.Dash; LineWeight = s.LineWeight; Transparency = s.Transparency; Visible = s.Visible; LayerName = s.LayerName; Elevation = s.Elevation;
    }

    // 变换/克隆深拷: 保留全部非几何属性。图层保留使 移动/复制/镜像/剪贴板 不改层;
    // DXF 块展开在 Apply 后另行覆盖层名, 故不受影响。
    protected T Colored<T>(T e) where T : SceneEntity { e.CopyStyleFrom(this); return e; }

    /// <summary>闭环(矩形/正多边形)打断：投两点到全部边(含闭合边)，移除两点间一段，返回绕另一侧的开口多段线；两点重合返 null。</summary>
    protected static PolylineEntity? BreakClosedLoop(IReadOnlyList<(double x, double y)> vs, double x1, double y1, double x2, double y2)
    {
        int n = vs.Count;
        if (n < 3) return null;
        (int seg, double t, double cum) Proj(double px, double py)
        {
            int bs = 0; double bt = 0, bd = double.MaxValue, best = 0, acc = 0;
            for (int i = 0; i < n; i++)   // 含闭合边 vs[n-1]->vs[0]
            {
                var a = vs[i]; var b = vs[(i + 1) % n];
                double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - a.x) * dx + (py - a.y) * dy) / len2, 0, 1);
                double cxp = a.x + t * dx, cyp = a.y + t * dy, d = (px - cxp) * (px - cxp) + (py - cyp) * (py - cyp);
                if (d < bd) { bd = d; bs = i; bt = t; best = acc + t * len; }
                acc += len;
            }
            return (bs, bt, best);
        }
        var p1 = Proj(x1, y1); var p2 = Proj(x2, y2);
        if (p1.cum > p2.cum) (p1, p2) = (p2, p1);
        if (Math.Abs(p1.cum - p2.cum) < 1e-9) return null;   // 两点重合
        (double x, double y) At(int seg, double t) { var a = vs[seg]; var b = vs[(seg + 1) % n]; return (a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t); }
        var b1 = At(p1.seg, p1.t); var b2 = At(p2.seg, p2.t);
        // 保留段 = [p1..p2] 的补：从 b2 起，沿 vs 绕行(mod n)回到 b1
        var c = new PolylineEntity(); c.Points.Add(b2);
        int idx = (p2.seg + 1) % n;
        while (true) { c.Points.Add(vs[idx]); if (idx == p1.seg) break; idx = (idx + 1) % n; }
        c.Points.Add(b1);
        return c.Points.Count >= 2 ? c : null;
    }
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

    /// <summary>复合变换：返回 outer∘inner（先 inner 再 outer），供块引用嵌套展开。</summary>
    public static Affine2 Multiply(Affine2 o, Affine2 i) => new(
        o.A * i.A + o.C * i.B, o.B * i.A + o.D * i.B,
        o.A * i.C + o.C * i.D, o.B * i.C + o.D * i.D,
        o.A * i.E + o.C * i.F + o.E, o.B * i.E + o.D * i.F + o.F);

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
    public override void Tessellate(List<float> o) => SegD(o, X0, Y0, X1, Y1);
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
    public override List<SceneEntity>? Break(double x1, double y1, double x2, double y2)
    {
        double dx = X1 - X0, dy = Y1 - Y0, len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return null;
        double t1 = ((x1 - X0) * dx + (y1 - Y0) * dy) / len2;   // 两点在直线上的参数
        double t2 = ((x2 - X0) * dx + (y2 - Y0) * dy) / len2;
        if (t1 > t2) (t1, t2) = (t2, t1);
        t1 = Math.Clamp(t1, 0, 1); t2 = Math.Clamp(t2, 0, 1);
        var res = new List<SceneEntity>();
        if (t1 > 1e-6)                                          // 保留前段 [0, t1]
            res.Add(Colored(new LineEntity { X0 = X0, Y0 = Y0, X1 = X0 + dx * t1, Y1 = Y0 + dy * t1 }));
        if (t2 < 1 - 1e-6)                                      // 保留后段 [t2, 1]
            res.Add(Colored(new LineEntity { X0 = X0 + dx * t2, Y0 = Y0 + dy * t2, X1 = X1, Y1 = Y1 }));
        return res;   // 非 null=支持打断；空=整体被移除
    }
    public override List<(double x, double y)> Grips() =>
        new() { (X0, Y0), ((X0 + X1) / 2, (Y0 + Y1) / 2), (X1, Y1) };   // 两端点 + 中点
    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        var e = Colored(new LineEntity { X0 = X0, Y0 = Y0, X1 = X1, Y1 = Y1 });
        if (i == 0) { e.X0 = nx; e.Y0 = ny; }
        else if (i == 2) { e.X1 = nx; e.Y1 = ny; }
        else { double mx = nx - (X0 + X1) / 2, my = ny - (Y0 + Y1) / 2; e.X0 = X0 + mx; e.Y0 = Y0 + my; e.X1 = X1 + mx; e.Y1 = Y1 + my; }
        return e;
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
    public override List<(double x, double y)> Grips() =>
        new() { (Cx, Cy), (Cx + Radius, Cy), (Cx, Cy + Radius), (Cx - Radius, Cy), (Cx, Cy - Radius) };  // 心 + 4 象限
    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        if (i == 0) return Colored(new CircleEntity { Cx = nx, Cy = ny, Radius = Radius, Segments = Segments });  // 移心
        double r = Math.Sqrt((nx - Cx) * (nx - Cx) + (ny - Cy) * (ny - Cy));   // 象限 → 改半径
        return Colored(new CircleEntity { Cx = Cx, Cy = Cy, Radius = r, Segments = Segments });
    }
    public override List<SceneEntity>? Break(double x1, double y1, double x2, double y2)   // 圆→弧：移除 CCW 第一点→第二点段，保留补段
    {
        if (Radius < 1e-9) return null;
        double a1 = Math.Atan2(y1 - Cy, x1 - Cx), a2 = Math.Atan2(y2 - Cy, x2 - Cx);
        double sweep = a1 - a2;                                    // 保留段 = CCW 第二点→第一点
        while (sweep <= 0) sweep += 2 * Math.PI;
        while (sweep >= 2 * Math.PI) sweep -= 2 * Math.PI;
        if (sweep < 1e-6 || sweep > 2 * Math.PI - 1e-6) return null;   // 两点重合，无法打断
        double sa = a2, ma = a2 + sweep / 2, ea = a1;              // 起/中/端角(中点必在保留段上)
        var arc = new ArcEntity
        {
            X1 = Cx + Radius * Math.Cos(sa), Y1 = Cy + Radius * Math.Sin(sa),
            X2 = Cx + Radius * Math.Cos(ma), Y2 = Cy + Radius * Math.Sin(ma),
            X3 = Cx + Radius * Math.Cos(ea), Y3 = Cy + Radius * Math.Sin(ea), Segments = Segments,
        };
        return new List<SceneEntity> { Colored(arc) };
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
    public override List<(double x, double y)> Grips() =>
        new() { (X0, Y0), (X1, Y0), (X1, Y1), (X0, Y1) };   // 4 角
    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        var e = Colored(new RectEntity { X0 = X0, Y0 = Y0, X1 = X1, Y1 = Y1 });
        switch (i)   // 拖角改相邻两边（对角固定）
        {
            case 0: e.X0 = nx; e.Y0 = ny; break;
            case 1: e.X1 = nx; e.Y0 = ny; break;
            case 2: e.X1 = nx; e.Y1 = ny; break;
            case 3: e.X0 = nx; e.Y1 = ny; break;
        }
        return e;
    }
    public override List<SceneEntity>? Break(double x1, double y1, double x2, double y2)   // 矩形→开口多段线
    {
        var vs = new[] { (X0, Y0), (X1, Y0), (X1, Y1), (X0, Y1) };
        var pl = BreakClosedLoop(vs, x1, y1, x2, y2);
        return pl != null ? new List<SceneEntity> { Colored(pl) } : null;
    }
}

public sealed class PointEntity : SceneEntity
{
    public double X, Y;
    public double Size = 0.5;
    public int Style = 2;   // 点样式(PDMODE): 低5位基本符号 0=点/2=加号/3=叉/4=竖线; +32=外接圆, +64=外接方。忠实原"修改点样式"
    public override void Tessellate(List<float> o)
    {
        double s = Size;
        switch (Style & 31)
        {
            case 0: Seg(o, X - s * 0.15, Y, X + s * 0.15, Y); Seg(o, X, Y - s * 0.15, X, Y + s * 0.15); break;   // 点(小十字)
            case 1: break;                                                                                       // 无标记
            case 3: Seg(o, X - s, Y - s, X + s, Y + s); Seg(o, X - s, Y + s, X + s, Y - s); break;               // ×
            case 4: Seg(o, X, Y, X, Y + s); break;                                                               // 竖线
            default: Seg(o, X - s, Y, X + s, Y); Seg(o, X, Y - s, X, Y + s); break;                              // +（2 及默认）
        }
        if ((Style & 32) != 0)   // 外接圆
        {
            const int n = 24; double px = X + s, py = Y;
            for (int i = 1; i <= n; i++) { double a = 2 * Math.PI * i / n; double nx = X + s * Math.Cos(a), ny = Y + s * Math.Sin(a); Seg(o, px, py, nx, ny); px = nx; py = ny; }
        }
        if ((Style & 64) != 0)   // 外接方
        { Seg(o, X - s, Y - s, X + s, Y - s); Seg(o, X + s, Y - s, X + s, Y + s); Seg(o, X + s, Y + s, X - s, Y + s); Seg(o, X - s, Y + s, X - s, Y - s); }
    }
    public override SceneEntity Apply(Affine2 m)
    {
        var (x, y) = m.Map(X, Y);
        return Colored(new PointEntity { X = x, Y = y, Size = Size, Style = Style });
    }
    public override List<(double x, double y)> Grips() => new() { (X, Y) };
    public override SceneEntity? MoveGrip(int i, double nx, double ny) => Colored(new PointEntity { X = nx, Y = ny, Size = Size, Style = Style });
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
    public override List<(double x, double y)> Grips() => new() { (X1, Y1), (X2, Y2), (X3, Y3) };   // 起/中/端
    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        var e = Colored(new ArcEntity { X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2, X3 = X3, Y3 = Y3, Segments = Segments });
        switch (i) { case 0: e.X1 = nx; e.Y1 = ny; break; case 1: e.X2 = nx; e.Y2 = ny; break; case 2: e.X3 = nx; e.Y3 = ny; break; }
        return e;
    }
    public override SceneEntity? Offset(double px, double py)   // 同心弧：过点偏移(新半径=心到点距)
    {
        var cc = ArcMath.Circumcircle(X1, Y1, X2, Y2, X3, Y3);
        if (cc == null) return null;
        var (cx, cy, r) = cc.Value;
        double r2 = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        if (r2 < 1e-6 || r < 1e-9) return null;
        double s = r2 / r;
        return Colored(new ArcEntity
        {
            X1 = cx + (X1 - cx) * s, Y1 = cy + (Y1 - cy) * s,
            X2 = cx + (X2 - cx) * s, Y2 = cy + (Y2 - cy) * s,
            X3 = cx + (X3 - cx) * s, Y3 = cy + (Y3 - cy) * s, Segments = Segments
        });
    }
    public override List<SceneEntity>? Break(double x1, double y1, double x2, double y2)   // 断成两段弧
    {
        var cc = ArcMath.Circumcircle(X1, Y1, X2, Y2, X3, Y3);
        if (cc == null) return null;
        var (cx, cy, r) = cc.Value;
        double a1 = Math.Atan2(Y1 - cy, X1 - cx), am = Math.Atan2(Y2 - cy, X2 - cx), a3 = Math.Atan2(Y3 - cy, X3 - cx);
        double sweep = Norm(a3 - a1), mid = Norm(am - a1);
        double total = mid <= sweep ? sweep : sweep - 2 * Math.PI;   // 经中点的扫向(可负)
        double AngOf(double px, double py) { double t = Norm(Math.Atan2(py - cy, px - cx) - a1); if (total < 0 && t > 0) t -= 2 * Math.PI; return t; }  // 相对起点的参数[与 total 同号]
        double t1 = AngOf(x1, y1), t2 = AngOf(x2, y2);
        if ((total >= 0 && t1 > t2) || (total < 0 && t1 < t2)) (t1, t2) = (t2, t1);   // 按扫向排序
        var res = new List<SceneEntity>();
        var seg1 = SubArc(cx, cy, r, a1, 0, t1); if (seg1 != null) res.Add(Colored(seg1));
        var seg2 = SubArc(cx, cy, r, a1, t2, total); if (seg2 != null) res.Add(Colored(seg2));
        return res.Count > 0 ? res : null;
    }
    private ArcEntity? SubArc(double cx, double cy, double r, double a0, double from, double to)
    {
        if (Math.Abs(to - from) < 1e-6) return null;
        double s = a0 + from, m = a0 + (from + to) / 2, e = a0 + to;
        return new ArcEntity
        {
            X1 = cx + r * Math.Cos(s), Y1 = cy + r * Math.Sin(s),
            X2 = cx + r * Math.Cos(m), Y2 = cy + r * Math.Sin(m),
            X3 = cx + r * Math.Cos(e), Y3 = cy + r * Math.Sin(e), Segments = Segments
        };
    }
}

public sealed class PolylineEntity : SceneEntity
{
    public List<(double x, double y)> Points = new();
    public bool Closed;
    /// <summary>逐顶点高程(可空=平面多段线, 统一用 Elevation)。与 Points 等长时生效——三维多段线(落面/交线/等值线/剖面线)用。</summary>
    public List<double>? Zs;
    public bool Has3D => Zs != null && Zs.Count == Points.Count && Points.Count > 0;
    /// <summary>第 i 顶点的 z(三维取 Zs[i]+Elevation, 平面取 Elevation)。</summary>
    public double ZAt(int i) => Has3D ? Zs![i] + Elevation : Elevation;
    public override void Tessellate(List<float> o)
    {
        bool z3 = Has3D;
        for (int i = 0; i + 1 < Points.Count; i++)
        {
            if (z3) Seg3(o, Points[i].x, Points[i].y, ZAt(i), Points[i + 1].x, Points[i + 1].y, ZAt(i + 1));
            else SegD(o, Points[i].x, Points[i].y, Points[i + 1].x, Points[i + 1].y);
        }
        if (Closed && Points.Count > 1)
        {
            if (z3) Seg3(o, Points[^1].x, Points[^1].y, ZAt(Points.Count - 1), Points[0].x, Points[0].y, ZAt(0));
            else SegD(o, Points[^1].x, Points[^1].y, Points[0].x, Points[0].y);
        }
    }
    public override SceneEntity Apply(Affine2 m)
    {
        var pl = new PolylineEntity { Closed = Closed, Zs = Has3D ? new List<double>(Zs!) : null };
        foreach (var p in Points) pl.Points.Add(m.Map(p.x, p.y));
        return Colored(pl);
    }
    public override List<SceneEntity>? Explode()
    {
        var list = new List<SceneEntity>();
        LineEntity L(int i, int j)
        {
            var l = Colored(new LineEntity { X0 = Points[i].x, Y0 = Points[i].y, X1 = Points[j].x, Y1 = Points[j].y });
            if (Has3D) l.Elevation = (ZAt(i) + ZAt(j)) / 2;
            return (LineEntity)l;
        }
        for (int i = 0; i + 1 < Points.Count; i++) list.Add(L(i, i + 1));
        if (Closed && Points.Count > 1) list.Add(L(Points.Count - 1, 0));
        return list.Count > 0 ? list : null;
    }
    public override List<(double x, double y)> Grips() => new(Points);   // 各顶点
    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        if (i < 0 || i >= Points.Count) return null;
        var pl = new PolylineEntity { Closed = Closed, Zs = Has3D ? new List<double>(Zs!) : null };
        for (int k = 0; k < Points.Count; k++) pl.Points.Add(k == i ? (nx, ny) : Points[k]);
        return Colored(pl);
    }
    public override SceneEntity? Offset(double px, double py)   // 等距偏移(逐段平移 + 相邻段求交 miter)
    {
        int n = Points.Count;
        if (n < 2) return null;
        int segN = Closed ? n : n - 1;
        int ns = -1; double best = double.MaxValue, off = 0;
        for (int i = 0; i < segN; i++)
        {
            var a = Points[i]; var b = Points[(i + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            double dseg = SegDist(px, py, a.x, a.y, b.x, b.y);
            if (dseg < best) { best = dseg; ns = i; off = (px - a.x) * (-dy / len) + (py - a.y) * (dx / len); }
        }
        if (ns < 0) return null;
        var oa = new (double x, double y)[segN];
        var ob = new (double x, double y)[segN];
        for (int i = 0; i < segN; i++)
        {
            var a = Points[i]; var b = Points[(i + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { oa[i] = a; ob[i] = b; continue; }
            double nx = -dy / len * off, ny = dx / len * off;
            oa[i] = (a.x + nx, a.y + ny); ob[i] = (b.x + nx, b.y + ny);
        }
        var r = new PolylineEntity { Closed = Closed };
        if (!Closed)
        {
            r.Points.Add(oa[0]);
            for (int i = 1; i < segN; i++)
            {
                var p = LineMath.IntersectInfinite(oa[i - 1].x, oa[i - 1].y, ob[i - 1].x, ob[i - 1].y, oa[i].x, oa[i].y, ob[i].x, ob[i].y);
                r.Points.Add(p ?? oa[i]);
            }
            r.Points.Add(ob[segN - 1]);
        }
        else
        {
            for (int i = 0; i < segN; i++)
            {
                int prev = (i - 1 + segN) % segN;
                var p = LineMath.IntersectInfinite(oa[prev].x, oa[prev].y, ob[prev].x, ob[prev].y, oa[i].x, oa[i].y, ob[i].x, ob[i].y);
                r.Points.Add(p ?? oa[i]);
            }
        }
        return r.Points.Count >= 2 ? Colored(r) : null;
    }
    public override List<SceneEntity>? Break(double x1, double y1, double x2, double y2)   // 两点间移除一段
    {
        if (Points.Count < 2) return null;
        (int seg, double t, double cum) Proj(double px, double py)
        {
            int bs = 0; double bt = 0, bd = double.MaxValue, cum = 0, acc = 0;
            for (int i = 0; i + 1 < Points.Count; i++)
            {
                var a = Points[i]; var b = Points[i + 1];
                double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - a.x) * dx + (py - a.y) * dy) / len2, 0, 1);
                double cxp = a.x + t * dx, cyp = a.y + t * dy, d = (px - cxp) * (px - cxp) + (py - cyp) * (py - cyp);
                if (d < bd) { bd = d; bs = i; bt = t; cum = acc + t * len; }
                acc += len;
            }
            return (bs, bt, cum);
        }
        var p1 = Proj(x1, y1); var p2 = Proj(x2, y2);
        if (p1.cum > p2.cum) (p1, p2) = (p2, p1);
        (double x, double y) At(int seg, double t) { var a = Points[seg]; var b = Points[seg + 1]; return (a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t); }
        var b1 = At(p1.seg, p1.t); var b2 = At(p2.seg, p2.t);
        if (Closed)   // 闭合 → 断成一条开口多段线(经另一侧绕回)
        {
            var c = new PolylineEntity(); c.Points.Add(b2);
            for (int i = p2.seg + 1; i < Points.Count; i++) c.Points.Add(Points[i]);
            for (int i = 0; i <= p1.seg; i++) c.Points.Add(Points[i]);
            c.Points.Add(b1);
            return c.Points.Count >= 2 ? new List<SceneEntity> { Colored(c) } : null;
        }
        var a1 = new PolylineEntity();
        for (int i = 0; i <= p1.seg; i++) a1.Points.Add(Points[i]);
        a1.Points.Add(b1);
        var a2 = new PolylineEntity(); a2.Points.Add(b2);
        for (int i = p2.seg + 1; i < Points.Count; i++) a2.Points.Add(Points[i]);
        var res = new List<SceneEntity>();
        if (a1.Points.Count >= 2) res.Add(Colored(a1));
        if (a2.Points.Count >= 2) res.Add(Colored(a2));
        return res.Count > 0 ? res : null;
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
    public override List<(double x, double y)> Grips() =>
        new() { (Cx, Cy), (Cx + Radius * Math.Cos(Rotation), Cy + Radius * Math.Sin(Rotation)) };   // 心 + 首顶点
    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        if (i == 0) return Colored(new PolygonEntity { Cx = nx, Cy = ny, Radius = Radius, Sides = Sides, Rotation = Rotation });
        double dx = nx - Cx, dy = ny - Cy;   // 首顶点 → 定半径+朝向
        return Colored(new PolygonEntity { Cx = Cx, Cy = Cy, Radius = Math.Sqrt(dx * dx + dy * dy), Sides = Sides, Rotation = Math.Atan2(dy, dx) });
    }
    public override SceneEntity? Offset(double px, double py)   // 同心多边形：过点偏移(新半径=心到点距，保边数/朝向)
    {
        double r = Math.Sqrt((px - Cx) * (px - Cx) + (py - Cy) * (py - Cy));
        return r < 1e-6 ? null : Colored(new PolygonEntity { Cx = Cx, Cy = Cy, Radius = r, Sides = Sides, Rotation = Rotation });
    }
    public override List<SceneEntity>? Break(double x1, double y1, double x2, double y2)   // 正多边形→开口多段线
    {
        if (Sides < 3) return null;
        var pl = BreakClosedLoop(new List<(double x, double y)>(Vertices()), x1, y1, x2, y2);
        return pl != null ? new List<SceneEntity> { Colored(pl) } : null;
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

    /// <summary>起点-圆心-端点 → ArcEntity 的三点(起/中/端)：半径取 |起-心|，端点投影到圆，逆时针取弧。退化返回 null。</summary>
    public static (double x1, double y1, double x2, double y2, double x3, double y3)? FromStartCenterEnd(
        double sx, double sy, double cx, double cy, double ex, double ey)
    {
        double r = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
        if (r < 1e-9) return null;
        double a0 = Math.Atan2(sy - cy, sx - cx);
        double a1 = Math.Atan2(ey - cy, ex - cx);
        double sweep = a1 - a0;
        while (sweep <= 1e-9) sweep += 2 * Math.PI;   // 逆时针 (0, 2π]
        double am = a0 + sweep / 2;
        return (sx, sy,
                cx + r * Math.Cos(am), cy + r * Math.Sin(am),      // 弧中点
                cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));     // 端点投影到圆
    }

    /// <summary>起点-端点-半径 → ArcEntity 三点：正半径圆心在 起→终 左侧、负半径右侧；半径不足(＜半弦)返回 null。</summary>
    public static (double x1, double y1, double x2, double y2, double x3, double y3)? FromStartEndRadius(
        double sx, double sy, double ex, double ey, double r)
    {
        double dx = ex - sx, dy = ey - sy, chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-9) return null;
        double d = chord / 2, rr = Math.Abs(r);
        if (rr < d - 1e-9) return null;                       // 半径太小，够不到端点
        double h = Math.Sqrt(Math.Max(0, rr * rr - d * d));
        double ux = dx / chord, uy = dy / chord;              // 单位方向
        double sign = r >= 0 ? 1 : -1;                        // 左/右侧
        double cx = (sx + ex) / 2 + (-uy) * h * sign, cy = (sy + ey) / 2 + ux * h * sign;
        return FromStartCenterEnd(sx, sy, cx, cy, ex, ey);
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

    /// <summary>直线向点击侧法向偏移 r，返回偏移后的两端点；退化返回 null。</summary>
    public static (double x0, double y0, double x1, double y1)? OffsetToward(
        double x0, double y0, double x1, double y1, double px, double py, double r)
    {
        double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return null;
        double nx = -dy / len, ny = dx / len;
        double side = (px - x0) * nx + (py - y0) * ny;   // 点击在直线哪侧
        double s = side >= 0 ? r : -r;
        return (x0 + nx * s, y0 + ny * s, x1 + nx * s, y1 + ny * s);
    }

    /// <summary>切-切-半径(TTR)：与两直线相切、半径 r 的圆心（按各自点击侧定唯一解）；平行返回 null。</summary>
    public static (double x, double y)? TtrCenter(
        double ax0, double ay0, double ax1, double ay1, double p1x, double p1y,
        double bx0, double by0, double bx1, double by1, double p2x, double p2y, double r)
    {
        var o1 = OffsetToward(ax0, ay0, ax1, ay1, p1x, p1y, r);
        var o2 = OffsetToward(bx0, by0, bx1, by1, p2x, p2y, r);
        if (o1 == null || o2 == null) return null;
        return IntersectInfinite(o1.Value.x0, o1.Value.y0, o1.Value.x1, o1.Value.y1,
                                 o2.Value.x0, o2.Value.y0, o2.Value.x1, o2.Value.y1);
    }

    /// <summary>过 A 两点的无限直线 与 有限线段 B 的交点；平行或交点不在 B 段内返回 null（供修剪/延伸边界为任意实体）。</summary>
    public static (double x, double y)? IntersectInfiniteWithSegment(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1x = ax1 - ax0, d1y = ay1 - ay0, d2x = bx1 - bx0, d2y = by1 - by0;
        double denom = d1x * d2y - d1y * d2x;
        if (Math.Abs(denom) < 1e-12) return null;
        double u = ((bx0 - ax0) * d1y - (by0 - ay0) * d1x) / denom;   // 边界段参数
        if (u < -1e-9 || u > 1 + 1e-9) return null;
        return (bx0 + u * d2x, by0 + u * d2y);
    }

    /// <summary>无限直线(过 a 两点) 与 圆 的交点（0/1/2 个）。供 TTR 线-圆相切。</summary>
    public static List<(double x, double y)> IntersectLineCircle(
        double ax, double ay, double bx, double by, double cx, double cy, double r)
    {
        var res = new List<(double x, double y)>();
        double dx = bx - ax, dy = by - ay;
        double A = dx * dx + dy * dy;
        if (A < 1e-12) return res;
        double fx = ax - cx, fy = ay - cy;
        double B = 2 * (fx * dx + fy * dy), C = fx * fx + fy * fy - r * r;
        double disc = B * B - 4 * A * C;
        if (disc < 0) return res;
        double sq = Math.Sqrt(disc);
        double t1 = (-B - sq) / (2 * A), t2 = (-B + sq) / (2 * A);
        res.Add((ax + t1 * dx, ay + t1 * dy));
        if (disc > 1e-12) res.Add((ax + t2 * dx, ay + t2 * dy));
        return res;
    }

    /// <summary>两圆的交点（0/1/2 个）。供 TTR 圆-圆相切。</summary>
    public static List<(double x, double y)> IntersectCircleCircle(
        double c1x, double c1y, double r1, double c2x, double c2y, double r2)
    {
        var res = new List<(double x, double y)>();
        double dx = c2x - c1x, dy = c2y - c1y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-9 || d > r1 + r2 + 1e-9 || d < Math.Abs(r1 - r2) - 1e-9) return res;
        double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        double h = Math.Sqrt(Math.Max(0, r1 * r1 - a * a));
        double mx = c1x + a * dx / d, my = c1y + a * dy / d;
        double ox = -dy / d * h, oy = dx / d * h;
        res.Add((mx + ox, my + oy));
        if (h > 1e-12) res.Add((mx - ox, my - oy));
        return res;
    }

    /// <summary>点是否在多边形内（射线法，供圈范围算量/裁剪）。</summary>
    public static bool PointInPolygon(double px, double py, IReadOnlyList<(double x, double y)> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if (((a.y > py) != (b.y > py)) &&
                (px < (b.x - a.x) * (py - a.y) / (b.y - a.y) + a.x))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>两条有限线段是否真相交（用于框选交叉判定；共线相接的退化情形忽略）。</summary>
    public static bool SegmentsIntersect(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double C(double ox, double oy, double px, double py) => ox * py - oy * px;
        double d1 = C(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
        double d2 = C(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
        double d3 = C(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
        double d4 = C(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}

/// <summary>文字（单笔画矢量字体渲染为线段）：锚点 + 字高 + 内容。数字/符号可显，中文暂空(记录)。</summary>
public sealed class TextEntity : SceneEntity
{
    public double X, Y, Height = 1;
    public double Rotation;   // 弧度，绕锚点 (X,Y) 逆时针；0 = 水平（向后兼容）
    public int HAlign;        // 水平对齐: 0=左(锚点在左端,向后兼容)/1=中/2=右
    public int VAlign;        // 垂直对齐: 0=底(基线)/1=中/2=顶
    public double WidthFactor = 1;   // 字宽系数(水平缩放); 1=正常(向后兼容)
    public double ObliqueAngle;      // 倾斜角(弧度, 正=向右倾/斜体); 0=直立(向后兼容)
    /// <summary>
    /// 始终朝屏幕(公告板)——忠实原版 PmbiWriter.WriteText(screenFacing: true) 的注记策略:
    /// 三维里注记不随模型倾倒, 永远正对观察者; 2D 视图下与普通文字一致。
    /// 由视口按相机基向量逐帧(相机变了才)重建, 不进静态场景缓冲。
    /// </summary>
    public bool ScreenFacing;
    public string Text = "";

    /// <summary>字符笔画 → 局部二维线段(锚点为原点, 未旋转/未平移)。公告板与普通文字共用同一套排版。</summary>
    public List<(double x0, double y0, double x1, double y1)> LocalStrokes()
    {
        var outp = new List<(double, double, double, double)>();
        double wf = WidthFactor <= 0 ? 1 : WidthFactor;
        double tanOb = Math.Tan(ObliqueAngle);
        double adv = Height * 0.8 * wf;
        var lines = (Text ?? "").Split('\n');
        double lineH = Height * 1.5;
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            double width = line.Length * adv;
            double hOff = HAlign == 1 ? -width / 2 : HAlign == 2 ? -width : 0;
            double vOff = (VAlign == 1 ? -Height / 2 : VAlign == 2 ? -Height : 0) - li * lineH;
            double cursor = hOff;
            foreach (char ch in line)
            {
                foreach (var (sx0, sy0, sx1, sy1) in StrokeFont.Strokes(ch))
                    outp.Add((cursor + sx0 * Height * wf + sy0 * Height * tanOb, vOff + sy0 * Height,
                              cursor + sx1 * Height * wf + sy1 * Height * tanOb, vOff + sy1 * Height));
                cursor += adv;
            }
        }
        return outp;
    }
    public override void Tessellate(List<float> o)
    {
        if (ScreenFacing) return;   // 公告板文字由视口按相机基向量单独绘制
        double c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        double wf = WidthFactor <= 0 ? 1 : WidthFactor;
        double tanOb = Math.Tan(ObliqueAngle);
        double adv = Height * 0.8 * wf;
        var lines = (Text ?? "").Split('\n');                            // 多行文字: 按 \n 分行, 逐行下落
        double lineH = Height * 1.5;                                     // 行距
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            double width = line.Length * adv;
            double hOff = HAlign == 1 ? -width / 2 : HAlign == 2 ? -width : 0;   // 对齐偏移(局部, 逐行)
            double lineBase = -li * lineH;                              // 第 li 行基线相对首行下移
            double vOff = (VAlign == 1 ? -Height / 2 : VAlign == 2 ? -Height : 0) + lineBase;
            double cursor = hOff;
            foreach (char ch in line)
            {
                foreach (var (sx0, sy0, sx1, sy1) in StrokeFont.Strokes(ch))
                {
                    double lx0 = cursor + sx0 * Height * wf + sy0 * Height * tanOb, ly0 = vOff + sy0 * Height;   // x 按字宽缩放 + 倾斜斜切
                    double lx1 = cursor + sx1 * Height * wf + sy1 * Height * tanOb, ly1 = vOff + sy1 * Height;
                    Seg(o, X + lx0 * c - ly0 * s, Y + lx0 * s + ly0 * c,      // 旋转后平移到锚点
                           X + lx1 * c - ly1 * s, Y + lx1 * s + ly1 * c);
                }
                cursor += adv;
            }
        }
    }
    public override SceneEntity Apply(Affine2 m)
    {
        var (x, y) = m.Map(X, Y);
        double addRot = Math.Atan2(m.B, m.A);   // 仿射的旋转分量并入文字角
        return Colored(new TextEntity { X = x, Y = y, Height = Height * m.ScaleMag, Rotation = Rotation + addRot, HAlign = HAlign, VAlign = VAlign, WidthFactor = WidthFactor, ObliqueAngle = ObliqueAngle, Text = Text });
    }
    public override List<(double x, double y)> Grips() => new() { (X, Y) };
    public override SceneEntity? MoveGrip(int i, double nx, double ny) => Colored(new TextEntity { X = nx, Y = ny, Height = Height, Rotation = Rotation, HAlign = HAlign, VAlign = VAlign, WidthFactor = WidthFactor, ObliqueAngle = ObliqueAngle, Text = Text });
}

/// <summary>实体 → 类型中文名（对象树 / 快速选择用；椭圆/样条导入后并为多段线）。</summary>
public static class EntityTypeName
{
    public static string Of(SceneEntity e) => e switch
    {
        LineEntity => "直线",
        CircleEntity => "圆",
        ArcEntity => "圆弧",
        RectEntity => "矩形",
        PolylineEntity => "多段线",
        PointEntity => "点",
        PolygonEntity => "正多边形",
        TextEntity => "文字",
        MeshEntity => "三角网",
        _ => "其他"
    };
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

    /// <summary>拾取：容差 tol 内离 (x,y) 最近的实体；无则 null。canSelect 为 null 时不按图层过滤。</summary>
    public SceneEntity? Pick(double x, double y, double tol, Func<string, bool>? canSelect = null)
    {
        SceneEntity? best = null;
        double bestD = tol;
        foreach (var e in Entities)
        {
            if (!e.Visible) continue;                                     // 隐藏对象不可拾取
            if (canSelect != null && !canSelect(e.LayerName)) continue;   // 锁定/隐藏层不可选
            double d = e.DistanceTo(x, y);
            if (d <= bestD) { bestD = d; best = e; }
        }
        return best;
    }

    public bool Remove(SceneEntity e) => Entities.Remove(e);

    /// <summary>隐藏给定实体(Visible=false)。返回实际隐藏数(已隐藏的不重复计)。忠实 OnCtxHideObjectClick。</summary>
    public int HideEntities(IEnumerable<SceneEntity> es)
    {
        int n = 0;
        foreach (var e in es) if (e.Visible) { e.Visible = false; n++; }
        return n;
    }

    /// <summary>恢复所有被隐藏的实体(Visible=true)。返回恢复数。忠实 OnCtxShowAllClick(实体部分)。</summary>
    public int ShowAllHidden()
    {
        int n = 0;
        foreach (var e in Entities) if (!e.Visible) { e.Visible = true; n++; }
        return n;
    }

    /// <summary>当前被隐藏实体数(供状态/测试)。</summary>
    public int HiddenCount { get { int n = 0; foreach (var e in Entities) if (!e.Visible) n++; return n; } }

    /// <summary>把 from 图层上的实体全部改指派到 to 图层（删图层时实体不丢，移到目标层）。返回移动数。</summary>
    public int ReassignLayer(string from, string to)
    {
        int n = 0;
        foreach (var e in Entities)
            if (e.LayerName == from) { e.LayerName = to; n++; }
        return n;
    }

    public void Replace(SceneEntity oldE, SceneEntity newE)
    {
        int i = Entities.IndexOf(oldE);
        if (i >= 0) Entities[i] = newE; else Entities.Add(newE);
    }

    /// <summary>汇总几何。isShown 为 null 时全画；否则跳过不上屏图层的实体。</summary>
    public float[] BuildGeometry(Func<string, bool>? isShown = null)
    {
        var o = new List<float>();
        foreach (var e in Entities)
            if (e.Visible && (isShown == null || isShown(e.LayerName))) e.Tessellate(o);
        return o.ToArray();
    }

    /// <summary>
    /// 可见的公告板文字(始终朝屏幕, 忠实原版 screenFacing 注记): 交给视口按相机基向量绘制。
    /// 每项 = (锚点 x,y,z, 字高, 水平对齐, 垂直对齐, 颜色 rgb, 笔画局部线段)。
    /// </summary>
    public List<BillboardText> BuildBillboards(Func<string, bool>? isShown = null)
    {
        var list = new List<BillboardText>();
        foreach (var e in Entities)
        {
            if (e is not TextEntity t || !t.ScreenFacing) continue;
            if (!t.Visible || (isShown != null && !isShown(t.LayerName))) continue;
            list.Add(new BillboardText(t.X, t.Y, t.Elevation, t.Cr, t.Cg, t.Cb, t.LocalStrokes()));
        }
        return list;
    }

    /// <summary>可见实体的着色三角面(交错 P3_C3, GL_TRIANGLES)。</summary>
    public float[] BuildFaces(Func<string, bool>? isShown = null)
    {
        var o = new List<float>();
        foreach (var e in Entities)
            if (e.Visible && (isShown == null || isShown(e.LayerName))) e.TessellateFaces(o);
        return o.ToArray();
    }

    /// <summary>对象捕捉候选点(端点/中点/圆心/象限/顶点)，交错 P3_C3(仅位置)。供 osnap。</summary>
    public float[] SnapCandidates(Func<string, bool>? isShown = null)
    {
        var o = new List<float>();
        void P(double x, double y) { o.Add((float)x); o.Add((float)y); o.Add(0); o.Add(0); o.Add(0); o.Add(0); }
        foreach (var e in Entities)
        {
            if (!e.Visible) continue;                              // 隐藏对象不参与捕捉
            if (isShown != null && !isShown(e.LayerName)) continue;
            foreach (var g in e.Grips()) P(g.x, g.y);              // 端点/中点/圆心/象限/顶点
            if (e is PolylineEntity pl)                             // 补：段中点
                for (int i = 0; i + 1 < pl.Points.Count; i++)
                    P((pl.Points[i].x + pl.Points[i + 1].x) / 2, (pl.Points[i].y + pl.Points[i + 1].y) / 2);
            else if (e is RectEntity r)                             // 补：矩形中心 + 边中点
            {
                P((r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2);
                P((r.X0 + r.X1) / 2, r.Y0); P((r.X0 + r.X1) / 2, r.Y1);
                P(r.X0, (r.Y0 + r.Y1) / 2); P(r.X1, (r.Y0 + r.Y1) / 2);
            }
            else if (e is ArcEntity a)                              // 补：圆弧圆心
            {
                var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
                if (cc != null) P(cc.Value.cx, cc.Value.cy);
            }
        }
        return o.ToArray();
    }

    /// <summary>把某图层上所有实体改为给定颜色，返回改动数（图层改色用）。</summary>
    public int RecolorLayer(string layer, float r, float g, float b)
    {
        int n = 0;
        foreach (var e in Entities)
            if (e.LayerName == layer) { e.Cr = r; e.Cg = g; e.Cb = b; n++; }
        return n;
    }
}

/// <summary>一条公告板文字(始终朝屏幕)：世界锚点 + 颜色 + 已排版好的局部笔画线段。</summary>
public readonly record struct BillboardText(
    double X, double Y, double Z, float Cr, float Cg, float Cb,
    List<(double x0, double y0, double x1, double y1)> Strokes);
