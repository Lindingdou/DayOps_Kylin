using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 实体标高 Elevation → 镶嵌 z 输出回归。默认 0(平面实体不变)；设标高后 Tessellate 的 z 取该值，
/// 使 MapGIS 等高线注入高程后能按标高抬升成三维地形；克隆/变换经 CopyStyleFrom 保留标高。
/// P3_C3 交错：每顶点 6 float，z 在偏移 2。
/// </summary>
public class EntityElevationTests
{
    [Fact]
    public void Default_elevation_tessellates_flat_at_zero()
    {
        var pl = new PolylineEntity { Points = { (0, 0), (10, 0) } };
        var o = new List<float>();
        pl.Tessellate(o);
        Assert.Equal(12, o.Count);          // 1 段 × 2 顶点 × 6
        Assert.Equal(0f, o[2]);
        Assert.Equal(0f, o[8]);
    }

    [Fact]
    public void Set_elevation_lifts_all_vertices_to_that_z()
    {
        var pl = new PolylineEntity { Elevation = 1234.5, Points = { (0, 0), (10, 0), (10, 10) } };
        var o = new List<float>();
        pl.Tessellate(o);
        Assert.Equal(24, o.Count);          // 2 段 × 2 顶点 × 6
        for (int i = 0; i + 5 < o.Count; i += 6)
            Assert.Equal(1234.5f, o[i + 2]);   // 每个顶点 z 都 = 标高
    }

    [Fact]
    public void CopyStyleFrom_preserves_elevation()
    {
        var src = new PolylineEntity { Elevation = 912.3 };
        var dst = new PolylineEntity();
        dst.CopyStyleFrom(src);
        Assert.Equal(912.3, dst.Elevation, 6);   // 移动/复制/镜像/剪贴板 不丢标高
    }
}
