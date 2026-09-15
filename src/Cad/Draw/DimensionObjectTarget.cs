using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>从被点击的可拾取图元中提取对齐标注所需的一条直边。</summary>
internal readonly record struct DimensionSegment(double X1, double Y1, double X2, double Y2);

internal static class DimensionObjectTarget
{
    internal static DimensionSegment? Find(SceneEntity entity, double pickX, double pickY)
    {
        DimensionSegment? best = null;
        double bestDistance2 = double.MaxValue;

        void Consider(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double length2 = dx * dx + dy * dy;
            if (length2 < 1e-12) return;
            double t = Math.Clamp(((pickX - x1) * dx + (pickY - y1) * dy) / length2, 0, 1);
            double px = x1 + t * dx, py = y1 + t * dy;
            double distance2 = (pickX - px) * (pickX - px) + (pickY - py) * (pickY - py);
            if (distance2 >= bestDistance2) return;
            bestDistance2 = distance2;
            best = new DimensionSegment(x1, y1, x2, y2);
        }

        switch (entity)
        {
            case LineEntity line:
                Consider(line.X0, line.Y0, line.X1, line.Y1);
                break;
            case PolylineEntity line:
                for (int i = 0; i + 1 < line.Points.Count; i++)
                    Consider(line.Points[i].x, line.Points[i].y, line.Points[i + 1].x, line.Points[i + 1].y);
                if (line.Closed && line.Points.Count > 1)
                    Consider(line.Points[^1].x, line.Points[^1].y, line.Points[0].x, line.Points[0].y);
                break;
            case RectEntity rect:
                Consider(rect.X0, rect.Y0, rect.X1, rect.Y0);
                Consider(rect.X1, rect.Y0, rect.X1, rect.Y1);
                Consider(rect.X1, rect.Y1, rect.X0, rect.Y1);
                Consider(rect.X0, rect.Y1, rect.X0, rect.Y0);
                break;
            case PolygonEntity polygon when polygon.Sides >= 3 && polygon.Radius > 1e-9:
                double firstX = polygon.Cx + polygon.Radius * Math.Cos(polygon.Rotation);
                double firstY = polygon.Cy + polygon.Radius * Math.Sin(polygon.Rotation);
                double previousX = firstX, previousY = firstY;
                for (int i = 1; i < polygon.Sides; i++)
                {
                    double angle = polygon.Rotation + 2 * Math.PI * i / polygon.Sides;
                    double x = polygon.Cx + polygon.Radius * Math.Cos(angle);
                    double y = polygon.Cy + polygon.Radius * Math.Sin(angle);
                    Consider(previousX, previousY, x, y);
                    previousX = x; previousY = y;
                }
                Consider(previousX, previousY, firstX, firstY);
                break;
        }

        return best;
    }
}
