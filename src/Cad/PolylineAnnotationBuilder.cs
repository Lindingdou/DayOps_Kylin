using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 为多段线生成临时的起点/线序辅助标识。
/// 标识使用独立图层和独立实体集合，调用方可以随时清除，不会进入源图形的存档或导出。
/// </summary>
public static class PolylineAnnotationBuilder
{
    public const string LayerName = "__Kylin_多段线标识";

    private const float RedR = 239f / 255f;
    private const float RedG = 68f / 255f;
    private const float YellowR = 251f / 255f;
    private const float YellowG = 191f / 255f;
    private const float YellowB = 36f / 255f;
    private const float GreenR = 16f / 255f;
    private const float GreenG = 185f / 255f;
    private const float GreenB = 129f / 255f;

    public static IReadOnlyList<SceneEntity> BuildStart(PolylineEntity line, double textHeight)
    {
        if (line.Points.Count == 0) return Array.Empty<SceneEntity>();

        double height = SafeHeight(textHeight);
        var p = line.Points[0];
        return new SceneEntity[]
        {
            Marker(p.x, p.y, line.ZAt(0), height, GreenR, GreenG, GreenB),
            Label(p.x, p.y, line.ZAt(0), height, "S"),
        };
    }

    public static IReadOnlyList<SceneEntity> BuildOrder(PolylineEntity line, double textHeight)
    {
        if (line.Points.Count == 0) return Array.Empty<SceneEntity>();

        double height = SafeHeight(textHeight);
        var result = new List<SceneEntity>(line.Points.Count * 2);
        int last = line.Points.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            var p = line.Points[i];
            (float r, float g, float b) color = i == 0
                ? (RedR, RedG, RedG)
                : i == last ? (GreenR, GreenG, GreenB) : (YellowR, YellowG, YellowB);
            result.Add(Marker(p.x, p.y, line.ZAt(i), height, color.r, color.g, color.b));
            result.Add(Label(p.x, p.y, line.ZAt(i), height, (i + 1).ToString(CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public static double SuggestTextHeight(IReadOnlyList<PolylineEntity> lines)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var line in lines)
        {
            foreach (var p in line.Points)
            {
                minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y);
                maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y);
            }
        }
        if (!double.IsFinite(minX) || !double.IsFinite(minY) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY)) return 1;

        double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        return double.IsFinite(diagonal) && diagonal > 1e-9 ? Math.Max(diagonal * 0.025, 1e-3) : 1;
    }

    private static CircleEntity Marker(double x, double y, double z,
        double height, float r, float g, float b) => new()
    {
        Cx = x, Cy = y, Radius = height * 0.78, Segments = 32,
        Elevation = z, Cr = r, Cg = g, Cb = b, LayerName = LayerName, LineWeight = 30,
    };

    private static TextEntity Label(double x, double y, double z,
        double height, string text) => new()
    {
        X = x, Y = y, Elevation = z, Height = height * 0.82, Text = text,
        HAlign = 1, VAlign = 1, ScreenFacing = true,
        Cr = 1, Cg = 1, Cb = 1, LayerName = LayerName, LineWeight = 30,
    };

    private static double SafeHeight(double height) => double.IsFinite(height) && height > 1e-9 ? height : 1;
}
