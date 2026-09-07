using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网框选不依赖显示模式(着色面模式下 Tessellate 无边线, 旧判定永远选不中——3dm 导入后框选失效的根因)。</summary>
[Collection("MeshRenderMode")]
public class SelectionBoxMeshTests
{
    private static MeshEntity Quad()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 11), (10, 10, 12), (0, 10, 13) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return new MeshEntity("网", v, t);
    }

    [Fact]
    public void ShadedMode_WindowAndCrossing_StillSelect()
    {
        var old = MeshEntity.RenderMode;
        try
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            var m = Quad();
            var o = new List<float>(); m.Tessellate(o); Assert.Empty(o);          // 纯面模式无边线
            Assert.True(SelectionBox.Match(m, -1, -1, 11, 11, crossing: false));    // 窗口选：全含
            Assert.False(SelectionBox.Match(m, -1, -1, 5, 11, crossing: false));    // 窗口选：只含一半 → 不选
            Assert.True(SelectionBox.Match(m, -1, -1, 5, 11, crossing: true));      // 交叉选：碰到即选
            Assert.True(SelectionBox.Match(m, 4, 4, 6, 6, crossing: true));         // 框整个落在三角内部(无顶点/无边穿过)也算碰到
            Assert.False(SelectionBox.Match(m, 20, 20, 30, 30, crossing: true));    // 远离
            var poly = new List<(double x, double y)> { (-1, -1), (11, -1), (11, 11), (-1, 11) };
            Assert.True(SelectionBox.MatchPolygon(m, poly, crossing: false));
        }
        finally { MeshEntity.RenderMode = old; }
    }

    [Fact]
    public void ContainsXY_InsideAndOutside()
    {
        var m = Quad();
        Assert.True(m.ContainsXY(5, 5));
        Assert.True(m.ContainsXY(0.1, 0.1));
        Assert.False(m.ContainsXY(10.5, 5));
        Assert.False(m.ContainsXY(-1, -1));
    }
}
