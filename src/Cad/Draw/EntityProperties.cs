using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    /// <summary>点相对圆心的极角(度, 0..360)。圆弧起止角显示用。</summary>
    private static double Deg(double cx, double cy, double px, double py)
    {
        // 先按显示精度定值再归一到 [0,360)：否则 0° 会因浮点负零绕成 "360°"(实测踩到)。
        double d = Math.Round(Math.Atan2(py - cy, px - cx) * 180.0 / Math.PI, 6);
        if (d == 0) return 0;          // 负零会显示成 "-0"
        if (d < 0) d += 360;
        return d >= 360 ? d - 360 : d;
    }

    /// <summary>逐实体覆盖的显示：未覆盖显「随样式」而不是 0 —— 0 是个合法值，显 0 会让人以为已经设过了。</summary>
    private static string Opt(double? v) => v is { } x ? N(x) : "随样式";

    /// <summary>颜色覆盖的显示：未覆盖显「随实体」。</summary>
    private static string ColOpt((float r, float g, float b)? c)
        => c is { } v ? $"#{(int)Math.Round(v.r * 255):X2}{(int)Math.Round(v.g * 255):X2}{(int)Math.Round(v.b * 255):X2}" : "随实体";

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
            case HatchEntity ha:
                r.Add(("几何", "图案", ha.PatternName));
                r.Add(("几何", "比例", ha.Scale > 1e-12 ? N(ha.Scale) : "自动"));
                r.Add(("几何", "角度", N(ha.Angle) + "°"));
                r.Add(("几何", "十字", ha.Cross ? "是" : "否"));
                r.Add(("几何", "边界点数", ha.Boundary.Count.ToString(Inv)));
                r.Add(("几何", "图案线数", ha.Lines().Count.ToString(Inv)));
                break;
            // 标注(§三二七): 原版是另开一扇「标注样式」窗逐条改; Kylin 的逐实体特性本来就在这块面板上,
            // 故把原版那几格直接落到这里 —— 分「直线和箭头 / 文字 / 几何」三组, 组名与格名照原窗。
            case DimensionEntity dm:
                r.Add(("直线和箭头", "箭头大小", Opt(dm.ArrowSize)));
                r.Add(("直线和箭头", "界线偏移", Opt(dm.ExtLineOffset)));
                r.Add(("直线和箭头", "界线延伸", Opt(dm.ExtLineExtension)));
                r.Add(("直线和箭头", "尺寸线颜色", ColOpt(dm.DimLineColor)));
                r.Add(("直线和箭头", "界线颜色", ColOpt(dm.ExtLineColor)));
                r.Add(("直线和箭头", "界线 1 可见", dm.ExtLine1Visible ? "是" : "否"));
                r.Add(("直线和箭头", "界线 2 可见", dm.ExtLine2Visible ? "是" : "否"));
                r.Add(("直线和箭头", "尺寸线可见", dm.DimLineVisible ? "是" : "否"));
                r.Add(("文字", "文字内容", string.IsNullOrEmpty(dm.TextOverride) ? "（测量值）" : dm.TextOverride!));
                r.Add(("文字", "文字高度", Opt(dm.TextHeight)));
                r.Add(("文字", "文字偏移", Opt(dm.TextOffset)));
                r.Add(("文字", "文字颜色", ColOpt(dm.TextColor)));
                r.Add(("文字", "文字位置 X", Opt(dm.TextPosX)));
                r.Add(("文字", "文字位置 Y", Opt(dm.TextPosY)));
                r.Add(("文字", "小数位", dm.DecimalPlaces is { } dp ? dp.ToString(Inv) : "随样式"));
                r.Add(("几何", "测量值", N(dm.Measurement)));   // 派生, 只读 —— 挪定义点它自己会变
                if (dm.Kind == DimensionEntity.DimKind.Radial)
                {
                    r.Add(("几何", "圆心", F(dm.Cx, dm.Cy)));
                    r.Add(("几何", "半径", N(dm.Radius)));
                    r.Add(("几何", "引出方向", F(dm.DirX, dm.DirY)));
                }
                else
                {
                    r.Add(("几何", "定义点1", F(dm.X1, dm.Y1)));
                    r.Add(("几何", "定义点2", F(dm.X2, dm.Y2)));
                    r.Add(("几何", "尺寸线通过点", F(dm.OffX, dm.OffY)));
                }
                break;
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
                if (cc != null)   // 起始/终止角(只读派生; 原版圆弧属性有这两行, 那边是可编辑的圆心角表示)
                {
                    r.Add(("几何", "起始角度", N(Deg(cc.Value.Item1, cc.Value.Item2, a.X1, a.Y1)) + "°"));
                    r.Add(("几何", "终止角度", N(Deg(cc.Value.Item1, cc.Value.Item2, a.X3, a.Y3)) + "°"));
                }
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
            case MeshEntity me:
                r.Add(("几何", "名称", me.Name));
                r.Add(("几何", "顶点数", me.VertexCount.ToString(Inv)));
                r.Add(("几何", "三角数", me.TriangleCount.ToString(Inv)));
                var mb = me.Bounds;
                r.Add(("几何", "范围X", N(mb.minX) + " ~ " + N(mb.maxX)));
                r.Add(("几何", "范围Y", N(mb.minY) + " ~ " + N(mb.maxY)));
                r.Add(("几何", "高程", N(mb.minZ) + " ~ " + N(mb.maxZ)));
                r.Add(("几何", "表面积", N(me.SurfaceArea())));
                break;
            case PointCloudEntity pc:
                r.Add(("几何", "名称", pc.Name));
                r.Add(("几何", "点数", pc.PointCount.ToString(Inv)));
                var pb = pc.Bounds;
                r.Add(("几何", "范围X", N(pb.minX) + " ~ " + N(pb.maxX)));
                r.Add(("几何", "范围Y", N(pb.minY) + " ~ " + N(pb.maxY)));
                r.Add(("几何", "高程", N(pb.minZ) + " ~ " + N(pb.maxZ)));
                r.Add(("几何", "逐点色", pc.HasColors ? "有" : "无"));
                r.Add(("几何", "真实色", pc.HasRgb ? "有" : "无"));
                r.Add(("几何", "法向缓存", pc.HasNormals ? "有" : "无"));
                if (pc.Source != null) r.Add(("几何", "源文件", System.IO.Path.GetFileName(pc.Source)));
                break;
            case PolylineEntity pl:
                r.Add(("几何", "闭合", pl.Closed ? "是" : "否"));
                r.Add(("几何", "顶点数", pl.Points.Count.ToString(Inv)));
                if (pl.Has3D) r.Add(("几何", "高程", N(pl.Zs!.Min()) + " ~ " + N(pl.Zs!.Max())));
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
            case HatchEntity: s.Add("图案"); s.Add("比例"); s.Add("角度"); s.Add("十字"); break;
            case DimensionEntity dm:
                foreach (var k in new[] { "箭头大小", "界线偏移", "界线延伸", "尺寸线颜色", "界线颜色",
                                          "界线 1 可见", "界线 2 可见", "尺寸线可见",
                                          "文字内容", "文字高度", "文字偏移", "文字颜色", "文字位置 X", "文字位置 Y", "小数位" })
                    s.Add(k);
                // 几何按种类给: 半径标注没有定义点1/2, 对齐标注没有圆心/半径
                if (dm.Kind == DimensionEntity.DimKind.Radial) { s.Add("圆心"); s.Add("半径"); s.Add("引出方向"); }
                else { s.Add("定义点1"); s.Add("定义点2"); s.Add("尺寸线通过点"); }
                break;
        }
        return s;
    }

    /// <summary>把特性面板某行(label)编辑为 text, 返回改写后的新实体(复制原色/层); 不可编辑或解析失败返回 null。</summary>
    public static SceneEntity? WithEdited(SceneEntity e, string label, string text)
    {
        if (!EditableLabels(e).Contains(label)) return null;
        if (e is DimensionEntity dim) { var d2 = EditDimension(dim, label, text); if (d2 != null) return d2; }
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
            // 填充: 图案/比例/角度/十字 就地改, 改完重算图案(整块跟着变, 不用重画)
            case HatchEntity h when label == "图案" && HatchPatternLibrary.ByName(text.Trim()) != null:
            { var c = h.Clone(); c.PatternName = HatchPatternLibrary.ByName(text.Trim())!.Name; return c; }
            case HatchEntity h when label == "比例" && (text.Trim() is "自动" or "auto" || TryD(text, out _)):
            { var c = h.Clone(); c.Scale = text.Trim() is "自动" or "auto" ? 0 : (TryD(text, out double sc) && sc > 0 ? sc : 0); return c; }
            case HatchEntity h when label == "角度" && TryD(text.Replace("°", ""), out double ang):
            { var c = h.Clone(); c.Angle = ang; return c; }
            case HatchEntity h when label == "十字":
            {
                string tt = text.Trim();
                bool? on = tt switch { "是" or "开" or "true" or "1" => true, "否" or "关" or "false" or "0" => false, _ => (bool?)null };
                if (on == null) return null;
                var c = h.Clone(); c.Cross = on.Value; return c;
            }
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
    /// <summary>
    /// 标注那几格的编辑（§三二七，对应原版「标注样式」窗里的每一格）。
    /// 长度/位置类的格子**留空即清除覆盖**回到随样式 —— 原版编辑器也是清空即恢复；
    /// 不能拿 0 当"没设"，0 本身是合法值。返回 null 表示这一格不归本方法管（交回通用路径）。
    /// </summary>
    private static SceneEntity? EditDimension(DimensionEntity e, string label, string text)
    {
        var c = (DimensionEntity)e.Apply(new Affine2(1, 0, 0, 1, 0, 0));
        string t = (text ?? "").Trim();
        bool blank = t.Length == 0 || t == "随样式" || t == "随实体" || t == "（测量值）";

        double? Num()
        {
            if (blank) return null;
            return double.TryParse(t, NumberStyles.Float, Inv, out double v) ? v : double.NaN;   // NaN = 解析失败
        }
        bool Bad(double? v) => v is { } x && double.IsNaN(x);

        switch (label)
        {
            case "箭头大小": { var v = Num(); if (Bad(v)) return null; c.ArrowSize = v; return c; }
            case "界线偏移": { var v = Num(); if (Bad(v)) return null; c.ExtLineOffset = v; return c; }
            case "界线延伸": { var v = Num(); if (Bad(v)) return null; c.ExtLineExtension = v; return c; }
            case "文字高度": { var v = Num(); if (Bad(v)) return null; c.TextHeight = v; return c; }
            case "文字偏移": { var v = Num(); if (Bad(v)) return null; c.TextOffset = v; return c; }
            case "文字位置 X": { var v = Num(); if (Bad(v)) return null; c.TextPosX = v; return c; }
            case "文字位置 Y": { var v = Num(); if (Bad(v)) return null; c.TextPosY = v; return c; }
            case "小数位":
            {
                if (blank) { c.DecimalPlaces = null; return c; }
                if (!int.TryParse(t, NumberStyles.Integer, Inv, out int dp) || dp < 0 || dp > 9) return null;
                c.DecimalPlaces = dp; return c;
            }
            case "文字内容": c.TextOverride = blank ? null : t; return c;
            case "尺寸线颜色":
            case "界线颜色":
            case "文字颜色":
            {
                (float, float, float)? col = null;
                if (!blank)
                {
                    if (!TryColor(t, out float cr, out float cg, out float cb)) return null;
                    col = (cr, cg, cb);
                }
                if (label == "尺寸线颜色") c.DimLineColor = col;
                else if (label == "界线颜色") c.ExtLineColor = col;
                else c.TextColor = col;
                return c;
            }
            case "界线 1 可见": c.ExtLine1Visible = IsYes(t); return c;
            case "界线 2 可见": c.ExtLine2Visible = IsYes(t); return c;
            case "尺寸线可见": c.DimLineVisible = IsYes(t); return c;
            case "定义点1": { if (!TryXY(t, out double x, out double y)) return null; c.X1 = x; c.Y1 = y; return c; }
            case "定义点2": { if (!TryXY(t, out double x, out double y)) return null; c.X2 = x; c.Y2 = y; return c; }
            case "尺寸线通过点": { if (!TryXY(t, out double x, out double y)) return null; c.OffX = x; c.OffY = y; return c; }
            case "圆心": { if (!TryXY(t, out double x, out double y)) return null; c.Cx = x; c.Cy = y; return c; }
            case "引出方向": { if (!TryXY(t, out double x, out double y)) return null; c.DirX = x; c.DirY = y; return c; }
            case "半径":
            {
                if (!double.TryParse(t, NumberStyles.Float, Inv, out double rr) || rr <= 0) return null;
                c.Radius = rr; return c;
            }
            default: return null;   // 常规那几格交回通用路径
        }
    }

    private static bool IsYes(string t) => t is "是" or "true" or "True" or "1" or "开" or "可见";

    private static SceneEntity? CloneShallow(SceneEntity e) => e switch
    {
        LineEntity l => Style(e, new LineEntity { X0 = l.X0, Y0 = l.Y0, X1 = l.X1, Y1 = l.Y1 }),
        CircleEntity c => Style(e, new CircleEntity { Cx = c.Cx, Cy = c.Cy, Radius = c.Radius, Segments = c.Segments }),
        RectEntity rc => Style(e, new RectEntity { X0 = rc.X0, Y0 = rc.Y0, X1 = rc.X1, Y1 = rc.Y1 }),
        PolygonEntity pg => Style(e, new PolygonEntity { Cx = pg.Cx, Cy = pg.Cy, Radius = pg.Radius, Rotation = pg.Rotation, Sides = pg.Sides }),
        PointEntity p => Style(e, new PointEntity { X = p.X, Y = p.Y, Size = p.Size, Style = p.Style }),
        ArcEntity a => Style(e, new ArcEntity { X1 = a.X1, Y1 = a.Y1, X2 = a.X2, Y2 = a.Y2, X3 = a.X3, Y3 = a.Y3, Segments = a.Segments }),
        TextEntity t => Style(e, new TextEntity { X = t.X, Y = t.Y, Height = t.Height, Text = t.Text, Rotation = t.Rotation }),
        PolylineEntity pl => Style(e, new PolylineEntity { Points = new List<(double, double)>(pl.Points), Closed = pl.Closed, Zs = pl.Has3D ? new List<double>(pl.Zs!) : null }),
        MeshEntity me => Style(e, new MeshEntity(me.Name, me.Verts, me.Tris)),
        PointCloudEntity pc => Style(e, pc.Clone()),
        HatchEntity ha => ha.Clone(),   // Clone 里已带样式
        // 标注字段多, 手抄一遍迟早漏; 借它自己的 Apply(单位阵) 深拷 —— 加字段只改 Apply 一处
        DimensionEntity dm => dm.Apply(new Affine2(1, 0, 0, 1, 0, 0)),
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
