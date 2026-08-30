using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>角度计算 + 三点测角状态机（MANG 命令用）。纯逻辑，可单测。</summary>
public static class AngleMath
{
    /// <summary>顶点 v 处，射线 v→a 与 v→b 的夹角（度，0..180）。</summary>
    public static double AngleDeg(double vx, double vy, double ax, double ay, double bx, double by)
    {
        double a1 = Math.Atan2(ay - vy, ax - vx);
        double a2 = Math.Atan2(by - vy, bx - vx);
        double d = Math.Abs(a1 - a2) * 180.0 / Math.PI;
        if (d > 180) d = 360 - d;
        return d;
    }
}

/// <summary>三点测角：1st 顶点，2nd 第一射线端，3rd 第二射线端 → 返回夹角并复位。</summary>
public sealed class AngleState
{
    private (double x, double y)? _v;
    private (double x, double y)? _a;

    public bool HasVertex => _v != null;
    public bool HasFirstRay => _a != null;
    public (double x, double y)? Vertex => _v;
    public (double x, double y)? FirstRay => _a;

    public double? AddPoint(double x, double y)
    {
        if (_v == null) { _v = (x, y); return null; }
        if (_a == null) { _a = (x, y); return null; }
        var v = _v.Value; var a = _a.Value;
        _v = null; _a = null;
        return AngleMath.AngleDeg(v.x, v.y, a.x, a.y, x, y);
    }

    public void Reset() { _v = null; _a = null; }
}
