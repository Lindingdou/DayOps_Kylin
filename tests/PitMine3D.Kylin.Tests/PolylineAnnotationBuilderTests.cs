using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

public sealed class PolylineAnnotationBuilderTests
{
    private static IReadOnlyList<SceneEntity> Build(string methodName, PolylineEntity line, double textHeight)
    {
        return methodName == "BuildStart"
            ? PolylineAnnotationBuilder.BuildStart(line, textHeight)
            : PolylineAnnotationBuilder.BuildOrder(line, textHeight);
    }

    [Fact]
    public void Start_annotation_marks_the_first_vertex_with_green_circle_and_S()
    {
        var line = new PolylineEntity { Elevation = 10 };
        line.Points.AddRange(new[] { (10.0, 20.0), (30.0, 20.0) });
        line.Zs = new List<double> { 1, 3 };

        var annotations = Build("BuildStart", line, 2);

        Assert.Equal(2, annotations.Count);
        var circle = Assert.IsType<CircleEntity>(annotations[0]);
        Assert.Equal((10.0, 20.0), (circle.Cx, circle.Cy));
        Assert.Equal(11, circle.Elevation, 6);
        Assert.Equal(16f / 255f, circle.Cr, 6);
        Assert.Equal(185f / 255f, circle.Cg, 6);
        Assert.Equal(129f / 255f, circle.Cb, 6);

        var text = Assert.IsType<TextEntity>(annotations[1]);
        Assert.Equal("S", text.Text);
        Assert.Equal((10.0, 20.0), (text.X, text.Y));
        Assert.Equal(11, text.Elevation, 6);
        Assert.True(text.ScreenFacing);
        Assert.Equal(1, text.HAlign);
        Assert.Equal(1, text.VAlign);
        Assert.Equal(PolylineAnnotationBuilder.LayerName, circle.LayerName);
        Assert.Equal(PolylineAnnotationBuilder.LayerName, text.LayerName);
    }

    [Fact]
    public void Order_annotation_labels_each_vertex_in_stored_direction()
    {
        var line = new PolylineEntity { Elevation = 5 };
        line.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 5.0) });
        line.Zs = new List<double> { 0, 2, 4 };

        var annotations = Build("BuildOrder", line, 2);
        var texts = annotations.OfType<TextEntity>().ToList();
        var circles = annotations.OfType<CircleEntity>().ToList();

        Assert.Equal(3, texts.Count);
        Assert.Equal(3, circles.Count);
        Assert.Equal(new[] { "1", "2", "3" }, texts.Select(t => t.Text));
        Assert.Equal(new[] { 5.0, 7.0, 9.0 }, texts.Select(t => t.Elevation));
        Assert.Equal(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 5.0) },
            texts.Select(t => (t.X, t.Y)));
        Assert.All(texts, t => Assert.True(t.ScreenFacing));
        Assert.Equal(239f / 255f, circles[0].Cr, 6);
        Assert.Equal(191f / 255f, circles[1].Cg, 6);
        Assert.Equal(185f / 255f, circles[2].Cg, 6);
    }

    [Fact]
    public void Empty_polyline_produces_no_annotations()
    {
        var line = new PolylineEntity();

        Assert.Empty(Build("BuildStart", line, 2));
        Assert.Empty(Build("BuildOrder", line, 2));
    }

    [Fact]
    public void Annotations_render_in_overlay_channels_without_touching_source_scene()
    {
        var source = new Scene();
        var line = new PolylineEntity();
        line.Points.AddRange(new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 3.0) });
        source.Add(line);

        var overlay = new Scene();
        foreach (var entity in PolylineAnnotationBuilder.BuildOrder(line, 1)) overlay.Add(entity);

        Assert.Single(source.Entities);
        Assert.Equal(6, overlay.Entities.Count);
        Assert.NotEmpty(overlay.BuildGeometryBatches());
        Assert.Equal(3, overlay.BuildBillboards().Count);
    }
}
