using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>绘制场景内部格式（保存/打开）round-trip 回归。</summary>
public class SceneIOTests
{
    [Fact]
    public void Save_load_roundtrip_preserves_entities()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 5 });
        s.Add(new CircleEntity { Cx = 3, Cy = 4, Radius = 7 });
        var pl = new PolylineEntity { Closed = true };
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        s.Add(pl);

        var s2 = SceneIO.Load(SceneIO.Save(s));

        Assert.Equal(3, s2.Count);
        var line = Assert.IsType<LineEntity>(s2.Entities[0]);
        Assert.Equal(10, line.X1); Assert.Equal(5, line.Y1);
        var circle = Assert.IsType<CircleEntity>(s2.Entities[1]);
        Assert.Equal(7, circle.Radius);
        var poly = Assert.IsType<PolylineEntity>(s2.Entities[2]);
        Assert.Equal(3, poly.Points.Count);
        Assert.True(poly.Closed);
    }

    [Fact]
    public void Save_load_roundtrip_preserves_style_attributes()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, Dash = DashPattern.ByName("虚线"), LineWeight = 25, Visible = false });
        s.Add(new TextEntity { X = 1, Y = 2, Height = 3, Text = "甲", HAlign = 1, VAlign = 2, WidthFactor = 0.8, ObliqueAngle = 0.3 });

        var s2 = SceneIO.Load(SceneIO.Save(s));

        var line = Assert.IsType<LineEntity>(s2.Entities[0]);
        Assert.Equal(new[] { 6.0, 3.0 }, line.Dash!);   // 线型持久化
        Assert.Equal(25, line.LineWeight);              // 线宽持久化
        Assert.False(line.Visible);                     // 隐藏持久化
        var txt = Assert.IsType<TextEntity>(s2.Entities[1]);
        Assert.Equal(1, txt.HAlign); Assert.Equal(2, txt.VAlign);   // 对齐持久化
        Assert.Equal(0.8, txt.WidthFactor, 6);          // 字宽持久化
        Assert.Equal(0.3, txt.ObliqueAngle, 6);         // 倾斜持久化
    }

    [Fact]
    public void Old_pmx_without_style_fields_loads_with_defaults()
    {
        // 旧 .pmx(无 D/W/H/Wf 字段) 仍可读, 属性回退默认(实线/ByLayer/可见/字宽1)
        var s2 = SceneIO.Load("[{\"T\":\"line\",\"N\":[0,0,10,0],\"C\":[0.8,0.8,0.8],\"L\":\"0\"}]");
        var line = Assert.IsType<LineEntity>(s2.Entities[0]);
        Assert.Null(line.Dash); Assert.Equal(-1, line.LineWeight); Assert.True(line.Visible);
    }

    [Fact]
    public void Load_empty_or_garbage_is_safe()
    {
        Assert.Equal(0, SceneIO.Load("[]").Count);
    }

    [Fact]
    public void Roundtrip_preserves_polygon_and_arc()
    {
        var s = new Scene();
        s.Add(new PolygonEntity { Cx = 1, Cy = 2, Radius = 5, Rotation = 0.5, Sides = 6 });
        s.Add(new ArcEntity { X1 = 1, Y1 = 0, X2 = 0, Y2 = 1, X3 = -1, Y3 = 0 });

        var s2 = SceneIO.Load(SceneIO.Save(s));

        var pg = Assert.IsType<PolygonEntity>(s2.Entities[0]);
        Assert.Equal(6, pg.Sides);
        Assert.Equal(5, pg.Radius, 6);
        Assert.Equal(0.5, pg.Rotation, 6);
        Assert.IsType<ArcEntity>(s2.Entities[1]);
    }

    [Fact]
    public void Roundtrip_preserves_layer_name()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "墙" });
        var s2 = SceneIO.Load(SceneIO.Save(s));
        Assert.Equal("墙", s2.Entities[0].LayerName);
    }

    [Fact]
    public void Roundtrip_preserves_rect_point_text()
    {
        var s = new Scene();
        s.Add(new RectEntity { X0 = 1, Y0 = 2, X1 = 8, Y1 = 6 });
        s.Add(new PointEntity { X = 3, Y = 4 });
        s.Add(new TextEntity { X = 5, Y = 6, Height = 2.5, Text = "ZK-12" });

        var s2 = SceneIO.Load(SceneIO.Save(s));
        Assert.Equal(3, s2.Count);
        var r = Assert.IsType<RectEntity>(s2.Entities[0]);
        Assert.Equal(8, r.X1, 6); Assert.Equal(6, r.Y1, 6);
        var p = Assert.IsType<PointEntity>(s2.Entities[1]);
        Assert.Equal(3, p.X, 6); Assert.Equal(4, p.Y, 6);
        var t = Assert.IsType<TextEntity>(s2.Entities[2]);
        Assert.Equal("ZK-12", t.Text);
        Assert.Equal(2.5, t.Height, 6);
    }

    [Fact]
    public void Roundtrip_preserves_entity_color()
    {
        var s = new Scene();
        s.Add(new CircleEntity { Cx = 0, Cy = 0, Radius = 1, Cr = 0.1f, Cg = 0.7f, Cb = 0.9f });
        var e = SceneIO.Load(SceneIO.Save(s)).Entities[0];
        Assert.Equal(0.1f, e.Cr, 3);
        Assert.Equal(0.7f, e.Cg, 3);
        Assert.Equal(0.9f, e.Cb, 3);
    }

    [Fact]
    public void SaveDoc_roundtrips_layer_states_and_empty_layer()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "墙" });
        var layers = new List<Layer>
        {
            new Layer("0", 1, 1, 1),
            new Layer("墙", 0.9f, 0.1f, 0.1f) { Frozen = true },
            new Layer("空层", 0.2f, 0.3f, 0.4f) { Locked = true, Visible = false },   // 无实体的空层
        };
        var doc = SceneIO.LoadDoc(SceneIO.SaveDoc(s, layers, "墙"));
        Assert.Equal(1, doc.Scene.Count);
        Assert.Equal(3, doc.Layers.Count);                     // 空层也保留
        Assert.Equal("墙", doc.Current);
        Assert.True(doc.Layers.First(l => l.Name == "墙").Frozen);
        var empty = doc.Layers.First(l => l.Name == "空层");
        Assert.True(empty.Locked); Assert.False(empty.Visible);
    }

    [Fact]
    public void LoadDoc_reads_old_array_format_backcompat()
    {
        var s = new Scene(); s.Add(new CircleEntity { Cx = 0, Cy = 0, Radius = 5 });
        var doc = SceneIO.LoadDoc(SceneIO.Save(s));   // 旧数组格式
        Assert.Equal(1, doc.Scene.Count);
        Assert.Empty(doc.Layers);                     // 旧格式无图层表
    }

    [Fact]
    public void Load_also_reads_new_doc_format()
    {
        var s = new Scene(); s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 2, Y1 = 0 });
        var s2 = SceneIO.Load(SceneIO.SaveDoc(s, new List<Layer> { new Layer("0", 1, 1, 1) }, "0"));
        Assert.Equal(1, s2.Count);                    // Load 也能读新文档格式
    }

    [Fact]
    public void LayerTable_restore_rebuilds_states()
    {
        var lt = new LayerTable();
        var states = new List<SceneIO.LayerState>
        {
            new("0", 1, 1, 1, true, false, false),
            new("道路", 0.5f, 0.5f, 0.5f, false, true, true),
        };
        lt.Restore(states, "道路");
        Assert.Equal(2, lt.Layers.Count);
        Assert.Equal("道路", lt.Current.Name);
        var road = lt.Get("道路")!;
        Assert.True(road.Frozen); Assert.True(road.Locked); Assert.False(road.Visible);
    }
}
