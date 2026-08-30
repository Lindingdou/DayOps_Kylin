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

    /// <summary>把进行中的几何（如多段线已点的段）追加为预览。</summary>
    public virtual void AppendPreview(List<float> o) { }
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
    public override void Reset() => _c = null;
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
    public override void Reset() { _p1 = null; _p2 = null; }
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
    public override void AppendPreview(List<float> o)
    {
        for (int i = 0; i + 1 < _pts.Count; i++)
        {
            var a = _pts[i]; var b = _pts[i + 1];
            o.Add((float)a.x); o.Add((float)a.y); o.Add(0); o.Add(0.86f); o.Add(0.9f); o.Add(0.6f);
            o.Add((float)b.x); o.Add((float)b.y); o.Add(0); o.Add(0.86f); o.Add(0.9f); o.Add(0.6f);
        }
    }
    public override void Reset() => _pts.Clear();
}
