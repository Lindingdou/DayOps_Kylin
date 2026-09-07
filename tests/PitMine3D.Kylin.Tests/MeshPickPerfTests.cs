using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 大网首次点选卡顿的回归锁：拾取/框选路径不得触发整张去重边表构建(4 万三角时那一步约 43ms)，
/// 且高亮面镶嵌要有缓存(反复选中不重算)。
/// </summary>
[Collection("MeshRenderMode")]
public class MeshPickPerfTests
{
    private static MeshEntity Grid(int n = 40)
    {
        var v = new List<(double x, double y, double z)>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add((i, j, (i + j) * 0.1));
        var t = new List<(int a, int b, int c)>();
        int W = n + 1;
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        { t.Add((j * W + i, j * W + i + 1, (j + 1) * W + i + 1)); t.Add((j * W + i, (j + 1) * W + i + 1, (j + 1) * W + i)); }
        return new MeshEntity("网", v, t);
    }

    [Fact]
    public void PickAndBoxSelect_DoNotBuildEdgeTable()
    {
        var m = Grid();
        Assert.False(m.HasEdgeCache);

        Assert.Equal(0, m.DistanceTo(20.5, 20.5));            // 点在面内
        Assert.True(m.DistanceTo(-5, -5) > 1e8);              // 包围盒外
        Assert.False(m.HasEdgeCache);

        Assert.True(SelectionBox.Match(m, -1, -1, 41, 41, crossing: false));   // 窗口选(包围盒)
        Assert.True(SelectionBox.Match(m, 20, 20, 25, 25, crossing: true));    // 交叉选(逐三角)
        Assert.False(SelectionBox.Match(m, 100, 100, 110, 110, crossing: true));
        Assert.False(m.HasEdgeCache);

        Func<double, double, double, (double sx, double sy, double depth)?> proj = (x, y, z) => (x * 10, y * 10, -z);
        Assert.Same(m, SelectionBox.PickScreen(new SceneEntity[] { m }, 205, 205, 8, proj));
        Assert.Null(SelectionBox.PickScreen(new SceneEntity[] { m }, 900, 900, 8, proj));
        Assert.False(m.HasEdgeCache);

        var edges = new List<float>();
        m.TessellateEdges(edges);                              // 只有真要画边线时才建表
        Assert.True(m.HasEdgeCache);
    }

    [Fact]
    public void HighlightFaces_Cached_AndInvalidatedOnGeometryChange()
    {
        var m = Grid(4);
        var a = new List<float>(); m.TessellateHighlightFaces(a, 0.15f, 0.95f, 1f);
        var b = new List<float>(); m.TessellateHighlightFaces(b, 0.15f, 0.95f, 1f);
        Assert.Equal(a, b);
        Assert.Equal(m.TriangleCount * 18, a.Count);
        // 换高亮色 → 重算(内容变)
        var c = new List<float>(); m.TessellateHighlightFaces(c, 1f, 0.2f, 0.2f);
        Assert.NotEqual(a, c);
        // 几何改动后 Invalidate → 顶点数变化反映到镶嵌
        m.Verts.Add((99, 99, 9)); m.Tris.Add((0, 1, m.Verts.Count - 1)); m.Invalidate();
        var d = new List<float>(); m.TessellateHighlightFaces(d, 0.15f, 0.95f, 1f);
        Assert.Equal(m.TriangleCount * 18, d.Count);
        Assert.True(d.Count > a.Count);
    }
}
