using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using PitMine3D.Kylin.Cad;
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
    public void Rejects_non_dxf()
    {
        var r = DxfImportService.Load("foo.dwg");
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
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
}
