using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>绘制工具基类 —— 点击状态机：喂点，凑齐后返回一个实体（否则 null，继续等点）。</summary>
public abstract class DrawTool
{
    /// <summary>当前提示（命令行/状态栏显示）。</summary>
    public abstract string Prompt { get; }

    /// <summary>喂一个点。凑齐则返回新实体并复位（可连续绘制）；否则 null。</summary>
    public abstract SceneEntity? AddPoint(double x, double y);

    public virtual void Reset() { }

    /// <summary>多点工具（如多段线）需显式结束。</summary>
    public virtual bool IsMultiPoint => false;

    /// <summary>结束多点绘制，返回实体（不足则 null）。</summary>
    public virtual SceneEntity? Finish() => null;

    /// <summary>追加进行中的预览（橡皮筋）：已点的点 + 当前光标。cursor 为 null 时只画已确定部分。</summary>
    public virtual void AppendPreview(List<float> o, (double x, double y)? cursor) { }

    /// <summary>预览色（灰蓝）。</summary>
    protected const float PR = 0.55f, PG = 0.62f, PB = 0.70f;

    protected static SceneEntity Tint(SceneEntity e) { e.Cr = PR; e.Cg = PG; e.Cb = PB; return e; }
}

/// <summary>直线：起点 → 终点。</summary>
public sealed class LineTool : DrawTool
{
    private (double x, double y)? _p0;
    public override string Prompt => _p0 == null ? "直线：指定起点" : "直线：指定终点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p0 == null) { _p0 = (x, y); return null; }
        var p = _p0.Value; _p0 = null;
        return new LineEntity { X0 = p.x, Y0 = p.y, X1 = x, Y1 = y };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_p0 != null && cursor != null)
            Tint(new LineEntity { X0 = _p0.Value.x, Y0 = _p0.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
    }
    public override void Reset() => _p0 = null;
}

/// <summary>圆：圆心 → 半径点。</summary>
public sealed class CircleTool : DrawTool
{
    private (double x, double y)? _c;
    public override string Prompt => _c == null ? "圆：指定圆心" : "圆：指定半径";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_c == null) { _c = (x, y); return null; }
        var c = _c.Value; _c = null;
        double r = Math.Sqrt((x - c.x) * (x - c.x) + (y - c.y) * (y - c.y));
        return new CircleEntity { Cx = c.x, Cy = c.y, Radius = r };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_c != null && cursor != null)
        {
            double dx = cursor.Value.x - _c.Value.x, dy = cursor.Value.y - _c.Value.y;
            Tint(new CircleEntity { Cx = _c.Value.x, Cy = _c.Value.y, Radius = Math.Sqrt(dx * dx + dy * dy) }).Tessellate(o);
        }
    }
    public override void Reset() => _c = null;
}

/// <summary>圆(2点)：两点为直径端点。</summary>
public sealed class Circle2PTool : DrawTool
{
    private (double x, double y)? _p0;
    public override string Prompt => _p0 == null ? "圆(2点)：指定直径第一端点" : "圆(2点)：指定直径第二端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p0 == null) { _p0 = (x, y); return null; }
        var a = _p0.Value; _p0 = null;
        return Make(a.x, a.y, x, y);
    }
    private static CircleEntity Make(double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return new CircleEntity { Cx = (x0 + x1) / 2, Cy = (y0 + y1) / 2, Radius = Math.Sqrt(dx * dx + dy * dy) / 2 };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_p0 != null && cursor != null)
            Tint(Make(_p0.Value.x, _p0.Value.y, cursor.Value.x, cursor.Value.y)).Tessellate(o);
    }
    public override void Reset() => _p0 = null;
}

