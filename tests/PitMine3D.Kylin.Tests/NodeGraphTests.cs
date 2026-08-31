using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Nodes;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>节点图模型回归：新增节点 + 连线校验 + 求值（参数→几何→烘焙）。</summary>
public class NodeGraphTests
{
    [Fact]
    public void Add_and_connect()
    {
        var g = new NodeGraph();
        var num = g.AddNode(NodeKind.Number, 0, 0);
        var circle = g.AddNode(NodeKind.Circle, 100, 0);

        Assert.Equal(2, g.Nodes.Count);
        Assert.Equal(1, num.OutputCount);      // 数字：0 入 1 出
        Assert.Equal(0, num.InputCount);
        Assert.Equal(2, circle.InputCount);    // 圆：2 入 1 出

        Assert.True(g.Connect(num.Id, 0, circle.Id, 0));
        Assert.Single(g.Connections);
    }

    [Fact]
    public void Connect_rejects_invalid()
    {
        var g = new NodeGraph();
        var a = g.AddNode(NodeKind.Circle, 0, 0);
        var b = g.AddNode(NodeKind.Circle, 100, 0);

        Assert.False(g.Connect(a.Id, 0, a.Id, 0));      // 自连
        Assert.False(g.Connect(a.Id, 5, b.Id, 0));      // 输出口越界
        Assert.False(g.Connect(a.Id, 0, b.Id, 9));      // 输入口越界
        Assert.False(g.Connect(999, 0, b.Id, 0));       // 节点不存在
        Assert.Empty(g.Connections);
    }

    [Fact]
    public void Input_port_is_exclusive()
    {
        var g = new NodeGraph();
        var n1 = g.AddNode(NodeKind.Number, 0, 0);
        var n2 = g.AddNode(NodeKind.Number, 0, 50);
        var circle = g.AddNode(NodeKind.Circle, 200, 0);

        Assert.True(g.Connect(n1.Id, 0, circle.Id, 0));
        Assert.True(g.Connect(n2.Id, 0, circle.Id, 0));   // 重连同一输入口
        Assert.Single(g.Connections);                     // 仍 1 条（替换）
        Assert.Equal(n2.Id, g.Connections[0].FromNode);
    }

    [Fact]
    public void Param_nodes_evaluate_to_their_values()
    {
        var g = new NodeGraph();
        var num = g.AddNode(NodeKind.Number, 0, 0); num.Value = 7.5;
        var str = g.AddNode(NodeKind.String, 0, 0); str.Value = "煤层";
        var bl = g.AddNode(NodeKind.Bool, 0, 0); bl.Value = true;
        var pt = g.AddNode(NodeKind.Point, 0, 0); pt.Value = new Vec3(3, 4, 5);
        Assert.Equal(7.5, (double)g.Evaluate(num.Id)!);
        Assert.Equal("煤层", (string)g.Evaluate(str.Id)!);
        Assert.True((bool)g.Evaluate(bl.Id)!);
        Assert.Equal(new Vec3(3, 4, 5), (Vec3)g.Evaluate(pt.Id)!);
    }

    [Fact]
    public void Circle_with_defaults_evaluates_to_radius_10()
    {
        var g = new NodeGraph();
        var c = g.AddNode(NodeKind.Circle, 0, 0);
        var e = Assert.IsType<CircleEntity>(g.Evaluate(c.Id));
        Assert.Equal(10.0, e.Radius, 6);          // 默认半径 10
        Assert.Equal(0, e.Cx); Assert.Equal(0, e.Cy);
    }

    [Fact]
    public void Number_feeds_circle_radius_through_connection()
    {
        var g = new NodeGraph();
        var num = g.AddNode(NodeKind.Number, 0, 0); num.Value = 5.0;
        var circle = g.AddNode(NodeKind.Circle, 100, 0);
        Assert.True(g.Connect(num.Id, 0, circle.Id, 1));   // 数字 → 半径口
        var e = Assert.IsType<CircleEntity>(g.Evaluate(circle.Id));
        Assert.Equal(5.0, e.Radius, 6);                    // 拉取上游值
    }

