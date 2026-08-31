using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>采区划分回归（剥采比场聚合 + 沿推进轴等煤量切分）。</summary>
public class PanelSplitTests
{
    // 均匀煤/岩场：Nx×Ny 列, 每列 coal=cv, waste=wv
    private static StripRatioField UniformField(int nx, int ny, double cv, double wv, double density = 1.35)
    {
        var f = new StripRatioField { Nx = nx, Ny = ny, Dx = 10, Dy = 10, Ox = 0, Oy = 0, Density = density,
            CoalVol = new double[nx * ny], WasteVol = new double[nx * ny] };
        for (int c = 0; c < nx * ny; c++) { f.CoalVol[c] = cv; f.WasteVol[c] = wv; }
        return f;
    }

    [Fact]
    public void FixedN_splits_into_requested_panels()
    {
        var f = UniformField(10, 10, 100, 200);
        var plan = new MiningPlanParams { Split = SplitObjective.FixedN, PanelCount = 4, AdvanceAzimuthDeg = 0 };
        var panels = PanelSplitter.Split(plan, f);
        Assert.Equal(4, panels.Count);
        // 每采区剥采比 = 岩/(煤·ρ) 一致(均匀场): 200/(100·1.35)=1.48 → 汇总后同
        foreach (var p in panels) Assert.Equal(1.48, p.StripRatio, 2);
        Assert.Equal(new[] { 1, 2, 3, 4 }, panels.Select(p => p.Order).ToArray());   // 开采序
    }

    [Fact]
    public void Panels_tile_the_field_extent()
    {
        var f = UniformField(10, 8, 100, 150);
        var plan = new MiningPlanParams { Split = SplitObjective.FixedN, PanelCount = 4, AdvanceAzimuthDeg = 0 };
        var panels = PanelSplitter.Split(plan, f);
        // 方位 0(沿 +Y 推进) → 采区沿 Y 排, 覆盖全 Y 幅[0, 8·10]
        Assert.Equal(0.0, panels.Min(p => p.MinY), 6);
        Assert.Equal(80.0, panels.Max(p => p.MaxY), 6);
        Assert.All(panels, p => { Assert.Equal(0.0, p.MinX, 6); Assert.Equal(100.0, p.MaxX, 6); });   // X 全幅
    }

    [Fact]
    public void FromBlocks_classifies_coal_by_grade_cutoff()
    {
        // 4 块(2×2 单位格): 品位 {2,2,0,0}, cutoff=1 → 两煤两岩
        var blocks = new List<(double X, double Y, double Z, double Size, double Grade)>
        {
            (0, 0, 0, 1, 2), (1, 0, 0, 1, 2), (0, 1, 0, 1, 0), (1, 1, 0, 1, 0)
        };
        var f = StripRatioField.FromBlocks(blocks, cutoff: 1, density: 1.35);
        Assert.NotNull(f);
        double totCoal = f!.CoalVol.Sum(), totWaste = f.WasteVol.Sum();
        Assert.Equal(2.0, totCoal, 6);    // 2 煤块 × 单位体积
        Assert.Equal(2.0, totWaste, 6);   // 2 岩块
    }

    [Fact]
    public void ByLife_adapts_panel_count()
    {
        var f = UniformField(20, 20, 100, 100);   // 大储量
        var plan = new MiningPlanParams { Split = SplitObjective.ByLife, PanelCount = 4, TargetCapacityWanTa = 400, TargetServiceLifeYears = 30 };
        var panels = PanelSplitter.Split(plan, f);
        Assert.InRange(panels.Count, 1, 16);   // 自适应, 夹在[1,16]
    }

    [Fact]
    public void AdvanceRateFrom_formula()
    {
        // v = cap·1e4/(L·H·ρ); cap=400万t, L=1000, H=12, ρ=1.35 → 400e4/(1000·12·1.35)=246.9
        Assert.Equal(400e4 / (1000 * 12 * 1.35), PanelSplitter.AdvanceRateFrom(400, 1000, 12, 1.35), 4);
        Assert.Equal(0, PanelSplitter.AdvanceRateFrom(400, 0, 12, 1.35), 9);   // 退化
    }

    [Fact]
    public void Empty_field_no_panels()
    {
        var f = new StripRatioField { Nx = 0, Ny = 0, Density = 1.35 };
        Assert.Empty(PanelSplitter.Split(new MiningPlanParams(), f));
    }
}
