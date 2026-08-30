using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 实体特性提取（对应原 EntityPropertyBag 的属性模型；此为纯数据版，脱 WPF PropertyGrid）。
/// 返回 (类别, 标签, 值) 行：常规(类型/图层/颜色) + 各类型几何。纯逻辑、可单测。
/// </summary>
public static class EntityProperties
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double v) => v.ToString("0.##", Inv);
    private static string F(double x, double y) => $"({N(x)}, {N(y)})";

    public static List<(string cat, string label, string value)> Describe(SceneEntity e)
    {
        var r = new List<(string, string, string)>
        {
            ("常规", "类型", EntityTypeName.Of(e)),
            ("常规", "图层", string.IsNullOrEmpty(e.LayerName) ? "0" : e.LayerName),
            ("常规", "颜色", $"#{(int)Math.Round(e.Cr * 255):X2}{(int)Math.Round(e.Cg * 255):X2}{(int)Math.Round(e.Cb * 255):X2}"),
        };
        switch (e)
        {
            case LineEntity l:
                r.Add(("几何", "起点", F(l.X0, l.Y0)));
                r.Add(("几何", "终点", F(l.X1, l.Y1)));
                r.Add(("几何", "长度", N(Math.Sqrt((l.X1 - l.X0) * (l.X1 - l.X0) + (l.Y1 - l.Y0) * (l.Y1 - l.Y0)))));
                break;
            case CircleEntity c:
                r.Add(("几何", "圆心", F(c.Cx, c.Cy)));
                r.Add(("几何", "半径", N(c.Radius)));
                break;
            case ArcEntity a:
                var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
                if (cc != null) { r.Add(("几何", "圆心", F(cc.Value.Item1, cc.Value.Item2))); r.Add(("几何", "半径", N(cc.Value.Item3))); }
                r.Add(("几何", "起点", F(a.X1, a.Y1)));
                r.Add(("几何", "端点", F(a.X3, a.Y3)));
                break;
            case RectEntity rc:
                r.Add(("几何", "角点1", F(rc.X0, rc.Y0)));
                r.Add(("几何", "角点2", F(rc.X1, rc.Y1)));
                r.Add(("几何", "宽", N(Math.Abs(rc.X1 - rc.X0))));
                r.Add(("几何", "高", N(Math.Abs(rc.Y1 - rc.Y0))));
                break;
            case PolygonEntity pg:
                r.Add(("几何", "圆心", F(pg.Cx, pg.Cy)));
                r.Add(("几何", "半径", N(pg.Radius)));
                r.Add(("几何", "边数", pg.Sides.ToString(Inv)));
                break;
            case PointEntity p:
                r.Add(("几何", "坐标", F(p.X, p.Y)));
                break;
            case PolylineEntity pl:
                r.Add(("几何", "闭合", pl.Closed ? "是" : "否"));
                r.Add(("几何", "顶点数", pl.Points.Count.ToString(Inv)));
                break;
            case TextEntity t:
                r.Add(("几何", "位置", F(t.X, t.Y)));
                r.Add(("几何", "字高", N(t.Height)));
                r.Add(("几何", "内容", t.Text));
                r.Add(("几何", "旋转", N(t.Rotation * 180.0 / Math.PI) + "°"));
                break;
        }
        return r;
    }
}
