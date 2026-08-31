using System;
using System.Collections.Generic;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using PitMine3D.Kylin.Cad.Draw;
using AcLayer = ACadSharp.Tables.Layer;
using DrawText = PitMine3D.Kylin.Cad.Draw.TextEntity;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 场景实体级导出 —— 把绘制场景实体映射为 ACadSharp 原生实体写 .dxf/.dwg（保留图层）。
/// 直线/圆/圆弧/点 用原生实体（非折线化，高保真）；矩形/多段线/正多边形 用 LwPolyline。
/// 对应原版 Save-As(DXF/DWG) 的托管路径。纯逻辑（除写文件），BuildDocument 可单测。
/// </summary>
public static class SceneExportService
{
    /// <summary>导出场景到 .dxf/.dwg（按扩展名）；返回写出的实体数。layers 非空则把图层颜色写入 DXF 图层表。</summary>
    public static int Export(Scene scene, string path, LayerTable? layers = null)
    {
        var doc = BuildDocument(scene, layers);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dwg") { using var w = new DwgWriter(path, doc); w.Write(); }
        else { using var w = new DxfWriter(path, doc, false); w.Write(); }
        return scene.Count;
    }

    /// <summary>场景 → CadDocument（可单测：导出再读回比对）。layers 非空则图层表带上各层颜色。</summary>
    public static CadDocument BuildDocument(Scene scene, LayerTable? layers = null)
    {
        var doc = new CadDocument();
        var cache = new Dictionary<string, AcLayer>();
        AcLayer LayerFor(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "0";
            if (cache.TryGetValue(name, out var c)) return c;
            AcLayer l;
            if (doc.Layers.Contains(name)) l = doc.Layers[name];   // "0" 等内建层已存在
            else { l = new AcLayer(name); doc.Layers.Add(l); }
            var src = layers?.Get(name);                            // 场景图层色 → DXF 图层表(真彩色)
            if (src != null)
            {
                l.Color = new Color(
                    (byte)Math.Clamp(src.Cr * 255f, 0, 255),
                    (byte)Math.Clamp(src.Cg * 255f, 0, 255),
                    (byte)Math.Clamp(src.Cb * 255f, 0, 255));
                l.IsOn = src.Visible;                                // 图层状态 round-trip: 开/冻结/锁定
                var f = ACadSharp.Tables.LayerFlags.None;
                if (src.Frozen) f |= ACadSharp.Tables.LayerFlags.Frozen;
                if (src.Locked) f |= ACadSharp.Tables.LayerFlags.Locked;
                l.Flags = f;
            }
            cache[name] = l;
            return l;
        }

        var ltCache = new Dictionary<string, ACadSharp.Tables.LineType>();
        ACadSharp.Tables.LineType? LineTypeFor(double[]? dash)   // 线型: 虚线样式→ DXF LineType(段长±=画/空), 实线返 null
        {
            if (dash == null || dash.Length == 0) return null;
            string key = string.Join("_", dash);
            if (ltCache.TryGetValue(key, out var c)) return c;
            // 名用标准线型名(ByName 逆)→ 再导入可识别; 段长仍精确写入
            string name = PitMine3D.Kylin.Cad.Draw.DashPattern.NameOf(dash);
            if (doc.LineTypes.Contains(name)) { var ex = doc.LineTypes[name]; ltCache[key] = ex; return ex; }   // 同名复用(防重复)
            var lt = new ACadSharp.Tables.LineType(name);
            for (int i = 0; i < dash.Length; i++)
                lt.AddSegment(new ACadSharp.Tables.LineType.Segment { Length = (i % 2 == 0) ? dash[i] : -dash[i] });
            doc.LineTypes.Add(lt);
            ltCache[key] = lt;
            return lt;
        }

        foreach (var e in scene.Entities)
        {
            var lt = LineTypeFor(e.Dash);
            foreach (var ent in Map(e))
            {
                ent.Layer = LayerFor(e.LayerName);
                ent.Color = new Color(                        // 场景烘焙的 RGB → 真彩色（保留可见颜色）
                    (byte)Math.Clamp(e.Cr * 255f, 0, 255),
                    (byte)Math.Clamp(e.Cg * 255f, 0, 255),
                    (byte)Math.Clamp(e.Cb * 255f, 0, 255));
                if (lt != null) ent.LineType = lt;            // 线型导出保真
                ent.LineWeight = (ACadSharp.LineWeightType)e.LineWeight;   // 线宽 round-trip 保值(不渲染变宽, 但存回 DXF)
                ent.IsInvisible = !e.Visible;                             // 逐实体隐藏状态 round-trip(隐藏对象→存盘→重开仍隐)
                doc.Entities.Add(ent);
            }
        }
        return doc;
    }