/// <summary>圆(3点)：过三点（三点外接圆）。</summary>
public sealed class Circle3PTool : DrawTool
{
    private (double x, double y)? _p1, _p2;
    public override string Prompt =>
        _p1 == null ? "圆(3点)：第一点" : _p2 == null ? "圆(3点)：第二点" : "圆(3点)：第三点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p1 == null) { _p1 = (x, y); return null; }
        if (_p2 == null) { _p2 = (x, y); return null; }
        var a = _p1.Value; var b = _p2.Value; _p1 = null; _p2 = null;
        var cc = ArcMath.Circumcircle(a.x, a.y, b.x, b.y, x, y);
        return cc == null ? null : new CircleEntity { Cx = cc.Value.cx, Cy = cc.Value.cy, Radius = cc.Value.r };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _p1 == null) return;
        if (_p2 == null)
            Tint(new LineEntity { X0 = _p1.Value.x, Y0 = _p1.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
        else
        {
            var cc = ArcMath.Circumcircle(_p1.Value.x, _p1.Value.y, _p2.Value.x, _p2.Value.y, cursor.Value.x, cursor.Value.y);
            if (cc != null) Tint(new CircleEntity { Cx = cc.Value.cx, Cy = cc.Value.cy, Radius = cc.Value.r }).Tessellate(o);
        }
    }
    public override void Reset() { _p1 = null; _p2 = null; }
}

/// <summary>矩形：角点 → 对角点。</summary>
public sealed class RectTool : DrawTool
{
    private (double x, double y)? _p0;
    public override string Prompt => _p0 == null ? "矩形：指定角点" : "矩形：指定对角点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p0 == null) { _p0 = (x, y); return null; }
        var p = _p0.Value; _p0 = null;
        return new RectEntity { X0 = p.x, Y0 = p.y, X1 = x, Y1 = y };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_p0 != null && cursor != null)
            Tint(new RectEntity { X0 = _p0.Value.x, Y0 = _p0.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
    }
    public override void Reset() => _p0 = null;
}

/// <summary>点：单击放置（连续）。</summary>
public sealed class PointTool : DrawTool
{
    public override string Prompt => "点：指定位置";
    public override SceneEntity? AddPoint(double x, double y) => new PointEntity { X = x, Y = y };
}

/// <summary>圆弧：三点（起点 → 圆弧上一点 → 端点）。</summary>
public sealed class ArcTool : DrawTool
{
    private (double x, double y)? _p1, _p2;
    public override string Prompt =>
        _p1 == null ? "圆弧：指定起点" : _p2 == null ? "圆弧：指定圆弧上一点" : "圆弧：指定端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_p1 == null) { _p1 = (x, y); return null; }
        if (_p2 == null) { _p2 = (x, y); return null; }
        var a = _p1.Value; var b = _p2.Value; _p1 = null; _p2 = null;
        return new ArcEntity { X1 = a.x, Y1 = a.y, X2 = b.x, Y2 = b.y, X3 = x, Y3 = y };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _p1 == null) return;
        if (_p2 == null)   // 只 1 点：起点→光标 直线预览
            Tint(new LineEntity { X0 = _p1.Value.x, Y0 = _p1.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o);
        else               // 2 点：三点圆弧预览
            Tint(new ArcEntity { X1 = _p1.Value.x, Y1 = _p1.Value.y, X2 = _p2.Value.x, Y2 = _p2.Value.y, X3 = cursor.Value.x, Y3 = cursor.Value.y }).Tessellate(o);
    }
    public override void Reset() { _p1 = null; _p2 = null; }
}

/// <summary>圆弧：起点 → 圆心 → 端点（逆时针）。</summary>
public sealed class ArcSceTool : DrawTool
{
    private (double x, double y)? _s, _c;
    public override string Prompt => _s == null ? "圆弧(起点圆心端点)：起点" : _c == null ? "圆弧：圆心" : "圆弧：端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_s == null) { _s = (x, y); return null; }
        if (_c == null) { _c = (x, y); return null; }
        var s = _s.Value; var c = _c.Value; _s = null; _c = null;
        return MakeSce(s.x, s.y, c.x, c.y, x, y);
    }
    internal static SceneEntity? MakeSce(double sx, double sy, double cx, double cy, double ex, double ey)
    {
        var t = ArcMath.FromStartCenterEnd(sx, sy, cx, cy, ex, ey);
        return t == null ? null : new ArcEntity { X1 = t.Value.x1, Y1 = t.Value.y1, X2 = t.Value.x2, Y2 = t.Value.y2, X3 = t.Value.x3, Y3 = t.Value.y3 };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _s == null) return;
        if (_c == null) { Tint(new LineEntity { X0 = _s.Value.x, Y0 = _s.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o); return; }
        var e = MakeSce(_s.Value.x, _s.Value.y, _c.Value.x, _c.Value.y, cursor.Value.x, cursor.Value.y);
        if (e != null) Tint(e).Tessellate(o);
    }
    public override void Reset() { _s = null; _c = null; }
}

