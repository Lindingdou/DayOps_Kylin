using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>由最后一次拾取点计算线性标注的尺寸线落位。</summary>
public readonly record struct LinearDimensionPlacement(double OffsetX, double OffsetY, bool Horizontal);

public static class DimensionPlacement
{
    /// <summary>
    /// 用用户拾取点决定标注线方向和位置：偏移点离测量中点的竖向距离较大时画水平线，
    /// 否则画竖直线；与拾取点无关的坐标归一到测量中点，避免把鼠标横向/纵向偏移误当成落位。
    /// </summary>
    public static LinearDimensionPlacement ResolveLinearAxis(
        double x1, double y1, double x2, double y2, double pickedX, double pickedY)
    {
        double midX = (x1 + x2) / 2.0;
        double midY = (y1 + y2) / 2.0;
        bool horizontal = Math.Abs(pickedY - midY) >= Math.Abs(pickedX - midX);
        return horizontal
            ? new LinearDimensionPlacement(midX, pickedY, true)
            : new LinearDimensionPlacement(pickedX, midY, false);
    }
}
