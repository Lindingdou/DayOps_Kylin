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
            ("常规", "线型", DashPattern.DisplayName(e.Dash)),
            ("常规", "线宽", LineWeightUtil.Display(e.LineWeight)),
            ("常规", "透明度", TranspDisplay(e.Transparency)),
            ("常规", "可见", e.Visible ? "是" : "否"),
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
                r.Add(("几何", "点大小", N(p.Size)));
                r.Add(("几何", "点样式", p.Style.ToString(Inv)));
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

    /// <summary>该实体在特性面板中可编辑的行标签集合(其余只读, 如长度/宽/高等派生量)。</summary>
    public static HashSet<string> EditableLabels(SceneEntity e)
    {
        var s = new HashSet<string> { "图层", "颜色", "线型", "线宽", "透明度", "可见" };   // 常规恒可编辑
        switch (e)
        {
            case LineEntity: s.Add("起点"); s.Add("终点"); break;
            case CircleEntity: s.Add("圆心"); s.Add("半径"); break;
            case RectEntity: s.Add("角点1"); s.Add("角点2"); break;
            case PolygonEntity: s.Add("圆心"); s.Add("半径"); s.Add("边数"); break;
            case PointEntity: s.Add("坐标"); s.Add("点大小"); s.Add("点样式"); break;
            case ArcEntity: s.Add("起点"); s.Add("端点"); break;
            case TextEntity: s.Add("位置"); s.Add("字高"); s.Add("内容"); s.Add("旋转"); break;
        }
        return s;
    }

    /// <summary>把特性面板某行(label)编辑为 text, 返回改写后的新实体(复制原色/层); 不可编辑或解析失败返回 null。</summary>
    public static SceneEntity? WithEdited(SceneEntity e, string label, string text)
    {
        if (!EditableLabels(e).Contains(label)) return null;
        // 常规: 图层 / 颜色
        if (label == "图层")
        { var c = CloneShallow(e); if (c == null) return null; c.LayerName = string.IsNullOrWhiteSpace(text) ? "0" : text.Trim(); return c; }
        if (label == "颜色")
        {
            if (!TryColor(text, out float cr, out float cg, out float cb)) return null;
            var c = CloneShallow(e); if (c == null) return null; c.Cr = cr; c.Cg = cg; c.Cb = cb; return c;
        }
        if (label == "线型")
        {
            if (!DashPattern.IsKnownName(text)) return null;             // 未知线型名拒绝
            var c = CloneShallow(e); if (c == null) return null; c.Dash = DashPattern.ByName(text.Trim()); return c;
        }
        if (label == "线宽")
        {
            if (!LineWeightUtil.TryParse(text, out short lw)) return null;
            var c = CloneShallow(e); if (c == null) return null; c.LineWeight = lw; return c;
        }
        if (label == "透明度")
        {
            if (!TryTransp(text, out short tr)) return null;
            var c = CloneShallow(e); if (c == null) return null; c.Transparency = tr; return c;
        }
        if (label == "可见")
        {
            string t = text.Trim();
            bool? v = t switch { "是" or "显示" or "true" or "True" or "1" => true, "否" or "隐藏" or "false" or "False" or "0" => false, _ => (bool?)null };
            if (v == null) return null;                                  // 无法解析
            var c = CloneShallow(e); if (c == null) return null; c.Visible = v.Value; return c;
        }
        // 几何: 按类型 + 标签
        switch (e)
        {
            case LineEntity l when label == "起点" && TryXY(text, out double x, out double y): return Style(e, new LineEntity { X0 = x, Y0 = y, X1 = l.X1, Y1 = l.Y1 });
            case LineEntity l when label == "终点" && TryXY(text, out double x, out double y): return Style(e, new LineEntity { X0 = l.X0, Y0 = l.Y0, X1 = x, Y1 = y });
            case CircleEntity c when label == "圆心" && TryXY(text, out double x, out double y): return Style(e, new CircleEntity { Cx = x, Cy = y, Radius = c.Radius, Segments = c.Segments });
            case CircleEntity c when label == "半径" && TryD(text, out double d) && d > 1e-9: return Style(e, new CircleEntity { Cx = c.Cx, Cy = c.Cy, Radius = d, Segments = c.Segments });
            case RectEntity rc when label == "角点1" && TryXY(text, out double x, out double y): return Style(e, new RectEntity { X0 = x, Y0 = y, X1 = rc.X1, Y1 = rc.Y1 });
            case RectEntity rc when label == "角点2" && TryXY(text, out double x, out double y): return Style(e, new RectEntity { X0 = rc.X0, Y0 = rc.Y0, X1 = x, Y1 = y });
            case PolygonEntity pg when label == "圆心" && TryXY(text, out double x, out double y): return Style(e, new PolygonEntity { Cx = x, Cy = y, Radius = pg.Radius, Rotation = pg.Rotation, Sides = pg.Sides });
            case PolygonEntity pg when label == "半径" && TryD(text, out double d) && d > 1e-9: return Style(e, new PolygonEntity { Cx = pg.Cx, Cy = pg.Cy, Radius = d, Rotation = pg.Rotation, Sides = pg.Sides });
            case PolygonEntity pg when label == "边数" && int.TryParse(text.Trim(), out int n) && n >= 3 && n <= 512: return Style(e, new PolygonEntity { Cx = pg.Cx, Cy = pg.Cy, Radius = pg.Radius, Rotation = pg.Rotation, Sides = n });
            case PointEntity p when label == "坐标" && TryXY(text, out double x, out double y): return Style(e, new PointEntity { X = x, Y = y, Size = p.Size, Style = p.Style });
            case PointEntity p when label == "点大小" && TryD(text, out double d) && d > 1e-9: return Style(e, new PointEntity { X = p.X, Y = p.Y, Size = d, Style = p.Style });
            case PointEntity p when label == "点样式" && int.TryParse(text.Trim(), out int st) && st >= 0 && st <= 127: return Style(e, new PointEntity { X = p.X, Y = p.Y, Size = p.Size, Style = st });
            case ArcEntity a when label == "起点" && TryXY(text, out double x, out double y): return Style(e, new ArcEntity { X1 = x, Y1 = y, X2 = a.X2, Y2 = a.Y2, X3 = a.X3, Y3 = a.Y3, Segments = a.Segments });
            case ArcEntity a when label == "端点" && TryXY(text, out double x, out double y): return Style(e, new ArcEntity { X1 = a.X1, Y1 = a.Y1, X2 = a.X2, Y2 = a.Y2, X3 = x, Y3 = y, Segments = a.Segments });
            case TextEntity t when label == "位置" && TryXY(text, out double x, out double y): return Style(e, new TextEntity { X = x, Y = y, Height = t.Height, Text = t.Text, Rotation = t.Rotation });
            case TextEntity t when label == "字高" && TryD(text, out double d) && d > 1e-9: return Style(e, new TextEntity { X = t.X, Y = t.Y, Height = d, Text = t.Text, Rotation = t.Rotation });
            case TextEntity t when label == "内容": return Style(e, new TextEntity { X = t.X, Y = t.Y, Height = t.Height, Text = text, Rotation = t.Rotation });
            case TextEntity t when label == "旋转" && TryD(text, out double deg): return Style(e, new TextEntity { X = t.X, Y = t.Y, Height = t.Height, Text = t.Text, Rotation = deg * Math.PI / 180.0 });
        }
        return null;
    }

    // 透明度显示/解析(-1=随层, 0=不透明, 1..90=百分比)。对应原版特性面板"透明度"(int)。
    private static string TranspDisplay(short t) => t < 0 ? "随层" : t == 0 ? "不透明" : t + "%";
    private static bool TryTransp(string text, out short t)
    {
        t = -1; string s = text.Trim();
        if (s is "随层" or "ByLayer" or "bylayer" or "BYLAYER") { t = -1; return true; }
        if (s is "不透明" or "opaque" or "Opaque" or "OPAQUE") { t = 0; return true; }
        s = s.Replace("%", "").Trim();
        if (!int.TryParse(s, out int v) || v < 0 || v > 90) return false;
        t = (short)v; return true;
    }

    // 复制全样式(色/线型/线宽/透明度/可见/层 + 文字专属格式)到新实体 —— 属性编辑不丢样式。
    private static SceneEntity Style(SceneEntity src, SceneEntity dst)
    {
        dst.CopyStyleFrom(src);
        if (src is TextEntity ts && dst is TextEntity td)   // 文字五属中未被本次编辑改写的(对齐/字宽/倾斜)一并保留
        { td.HAlign = ts.HAlign; td.VAlign = ts.VAlign; td.WidthFactor = ts.WidthFactor; td.ObliqueAngle = ts.ObliqueAngle; }
        return dst;
    }

    // 同类型浅拷贝(改 图层/颜色 用, 几何不变)。
    private static SceneEntity? CloneShallow(SceneEntity e) => e switch
    {
        LineEntity l => Style(e, new LineEntity { X0 = l.X0, Y0 = l.Y0, X1 = l.X1, Y1 = l.Y1 }),
        CircleEntity c => Style(e, new CircleEntity { Cx = c.Cx, Cy = c.Cy, Radius = c.Radius, Segments = c.Segments }),
        RectEntity rc => Style(e, new RectEntity { X0 = rc.X0, Y0 = rc.Y0, X1 = rc.X1, Y1 = rc.Y1 }),
        PolygonEntity pg => Style(e, new PolygonEntity { Cx = pg.Cx, Cy = pg.Cy, Radius = pg.Radius, Rotation = pg.Rotation, Sides = pg.Sides }),
        PointEntity p => Style(e, new PointEntity { X = p.X, Y = p.Y, Size = p.Size, Style = p.Style }),
        ArcEntity a => Style(e, new ArcEntity { X1 = a.X1, Y1 = a.Y1, X2 = a.X2, Y2 = a.Y2, X3 = a.X3, Y3 = a.Y3, Segments = a.Segments }),
        TextEntity t => Style(e, new TextEntity { X = t.X, Y = t.Y, Height = t.Height, Text = t.Text, Rotation = t.Rotation }),
        PolylineEntity pl => Style(e, new PolylineEntity { Points = new List<(double, double)>(pl.Points), Closed = pl.Closed }),
        _ => null,
    };

    private static bool TryXY(string text, out double x, out double y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('(').TrimEnd(')').Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return t.Length >= 2 && double.TryParse(t[0], NumberStyles.Float, Inv, out x) && double.TryParse(t[1], NumberStyles.Float, Inv, out y);
    }

    private static bool TryD(string text, out double d) =>
        double.TryParse((text ?? "").Trim().TrimEnd('°'), NumberStyles.Float, Inv, out d);

    private static bool TryColor(string text, out float cr, out float cg, out float cb)
    {
        cr = cg = cb = 0;
        var s = (text ?? "").Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try
        {
            cr = Convert.ToInt32(s.Substring(0, 2), 16) / 255f;
            cg = Convert.ToInt32(s.Substring(2, 2), 16) / 255f;
            cb = Convert.ToInt32(s.Substring(4, 2), 16) / 255f;
            return true;
        }
        catch { return false; }
    }
}
