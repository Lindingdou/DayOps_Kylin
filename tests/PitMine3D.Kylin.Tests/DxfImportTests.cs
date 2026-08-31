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
    public void Loads_polyface_mesh_faces_as_wireframe_polylines()
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
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(er.Entities));
        Assert.True(pl.Closed);
        Assert.Equal(4, pl.Points.Count);                                   // 四边形面 → 4 点闭合折线
        Assert.Contains(pl.Points, p => System.Math.Abs(p.x - 10) < 1e-6 && System.Math.Abs(p.y - 10) < 1e-6);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
    }

    [Fact]
    public void Loads_mesh_faces_as_wireframe_polylines()
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
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(er.Entities));
        Assert.True(pl.Closed);
        Assert.Equal(4, pl.Points.Count);
        Assert.Contains(pl.Points, p => System.Math.Abs(p.x - 10) < 1e-6 && System.Math.Abs(p.y - 10) < 1e-6);
        try { File.Delete(path); } catch { /* 清理失败无碍 */ }
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
}