/// <summary>圆弧：圆心 → 起点 → 端点（逆时针）。</summary>
public sealed class ArcCseTool : DrawTool
{
    private (double x, double y)? _c, _s;
    public override string Prompt => _c == null ? "圆弧(圆心起点端点)：圆心" : _s == null ? "圆弧：起点" : "圆弧：端点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_c == null) { _c = (x, y); return null; }
        if (_s == null) { _s = (x, y); return null; }
        var c = _c.Value; var s = _s.Value; _c = null; _s = null;
        return ArcSceTool.MakeSce(s.x, s.y, c.x, c.y, x, y);
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (cursor == null || _c == null) return;
        if (_s == null) { Tint(new LineEntity { X0 = _c.Value.x, Y0 = _c.Value.y, X1 = cursor.Value.x, Y1 = cursor.Value.y }).Tessellate(o); return; }
        var e = ArcSceTool.MakeSce(_s.Value.x, _s.Value.y, _c.Value.x, _c.Value.y, cursor.Value.x, cursor.Value.y);
        if (e != null) Tint(e).Tessellate(o);
    }
    public override void Reset() { _c = null; _s = null; }
}

/// <summary>多段线：连续点，双击结束。</summary>
public sealed class PolylineTool : DrawTool
{
    private readonly List<(double x, double y)> _pts = new();
    public override bool IsMultiPoint => true;
    public override string Prompt => _pts.Count == 0 ? "多段线：指定起点" : $"多段线：下一点（双击结束，已 {_pts.Count} 点）";
    public override SceneEntity? AddPoint(double x, double y) { _pts.Add((x, y)); return null; }
    public override SceneEntity? Finish()
    {
        if (_pts.Count < 2) { _pts.Clear(); return null; }
        var e = new PolylineEntity { Points = new List<(double, double)>(_pts) };
        _pts.Clear();
        return e;
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        for (int i = 0; i + 1 < _pts.Count; i++)
            Seg(o, _pts[i], _pts[i + 1], 0.86f, 0.9f, 0.6f);
        if (cursor != null && _pts.Count > 0)
            Seg(o, _pts[^1], cursor.Value, PR, PG, PB);       // 橡皮筋段
    }
    private static void Seg(List<float> o, (double x, double y) a, (double x, double y) b, float r, float g, float bl)
    {
        o.Add((float)a.x); o.Add((float)a.y); o.Add(0); o.Add(r); o.Add(g); o.Add(bl);
        o.Add((float)b.x); o.Add((float)b.y); o.Add(0); o.Add(r); o.Add(g); o.Add(bl);
    }
    public override void Reset() => _pts.Clear();
}

/// <summary>正多边形：中心 → 半径点(顶点方向)。边数默认 6，可设 Sides。</summary>
public sealed class PolygonTool : DrawTool
{
    private (double x, double y)? _c;
    public int Sides = 6;
    public override string Prompt => _c == null ? $"正多边形（{Sides} 边）：指定中心" : $"正多边形（{Sides} 边）：指定顶点";
    public override SceneEntity? AddPoint(double x, double y)
    {
        if (_c == null) { _c = (x, y); return null; }
        var c = _c.Value; _c = null;
        return Make(c.x, c.y, x, y, Sides);
    }
    private static PolygonEntity Make(double cx, double cy, double px, double py, int sides)
    {
        double dx = px - cx, dy = py - cy;
        return new PolygonEntity { Cx = cx, Cy = cy, Radius = Math.Sqrt(dx * dx + dy * dy), Sides = sides, Rotation = Math.Atan2(dy, dx) };
    }
    public override void AppendPreview(List<float> o, (double x, double y)? cursor)
    {
        if (_c != null && cursor != null)
            Tint(Make(_c.Value.x, _c.Value.y, cursor.Value.x, cursor.Value.y, Sides)).Tessellate(o);
    }
    public override void Reset() => _c = null;
}
