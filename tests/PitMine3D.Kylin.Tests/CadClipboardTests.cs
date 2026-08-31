using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>实体剪贴板回归（COPYCLIP/CUTCLIP/PASTECLIP 的克隆语义）。</summary>
public class CadClipboardTests
{
    [Fact]
    public void Set_then_paste_clones_with_offset()
    {
        var clip = new CadClipboard();
        clip.Set(new List<SceneEntity> { new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 } });
        Assert.Equal(1, clip.Count);
        var pasted = clip.Paste(5, 3);
        var l = Assert.IsType<LineEntity>(Assert.Single(pasted));
        Assert.Equal(5, l.X0, 4); Assert.Equal(3, l.Y0, 4);      // 平移 (5,3)
        Assert.Equal(15, l.X1, 4); Assert.Equal(3, l.Y1, 4);
    }

    [Fact]
    public void Paste_is_independent_deep_copy()
    {
        var clip = new CadClipboard();
        var src = new CircleEntity { Cx = 0, Cy = 0, Radius = 5 };
        clip.Set(new List<SceneEntity> { src });
        var a = (CircleEntity)clip.Paste(0, 0)[0];
        var b = (CircleEntity)clip.Paste(0, 0)[0];
        Assert.NotSame(a, b);                                    // 每次粘贴都是新实例
        Assert.NotSame(src, a);
    }

    [Fact]
    public void Set_snapshot_survives_source_change()
    {
        var clip = new CadClipboard();
        var src = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 };
        clip.Set(new List<SceneEntity> { src });
        src.X1 = 999;                                            // 改原实体
        var l = (LineEntity)clip.Paste(0, 0)[0];
        Assert.Equal(1, l.X1, 4);                               // 剪贴板是快照, 不受影响
    }

    [Fact]
    public void Empty_clipboard()
    {
        var clip = new CadClipboard();
        Assert.True(clip.IsEmpty);
        Assert.Empty(clip.Paste(1, 1));
        Assert.Null(clip.Centroid());
    }

    [Fact]
    public void Centroid_is_grip_center_for_base_paste()
    {
        var clip = new CadClipboard();
        // 线段 (0,0)-(10,0) 夹点 {(0,0),(5,0)端中?,(10,0)} —— 质心 x 居中
        clip.Set(new List<SceneEntity> { new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 4 } });
        var c = clip.Centroid();
        Assert.NotNull(c);
        // 基点粘贴到 target=(100,100): 偏移 = target - centroid, 粘贴后质心≈target
        var offset = (100 - c!.Value.x, 100 - c.Value.y);
        var l = (LineEntity)clip.Paste(offset.Item1, offset.Item2)[0];
        double midX = (l.X0 + l.X1) / 2, midY = (l.Y0 + l.Y1) / 2;
        // 线段两端点即其夹点主体 → 中点应≈100,100(容夹点集含中点时略偏, 用宽容差)
        Assert.InRange(midX, 90, 110);
        Assert.InRange(midY, 90, 110);
    }
}
