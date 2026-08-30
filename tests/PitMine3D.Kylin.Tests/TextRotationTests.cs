using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>文字旋转回归（Tessellate 绕锚点旋转 / Apply 合成 / SceneIO 往返）。</summary>
public class TextRotationTests
{
    [Fact]
    public void Rotated_90_maps_local_x_to_world_y()
    {
        // "1" 在锚点(0,0)，旋转 90°：局部 +X 笔画 → 世界 +Y。取镶嵌线段验证有分量落在 +Y。
        var t = new TextEntity { X = 0, Y = 0, Height = 10, Rotation = Math.PI / 2, Text = "1" };
        var o = new List<float>();
        t.Tessellate(o);
        Assert.True(o.Count > 0);
        double maxY = double.MinValue, maxX = double.MinValue;
        for (int i = 0; i + 1 < o.Count; i += 6) { if (o[i + 1] > maxY) maxY = o[i + 1]; if (o[i] > maxX) maxX = o[i]; }
        Assert.True(maxY > 1.0, "旋转90°后字形应向 +Y 延展");
        Assert.True(maxX < 1e-3, "旋转90°后不应有明显 +X 延展");
    }

    [Fact]
    public void Zero_rotation_is_horizontal_backcompat()
    {
        var t = new TextEntity { X = 0, Y = 0, Height = 10, Rotation = 0, Text = "1" };
        var o = new List<float>();
        t.Tessellate(o);
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 1 < o.Count; i += 6) { if (o[i] > maxX) maxX = o[i]; if (o[i + 1] > maxY) maxY = o[i + 1]; }
        Assert.True(maxX > 1.0, "水平文字应向 +X 延展");
    }

    [Fact]
    public void Apply_rotation_composes_into_text_angle()
    {
        var t = new TextEntity { X = 0, Y = 0, Height = 1, Rotation = 0, Text = "A" };
        var rot = Affine2.Rotate(Math.PI / 2, 0, 0);      // 绕原点旋 90°
        var t2 = (TextEntity)t.Apply(rot);
        Assert.Equal(Math.PI / 2, t2.Rotation, 4);
    }

    [Fact]
    public void SceneIO_roundtrips_rotation()
    {
        var s = new Scene();
        s.Add(new TextEntity { X = 1, Y = 2, Height = 3, Rotation = 0.7, Text = "R" });
        var t = (TextEntity)SceneIO.Load(SceneIO.Save(s)).Entities[0];
        Assert.Equal(0.7, t.Rotation, 6);
    }
}
