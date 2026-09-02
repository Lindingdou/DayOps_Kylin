using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 地形 Z 采样器已知值回归 —— 忠实原 MeshZSampler.cs：
/// MeshZSampler(TIN 重心插值) / BenchZField(台阶线 IDW 场) / CompositeZSampler(TIN 优先退台阶线) /
/// RoadTerrainSampler.BuildSampler(统一构建)。给 RoadNetworkConnector 的 IRoadZSampler 落地实现。
/// </summary>
public class RoadZSamplersTests
{
    // 斜平面 z=x：正方形 [0,10]×[0,10] 两三角，四角 (0,0,0)(10,0,10)(0,10,0)(10,10,10) 均满足 z=x。
    static readonly double[] PlaneVerts = { 0, 0, 0, 10, 0, 10, 0, 10, 0, 10, 10, 10 };
    static readonly int[] PlaneTris = { 0, 1, 2, 1, 3, 2 };

    [Fact]
    public void MeshZSampler_barycentric_on_tilted_plane_equals_x()
    {
        var m = MeshZSampler.Build(PlaneVerts, PlaneTris);
        Assert.NotNull(m);
        Assert.True(m!.TrySample(3, 3, out double z1)); Assert.Equal(3, z1, 6);   // 下三角内 z=x=3
        Assert.True(m.TrySample(7, 7, out double z2)); Assert.Equal(7, z2, 6);    // 上三角内 z=x=7
        Assert.True(m.TrySample(8, 2, out double z3)); Assert.Equal(8, z3, 6);    // z=x=8
    }

    [Fact]
    public void MeshZSampler_returns_false_outside_mesh()
    {
        var m = MeshZSampler.Build(PlaneVerts, PlaneTris)!;
        Assert.False(m.TrySample(100, 100, out _));
        Assert.False(m.TrySample(-5, 5, out _));
    }

    [Fact]
    public void MeshZSampler_build_guards_invalid_input()
    {
        Assert.Null(MeshZSampler.Build(null!, null!));
        Assert.Null(MeshZSampler.Build(new double[] { 0, 0, 0 }, new[] { 0, 1, 2 }));   // 顶点不足
        Assert.Null(MeshZSampler.Build(new double[] { 0, 0, 0, 0, 0, 5, 0, 0, 9 }, new[] { 0, 1, 2 })); // 退化 bbox(全同 x,y)
    }

    [Fact]
    public void BenchZField_exact_hit_returns_that_bench_z()
    {
        // 两条水平台阶线：A y=0 z=100，B y=50 z=110。
        var lines = new[]
        {
            new double[] { 0, 0, 100, 100, 0, 100 },
            new double[] { 0, 50, 110, 100, 50, 110 },
        };
        var f = BenchZField.Build(lines);
        Assert.NotNull(f);
        Assert.True(f!.TrySample(50, 0, out double za)); Assert.Equal(100, za, 6);   // 正落 A 加密点
        Assert.True(f.TrySample(50, 50, out double zb)); Assert.Equal(110, zb, 6);   // 正落 B 加密点
    }

    [Fact]
    public void BenchZField_midway_blends_symmetrically()
    {
        var lines = new[]
        {
            new double[] { 0, 0, 100, 100, 0, 100 },
            new double[] { 0, 50, 110, 100, 50, 110 },
        };
        var f = BenchZField.Build(lines)!;
        // (50,25) 到两线对称 → IDW 权重对称 → (100+110)/2=105。
        Assert.True(f.TrySample(50, 25, out double z));
        Assert.Equal(105, z, 3);
    }

    [Fact]
    public void BenchZField_returns_false_beyond_radius_and_guards_empty()
    {
        var f = BenchZField.Build(new[] { new double[] { 0, 0, 100, 100, 0, 100 } })!;
        Assert.False(f.TrySample(50, 500, out _));   // 距唯一台阶线 500m > 60m 半径
        Assert.Null(BenchZField.Build(null!));
        Assert.Null(BenchZField.Build(System.Array.Empty<double[]>()));
    }

    [Fact]
    public void CompositeZSampler_prefers_tin_then_falls_back_to_bench()
    {
        var mesh = MeshZSampler.Build(PlaneVerts, PlaneTris)!;                       // 覆盖 [0,10]²
        var bench = BenchZField.Build(new[] { new double[] { 40, 50, 200, 60, 50, 200 } })!; // 覆盖 (50,50) 附近
        var c = new CompositeZSampler(mesh, bench);

        Assert.True(c.TrySample(5, 5, out double zTin)); Assert.Equal(5, zTin, 6);   // TIN 命中优先 z=x=5
        Assert.True(c.TrySample(50, 50, out double zBench)); Assert.Equal(200, zBench, 6); // TIN 落空退台阶线
        Assert.False(c.TrySample(500, 500, out _));                                  // 都落空
    }

    [Fact]
    public void RoadTerrainSampler_builds_composite_mesh_or_bench_by_availability()
    {
        var lines = new[] { new double[] { 40, 50, 200, 60, 50, 200 } };
        // 两者都有 → 组合(TIN 优先)。
        var both = RoadTerrainSampler.BuildSampler(PlaneVerts, PlaneTris, lines)!;
        Assert.True(both.TrySample(5, 5, out double z1)); Assert.Equal(5, z1, 6);
        Assert.True(both.TrySample(50, 50, out double z2)); Assert.Equal(200, z2, 6);
        // 仅 TIN。
        var meshOnly = RoadTerrainSampler.BuildSampler(PlaneVerts, PlaneTris, System.Array.Empty<double[]>())!;
        Assert.IsType<MeshZSampler>(meshOnly);
        // 仅台阶线。
        var benchOnly = RoadTerrainSampler.BuildSampler(System.Array.Empty<double>(), System.Array.Empty<int>(), lines)!;
        Assert.IsType<BenchZField>(benchOnly);
        // 都无 → null。
        Assert.Null(RoadTerrainSampler.BuildSampler(System.Array.Empty<double>(), System.Array.Empty<int>(), System.Array.Empty<double[]>()));
    }
}
