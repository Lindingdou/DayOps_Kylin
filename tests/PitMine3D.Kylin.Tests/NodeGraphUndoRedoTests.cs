using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Nodes;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>节点图撤销/重做回归（忠实原 NodeGraphUndoRedo：SaveState 改动前调用，快照栈回退/前进）。</summary>
public class NodeGraphUndoRedoTests
{
    [Fact]
    public void Fresh_controller_cannot_undo_or_redo()
    {
        var ur = new NodeGraphUndoRedo(new NodeGraph());
        Assert.False(ur.CanUndo);
        Assert.False(ur.CanRedo);
    }

    [Fact]
    public void Undo_redo_add_node()
    {
        var g = new NodeGraph();
        var ur = new NodeGraphUndoRedo(g);
        ur.SaveState();                       // 改动前：记录空图
        g.AddNode(NodeKind.Circle, 0, 0);
        Assert.Single(g.Nodes);

        ur.Undo();
        Assert.Empty(g.Nodes);                // 回退到空
        ur.Redo();
        Assert.Single(g.Nodes);               // 前进重现
        Assert.Equal(NodeKind.Circle, g.Nodes[0].Kind);
    }

    [Fact]
    public void Undo_redo_connect_keeps_nodes()
    {
        var g = new NodeGraph();
        var num = g.AddNode(NodeKind.Number, 0, 0);
        var circle = g.AddNode(NodeKind.Circle, 100, 0);
        var ur = new NodeGraphUndoRedo(g);
        ur.SaveState();                       // 改动前：2 节点 0 连线
        g.Connect(num.Id, 0, circle.Id, 1);
        Assert.Single(g.Connections);

        ur.Undo();
        Assert.Empty(g.Connections);          // 连线撤销
        Assert.Equal(2, g.Nodes.Count);       // 节点仍在
        ur.Redo();
        Assert.Single(g.Connections);         // 连线重现（按旧ID映射重连）
    }

    [Fact]
    public void Undo_restores_param_value()
    {
        var g = new NodeGraph();
        var num = g.AddNode(NodeKind.Number, 0, 0); num.Value = 1.0;
        var ur = new NodeGraphUndoRedo(g);
        ur.SaveState();                       // 改动前：值 1.0
        num.Value = 99.0;
        Assert.Equal(99.0, (double)g.Evaluate(num.Id)!);

        ur.Undo();
        Assert.Single(g.Nodes);
        Assert.Equal(1.0, (double)g.Evaluate(g.Nodes[0].Id)!);   // 值随快照还原
    }

    [Fact]
    public void New_save_after_undo_clears_redo()
    {
        var g = new NodeGraph();
        var ur = new NodeGraphUndoRedo(g);
        ur.SaveState();
        g.AddNode(NodeKind.Number, 0, 0);
        ur.Undo();
        Assert.True(ur.CanRedo);

        ur.SaveState();                       // 新分支 → 重做栈清空（忠实原 SaveState）
        Assert.False(ur.CanRedo);
    }

    [Fact]
    public void Undo_redo_roundtrip_preserves_end_to_end_bake()
    {
        // 端到端：Number(5)→Circle→Bake 出半径5的圆；撤销全过程再重做，几何仍等价。
        var g = new NodeGraph();
        var ur = new NodeGraphUndoRedo(g);
        ur.SaveState();
        var num = g.AddNode(NodeKind.Number, 0, 0); num.Value = 5.0;
        var circle = g.AddNode(NodeKind.Circle, 100, 0);
        var bake = g.AddNode(NodeKind.Bake, 200, 0);
        g.Connect(num.Id, 0, circle.Id, 1);
        g.Connect(circle.Id, 0, bake.Id, 0);
        var before = Assert.IsType<CircleEntity>(Assert.Single(g.EvaluateBakes()));
        Assert.Equal(5.0, before.Radius, 6);

        ur.Undo();
        Assert.Empty(g.EvaluateBakes());      // 空图无烘焙
        ur.Redo();
        var after = Assert.IsType<CircleEntity>(Assert.Single(g.EvaluateBakes()));
        Assert.Equal(5.0, after.Radius, 6);   // 重做后几何等价
    }
}
