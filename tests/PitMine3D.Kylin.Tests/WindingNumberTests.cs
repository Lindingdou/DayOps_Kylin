using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>广义缠绕数点在网格内测试 + 均匀体素体积回归。</summary>
public class WindingNumberTests
{
    // 用 PrimitiveBodies.Box 造闭合盒 → flat 数组
    private static (double[] v, int[] t) BoxFlat(double cx, double cy, double cz, double s)
    {
        var (vv, tt) = PrimitiveBodies.Box(cx, cy, cz, s, s, s);
        var v = new double[vv.Count * 3];
        for (int i = 0; i < vv.Count; i++) { v[i * 3] = vv[i].x; v[i * 3 + 1] = vv[i].y; v[i * 3 + 2] = vv[i].z; }
        var t = new int[tt.Count * 3];
        for (int i = 0; i < tt.Count; i++) { t[i * 3] = tt[i].a; t[i * 3 + 1] = tt[i].b; t[i * 3 + 2] = tt[i].c; }
        return (v, t);
    }

    [Fact]
    public void Inside_box_center_winding_near_one()
    {
        var (v, t) = BoxFlat(0, 0, 0, 10);
        var wn = new WindingNumberTester(v, t) { Beta = 0 };   // 全精确
        Assert.Equal(1.0, wn.Winding(0, 0, 0), 3);
        Assert.True(wn.IsInsideClosed(0, 0, 0));
        Assert.True(wn.IsInsideClosed(4, 4, 4));
    }

    [Fact]
    public void Outside_box_winding_near_zero()
    {
        var (v, t) = BoxFlat(0, 0, 0, 10);
        var wn = new WindingNumberTester(v, t) { Beta = 0 };
        Assert.Equal(0.0, wn.Winding(100, 100, 100), 3);
        Assert.False(wn.IsInsideClosed(100, 0, 0));
        Assert.False(wn.IsInsideClosed(6, 0, 0));   // 盒半宽 5, x=6 在外
    }

    [Fact]
    public void Bbox_reject_outside_fast()
    {
        var (v, t) = BoxFlat(0, 0, 0, 10);
        var wn = new WindingNumberTester(v, t);
        Assert.False(wn.IsInsideClosed(1000, 0, 0));   // 盒外快速排除
    }

    [Fact]
    public void Bvh_and_exact_agree()
    {
        // 用较密的球验证 BVH 远场近似(Beta=2) 与全精确(Beta=0) 判定一致
        var (vv, tt) = PrimitiveBodies.Sphere(0, 0, 0, 5, 20, 30);
        var v = new double[vv.Count * 3];
        for (int i = 0; i < vv.Count; i++) { v[i * 3] = vv[i].x; v[i * 3 + 1] = vv[i].y; v[i * 3 + 2] = vv[i].z; }
        var t = new int[tt.Count * 3];
        for (int i = 0; i < tt.Count; i++) { t[i * 3] = tt[i].a; t[i * 3 + 1] = tt[i].b; t[i * 3 + 2] = tt[i].c; }
        var exact = new WindingNumberTester(v, t) { Beta = 0 };
        var bvh = new WindingNumberTester(v, t) { Beta = 2 };
        foreach (var (px, py, pz) in new[] { (0.0, 0.0, 0.0), (3.0, 0.0, 0.0), (10.0, 0.0, 0.0), (0.0, 6.0, 0.0) })
            Assert.Equal(exact.IsInsideClosed(px, py, pz), bvh.IsInsideClosed(px, py, pz));
    }

    [Fact]
    public void Voxel_volume_approaches_box_volume()
    {
        var (v, t) = BoxFlat(0, 0, 0, 10);   // 体积 1000
        var wn = new WindingNumberTester(v, t);
        double cell = 1.0;
        long occ = 0;
        for (double z = wn.MinZ + cell * 0.5; z <= wn.MaxZ; z += cell)
            for (double y = wn.MinY + cell * 0.5; y <= wn.MaxY; y += cell)
                for (double x = wn.MinX + cell * 0.5; x <= wn.MaxX; x += cell)
                    if (wn.IsInsideClosed(x, y, z)) occ++;
        double vol = occ * cell * cell * cell;
        Assert.InRange(vol, 900, 1000);   // 10×10×10 网格, 边界半格误差内
    }
}
