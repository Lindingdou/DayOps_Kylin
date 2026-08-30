using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 两点测距状态机（DIST 命令用）。第一次取点记下，第二次取点返回两点距离。
/// 纯逻辑、不依赖 UI，可单测；交互由 MainWindow 驱动。
/// </summary>
public sealed class MeasureState
{
    private (double x, double y)? _first;

    /// <summary>已取第一点。</summary>
    public bool HasFirst => _first != null;

    /// <summary>第一点（供画测量线）。</summary>
    public (double x, double y)? First => _first;

    /// <summary>取一个点：第一点返回 null；第二点返回两点距离并复位（可继续下一次测量）。</summary>
    public double? AddPoint(double x, double y)
    {
        if (_first == null) { _first = (x, y); return null; }
        var f = _first.Value;
        _first = null;
        double dx = x - f.x, dy = y - f.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void Reset() => _first = null;
}
