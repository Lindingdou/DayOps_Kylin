using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

/// <summary>
/// 月度计划→日目标分配 ShortTermLink 已知值回归 —— 忠实原 TaskLib.Engine.ShortTermLink.ApplyToConfig:
/// 日采出=月采出万t·1e4/密度/作业日, 日剥离=月剥离万m³·1e4/作业日, 按面份额分配。
/// </summary>
public class ShortTermLinkTests
{
    [Fact]
    public void DailyCoal_and_rock_known_values()
    {
        // 15万t/25日/1.4 → 150000/1.4/25 = 4285.71 m³/日。
        Assert.Equal(150000.0 / 1.4 / 25, ShortTermLink.DailyCoalM3(15, 25, 1.4), 4);
        // 30万m³/25日 → 12000 m³/日。
        Assert.Equal(12000.0, ShortTermLink.DailyRockM3(30, 25), 6);
    }

    [Fact]
    public void NormalizeShares_defaults_and_renormalizes()
    {
        Assert.Equal(new[] { 0.5, 0.5 }, ShortTermLink.NormalizeShares(null, 2));       // 缺省均分
        Assert.Equal(new[] { 0.6, 0.4 }, ShortTermLink.NormalizeShares(new[] { 0.6, 0.4 }, 2));
        Assert.Equal(new[] { 0.5, 0.5 }, ShortTermLink.NormalizeShares(new[] { 0.3, 0.3 }, 2));  // 和 0.6 → 归一
        Assert.Equal(new[] { 0.5, 0.5 }, ShortTermLink.NormalizeShares(new[] { 1.0 }, 2));       // 长度不符 → 均分
    }

    [Fact]
    public void ApplyToConfig_distributes_by_share_and_reports()
    {
        var cfg = new ExploderConfig
        {
            Faces =
            {
                new FaceInput { Zone = "A", Process = ProcessType.Load },
                new FaceInput { Zone = "B", Process = ProcessType.Load },
                new FaceInput { Zone = "排土", Process = ProcessType.Dump },
            },
        };
        var month = new ShortTermLink.MonthInfo { HasPlan = true, PlanName = "9月计划", MonthLabel = "9",
            CoalWanT = 15, StripWanM3 = 30, Workdays = 25, Density = 1.4 };
        string note = ShortTermLink.ApplyToConfig(cfg, month, new[] { 0.6, 0.4 });
        double dayCoal = 150000.0 / 1.4 / 25;   // 4285.71
        Assert.Equal(System.Math.Round(dayCoal * 0.6), cfg.Faces[0].DayTargetM3, 6);   // A 2571
        Assert.Equal(System.Math.Round(dayCoal * 0.4), cfg.Faces[1].DayTargetM3, 6);   // B 1714
        Assert.Equal(12000, cfg.Faces[2].DayTargetM3, 6);                               // 排土 日剥离
        Assert.Contains("短期计划", note);
    }

    [Fact]
    public void ApplyToConfig_no_plan_leaves_targets_and_reports_fallback()
    {
        var cfg = new ExploderConfig { Faces = { new FaceInput { Zone = "A", Process = ProcessType.Load, DayTargetM3 = 999 } } };
        string note = ShortTermLink.ApplyToConfig(cfg, new ShortTermLink.MonthInfo { HasPlan = false });
        Assert.Equal(999, cfg.Faces[0].DayTargetM3, 6);   // 未改
        Assert.Contains("样例", note);
    }

    [Fact]
    public void MaterialWanM3_is_coal_over_density_plus_strip()
    {
        var m = new ShortTermLink.MonthInfo { CoalWanT = 140, StripWanM3 = 200, Density = 1.4 };
        Assert.Equal(140 / 1.4 + 200, m.MaterialWanM3, 6);   // 100+200=300
    }
}
