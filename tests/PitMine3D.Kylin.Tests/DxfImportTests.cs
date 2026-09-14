using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// DXF 导入功能回归：用 ACadSharp 生成 DXF → 用 DxfImportService 读回 → 断言几何提取正确。
/// 全托管、跨平台，Windows 上即可验证（对应"打开图纸看"这项功能）。
/// </summary>
[Collection("TextGeometry")]
public class DxfImportTests
{
    [Fact]
    public void Loads_line_and_circle_from_dxf()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_import_test.dxf");

        // 生成：一条线 (0,0)->(10,5) + 一个圆 (圆心 5,5, 半径 3)
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 5, 0) });
        doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3 });
        using (var writer = new DxfWriter(path, doc, false))
            writer.Write();

        var r = DxfImportService.Load(path);

        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.EntityCount);
        Assert.True(r.SegmentCount >= 8, $"segments={r.SegmentCount}");        // 1 线段 + 圆分段
        Assert.Equal(r.SegmentCount * 12, r.LineVertices.Length);             // 每段 2 顶点 × 6 float
        Assert.True(r.Bounds[0] <= 0.01, $"minX={r.Bounds[0]}");              // 含线起点 (0,0)
        Assert.True(r.Bounds[2] >= 9.99, $"maxX={r.Bounds[2]}");              // 含线终点 x=10
        Assert.Equal(1, r.TypeCounts["直线"]);                                // 对象管理器计数
        Assert.Equal(1, r.TypeCounts["圆"]);
        Assert.True(r.TypeGeometry.ContainsKey("直线"));                       // 类型几何(供高亮)
        Assert.True(r.TypeGeometry["圆"].Length > 0);

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Rejects_unsupported_extension()
    {
        var r = DxfImportService.Load("foo.xyz");   // 非 CAD 格式
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Loads_line_and_circle_from_dwg()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dwg_import_test.dwg");

        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 5, 0) });
        doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3 });
        using (var writer = new DwgWriter(path, doc))
            writer.Write();

        var r = DxfImportService.Load(path);        // 走 .dwg → DwgReader 分支

        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.EntityCount);
        Assert.True(r.SegmentCount >= 8, $"segments={r.SegmentCount}");
        Assert.Equal(1, r.TypeCounts["直线"]);
        Assert.Equal(1, r.TypeCounts["圆"]);

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Uses_entity_aci_color()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_color_test.dxf");

        var doc = new CadDocument();
        doc.Entities.Add(new Line
        {
            StartPoint = new XYZ(0, 0, 0),
            EndPoint = new XYZ(5, 0, 0),
            Color = new Color((short)1)   // ACI 1 = 红
        });
        using (var writer = new DxfWriter(path, doc, false))
            writer.Write();

        var r = DxfImportService.Load(path);

        Assert.True(r.Success, r.Error);
        Assert.True(r.SegmentCount >= 1);
        // 颜色在每顶点的 [3,4,5] 分量；红色应 r 分量最大
        float cr = r.LineVertices[3], cg = r.LineVertices[4], cb = r.LineVertices[5];
        Assert.True(cr > cg && cr > cb, $"expected red-dominant, got ({cr},{cg},{cb})");

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Loads_point_and_ellipse()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_pe_test.dxf");

        var doc = new CadDocument();
        doc.Entities.Add(new Point { Location = new XYZ(3, 3, 0) });
        doc.Entities.Add(new Ellipse
        {
            Center = new XYZ(0, 0, 0),
            MajorAxisEndPoint = new XYZ(10, 0, 0),
            RadiusRatio = 0.5
        });
        using (var writer = new DxfWriter(path, doc, false))
            writer.Write();

        var r = DxfImportService.Load(path);

        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.EntityCount);
        Assert.True(r.SegmentCount >= 2 + 12, $"segments={r.SegmentCount}");   // 点十字=2段, 椭圆多段

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Groups_geometry_by_layer()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_layer_test.dxf");

        var doc = new CadDocument();
        var wall = new ACadSharp.Tables.Layer("墙");
        var col = new ACadSharp.Tables.Layer("柱");
        doc.Layers.Add(wall);
        doc.Layers.Add(col);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(5, 0, 0), Layer = wall });
        doc.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 2, Layer = col });
        using (var writer = new DxfWriter(path, doc, false))
            writer.Write();

        var r = DxfImportService.Load(path);

        Assert.True(r.Success, r.Error);
        Assert.True(r.LayerGeometry.ContainsKey("墙"), "缺图层 墙");
        Assert.True(r.LayerGeometry.ContainsKey("柱"), "缺图层 柱");
        Assert.Equal(1, r.LayerCounts["墙"]);
        Assert.Equal(1, r.LayerCounts["柱"]);
        Assert.True(r.LayerGeometry["墙"].Length > 0);

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void ApplyInsert_scales_rotates_translates()
    {
        // (1,0) 缩放×2 → (2,0)；旋转 90° → (0,2)；平移 (10,5) → (10,7)
        var (x, y) = DxfImportService.ApplyInsert(1, 0, 10, 5, 2, 2, System.Math.PI / 2);
        Assert.Equal(10, x, 4);
        Assert.Equal(7, y, 4);
    }

    [Fact]
    public void Expands_block_reference()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_insert_test.dxf");

        var doc = new CadDocument();
        var block = new ACadSharp.Tables.BlockRecord("blk");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(100, 0, 0) });
        using (var writer = new DxfWriter(path, doc, false))
            writer.Write();

        var r = DxfImportService.Load(path);

        Assert.True(r.Success, r.Error);
        // 块内直线经插入点平移，包围盒应到达 x≈100（未展开则不会）
        Assert.True(r.Bounds[2] >= 100.0, $"maxX={r.Bounds[2]}（块未展开?）");

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void EvalBSpline_degree1_is_linear()
    {
        var px = new double[] { 0, 10 };
        var py = new double[] { 0, 0 };
        var pz = new double[] { 0, 0 };
        var knots = new double[] { 0, 0, 1, 1 };
        Assert.Equal(0, DxfImportService.EvalBSpline(px, py, pz, knots, 1, 0.0).x, 4);
        Assert.Equal(5, DxfImportService.EvalBSpline(px, py, pz, knots, 1, 0.5).x, 4);
        Assert.Equal(10, DxfImportService.EvalBSpline(px, py, pz, knots, 1, 1.0).x, 4);
    }

    [Fact]
    public void EvalBSpline_clamped_hits_endpoints()
    {
        var px = new double[] { 0, 1, 2, 3 };
        var py = new double[] { 0, 5, 5, 0 };
        var pz = new double[] { 0, 0, 0, 0 };
        var knots = new double[] { 0, 0, 0, 0, 1, 1, 1, 1 };   // clamped 三次
        var a = DxfImportService.EvalBSpline(px, py, pz, knots, 3, 0.0);
        var b = DxfImportService.EvalBSpline(px, py, pz, knots, 3, 1.0);
        Assert.Equal(0, a.x, 4); Assert.Equal(0, a.y, 4);     // 起点 = P0
        Assert.Equal(3, b.x, 4); Assert.Equal(0, b.y, 4);     // 终点 = P3
    }

    [Fact]
    public void MapDocument_produces_editable_entities()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3 });
        doc.Entities.Add(new Arc { Center = new XYZ(0, 0, 0), Radius = 2, StartAngle = 0, EndAngle = System.Math.PI / 2 });

        var r = DxfImportService.MapDocument(doc);
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.Entities.Count);
        Assert.Contains(r.Entities, e => e is LineEntity);
        Assert.Contains(r.Entities, e => e is CircleEntity);
        Assert.Contains(r.Entities, e => e is ArcEntity);
    }

    // 三维多段线(道路中线/断面线)要逐点保 Z: 只留一个平均标高的话, 转到三维就是压平的一条线。
    [Fact]
    public void MapDocument_保住三维多段线的逐点高程()
    {
        var doc = new CadDocument();
        var p3 = new Polyline3D();
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(0, 0, 100) });
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(10, 0, 130) });
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(20, 0, 70) });
        doc.Entities.Add(p3);

        var r = DxfImportService.MapDocument(doc);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(r.Entities));
        Assert.True(pl.Has3D, "逐点变高的三维多段线应保住 Zs");
        Assert.Equal(100, pl.ZAt(0), 4);
        Assert.Equal(130, pl.ZAt(1), 4);
        Assert.Equal(70, pl.ZAt(2), 4);
    }

    // 等高的三维多段线不必占一份 Zs, 一个标高就够(等高线的常态)。
    [Fact]
    public void MapDocument_等高的三维多段线只留标高()
    {
        var doc = new CadDocument();
        var p3 = new Polyline3D();
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(0, 0, 1330) });
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(10, 0, 1330) });
        doc.Entities.Add(p3);

        var r = DxfImportService.MapDocument(doc);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(r.Entities));
        Assert.False(pl.Has3D);
        Assert.Equal(1330, pl.Elevation, 4);
        Assert.Equal(1330, pl.ZAt(0), 4);
    }

    [Fact]
    public void MapDocument_expands_insert_to_world_position()
    {
        var doc = new CadDocument();
        var block = new ACadSharp.Tables.BlockRecord("blk");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(100, 0, 0) });

        var r = DxfImportService.MapDocument(doc);
        Assert.True(r.Success, r.Error);
        var line = Assert.IsType<LineEntity>(Assert.Single(r.Entities));
        Assert.Equal(100, line.X0, 4);          // 块内线平移到插入点
        Assert.Equal(101, line.X1, 4);
    }

    [Fact]
    public void MapDocument_imports_text()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new ACadSharp.Entities.TextEntity { InsertPoint = new XYZ(5, 6, 0), Height = 2.5, Value = "ZK01" });
        var r = DxfImportService.MapDocument(doc);
        Assert.True(r.Success, r.Error);
        var t = Assert.IsType<PitMine3D.Kylin.Cad.Draw.TextEntity>(Assert.Single(r.Entities));
        Assert.Equal("ZK01", t.Text);
        Assert.Equal(5, t.X, 4); Assert.Equal(2.5, t.Height, 4);
    }

    [Fact]
    public void MapDocument_captures_layer_name()
    {
        var doc = new CadDocument();
        var wall = new ACadSharp.Tables.Layer("墙");
        doc.Layers.Add(wall);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(5, 0, 0), Layer = wall });

        var r = DxfImportService.MapDocument(doc);
        Assert.True(r.Success, r.Error);
        Assert.Contains("墙", r.LayerOrder);
        Assert.Equal("墙", r.Entities[0].LayerName);
    }

    [Fact]
    public void SceneExport_roundtrip_dxf_preserves_types_and_layers()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, LayerName = "墙" });
        s.Add(new CircleEntity { Cx = 5, Cy = 5, Radius = 3, LayerName = "柱" });
        s.Add(new ArcEntity { X1 = 1, Y1 = 0, X2 = 0, Y2 = 1, X3 = -1, Y3 = 0 });
        var pl = new PolylineEntity { Closed = true };
        pl.Points.Add((0, 0)); pl.Points.Add((4, 0)); pl.Points.Add((4, 4));
        s.Add(pl);

        string path = Path.Combine(Path.GetTempPath(), "pm_scene_export.dxf");
        int written = SceneExportService.Export(s, path);
        Assert.Equal(4, written);

        var er = DxfImportService.LoadEntities(path);            // 读回
        Assert.True(er.Success, er.Error);
        Assert.Contains(er.Entities, e => e is LineEntity);
        Assert.Contains(er.Entities, e => e is CircleEntity);
        Assert.Contains(er.Entities, e => e is ArcEntity);
        Assert.Contains(er.Entities, e => e is PolylineEntity);   // 闭合多段线
        Assert.Contains("墙", er.LayerOrder);
        Assert.Contains("柱", er.LayerOrder);

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void SceneExport_roundtrip_dwg_preserves_types_and_layers()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, LayerName = "墙" });
        s.Add(new CircleEntity { Cx = 5, Cy = 5, Radius = 3, LayerName = "柱" });
        var pl = new PolylineEntity { Closed = true };
        pl.Points.Add((0, 0)); pl.Points.Add((4, 0)); pl.Points.Add((4, 4));
        s.Add(pl);

        string path = Path.Combine(Path.GetTempPath(), "pm_scene_export.dwg");
        int written = SceneExportService.Export(s, path);        // 走 DwgWriter
        Assert.Equal(3, written);

        var er = DxfImportService.LoadEntities(path);            // 走 DwgReader 读回
        Assert.True(er.Success, er.Error);
        Assert.Contains(er.Entities, e => e is LineEntity);
        Assert.Contains(er.Entities, e => e is CircleEntity);
        Assert.Contains(er.Entities, e => e is PolylineEntity);
        Assert.Contains("墙", er.LayerOrder);
        Assert.Contains("柱", er.LayerOrder);

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Text_roundtrips_through_dxf()
    {
        var s = new Scene();
        s.Add(new PitMine3D.Kylin.Cad.Draw.TextEntity { X = 5, Y = 6, Height = 2.5, Text = "ZK01" });
        string path = Path.Combine(Path.GetTempPath(), "pm_text_rt.dxf");
        SceneExportService.Export(s, path);
        var er = DxfImportService.LoadEntities(path);
        Assert.True(er.Success, er.Error);
        var t = Assert.IsType<PitMine3D.Kylin.Cad.Draw.TextEntity>(Assert.Single(er.Entities));
        Assert.Equal("ZK01", t.Text);
        Assert.Equal(2.5, t.Height, 3);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void LwPolyline_bulge_imports_as_arc_not_chord()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_bulge.dxf");
        var doc = new CadDocument();
        var lwp = new LwPolyline();
        lwp.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)) { Bulge = 1.0 });   // 半圆
        lwp.Vertices.Add(new LwPolyline.Vertex(new XY(2, 0)));
        doc.Entities.Add(lwp);
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var er = DxfImportService.LoadEntities(src);
        Assert.True(er.Success, er.Error);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(er.Entities));
        Assert.True(pl.Points.Count > 8, $"弧应细分为多点, 实得 {pl.Points.Count}");   // 非直线弦(2点)
        // 弧顶应鼓到 y≈1（半圆峰值）
        double maxY = 0; foreach (var p in pl.Points) if (p.y > maxY) maxY = p.y;
        Assert.True(maxY > 0.8, $"半圆弧顶 y 应≈1, 实得 {maxY:0.##}");

        try { File.Delete(src); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Solid_imports_as_closed_quad_polyline()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_solid.dxf");
        var doc = new CadDocument();
        var so = new Solid
        {
            FirstCorner = new XYZ(0, 0, 0),
            SecondCorner = new XYZ(4, 0, 0),
            ThirdCorner = new XYZ(0, 3, 0),
            FourthCorner = new XYZ(4, 3, 0),
        };
        doc.Entities.Add(so);
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var er = DxfImportService.LoadEntities(src);
        Assert.True(er.Success, er.Error);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(er.Entities));
        Assert.True(pl.Closed);
        Assert.Equal(4, pl.Points.Count);
        try { File.Delete(src); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Hatch_imports_boundary_as_closed_polyline()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_hatch.dxf");
        var doc = new CadDocument();
        var ha = new ACadSharp.Entities.Hatch();
        var bp = new ACadSharp.Entities.Hatch.BoundaryPath();
        bp.Edges.Add(new ACadSharp.Entities.Hatch.BoundaryPath.Line { Start = new XY(0, 0), End = new XY(4, 0) });
        bp.Edges.Add(new ACadSharp.Entities.Hatch.BoundaryPath.Line { Start = new XY(4, 0), End = new XY(4, 3) });
        bp.Edges.Add(new ACadSharp.Entities.Hatch.BoundaryPath.Line { Start = new XY(4, 3), End = new XY(0, 3) });
        bp.Edges.Add(new ACadSharp.Entities.Hatch.BoundaryPath.Line { Start = new XY(0, 3), End = new XY(0, 0) });
        ha.Paths.Add(bp);
        doc.Entities.Add(ha);
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var er = DxfImportService.LoadEntities(src);
        Assert.True(er.Success, er.Error);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(er.Entities));
        Assert.True(pl.Closed);
        Assert.Equal(4, pl.Points.Count);                 // 方形边界 4 点(首尾重合点已剥)
        try { File.Delete(src); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Polyline3D_imports_as_polyline()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_pl3d.dxf");
        var doc = new CadDocument();
        var p3 = new Polyline3D();
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(0, 0, 0) });
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(5, 0, 1) });
        p3.Vertices.Add(new Vertex3D { Location = new XYZ(5, 5, 2) });
        doc.Entities.Add(p3);
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var er = DxfImportService.LoadEntities(src);
        Assert.True(er.Success, er.Error);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(er.Entities));
        Assert.Equal(3, pl.Points.Count);
        try { File.Delete(src); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void XLine_imports_as_long_line_both_directions()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_xline.dxf");
        var doc = new CadDocument();
        doc.Entities.Add(new XLine { FirstPoint = new XYZ(0, 0, 0), Direction = new XYZ(1, 0, 0) });
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var er = DxfImportService.LoadEntities(src);
        Assert.True(er.Success, er.Error);
        var ln = Assert.IsType<LineEntity>(Assert.Single(er.Entities));
        Assert.True(ln.X1 - ln.X0 > 10000, "构造线应近似为长线段");   // 双向 ±10000
        try { File.Delete(src); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Text_rotation_roundtrips_through_dxf()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_txtrot.dxf");
        var doc = new CadDocument();
        doc.Entities.Add(new ACadSharp.Entities.TextEntity
        {
            InsertPoint = new XYZ(0, 0, 0), Height = 2, Rotation = System.Math.PI / 4, Value = "R"
        });
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var er = DxfImportService.LoadEntities(src);
        Assert.True(er.Success, er.Error);
        var t = Assert.IsType<PitMine3D.Kylin.Cad.Draw.TextEntity>(Assert.Single(er.Entities));
        Assert.Equal(System.Math.PI / 4, t.Rotation, 4);   // ACadSharp 旋转为弧度, 导入原样带入
        try { File.Delete(src); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Export_writes_layer_color_to_dxf_layer_table()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 5, Y1 = 0, LayerName = "墙" });
        var lt = new PitMine3D.Kylin.Cad.Draw.LayerTable();
        var wall = lt.EnsureImported("墙", 0.9f, 0.1f, 0.1f);   // 偏红图层
        var doc = SceneExportService.BuildDocument(s, lt);
        Assert.True(doc.Layers.Contains("墙"));
        var layer = doc.Layers["墙"];
        Assert.True(layer.Color.IsTrueColor);
        Assert.InRange(layer.Color.R, 220, 240);               // 0.9*255≈229
        Assert.InRange(layer.Color.G, 20, 35);
    }

    [Fact]
    public void Export_roundtrip_preserves_entity_color()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 5, Y1 = 0, Cr = 0.90f, Cg = 0.10f, Cb = 0.10f });   // 偏红
        string path = Path.Combine(Path.GetTempPath(), "pm_color_rt.dxf");
        SceneExportService.Export(s, path);

        var er = DxfImportService.LoadEntities(path);
        Assert.True(er.Success, er.Error);
        var e = Assert.Single(er.Entities);
        Assert.InRange(e.Cr, 0.85f, 0.95f);   // 真彩色往返(255 量化容差内)
        Assert.InRange(e.Cg, 0.05f, 0.15f);
        Assert.InRange(e.Cb, 0.05f, 0.15f);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Export_roundtrip_preserves_segments()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_exp_src.dxf");
        string outp = Path.Combine(Path.GetTempPath(), "pm_exp_out.dxf");

        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 5 });
        using (var w = new DxfWriter(src, doc, false)) w.Write();

        var r1 = DxfImportService.Load(src);
        int written = DxfExportService.Export(r1.LineVertices, outp);
        var r2 = DxfImportService.Load(outp);

        Assert.True(r2.Success, r2.Error);
        Assert.Equal(r1.SegmentCount, written);            // 每段写 1 条 Line
        Assert.Equal(r1.SegmentCount, r2.SegmentCount);    // 再导入段数一致

        try { File.Delete(src); File.Delete(outp); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Loads_leader_as_polyline()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_leader_test.dxf");
        var doc = new CadDocument();
        var ld = new Leader();
        ld.Vertices.Add(new XYZ(0, 0, 0));
        ld.Vertices.Add(new XYZ(5, 2, 0));
        ld.Vertices.Add(new XYZ(9, 2, 0));
        doc.Entities.Add(ld);
        using (var writer = new DxfWriter(path, doc, false)) writer.Write();

        var r = DxfImportService.Load(path);
        Assert.True(r.Success, r.Error);
        Assert.True(r.SegmentCount >= 2, $"segments={r.SegmentCount}");   // 3 顶点 → 2 段
        Assert.True(r.Bounds[2] >= 8.99, $"maxX={r.Bounds[2]}");          // 含末点 x=9
        Assert.Equal(1, r.TypeCounts["引线"]);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Loads_polyface_mesh_as_merged_triangle_mesh()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_pfm_test.dxf");
        var doc = new CadDocument();
        var pfm = new PolyfaceMesh();
        pfm.Vertices.Add(new VertexFaceMesh { Location = new XYZ(0, 0, 0) });    // 1
        pfm.Vertices.Add(new VertexFaceMesh { Location = new XYZ(10, 0, 0) });   // 2
        pfm.Vertices.Add(new VertexFaceMesh { Location = new XYZ(10, 10, 0) });  // 3
        pfm.Vertices.Add(new VertexFaceMesh { Location = new XYZ(0, 10, 0) });   // 4
        pfm.Faces.Add(new VertexFaceRecord { Index1 = 1, Index2 = 2, Index3 = 3, Index4 = 4 });   // 一个四边形面
        doc.Entities.Add(pfm);
        using (var writer = new DxfWriter(path, doc, false)) writer.Write();

        var er = DxfImportService.LoadEntities(path);
        Assert.True(er.Success, er.Error);
        var me = Assert.IsType<MeshEntity>(Assert.Single(er.Entities));   // 多面网格 → 一张三角网(不再是散碎的平面轮廓)
        Assert.Equal(4, me.Verts.Count);
        Assert.Equal(2, me.Tris.Count);                                     // 四边形面扇形剖分成 2 三角
        Assert.Contains(me.Verts, v => System.Math.Abs(v.x - 10) < 1e-6 && System.Math.Abs(v.y - 10) < 1e-6);
        Assert.Equal(1, er.TypeCounts["三角网"]);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Loads_mesh_as_merged_triangle_mesh()
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_mesh_test.dxf");
        var doc = new CadDocument();
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(10, 0, 0));
        mesh.Vertices.Add(new XYZ(10, 10, 0));
        mesh.Vertices.Add(new XYZ(0, 10, 0));
        mesh.Faces.Add(new int[] { 0, 1, 2, 3 });     // 一个四边形面(0-based 索引)
        doc.Entities.Add(mesh);
        using (var writer = new DxfWriter(path, doc, false)) writer.Write();

        var er = DxfImportService.LoadEntities(path);
        Assert.True(er.Success, er.Error);
        var me = Assert.IsType<MeshEntity>(Assert.Single(er.Entities));
        Assert.Equal(4, me.Verts.Count);
        Assert.Equal(2, me.Tris.Count);
        Assert.Contains(me.Verts, v => System.Math.Abs(v.x - 10) < 1e-6 && System.Math.Abs(v.y - 10) < 1e-6);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    // ── 三维面(3DFACE)按图层合网 —— 12煤.dxf 这类煤层底板面 1.5 万个 3DFACE 须合成一张可建模的三角网 ──

    private static Face3D Tri(double x0, double y0, double z0, double x1, double y1, double z1, double x2, double y2, double z2, string layer, CadDocument doc)
    {
        if (!doc.Layers.Contains(layer)) doc.Layers.Add(new ACadSharp.Tables.Layer(layer));
        var p2 = new XYZ(x2, y2, z2);
        return new Face3D { FirstCorner = new XYZ(x0, y0, z0), SecondCorner = new XYZ(x1, y1, z1), ThirdCorner = p2, FourthCorner = p2, Layer = doc.Layers[layer] };
    }

    [Fact]
    public void Face3D_merges_per_layer_into_one_welded_mesh()
    {
        var doc = new CadDocument();
        // 煤层 A: 两个共边三角形(共享 (10,0,5)-(0,10,6) 边); 煤层 B: 一个独立三角形
        doc.Entities.Add(Tri(0, 0, 5, 10, 0, 5, 0, 10, 6, "12煤", doc));
        doc.Entities.Add(Tri(10, 0, 5, 10, 10, 7, 0, 10, 6, "12煤", doc));
        doc.Entities.Add(Tri(100, 100, 1, 110, 100, 1, 100, 110, 1, "22煤", doc));

        var er = DxfImportService.MapDocument(doc);
        Assert.True(er.Success, er.Error);
        var meshes = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<MeshEntity>(er.Entities));
        Assert.Equal(2, meshes.Count);                                       // 每图层一张网, 不是每面一个实体
        Assert.Equal(2, er.Entities.Count);
        var a = meshes.Find(m => m.LayerName == "12煤")!;
        Assert.NotNull(a);
        Assert.Equal(4, a.Verts.Count);                                       // 共享顶点焊接: 6 角点 → 4 顶点
        Assert.Equal(2, a.Tris.Count);
        Assert.Equal("12煤", a.Name);                                         // 网名 = 图层名(对象树按名找网)
        Assert.Contains(a.Verts, v => System.Math.Abs(v.z - 7) < 1e-9);       // 三维面的 Z 逐点保住(不再压平成一个平均标高)
        Assert.Equal(0, a.Elevation, 9);                                      // 顶点已是绝对坐标, 标高不再叠加
        Assert.Equal(2, er.TypeCounts["三角网"]);                             // 对象树按产出类型计数
        Assert.True(er.Bounds[2] >= 110 - 1e-9 && er.Bounds[0] <= 1e-9, $"bounds={string.Join(",", er.Bounds)}");   // 包围盒含两张网
    }

    [Fact]
    public void Face3D_quad_fans_into_two_triangles()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Face3D { FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(10, 0, 0), ThirdCorner = new XYZ(10, 10, 0), FourthCorner = new XYZ(0, 10, 0) });
        var er = DxfImportService.MapDocument(doc);
        var me = Assert.IsType<MeshEntity>(Assert.Single(er.Entities));
        Assert.Equal(4, me.Verts.Count);
        Assert.Equal(2, me.Tris.Count);
    }

    [Fact]
    public void Face3D_inside_block_is_placed_by_insert_transform()
    {
        var doc = new CadDocument();
        var blk = new ACadSharp.Tables.BlockRecord("TIN");
        blk.Entities.Add(new Face3D { FirstCorner = new XYZ(0, 0, 3), SecondCorner = new XYZ(1, 0, 3), ThirdCorner = new XYZ(0, 1, 3), FourthCorner = new XYZ(0, 1, 3) });
        doc.BlockRecords.Add(blk);
        doc.Entities.Add(new Insert(blk) { InsertPoint = new XYZ(100, 200, 0) });
        var er = DxfImportService.MapDocument(doc);
        var me = Assert.IsType<MeshEntity>(Assert.Single(er.Entities));
        Assert.Contains(me.Verts, v => System.Math.Abs(v.x - 101) < 1e-9 && System.Math.Abs(v.y - 200) < 1e-9 && System.Math.Abs(v.z - 3) < 1e-9);
    }

    [Fact]
    public void Mesh_exports_as_3dface_and_reimports_as_mesh()
    {
        var scene = new Scene();
        var me = new MeshEntity("底板", new[] { (0.0, 0.0, 5.0), (10.0, 0.0, 5.0), (0.0, 10.0, 6.0), (10.0, 10.0, 7.0) }, new[] { (0, 1, 2), (1, 3, 2) }) { LayerName = "12煤" };
        scene.Add(me);
        var doc = SceneExportService.BuildDocument(scene);
        Assert.Equal(2, System.Linq.Enumerable.Count(System.Linq.Enumerable.OfType<Face3D>(doc.Entities)));   // 逐三角 3DFACE
        var back = Assert.IsType<MeshEntity>(Assert.Single(DxfImportService.MapDocument(doc).Entities));
        Assert.Equal(4, back.Verts.Count);
        Assert.Equal(2, back.Tris.Count);
        Assert.Equal("12煤", back.LayerName);
        Assert.Contains(back.Verts, v => System.Math.Abs(v.z - 7) < 1e-9);
    }

    // ── 文字高度归一化接入导入(原版在导入里自动做; 类本身的单测见 TextHeightNormalizerTests): kdf_export_v2.dxf 里 9139m 高的 MText 盖住半张图 ──

    [Fact]
    public void Import_normalizes_abnormally_tall_text_against_geometry_extent()
    {
        var doc = new CadDocument();
        var lp = new LwPolyline();                                 // 图幅 1000×1000
        lp.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
        lp.Vertices.Add(new LwPolyline.Vertex(new XY(1000, 1000)));
        doc.Entities.Add(lp);
        doc.Entities.Add(new ACadSharp.Entities.TextEntity { InsertPoint = new XYZ(1, 1, 0), Height = 5, Value = "正常" });
        doc.Entities.Add(new ACadSharp.Entities.TextEntity { InsertPoint = new XYZ(2, 2, 0), Height = 5, Value = "正常2" });
        doc.Entities.Add(new MText { InsertPoint = new XYZ(3, 3, 0), Height = 9139.2, Value = "巨型" });

        var er = DxfImportService.MapDocument(doc);
        Assert.True(er.Success, er.Error);
        var texts = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<PitMine3D.Kylin.Cad.Draw.TextEntity>(er.Entities));
        Assert.Equal(3, texts.Count);
        Assert.All(texts, t => Assert.Equal(5.0, t.Height, 6));    // 巨型文字被拉回典型高度, 正常文字不动
        Assert.Contains(er.Warnings, w => w.StartsWith("文字高度归一化:1 条"));
        Assert.Equal(3, er.TypeCounts["文字"]);                    // 文字也计入对象树(以前只数源类型, 漏掉 TEXT/MTEXT)
    }

    [Fact]
    public void MText_attachment_point_maps_to_anchor_alignment()
    {
        Assert.Equal((0, 2), DxfImportService.MTextAlign("TopLeft"));
        Assert.Equal((1, 1), DxfImportService.MTextAlign("MiddleCenter"));
        Assert.Equal((2, 0), DxfImportService.MTextAlign("BottomRight"));
        var doc = new CadDocument();
        doc.Entities.Add(new MText { InsertPoint = new XYZ(0, 0, 0), Height = 2, Value = "A", AttachmentPoint = AttachmentPointType.MiddleCenter });
        doc.Entities.Add(new MText { InsertPoint = new XYZ(0, 0, 0), Height = 2, Value = "B" });   // 默认左上
        var er = DxfImportService.MapDocument(doc);
        var texts = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<PitMine3D.Kylin.Cad.Draw.TextEntity>(er.Entities));
        var a = texts.Find(t => t.Text == "A")!; var b = texts.Find(t => t.Text == "B")!;
        Assert.Equal((1, 1), (a.HAlign, a.VAlign));
        Assert.Equal((0, 2), (b.HAlign, b.VAlign));                 // 插入点是文字框左上角 → 顶对齐, 不再整块上浮一行
    }

    [Fact]
    public void MText_alignment_and_rotation_survive_export_import_roundtrip()
    {
        var scene = new Scene();
        scene.Add(new PitMine3D.Kylin.Cad.Draw.TextEntity { X = 1, Y = 2, Height = 2, Rotation = 0.5, HAlign = 1, VAlign = 1, Text = "第一行" + "\n" + "第二行" });
        var back = Assert.IsType<PitMine3D.Kylin.Cad.Draw.TextEntity>(Assert.Single(DxfImportService.MapDocument(SceneExportService.BuildDocument(scene)).Entities));
        Assert.Equal((1, 1), (back.HAlign, back.VAlign));
        Assert.Equal(0.5, back.Rotation, 6);
        Assert.Equal(1.0, back.X, 9); Assert.Equal(2.0, back.Y, 9);
    }

    // ── 高程往返: 导入的等高线/三维线存回 DXF 再打开不能变成一张平图 ──

    [Fact]
    public void Elevation_and_per_vertex_z_survive_export_import_roundtrip()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 5, Y1 = 5, Elevation = 30 });
        scene.Add(new CircleEntity { Cx = 2, Cy = 2, Radius = 3, Elevation = 20 });
        scene.Add(new PolylineEntity { Points = { (0, 0), (10, 0), (10, 10) }, Elevation = 50 });                       // 等高线
        scene.Add(new PolylineEntity { Points = { (0, 0), (10, 0), (10, 10) }, Elevation = 100, Zs = new System.Collections.Generic.List<double> { -1, 0, 1 } });   // 三维线 99/100/101
        var doc = SceneExportService.BuildDocument(scene);
        Assert.Single(System.Linq.Enumerable.OfType<Polyline3D>(doc.Entities));                                       // 逐点 Z → POLYLINE(3D)
        var back = DxfImportService.MapDocument(doc);
        Assert.True(back.Success, back.Error);
        Assert.Contains(back.Entities, e => e is LineEntity && System.Math.Abs(e.Elevation - 30) < 1e-6);
        Assert.Contains(back.Entities, e => e is CircleEntity && System.Math.Abs(e.Elevation - 20) < 1e-6);
        Assert.Contains(back.Entities, e => e is PolylineEntity p && !p.Has3D && System.Math.Abs(p.Elevation - 50) < 1e-6);
        var p3 = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<PolylineEntity>(back.Entities), p => p.Has3D);
        Assert.Equal(99.0, p3.ZAt(0), 6); Assert.Equal(100.0, p3.ZAt(1), 6); Assert.Equal(101.0, p3.ZAt(2), 6);
    }

    [Fact]
    public void SummarizeWarnings_groups_skipped_types_and_keeps_others()
    {
        var w = new[] { "跳过未支持实体：Ole2Frame", "跳过未支持实体：Ole2Frame", "跳过未支持实体：Wipeout", "文字高度归一化:2 条异常高度已拉回 ~5.0m(图幅对角 1414m)", "文字高度归一化:2 条异常高度已拉回 ~5.0m(图幅对角 1414m)" };
        Assert.Equal("跳过 3 个未支持实体(Ole2Frame×2/Wipeout×1)；文字高度归一化:2 条异常高度已拉回 ~5.0m(图幅对角 1414m)", DxfImportService.SummarizeWarnings(w));
        Assert.Equal("", DxfImportService.SummarizeWarnings(new string[0]));
    }

    [Fact]
    public void ImportCache_roundtrips_mesh_entities()
    {
        string src = Path.Combine(Path.GetTempPath(), "pm_cache_mesh_src.dxf");
        File.WriteAllText(src, "stub");                            // 缓存只看源文件大小/修改时间, 内容无所谓
        var res = new DxfImportService.EntityImportResult();
        res.LayerOrder.Add("12煤"); res.LayerColors["12煤"] = (0.1f, 0.2f, 0.3f);
        var me = new MeshEntity("12煤", new[] { (0.0, 0.0, 5.0), (10.0, 0.0, 5.0), (0.0, 10.0, 6.0) }, new[] { (0, 1, 2) }) { LayerName = "12煤", Cr = 0.1f, Cg = 0.2f, Cb = 0.3f };
        res.Entities.Add(me);
        res.Entities.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "12煤" });
        res.TypeCounts["三角网"] = 1; res.TypeCounts["直线"] = 1;
        res.Bounds = new[] { 0.0, 0.0, 10.0, 10.0 };
        try
        {
            ImportCache.Save(src, res);
            Assert.True(File.Exists(ImportCache.PathFor(src)), "缓存文件应写出(三角网现已是可缓存类型)");
            Assert.True(ImportCache.TryLoad(src, out var back));
            Assert.Equal(2, back.Entities.Count);
            var bm = Assert.IsType<MeshEntity>(back.Entities[0]);
            Assert.Equal("12煤", bm.Name); Assert.Equal("12煤", bm.LayerName);
            Assert.Equal(3, bm.Verts.Count); Assert.Single(bm.Tris);
            Assert.Equal(6.0, bm.Verts[2].z, 9);
            Assert.Equal(0.2f, bm.Cg, 5);
            Assert.Equal(1, back.TypeCounts["三角网"]);
        }
        finally
        {
            try { File.Delete(ImportCache.PathFor(src)); File.Delete(src); } catch { /* 清理失败无碍 */ }
        }
    }

    [Fact]
    public void Text_alignment_width_oblique_survive_export_import_roundtrip()
    {
        // Kylin 文字(对齐/字宽/倾斜/旋转) → BuildDocument 导出 → MapDocument 导入 → 属性保留
        var scene = new Scene();
        scene.Add(new PitMine3D.Kylin.Cad.Draw.TextEntity
        { X = 5, Y = 3, Height = 2, Text = "AB", HAlign = 1, VAlign = 2, WidthFactor = 1.5, ObliqueAngle = 0.3, Rotation = 0.2 });
        var doc = SceneExportService.BuildDocument(scene);
        var res = DxfImportService.MapDocument(doc);
        var txt = System.Linq.Enumerable.FirstOrDefault(
            System.Linq.Enumerable.OfType<PitMine3D.Kylin.Cad.Draw.TextEntity>(res.Entities));
        Assert.NotNull(txt);
        Assert.Equal(1, txt!.HAlign);
        Assert.Equal(2, txt.VAlign);
        Assert.Equal(1.5, txt.WidthFactor, 2);
        Assert.Equal(0.3, txt.ObliqueAngle, 2);   // 弧度→度→弧度往返
        Assert.Equal(0.2, txt.Rotation, 2);
    }

    [Fact]
    public void Dashed_line_survives_export_import_roundtrip()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, Dash = PitMine3D.Kylin.Cad.Draw.DashPattern.ByName("虚线") });
        var doc = SceneExportService.BuildDocument(scene);
        var res = DxfImportService.MapDocument(doc);
        var line = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<LineEntity>(res.Entities));
        Assert.NotNull(line);
        Assert.NotNull(line!.Dash);                       // 虚线保留(非实线)
        Assert.Equal(new[] { 6.0, 3.0 }, line.Dash!);     // 样式往返(名 DASHED→ByName)
    }

    [Fact]
    public void Lineweight_survives_export_import_roundtrip()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, LineWeight = 25 });   // W25 = 0.25mm
        var doc = SceneExportService.BuildDocument(scene);
        var res = DxfImportService.MapDocument(doc);
        var line = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<LineEntity>(res.Entities));
        Assert.NotNull(line);
        Assert.Equal(25, line!.LineWeight);               // 线宽值往返(DXF LineWeightType)
    }

    [Fact]
    public void Multiline_text_survives_export_import_roundtrip_as_mtext()
    {
        var scene = new Scene();
        scene.Add(new PitMine3D.Kylin.Cad.Draw.TextEntity { X = 1, Y = 2, Height = 2, Text = "第一行\n第二行\n第三行" });
        var doc = SceneExportService.BuildDocument(scene);      // 多行 → MText(\P)
        var res = DxfImportService.MapDocument(doc);
        var txt = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<PitMine3D.Kylin.Cad.Draw.TextEntity>(res.Entities));
        Assert.NotNull(txt);
        Assert.Equal("第一行\n第二行\n第三行", txt!.Text);    // 多行往返为单一多行实体
    }

    [Fact]
    public void Transparency_survives_export_import_roundtrip()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, Transparency = 40 });   // 40% 透明
        var doc = SceneExportService.BuildDocument(scene);
        var res = DxfImportService.MapDocument(doc);
        var line = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<LineEntity>(res.Entities));
        Assert.NotNull(line);
        Assert.Equal(40, line!.Transparency);             // 透明度值往返(DXF Transparency)
    }

    [Fact]
    public void Export_selection_subset_yields_only_those_entities()
    {
        // "导出选中实体" 核心机制: 仅选中实体入临时场景 → 导出只含该子集
        var keep = new LineEntity { X0 = 0, Y0 = 0, X1 = 5, Y1 = 0 };
        var sub = new Scene();
        sub.Add(keep);                                       // 选中的 1 条线（不含场景其余实体）
        var doc = SceneExportService.BuildDocument(sub);
        var res = DxfImportService.MapDocument(doc);
        Assert.Single(System.Linq.Enumerable.OfType<LineEntity>(res.Entities));
        Assert.Empty(System.Linq.Enumerable.OfType<CircleEntity>(res.Entities));   // 未选中的圆不导出
        var line = System.Linq.Enumerable.First(System.Linq.Enumerable.OfType<LineEntity>(res.Entities));
        Assert.Equal(5, line.X1, 6);
    }

    [Fact]
    public void Layer_state_survives_export_import_roundtrip()
    {
        var layers = new LayerTable();
        var lyr = layers.EnsureImported("测试层", 1, 0, 0);
        lyr.Visible = false; lyr.Frozen = true; lyr.Locked = true;   // 关闭+冻结+锁定
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, LayerName = "测试层" });
        var doc = SceneExportService.BuildDocument(scene, layers);
        var res = DxfImportService.MapDocument(doc);
        Assert.True(res.LayerStates.ContainsKey("测试层"));
        var st = res.LayerStates["测试层"];
        Assert.False(st.on);       // 开关往返
        Assert.True(st.frozen);    // 冻结往返
        Assert.True(st.locked);    // 锁定往返
    }

    [Fact]
    public void Entity_hidden_state_survives_export_import_roundtrip()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, Visible = false });   // 逐实体隐藏
        var doc = SceneExportService.BuildDocument(scene);
        var res = DxfImportService.MapDocument(doc);
        var line = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<LineEntity>(res.Entities));
        Assert.NotNull(line);
        Assert.False(line!.Visible);   // 隐藏状态往返(DXF IsInvisible)
    }

    [Fact]
    public void LoadEntities_preserves_entity_elevation_z()
    {
        // 带高程的图元: 等高线 LwPolyline(Elevation=50) + 空间线(端点 Z=30) + 圆(圆心 Z=20)。
        // 回归"导入 DWG/DXF 只展 2D 图元"——可编辑导入须按源高程抬升, 否则全压平 Z=0。
        var doc = new CadDocument();
        var lp = new LwPolyline { Elevation = 50 };
        lp.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
        lp.Vertices.Add(new LwPolyline.Vertex(new XY(10, 0)));
        lp.Vertices.Add(new LwPolyline.Vertex(new XY(10, 10)));
        doc.Entities.Add(lp);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 30), EndPoint = new XYZ(5, 5, 30) });
        doc.Entities.Add(new Circle { Center = new XYZ(2, 2, 20), Radius = 3 });

        string path = Path.Combine(Path.GetTempPath(), "pm_dxf_elev.dxf");
        using (var writer = new DxfWriter(path, doc, false)) writer.Write();

        var er = DxfImportService.LoadEntities(path);
        Assert.True(er.Success, er.Error);
        Assert.Contains(er.Entities, e => e is PolylineEntity p && System.Math.Abs(p.Elevation - 50) < 1e-3);   // 等高线抬到 50
        Assert.Contains(er.Entities, e => e is LineEntity l && System.Math.Abs(l.Elevation - 30) < 1e-3);       // 线取端点 Z 均值
        Assert.Contains(er.Entities, e => e is CircleEntity c && System.Math.Abs(c.Elevation - 20) < 1e-3);     // 圆取圆心 Z
        // 反证: 若没有高程注入, 三者 Elevation 都会是 0（旧的"只展 2D"行为）
        Assert.DoesNotContain(er.Entities, e => (e is PolylineEntity || e is LineEntity || e is CircleEntity) && e.Elevation == 0);

        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }
}
