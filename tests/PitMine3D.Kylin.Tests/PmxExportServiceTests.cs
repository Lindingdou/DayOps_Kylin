using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>PMX 工程导出回归 —— Kylin 实体 → PmxExportService 写 → PmxImportService 读 → 往返一致(与导入互验)。</summary>
public class PmxExportServiceTests
{
    static PmxImportService.Result RoundTrip(IEnumerable<SceneEntity> ents)
    {
        var (bytes, _) = PmxExportService.Build(ents);
        string tmp = Path.GetTempFileName();
        try { File.WriteAllBytes(tmp, bytes); return PmxImportService.Load(tmp); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static List<SceneEntity> Sample()
    {
        var pl = new PolylineEntity { Closed = true, LayerName = "L1" };
        pl.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) });
        return new List<SceneEntity>
        {
            new LineEntity { X0 = 10, Y0 = 20, X1 = 30, Y1 = 40, Cr = 0.2f, Cg = 0.4f, Cb = 0.6f, LayerName = "L1" },
            new PointEntity { X = 50, Y = 60, LayerName = "L2" },
            pl,
            new CircleEntity { Cx = 5, Cy = 5, Radius = 3, LayerName = "L1" },
            new TextEntity { X = 1, Y = 2, Height = 2.5, Rotation = 0.5, Text = "标注", LayerName = "L2" },
        };
    }

    [Fact]
    public void Polygon_exports_as_closed_polyline_with_vertices()
    {
        // 正方形(4 边, 半径10, 旋转0): 顶点在 0/90/180/270° → (10,0)(0,10)(-10,0)(0,-10)
        var pg = new PolygonEntity { Cx = 0, Cy = 0, Radius = 10, Sides = 4, Rotation = 0, LayerName = "L1" };
        var r = RoundTrip(new List<SceneEntity> { pg });
        Assert.True(r.Success, r.Error);
        var pl = (PolylineEntity)r.Entities.First(e => e is PolylineEntity);
        Assert.True(pl.Closed);
        Assert.Equal(4, pl.Points.Count);
        Assert.Equal(10, pl.Points[0].Item1, 6); Assert.Equal(0, pl.Points[0].Item2, 6);   // 0°
        Assert.Equal(0, pl.Points[1].Item1, 6); Assert.Equal(10, pl.Points[1].Item2, 6);   // 90°
        Assert.Equal(-10, pl.Points[2].Item1, 6);                                            // 180°
    }

    [Fact]
    public void Round_trip_preserves_entity_counts()
    {
        var r = RoundTrip(Sample());
        Assert.True(r.Success, r.Error);
        Assert.Equal(1, r.Lines);
        Assert.Equal(1, r.Points);
        Assert.Equal(1, r.Polylines);
        Assert.Equal(1, r.Circles);
        Assert.Equal(1, r.Texts);
    }

    [Fact]
    public void Round_trip_preserves_line_geometry_and_color()
    {
        var r = RoundTrip(Sample());
        var le = (LineEntity)r.Entities.First(e => e is LineEntity);
        Assert.Equal(10, le.X0, 6); Assert.Equal(20, le.Y0, 6);
        Assert.Equal(30, le.X1, 6); Assert.Equal(40, le.Y1, 6);
        Assert.Equal(0.2f, le.Cr, 2); Assert.Equal(0.4f, le.Cg, 2); Assert.Equal(0.6f, le.Cb, 2);   // 字节量化容差
        Assert.Equal("L1", le.LayerName);
    }

    [Fact]
    public void Round_trip_preserves_polyline_circle_text()
    {
        var r = RoundTrip(Sample());
        var pl = (PolylineEntity)r.Entities.First(e => e is PolylineEntity);
        Assert.True(pl.Closed); Assert.Equal(3, pl.Points.Count);
        Assert.Equal(10, pl.Points[1].Item1, 6);

        var ce = (CircleEntity)r.Entities.First(e => e is CircleEntity);
        Assert.Equal(5, ce.Cx, 6); Assert.Equal(5, ce.Cy, 6); Assert.Equal(3, ce.Radius, 6);

        var te = (TextEntity)r.Entities.First(e => e is TextEntity);
        Assert.Equal(1, te.X, 6); Assert.Equal(2, te.Y, 6);
        Assert.Equal(2.5, te.Height, 6); Assert.Equal(0.5, te.Rotation, 6);
        Assert.Equal("标注", te.Text);
    }

    [Fact]
    public void Round_trip_preserves_arc_curve()
    {
        // CCW 半圆 P1(10,0) P2(0,10) P3(-10,0): 圆心(0,0) r=10, a0=0/a1=π。往返后三点精确还原(对称→角中点=几何中点)。
        var arc = new ArcEntity { X1 = 10, Y1 = 0, X2 = 0, Y2 = 10, X3 = -10, Y3 = 0, LayerName = "L1" };
        var r = RoundTrip(new System.Collections.Generic.List<SceneEntity> { arc });
        Assert.Equal(1, r.Arcs);
        var a = (ArcEntity)r.Entities.First(e => e is ArcEntity);
        Assert.Equal(10, a.X1, 4); Assert.Equal(0, a.Y1, 4);
        Assert.Equal(0, a.X2, 4); Assert.Equal(10, a.Y2, 4);
        Assert.Equal(-10, a.X3, 4); Assert.Equal(0, a.Y3, 4);
    }

    [Fact]
    public void Round_trip_arc_cw_preserves_curve_through_midpoint()
    {
        // CW 输入 P1(-10,0) P2(0,10) P3(10,0): 端点可交换但曲线恒等——重建仍过中点(0,10)、外接圆(0,0,10)。
        var arc = new ArcEntity { X1 = -10, Y1 = 0, X2 = 0, Y2 = 10, X3 = 10, Y3 = 0, LayerName = "L1" };
        var a = (ArcEntity)RoundTrip(new System.Collections.Generic.List<SceneEntity> { arc }).Entities.First(e => e is ArcEntity);
        Assert.Equal(0, a.X2, 4); Assert.Equal(10, a.Y2, 4);                 // 过中点
        Assert.Equal(10, System.Math.Abs(a.X1), 4); Assert.Equal(0, a.Y1, 4); // 端点在 (±10,0)
        Assert.Equal(10, System.Math.Abs(a.X3), 4); Assert.Equal(0, a.Y3, 4);
    }

    [Fact]
    public void Header_magic_and_footer_are_well_formed()
    {
        var (bytes, count) = PmxExportService.Build(Sample());
        Assert.Equal(5, count);
        Assert.Equal(0x50u, bytes[0]); Assert.Equal(0x4Du, bytes[1]); Assert.Equal(0x58u, bytes[2]); Assert.Equal(0x31u, bytes[3]);   // 'P','M','X','1'
        // 末 4 字节 magicEnd '1','X','M','P'
        int n = bytes.Length;
        Assert.Equal(0x31u, bytes[n - 4]); Assert.Equal(0x58u, bytes[n - 3]); Assert.Equal(0x4Du, bytes[n - 2]); Assert.Equal(0x50u, bytes[n - 1]);
    }
}
