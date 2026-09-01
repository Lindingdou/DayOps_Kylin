using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using BPE = PitMine3D.Kylin.Cad.BenchParameterExtractor;
using BPV = PitMine3D.Kylin.Cad.BenchParameterVerifier;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 现状台阶参数校核 回归 —— 忠实移植原 ParameterVerifier 兜底路径的已知值验证:
/// 规范默认基准(Norm) + FallbackStatus(|偏差|>15%→warning) + 稳定性 F=tanφ/tanβ + Worst 状态。
/// </summary>
public class BenchParameterVerifierTests
{
    static BPE.Result M(double h, double a, double w, double beta)
        => new() { Ok = true, BenchCount = 3, BenchHeight = h, FaceAngleDeg = a, BermWidth = w, OverallSlopeAngleDeg = beta };

    [Fact]
    public void Norm_defaults_match_original()
    {
        Assert.Equal((12.0, 70.0, 4.0), BPV.Norm(false, null));
        Assert.Equal((15.0, 70.0, 8.0), BPV.Norm(false, "hard"));
        Assert.Equal((12.0, 68.0, 6.0), BPV.Norm(false, "medium"));
        Assert.Equal((10.0, 60.0, 5.0), BPV.Norm(false, "soft"));
        Assert.Equal((10.0, 35.0, 3.0), BPV.Norm(true, null));   // 排土场
    }

    [Fact]
    public void Measured_equals_norm_all_pass()
    {
        double beta = BPE.OverallSlopeAngleDeg(12, 70, 4);   // ≈55.11°
        var rep = BPV.Verify(M(12, 70, 4, beta), isDump: false);
        Assert.Equal(4, rep.Rows.Count);
        Assert.All(rep.Rows, r => Assert.Equal("pass", r.Status));
        Assert.Equal("pass", rep.OverallStatus);
        Assert.Contains("规范默认", rep.DesignProvenance);
        // 各行偏差≈0
        Assert.All(rep.Rows, r => Assert.True(System.Math.Abs(r.DeviationPct ?? 0) < 0.5));
    }

    [Fact]
    public void Deviation_beyond_15pct_flags_warning_boundary()
    {
        double beta = BPE.OverallSlopeAngleDeg(12, 70, 4);
        // 台阶高 14 vs 设计 12 → +16.67% >15 → warning; 其它三项按设计 → pass。
        var warn = BPV.Verify(M(14, 70, 4, beta), isDump: false);
        var hRow = warn.Rows.First(r => r.Code == "bench_height");
        Assert.Equal("warning", hRow.Status);
        Assert.Equal(16.67, hRow.DeviationPct!.Value, 1);
        Assert.Equal("warning", warn.OverallStatus);   // Worst = warning
        // 13.5 vs 12 → +12.5% <15 → pass。
        var pass = BPV.Verify(M(13.5, 70, 4, beta), isDump: false);
        Assert.Equal("pass", pass.Rows.First(r => r.Code == "bench_height").Status);
    }

    [Fact]
    public void Dump_uses_dump_norm_baseline()
    {
        double beta = BPE.OverallSlopeAngleDeg(10, 35, 3);
        var rep = BPV.Verify(M(10, 35, 3, beta), isDump: true);
        Assert.All(rep.Rows, r => Assert.Equal("pass", r.Status));
        Assert.Contains("排土场", rep.DesignProvenance);
    }

    [Fact]
    public void Stability_factor_note_threshold()
    {
        double b1 = BPE.OverallSlopeAngleDeg(12, 70, 4);
        // β≈55 太陡, φ=35 → F=tan35/tan55 < 1.3 → 警示。
        var steep = BPV.Verify(M(12, 70, 4, b1), isDump: false, frictionAngleDeg: 35);
        Assert.True(steep.StabilityF > 0);
        Assert.True(steep.StabilityF < 1.3);
        Assert.Contains(steep.Notes, n => n.Contains("<1.3"));
        // 缓坡 β=20, φ=35 → F=tan35/tan20 ≈1.92 ≥1.3 → 合格。
        var gentle = BPV.Verify(M(10, 45, 8, 20), isDump: false, frictionAngleDeg: 35);
        Assert.True(gentle.StabilityF >= 1.3);
        Assert.Contains(gentle.Notes, n => n.Contains("≥1.3"));
    }

    [Fact]
    public void Stability_not_computed_without_friction_or_slope()
    {
        var noPhi = BPV.Verify(M(12, 70, 4, 55), isDump: false);
        Assert.Equal(0, noPhi.StabilityF);
        var noBeta = BPV.Verify(M(12, 70, 0, 0), isDump: false, frictionAngleDeg: 35);
        Assert.Equal(0, noBeta.StabilityF);   // β=0 → 不算
    }

    [Fact]
    public void Design_override_replaces_norm()
    {
        double beta = BPE.OverallSlopeAngleDeg(10, 45, 5);
        var rep = BPV.Verify(M(10, 45, 5, beta), isDump: false, designOverride: (10, 45, 5));
        Assert.All(rep.Rows, r => Assert.Equal("pass", r.Status));
        Assert.Contains("调用方", rep.DesignProvenance);
    }

    [Fact]
    public void Worst_status_aggregates()
    {
        double beta = BPE.OverallSlopeAngleDeg(12, 70, 4);
        // 平盘宽 8 vs 设计 4 → +100% → warning; 其余 pass → 总体 warning。
        var rep = BPV.Verify(M(12, 70, 8, beta), isDump: false);
        Assert.Equal("warning", rep.OverallStatus);
        Assert.Equal("warning", rep.Rows.First(r => r.Code == "safety_platform_width").Status);
    }

    [Fact]
    public void Null_result_is_safe()
    {
        var rep = BPV.Verify(null!, isDump: false);
        Assert.Equal("pending", rep.OverallStatus);
        Assert.Contains(rep.Notes, n => n.Contains("无提取结果"));
    }

    [Fact]
    public void BuildReport_has_overall_and_rows()
    {
        double beta = BPE.OverallSlopeAngleDeg(12, 70, 4);
        var csv = BPV.BuildReport(BPV.Verify(M(12, 70, 4, beta), isDump: false));
        Assert.Contains("总体状态,合格", csv);
        Assert.Contains("参数,单位,实测,设计,偏差%,状态,依据", csv);
        Assert.Contains("台阶高", csv);
    }

    [Fact]
    public void Extractor_warnings_flow_into_report_notes()
    {
        var m = M(12, 70, 4, 55);
        m.Warnings.Add("台阶高不齐: 四分位距 5m。");
        var rep = BPV.Verify(m, isDump: false);
        Assert.Contains(rep.Notes, n => n.Contains("台阶高不齐"));
    }
}
