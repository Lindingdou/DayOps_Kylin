using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 块体侧采样器 SectionSampler.SampleClipped 已知值回归 —— 忠实原 SectionSampler.SampleLayers 的
/// 逐层裁剪 + 回收率语义: 圈入量受裁, TotalCoalVol 不受裁。层序同 ResourceProfileLite(k=0 底)。
/// </summary>
public class SectionSamplerTests
{
    // 层0(Z=5): 两煤块(x=5 内 / x=15 外); 层1(Z=15): 两岩块。cell=10。
    static List<(double X, double Y, double Z, double Size, double Grade)> Grid()
        => new()
        {
            (5, 5, 5, 10, 1.0), (15, 5, 5, 10, 1.0),
            (5, 5, 15, 10, 0.0), (15, 5, 15, 10, 0.0),
        };

    static List<(double x, double y)> Rect(double x0, double x1)
        => new() { (x0, -100), (x1, -100), (x1, 100), (x0, 100) };

    [Fact]
    public void Null_clip_collects_all_recovery_100()
    {
        var p = SectionSampler.SampleClipped(Grid(), 0.5, 1.35, _ => null)!;
        Assert.Equal(2, p.Nz);
        Assert.Equal(2000, p.TotalCoalVol, 6);       // 2 煤块 × 1000 m³
        Assert.Equal(2000, p.EnclosedCoalVol, 6);    // 不裁全收
        Assert.Equal(100, p.RecoveryPct, 6);
    }

    [Fact]
    public void Clip_half_gives_recovery_50()
    {
        var p = SectionSampler.SampleClipped(Grid(), 0.5, 1.35, _ => Rect(0, 10))!;   // 只覆 x<10
        Assert.Equal(2000, p.TotalCoalVol, 6);       // 全模型煤不受裁
        Assert.Equal(1000, p.EnclosedCoalVol, 6);    // 只 (5,5,5) 圈入
        Assert.Equal(50, p.RecoveryPct, 6);
        Assert.Equal(1000, p.WasteM3, 6);            // 层1 (5,5,15) 岩圈入
    }

    [Fact]
    public void Empty_clip_layer_excludes_all_but_counts_total()
    {
        // 层0(煤)空裁=坑底以下全出圈; 层1(岩)全收。
        var p = SectionSampler.SampleClipped(Grid(), 0.5, 1.35,
            k => k == 0 ? new List<(double x, double y)>() : Rect(-100, 100))!;
        Assert.Equal(2000, p.TotalCoalVol, 6);       // 煤全入总数(不受裁)
        Assert.Equal(0, p.EnclosedCoalVol, 6);       // 层0 空裁全出圈
        Assert.Equal(0, p.RecoveryPct, 6);
        Assert.Equal(2000, p.WasteM3, 6);            // 层1 两岩块圈入
    }

    [Fact]
    public void Strip_ratio_from_enclosed_coal_and_waste()
    {
        var p = SectionSampler.SampleClipped(Grid(), 0.5, 1.35, _ => Rect(-100, 100))!;
        // 圈入煤 2000 m³ → 2700 t; 岩 2000 m³; SR = 2000/2700。
        Assert.Equal(2000.0 / (2000 * 1.35), p.StripRatioM3PerT, 6);
    }

    [Fact]
    public void Null_result_on_empty_blocks()
        => Assert.Null(SectionSampler.SampleClipped(new List<(double, double, double, double, double)>(), 0.5, 1.35, _ => null));
}
