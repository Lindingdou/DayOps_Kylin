using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>撤销/重做栈回归。</summary>
public class UndoManagerTests
{
    [Fact]
    public void Undo_then_redo_sequence()
    {
        var u = new UndoManager();
        u.Push("A");                       // 改动前 A → 变 B
        u.Push("B");                       // 改动前 B → 变 C（current=C）

        Assert.True(u.CanUndo);
        Assert.Equal("B", u.Undo("C"));    // 回到 B
        Assert.Equal("A", u.Undo("B"));    // 回到 A
        Assert.Null(u.Undo("A"));          // 无更多
        Assert.Equal("B", u.Redo("A"));    // 重做 → B
        Assert.Equal("C", u.Redo("B"));    // 重做 → C
    }

    [Fact]
    public void New_change_clears_redo()
    {
        var u = new UndoManager();
        u.Push("A");
        u.Undo("B");                       // redo 里有 B
        Assert.True(u.CanRedo);
        u.Push("X");                       // 新改动清空重做
        Assert.False(u.CanRedo);
    }
}