    private static IEnumerable<Entity> Map(SceneEntity e)
    {
        switch (e)
        {
            case LineEntity l:
                yield return new Line { StartPoint = new XYZ(l.X0, l.Y0, 0), EndPoint = new XYZ(l.X1, l.Y1, 0) };
                break;
            case CircleEntity c:
                yield return new Circle { Center = new XYZ(c.Cx, c.Cy, 0), Radius = c.Radius };
                break;
            case ArcEntity a:
                yield return MapArc(a);
                break;
            case PointEntity p:
                yield return new Point { Location = new XYZ(p.X, p.Y, 0) };
                break;
            case DrawText t:
            {
                var te = new ACadSharp.Entities.TextEntity
                {
                    InsertPoint = new XYZ(t.X, t.Y, 0), Height = t.Height, Rotation = t.Rotation, Value = t.Text,
                    WidthFactor = t.WidthFactor > 0 ? t.WidthFactor : 1, ObliqueAngle = t.ObliqueAngle * 180.0 / System.Math.PI,
                };
                if (t.HAlign != 0 || t.VAlign != 0)   // 对齐: 设枚举 + AlignmentPoint(非左/基线时 DXF 用对齐点)
                {
                    te.HorizontalAlignment = t.HAlign switch { 1 => ACadSharp.Entities.TextHorizontalAlignment.Center, 2 => ACadSharp.Entities.TextHorizontalAlignment.Right, _ => ACadSharp.Entities.TextHorizontalAlignment.Left };
                    te.VerticalAlignment = t.VAlign switch { 1 => ACadSharp.Entities.TextVerticalAlignmentType.Middle, 2 => ACadSharp.Entities.TextVerticalAlignmentType.Top, _ => ACadSharp.Entities.TextVerticalAlignmentType.Baseline };
                    te.AlignmentPoint = new XYZ(t.X, t.Y, 0);
                }
                yield return te;
                break;
            }
            case RectEntity r:
                yield return Poly(new[] { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) }, true);
                break;
            case PolygonEntity pg:
                yield return Poly(PolygonVerts(pg), true);
                break;
            case PolylineEntity pl:
                yield return Poly(pl.Points, pl.Closed);
                break;
        }
    }

    private static Entity MapArc(ArcEntity a)
    {
        var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
        if (cc == null) return new Line { StartPoint = new XYZ(a.X1, a.Y1, 0), EndPoint = new XYZ(a.X3, a.Y3, 0) };
        var (cx, cy, r) = cc.Value;
        double a1 = Math.Atan2(a.Y1 - cy, a.X1 - cx), am = Math.Atan2(a.Y2 - cy, a.X2 - cx), a3 = Math.Atan2(a.Y3 - cy, a.X3 - cx);
        double N(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
        bool ccw = N(am - a1) <= N(a3 - a1);      // 经中点是否 CCW
        return new Arc { Center = new XYZ(cx, cy, 0), Radius = r, StartAngle = ccw ? a1 : a3, EndAngle = ccw ? a3 : a1 };
    }

    private static IEnumerable<(double x, double y)> PolygonVerts(PolygonEntity pg)
    {
        for (int i = 0; i < pg.Sides; i++)
        {
            double ang = pg.Rotation + 2 * Math.PI * i / pg.Sides;
            yield return (pg.Cx + pg.Radius * Math.Cos(ang), pg.Cy + pg.Radius * Math.Sin(ang));
        }
    }

    private static LwPolyline Poly(IEnumerable<(double x, double y)> pts, bool closed)
    {
        var lp = new LwPolyline { IsClosed = closed };
        foreach (var p in pts) lp.Vertices.Add(new LwPolyline.Vertex(new XY(p.x, p.y)));
        return lp;
    }
}
