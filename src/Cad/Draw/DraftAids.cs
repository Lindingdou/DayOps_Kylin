using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 制图辅助（ORTHO 正交 / SNAP 栅格捕捉）的纯几何核。纯逻辑、可单测。
/// </summary>
public static class DraftAids
{
    /// <summary>正交约束：从基点(bx,by)看，把 (x,y) 锁到水平或垂直——取偏移较大的轴，另一轴对齐基点。</summary>
    public static (double x, double y) Ortho(double bx, double by, double x, double y)
        => Math.Abs(x - bx) >= Math.Abs(y - by) ? (x, by) : (bx, y);

    /// <summary>栅格捕捉：四舍五入到 step 网格。step≤0 原样返回。</summary>
    public static (double x, double y) Snap(double x, double y, double step)
        => step <= 1e-9 ? (x, y) : (Math.Round(x / step) * step, Math.Round(y / step) * step);
}
