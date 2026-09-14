using System.Collections.Generic;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <see cref="CadGlViewport.VisibleZMedian"/> 回归：2D→3D 切换时注视点高程要落到「视野内几何」上。
/// 起因: 场景里既有 z=0 的新画点又有 z≈1300 的等高线, 取景把注视点定在 650(极值中点); 把点「修改高程」到 1300 后
/// 全部几何都在注视点上方六七百米, 一切 3D 透视视锥罩不到 → "整个视图一片黑"。
/// </summary>
public class ViewportAimZTests
{
    // 交错 P3_C3: 每顶点 (x, y, z, r, g, b)
    static float[] Verts(params (float x, float y, float z)[] p)
    {
        var v = new float[p.Length * 6];
        for (int i = 0; i < p.Length; i++) { v[i * 6] = p[i].x; v[i * 6 + 1] = p[i].y; v[i * 6 + 2] = p[i].z; }
        return v;
    }

    static List<(float[]? verts, int stride, float ox, float oy)> One(float[]? v, int stride = 6, float ox = 0, float oy = 0)
        => new() { (v, stride, ox, oy) };

    [Fact]
    public void Median_of_vertices_inside_window_only()
    {
        // 窗口 [0,100]² 里: 等高线 1300/1310/1320 + 一个 z=0 的杂点; 窗口外: 一堆 z=0
        var scene = Verts((10, 10, 1300), (20, 20, 1310), (30, 30, 1320), (40, 40, 0),
                          (500, 500, 0), (510, 510, 0), (520, 520, 0), (530, 530, 0));
        var zc = CadGlViewport.VisibleZMedian(One(scene), 0, 0, 100, 100);
        Assert.NotNull(zc);
        Assert.Equal(1305, zc!.Value, 3);   // 偶数个取中间两个均值 (1300+1310)/2; 极值中点会是 660 → 哪层都不靠
    }

    [Fact]
    public void Falls_back_to_whole_scene_when_window_is_empty()
    {
        var scene = Verts((500, 500, 1200), (510, 510, 1250), (520, 520, 1300));
        var zc = CadGlViewport.VisibleZMedian(One(scene), 0, 0, 100, 100);
        Assert.Equal(1250, zc!.Value, 6);
    }

    [Fact]
    public void Null_when_no_geometry_at_all()
    {
        Assert.Null(CadGlViewport.VisibleZMedian(One(null), 0, 0, 100, 100));
        Assert.Null(CadGlViewport.VisibleZMedian(One(new float[0]), 0, 0, 100, 100));
    }

    [Fact]
    public void World_channel_is_localized_by_its_origin_before_window_test()
    {
        // 显示态导入线框存世界坐标(622945+…), 窗口是局部系: 减原点后才落进 [0,100]²
        var world = Verts((622955, 4380969, 1400), (623945, 4381959, 0));
        var zc = CadGlViewport.VisibleZMedian(One(world, 6, 622945, 4380959), 0, 0, 100, 100);
        Assert.Equal(1400, zc!.Value, 6);
    }

    [Fact]
    public void Stride9_material_channel_and_subsampling()
    {
        // 材质面 P3_C3_N3(步长 9); 顶点多于 cap 时抽样, 中值仍落在几何最密的那层
        int n = 10_000;
        var v = new float[n * 9];
        for (int i = 0; i < n; i++) { v[i * 9] = 5; v[i * 9 + 1] = 5; v[i * 9 + 2] = i < 9_000 ? 1300 : 0; }
        var zc = CadGlViewport.VisibleZMedian(One(v, 9), 0, 0, 10, 10, cap: 1000);
        Assert.Equal(1300, zc!.Value, 6);
    }
}
