using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点样式(PDMODE 符号 + 大小)回归 —— 忠实原"修改点样式"。</summary>
public class PointStyleTests
{
    private static int SegCount(SceneEntity e) { var o = new List<float>(); e.Tessellate(o); return o.Count / 12; }   // 每段 2 顶点 ×6 float

    [Fact]
    public void Style_symbols_tessellate_distinctly()
    {
        Assert.Equal(2, SegCount(new PointEntity { Style = 2, Size = 1 }));   // 加号 = 2 段
        Assert.Equal(2, SegCount(new PointEntity { Style = 3, Size = 1 }));   // × = 2 段
        Assert.Equal(1, SegCount(new PointEntity { Style = 4, Size = 1 }));   // 竖线 = 1 段
        Assert.Equal(0, SegCount(new PointEntity { Style = 1, Size = 1 }));   // 无标记 = 0 段
        Assert.Equal(2 + 4, SegCount(new PointEntity { Style = 2 | 64, Size = 1 }));   // 加号 + 外接方(4 段)
        Assert.True(SegCount(new PointEntity { Style = 2 | 32, Size = 1 }) > 2);       // 加号 + 外接圆(多段)
    }

    [Fact]
    public void Size_and_style_survive_pmx_roundtrip()
    {
        var s = new Scene();
        s.Add(new PointEntity { X = 3, Y = 4, Size = 2.5, Style = 3 | 32 });
        var s2 = SceneIO.Load(SceneIO.Save(s));
        var p = Assert.IsType<PointEntity>(s2.Entities[0]);
        Assert.Equal(3, p.X, 6); Assert.Equal(4, p.Y, 6);
        Assert.Equal(2.5, p.Size, 6);            // 大小往返
        Assert.Equal(3 | 32, p.Style);           // 样式往返
    }

    [Fact]
    public void Old_pmx_point_without_size_style_defaults()
    {
        var s2 = SceneIO.Load("[{\"T\":\"point\",\"N\":[1,2]}]");   // 旧档只有 X,Y
        var p = Assert.IsType<PointEntity>(s2.Entities[0]);
        Assert.Equal(0.5, p.Size, 6); Assert.Equal(2, p.Style);   // 回退默认
    }

    [Fact]
    public void Panel_edits_point_size_and_style()
    {
        var p = new PointEntity { X = 0, Y = 0, Size = 0.5, Style = 2 };
        var rows = EntityProperties.Describe(p);
        Assert.Contains(rows, r => r.label == "点大小");
        Assert.Contains(rows, r => r.label == "点样式" && r.value == "2");
        var e1 = EntityProperties.WithEdited(p, "点大小", "3") as PointEntity;
        Assert.Equal(3, e1!.Size, 6); Assert.Equal(2, e1.Style);   // 改大小保样式
        var e2 = EntityProperties.WithEdited(p, "点样式", "35") as PointEntity;
        Assert.Equal(35, e2!.Style); Assert.Equal(0.5, e2.Size, 6);  // 改样式保大小
        Assert.Null(EntityProperties.WithEdited(p, "点样式", "200"));   // 越界拒绝
    }
}
