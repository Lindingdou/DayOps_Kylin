using System.Linq;
using System;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

public class SceneStyleRenderingTests
{
    [Fact]
    public void Rectangles_follow_the_selected_dash_pattern()
    {
        var rect = new RectEntity
        {
            X0 = 0, Y0 = 0, X1 = 12, Y1 = 8,
            Dash = DashPattern.ByName("虚线"),
        };
        var solid = new RectEntity { X0 = 0, Y0 = 0, X1 = 12, Y1 = 8 };

        var dashed = new System.Collections.Generic.List<float>();
        var plain = new System.Collections.Generic.List<float>();
        rect.Tessellate(dashed);
        solid.Tessellate(plain);

        Assert.True(dashed.Count > plain.Count, "矩形线型修改后应按虚线切成更多可绘制线段");
    }

    [Fact]
    public void Scene_geometry_batches_keep_effective_line_weights()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 0, X1 = 10, LineWeight = -1 });
        scene.Add(new LineEntity { X0 = 0, Y0 = 2, X1 = 10, Y1 = 2, LineWeight = 50 });
        var layers = new LayerTable();
        layers.Current.LineWeight = 25;

        var buildBatches = typeof(Scene).GetMethod("BuildGeometryBatches");
        Assert.NotNull(buildBatches);
        var effective = (Func<SceneEntity, short>)(e => layers.EffectiveLineWeight(e.LineWeight, e.LayerName));
        var batches = (System.Collections.IEnumerable)buildBatches!.Invoke(scene, new object?[] { null, effective })!;
        var batchList = batches.Cast<object>().ToArray();

        Assert.Equal(2, batchList.Length);
        var weights = batchList.Select(x => (short)x.GetType().GetProperty("LineWeight")!.GetValue(x)!).OrderBy(x => x).ToArray();
        Assert.Equal(new short[] { 25, 50 }, weights);
        Assert.All(batchList, x => Assert.NotEmpty((float[])x.GetType().GetProperty("Vertices")!.GetValue(x)!));
    }

    [Fact]
    public void Dimension_parts_inherit_line_style_from_the_dimension()
    {
        var dimension = new DimensionEntity
        {
            X1 = 0, Y1 = 0, X2 = 10, Y2 = 0, OffX = 0, OffY = 4,
            Dash = DashPattern.ByName("虚线"), LineWeight = 50,
        };

        var parts = dimension.VisibleParts();
        var lines = parts.OfType<LineEntity>().ToArray();

        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.Equal(dimension.LineWeight, line.LineWeight);
            Assert.Equal(dimension.Dash, line.Dash);
        });
    }
}
