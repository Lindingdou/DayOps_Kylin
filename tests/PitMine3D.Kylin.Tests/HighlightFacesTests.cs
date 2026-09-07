using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>选中高亮面：每三角 3 顶点、高亮色主导、保留明暗、不受显示模式影响(线框模式选中也上色)。</summary>
[Collection("MeshRenderMode")]
public class HighlightFacesTests
{
    private static MeshEntity Quad()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 5), (0, 10, 5) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return new MeshEntity("网", v, t) { Cr = 1f, Cg = 1f, Cb = 0f };   // 原色黄
    }

    [Fact]
    public void HighlightFaces_CyanDominant_ShadingKept_ModeIndependent()
    {
        var old = MeshEntity.RenderMode;
        try
        {
            foreach (var mode in new[] { MeshEntity.DisplayMode.Shaded, MeshEntity.DisplayMode.Wireframe, MeshEntity.DisplayMode.ShadedWireframe })
            {
                MeshEntity.RenderMode = mode;
                var m = Quad();
                var o = new List<float>();
                m.TessellateHighlightFaces(o, 0.15f, 0.95f, 1.0f);
                Assert.Equal(2 * 3 * 6, o.Count);            // 2 三角 × 3 顶点 × (xyz + rgb)
                for (int i = 3; i < o.Count; i += 6)
                {
                    float r = o[i], g = o[i + 1], b = o[i + 2];
                    Assert.True(b > r && g > r, $"高亮应偏青(mode={mode}) r={r} g={g} b={b}");
                    Assert.InRange(b, 0.55f, 1.0f);          // 明暗调制后仍在合理区间
                }
            }
        }
        finally { MeshEntity.RenderMode = old; }
    }

    [Fact]
    public void HighlightFaces_UsesElevationOffset()
    {
        var m = Quad(); m.Elevation = 100;
        var o = new List<float>();
        m.TessellateHighlightFaces(o, 0.15f, 0.95f, 1f);
        Assert.Equal(100f, o[2]);          // 第一个顶点 z = 0 + Elevation
    }
}
