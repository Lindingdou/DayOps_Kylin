using PitMine3D.Kylin.Nodes;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>节点图模型回归：新增节点 + 连线校验。</summary>
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
}
