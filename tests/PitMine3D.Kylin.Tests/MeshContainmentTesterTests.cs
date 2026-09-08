using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 奇偶射线内外测试器（原 BlockModelLib.MeshContainmentTester）——「约束块体 / 实体转块体 / 体素格网体积」
/// 的默认判据；「高容错」勾选后才换缠绕数。这里同时钉住两者在水密体上必须一致。
/// </summary>
public class MeshContainmentTesterTests
{
    private static MeshContainmentTester Box(double cx, double cy, double cz, double sx, double sy, double sz)
    {
        var (v, t) = PrimitiveBodies.Box(cx, cy, cz, sx, sy, sz);
        return MeshContainmentTester.FromMesh(v, t);
    }

    [Fact]
    public void Parity_inside_outside_on_closed_box()
    {
        var box = Box(5, 5, 5, 10, 10, 10);   // [0,10]³
        Assert.Equal(0, box.MinX, 6);
        Assert.Equal(10, box.MaxZ, 6);
        Assert.True(box.IsInsideClosed(5, 5, 5));
        Assert.True(box.IsInsideClosed(1, 9, 2));
        Assert.False(box.IsInsideClosed(5, 5, 12));    // 盒上方
        Assert.False(box.IsInsideClosed(5, 5, -1));    // 盒下方
        Assert.False(box.IsInsideClosed(20, 5, 5));    // 投影外
    }

    [Fact]
    public void Parity_agrees_with_winding_number_on_watertight_mesh()
    {
        var (v, t) = PrimitiveBodies.Sphere(0, 0, 0, 5, 20, 30);
        var (fv, ft) = MeshContainment.Flatten(v, t);
        var parity = new MeshContainmentTester(fv, ft);
        var gwn = new WindingNumberTester(fv, ft);
        var rnd = new Random(20260908);
        int checkedPts = 0;
        for (int i = 0; i < 400; i++)
        {
            double x = rnd.NextDouble() * 14 - 7, y = rnd.NextDouble() * 14 - 7, z = rnd.NextDouble() * 14 - 7;
            double r = Math.Sqrt(x * x + y * y + z * z);
            if (Math.Abs(r - 5) < 0.35) continue;      // 皮上不比（离散化误差带）
            Assert.Equal(gwn.IsInsideClosed(x, y, z), parity.IsInsideClosed(x, y, z));
            checkedPts++;
        }
        Assert.True(checkedPts > 300);
    }

    [Fact]
    public void RaycastAbove_gives_above_below_on_open_surface()
    {
        // 水平面 z=100，XY ∈ [0,10]²，两个三角
        var verts = new double[] { 0, 0, 100, 10, 0, 100, 10, 10, 100, 0, 10, 100 };
        var tris = new[] { 0, 1, 2, 0, 2, 3 };
        var t = new MeshContainmentTester(verts, tris);

        Assert.True(t.RaycastAbove(5, 5, 50, out int crossings, out double mz));
        Assert.Equal(1, crossings);
        Assert.Equal(100, mz, 6);
        Assert.True(t.IsBelowSurface(5, 5, 50));
        Assert.False(t.IsAboveSurface(5, 5, 50));

        Assert.True(t.RaycastAbove(5, 5, 150, out crossings, out mz));
        Assert.Equal(0, crossings);
        Assert.True(double.IsNaN(mz));
        Assert.True(t.IsAboveSurface(5, 5, 150));
        Assert.False(t.IsBelowSurface(5, 5, 150));

        // 投影外：两个方向都判不出（约束块体据此走「地表未覆盖区」选项）
        Assert.False(t.RaycastAbove(50, 5, 50, out _, out _));
        Assert.False(t.IsAboveSurface(50, 5, 50));
        Assert.False(t.IsBelowSurface(50, 5, 50));
    }

    [Fact]
    public void BoxTouchesSurface_only_when_plane_crosses_box()
    {
        var verts = new double[] { 0, 0, 100, 10, 0, 100, 10, 10, 100, 0, 10, 100 };
        var tris = new[] { 0, 1, 2, 0, 2, 3 };
        var t = new MeshContainmentTester(verts, tris);
        Assert.True(t.BoxTouchesSurface(2, 2, 90, 4, 4, 110));    // 面从盒肚子穿过
        Assert.False(t.BoxTouchesSurface(2, 2, 0, 4, 4, 50));     // 整盒在面下
        Assert.False(t.BoxTouchesSurface(2, 2, 150, 4, 4, 200));  // 整盒在面上
        Assert.False(t.BoxTouchesSurface(50, 50, 90, 60, 60, 110)); // XY 投影外
    }

    [Fact]
    public void Voxel_build_high_tolerance_matches_parity_on_closed_mesh()
    {
        // 水密体：勾不勾「高容错」结果必须一样（原版据此对闭合网格一律走奇偶射线提速）
        var (v, t) = PrimitiveBodies.Box(5, 5, 5, 10, 10, 10);
        var box = BlockVoxelBuilder.FromMesh("box", v, t);
        Assert.True(box.Closed);
        var plain = BlockVoxelBuilder.Build(new[] { box }, 2, 2, 2, out _)!;
        var high = BlockVoxelBuilder.Build(new[] { box }, 2, 2, 2, out _, highTolerance: true)!;
        Assert.Equal(plain.KeepCount, high.KeepCount);
        Assert.Equal(1000, plain.TotalVolume, 6);
        // 闭合体在高容错下仍返回同一个奇偶测试器实例（不白建 GWN）
        Assert.Same(box.Tester, box.TesterFor(true));
        Assert.Same(box.Tester, box.TesterFor(false));
    }

    [Fact]
    public void TesterFor_switches_to_winding_number_only_when_mesh_is_open()
    {
        // 开放面（单个三角）：不勾高容错走奇偶，勾了才换缠绕数
        var v = new[] { (0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (0.0, 10.0, 0.0) };
        var t = new[] { (0, 1, 2) };
        var open = BlockVoxelBuilder.FromMesh("open", v, t);
        Assert.False(open.Closed);
        Assert.Same(open.Tester, open.TesterFor(false));
        Assert.IsType<WindingNumberTester>(open.TesterFor(true));
    }
}
