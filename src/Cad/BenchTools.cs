using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 分帮扩帮（MineAssLib 托管切片）—— 台阶线批量平行偏移：
/// 以点击到实体的垂距为步距，向该侧偏移 1..count 倍，生成一族平行台阶线。
/// 复用实体 Offset；仅支持直线/多段线（台阶线）。纯逻辑、可单测。
/// </summary>
public static class BenchTools
{
    public static List<SceneEntity> BatchOffset(SceneEntity e, double cx, double cy, int count)
    {
        var res = new List<SceneEntity>();
        (double x, double y) foot;
        if (e is LineEntity l)
        {
            double dx = l.X1 - l.X0, dy = l.Y1 - l.Y0, len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : ((cx - l.X0) * dx + (cy - l.Y0) * dy) / len2;   // 无限线垂足
            foot = (l.X0 + t * dx, l.Y0 + t * dy);
        }
        else if (e is PolylineEntity pl) foot = RoadTools.NearestOnPolyline(cx, cy, pl.Points);
        else return res;   // 仅线/多段线

        double vx = cx - foot.x, vy = cy - foot.y;
        for (int k = 1; k <= count; k++)
        {
            var off = e.Offset(foot.x + vx * k, foot.y + vy * k);   // 偏移 k×步距
            if (off != null) res.Add(off);
        }
        return res;
    }
}
