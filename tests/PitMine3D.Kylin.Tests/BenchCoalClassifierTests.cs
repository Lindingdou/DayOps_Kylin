using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>台阶煤/岩判定回归 —— 台阶区间∩煤层区间重叠煤厚 → 占比 → 煤/混/岩; 尖灭以全采样点为分母。</summary>
public class BenchCoalClassifierTests
{
    // 平顶底板煤层(roof/floor 各 4 角同高), 覆盖 x∈[x0,x1]×y∈[0,20]
    static VirtualBorehole.Seam FlatSeam(string code, double roofZ, double floorZ, double x0 = 0, double x1 = 20)
        => new(code,
            new List<(double, double, double)> { (x0, 0, roofZ), (x1, 0, roofZ), (x1, 20, roofZ), (x0, 20, roofZ) },
            new List<(double, double, double)> { (x0, 0, floorZ), (x1, 0, floorZ), (x1, 20, floorZ), (x0, 20, floorZ) });

    static readonly List<(double x, double y)> Foot = new() { (5, 10), (10, 10), (15, 10) };

    [Fact]
    public void Mixed_bench_when_coal_ratio_between_floor_and_threshold()
    {
        // 煤层[5,8](厚3) ∩ 台阶[0,10] = 3; 占比 3/10=0.3 → 混合台阶(>0.05,<0.5)
        var seams = new[] { FlatSeam("4-1", 8, 5) };
        var r = BenchCoalClassifier.Classify(Foot, 0, 10, seams);
        Assert.Equal(BenchCoalResult.Kind.Mixed, r.BenchKind);
        Assert.Equal(0.3, r.CoalRatio, 6);
        Assert.Equal(3.0, r.MeanCoalThickM, 6);
        Assert.True(r.IsMineableCoal);                 // 3 ≥ 0.8
        Assert.Equal(3, r.SampleHit);
        Assert.Equal("4-1", r.Seams.Single().Name);
        Assert.Equal(3.0, r.Seams.Single().MeanThickM, 6);
    }

    [Fact]
    public void Coal_bench_when_ratio_reaches_threshold()
    {
        // 煤层[5,8] ∩ 台阶[6,10] = [6,8]=2; 占比 2/4=0.5 → 煤台阶(≥0.5)
        var seams = new[] { FlatSeam("4-1", 8, 5) };
        var r = BenchCoalClassifier.Classify(Foot, 6, 10, seams);
        Assert.Equal(BenchCoalResult.Kind.Coal, r.BenchKind);
        Assert.Equal(0.5, r.CoalRatio, 6);
    }

    [Fact]
    public void Rock_bench_when_interval_misses_seam()
    {
        // 台阶[15,20] 在煤层[5,8]之上 → 无重叠 → 岩台阶, 无见煤点
        var seams = new[] { FlatSeam("4-1", 8, 5) };
        var r = BenchCoalClassifier.Classify(Foot, 15, 20, seams);
        Assert.Equal(BenchCoalResult.Kind.Rock, r.BenchKind);
        Assert.Equal(0, r.SampleHit);
        Assert.Equal(0.0, r.MeanCoalThickM, 9);
        Assert.Empty(r.Seams);
    }

    [Fact]
    public void Pinchout_pulls_mean_down_using_all_points_as_denominator()
    {
        // 煤层只覆盖 x∈[0,12] → 采样点 (5,10)(10,10) 见煤 3, (15,10) 尖灭(TIN 外)煤厚 0
        var seams = new[] { FlatSeam("4-1", 8, 5, 0, 12) };
        var r = BenchCoalClassifier.Classify(Foot, 0, 10, seams);
        Assert.Equal(2, r.SampleHit);
        Assert.Equal(3, r.SampleCount);
        Assert.Equal(2.0, r.MeanCoalThickM, 6);        // (3+3+0)/3=2, 分母含尖灭点
        Assert.Equal(0.2, r.CoalRatio, 6);
    }

    [Fact]
    public void Thin_seam_is_mixed_but_not_mineable_ratio_and_mineable_decoupled()
    {
        // 薄煤层[5,5.7](厚0.7) ∩ 台阶[0,10]=0.7; 占比 0.07>0.05 → 混合台阶, 但均厚 0.7<0.8 → 不出煤体。
        // 验证"贴标签(占比)"与"出不出煤体(可采厚)"脱钩——薄煤层不因台阶高被永远判成岩。
        var seams = new[] { FlatSeam("9", 5.7, 5) };
        var r = BenchCoalClassifier.Classify(Foot, 0, 10, seams);
        Assert.Equal(BenchCoalResult.Kind.Mixed, r.BenchKind);   // 0.07 > 0.05
        Assert.False(r.IsMineableCoal);                          // 0.7 < 0.8
        Assert.Equal(0.7, r.MeanCoalThickM, 6);
    }

    [Fact]
    public void Multiple_seams_in_one_bench_accumulate_separately()
    {
        // 一台阶[0,15] 压两层: 4-1[10,12](2), 4-2[5,6](1) → 总煤厚3, 各层分列
        var seams = new[] { FlatSeam("4-1", 12, 10), FlatSeam("4-2", 6, 5) };
        var r = BenchCoalClassifier.Classify(Foot, 0, 15, seams);
        Assert.Equal(3.0, r.MeanCoalThickM, 6);
        Assert.Equal(2, r.Seams.Count);
        Assert.Equal("4-1", r.Seams[0].Name);          // 降序: 4-1(2) 在前
        Assert.Equal(2.0, r.Seams[0].MeanThickM, 6);
        Assert.Equal(1.0, r.Seams[1].MeanThickM, 6);
    }
}
