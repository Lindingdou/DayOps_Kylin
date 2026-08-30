using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 框选判定 —— 窗口选(全含)/交叉选(相交或含)。对实体自镶嵌的每段做矩形包含/相交测试。纯逻辑、可单测。
/// </summary>
public static class SelectionBox
{
    /// <summary>实体是否被选框选中。crossing=false 窗口选(整体在框内)；true 交叉选(任一点在框内或任一段与框相交)。</summary>
    public static bool Match(SceneEntity e, double minX, double minY, double maxX, double maxY, bool crossing)
    {
        var o = new List<float>();
        e.Tessellate(o);
        if (o.Count == 0) return false;

        bool all = true, hit = false;
        for (int i = 0; i + 11 < o.Count; i += 12)
        {
            double x0 = o[i], y0 = o[i + 1], x1 = o[i + 6], y1 = o[i + 7];
            bool in0 = In(x0, y0, minX, minY, maxX, maxY);
            bool in1 = In(x1, y1, minX, minY, maxX, maxY);
            if (!in0 || !in1) all = false;
            if (in0 || in1) hit = true;
            else if (SegRect(x0, y0, x1, y1, minX, minY, maxX, maxY)) hit = true;   // 两端都在外但穿过框
        }
        return crossing ? hit : all;
    }

    private static bool In(double x, double y, double minX, double minY, double maxX, double maxY)
        => x >= minX && x <= maxX && y >= minY && y <= maxY;

    private static bool SegRect(double x0, double y0, double x1, double y1, double minX, double minY, double maxX, double maxY)
        => LineMath.SegmentsIntersect(x0, y0, x1, y1, minX, minY, maxX, minY)   // 下
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, maxX, minY, maxX, maxY)   // 右
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, maxX, maxY, minX, maxY)   // 上
        || LineMath.SegmentsIntersect(x0, y0, x1, y1, minX, maxY, minX, minY);  // 左
}
