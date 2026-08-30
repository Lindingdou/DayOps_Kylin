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
    /// <summary>导出场景到 .dxf/.dwg（按扩展名）；返回写出的实体数。</summary>
    public static int Export(Scene scene, string path)
    {
        var doc = BuildDocument(scene);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dwg") { using var w = new DwgWriter(path, doc); w.Write(); }
        else { using var w = new DxfWriter(path, doc, false); w.Write(); }
        return scene.Count;
    }

    /// <summary>场景 → CadDocument（可单测：导出再读回比对）。</summary>
    public static CadDocument BuildDocument(Scene scene)
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
            cache[name] = l;
            return l;
        }

        foreach (var e in scene.Entities)
        {
            foreach (var ent in Map(e))
            {
                ent.Layer = LayerFor(e.LayerName);
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
                yield return new ACadSharp.Entities.TextEntity { InsertPoint = new XYZ(t.X, t.Y, 0), Height = t.Height, Value = t.Text };
                break;
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
