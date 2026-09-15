using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>连续点取闭合区域的面积测量状态机。纯逻辑，可单测；交互由 MainWindow 驱动。</summary>
public sealed class AreaState
{
    private readonly List<(double x, double y)> _points = new();

    /// <summary>当前已取点，供 jig 预览使用。</summary>
    public IReadOnlyList<(double x, double y)> Points => _points;

    public bool HasPoints => _points.Count > 0;

    public void AddPoint(double x, double y) => _points.Add((x, y));

    /// <summary>以当前点列闭合计算面积与周长；点数不足时保留现场等待继续取点。</summary>
    public AreaMeasurement? Finish()
    {
        if (_points.Count < 3) return null;
        var points = _points.ToArray();
        var result = new AreaMeasurement(points, GeomMeasure.Area(points), GeomMeasure.Perimeter(points, true));
        _points.Clear();
        return result;
    }

    public void Reset() => _points.Clear();
}

/// <summary>一次闭合区域测量结果。</summary>
public readonly record struct AreaMeasurement(
    IReadOnlyList<(double x, double y)> Points,
    double Area,
    double Perimeter);
