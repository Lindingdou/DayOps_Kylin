using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>绘制工具基类 —— 点击状态机：喂点，凑齐后返回一个实体（否则 null，继续等点）。</summary>
public abstract class DrawTool
{
    /// <summary>当前提示（命令行/状态栏显示）。</summary>
    public abstract string Prompt { get; }

    /// <summary>喂一个点。凑齐则返回新实体并复位（可连续绘制）；否则 null。</summary>
    public abstract SceneEntity? AddPoint(double x, double y);

    public virtual void Reset() { }
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