    [Fact]
    public void Point_feeds_circle_center()
    {
        var g = new NodeGraph();
        var pt = g.AddNode(NodeKind.Point, 0, 0); pt.Value = new Vec3(30, 40, 0);
        var circle = g.AddNode(NodeKind.Circle, 100, 0);
        Assert.True(g.Connect(pt.Id, 0, circle.Id, 0));    // 点 → 圆心口
        var e = Assert.IsType<CircleEntity>(g.Evaluate(circle.Id));
        Assert.Equal(30, e.Cx); Assert.Equal(40, e.Cy);
    }

    [Fact]
    public void Geometry_nodes_produce_expected_entities()
    {
        var g = new NodeGraph();
        Assert.IsType<LineEntity>(g.Evaluate(g.AddNode(NodeKind.Line, 0, 0).Id));
        Assert.IsType<RectEntity>(g.Evaluate(g.AddNode(NodeKind.Rectangle, 0, 0).Id));
        Assert.IsType<ArcEntity>(g.Evaluate(g.AddNode(NodeKind.Arc, 0, 0).Id));
        var poly = Assert.IsType<PolygonEntity>(g.Evaluate(g.AddNode(NodeKind.Polygon, 0, 0).Id));
        Assert.Equal(6, poly.Sides);
        var pl = Assert.IsType<PolylineEntity>(g.Evaluate(g.AddNode(NodeKind.Polyline, 0, 0).Id));
        Assert.True(pl.Points.Count >= 3);
    }

    [Fact]
    public void Polygon_node_inscribed_vs_circumscribed()
    {
        var g = new NodeGraph();
        var poly = g.AddNode(NodeKind.Polygon, 0, 0);
        Assert.Equal(4, poly.InputCount);                     // Center/Sides/Radius/Inscribed(忠实原 PolygonNode)
        var inscr = Assert.IsType<PolygonEntity>(g.Evaluate(poly.Id));
        Assert.Equal(10, inscr.Radius, 4);                    // 默认内接: 顶点半径=10

        var bl = g.AddNode(NodeKind.Bool, 0, 0);              // 布尔默认 false → 外切
        Assert.True(g.Connect(bl.Id, 0, poly.Id, 3));
        var circum = Assert.IsType<PolygonEntity>(g.Evaluate(poly.Id));
        Assert.Equal(10.0 / System.Math.Cos(System.Math.PI / 6), circum.Radius, 4);   // 外切 6 边: 半径放大到边中点=10
    }

    [Fact]
    public void Arc_geometry_matches_center_radius_angles()
    {
        // 默认 圆心(0,0) 半径10 起0° 终90° → 起点(10,0) 端点(0,10)
        var g = new NodeGraph();
        var arc = Assert.IsType<ArcEntity>(g.Evaluate(g.AddNode(NodeKind.Arc, 0, 0).Id));
        Assert.Equal(10, arc.X1, 6); Assert.Equal(0, arc.Y1, 6);      // 起点
        Assert.Equal(0, arc.X3, 6); Assert.Equal(10, arc.Y3, 6);      // 端点
    }

    [Fact]
    public void Bake_collects_upstream_geometry()
    {
        var g = new NodeGraph();
        var num = g.AddNode(NodeKind.Number, 0, 0); num.Value = 5.0;
        var circle = g.AddNode(NodeKind.Circle, 100, 0);
        var bake = g.AddNode(NodeKind.Bake, 200, 0);
        g.Connect(num.Id, 0, circle.Id, 1);      // 数字→半径
        g.Connect(circle.Id, 0, bake.Id, 0);     // 圆→烘焙
        var outp = g.EvaluateBakes();
        var e = Assert.IsType<CircleEntity>(Assert.Single(outp));
        Assert.Equal(5.0, e.Radius, 6);          // 端到端：Number(5)→Circle→Bake→半径5的圆
    }

    [Fact]
    public void Cycle_does_not_infinite_loop()
    {
        var g = new NodeGraph();
        var a = g.AddNode(NodeKind.Bake, 0, 0);
        var b = g.AddNode(NodeKind.Bake, 100, 0);
        g.Connect(a.Id, 0, b.Id, 0);
        g.Connect(b.Id, 0, a.Id, 0);             // 成环
        Assert.Null(g.Evaluate(a.Id));           // 环 → null，不死循环
    }
}
