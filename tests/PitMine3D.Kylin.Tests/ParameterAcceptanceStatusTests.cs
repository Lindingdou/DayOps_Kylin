using PitMine3D.Kylin.Tests;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>
/// 参数验收状态判定(GeoDataQueries.ComputeAcceptanceStatus)+ DB 逐参数标准/报警范围(GetParameterNorm)已知值回归。
/// 忠实原 GeoDataBase ParameterAcceptanceService.ComputeStatus —— 报警阈超限→fail, 标准范围/偏差>15%→warning。
/// (区别 BenchParameterVerifier 兜底路径: 此用 DB 逐参数标定的 standard/alarm 范围, 数据已种子于 parameter_definition。)
/// </summary>
public class ParameterAcceptanceStatusTests
{
    [Fact]
    public void No_measured_is_pending()
    {
        var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(null, null, null, null, 100, null);
        Assert.Null(dev);
        Assert.Equal("pending", status);
    }

    [Fact]
    public void Alarm_breach_is_fail()
    {
        // 实测 3 < 报警下限 5 → fail(无模板→偏差 null)。
        var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(alarmLow: 5, alarmHigh: null,
            standardMin: null, standardMax: null, templateValue: null, measuredValue: 3);
        Assert.Null(dev);
        Assert.Equal("fail", status);
    }

    [Fact]
    public void Below_standard_min_is_warning()
    {
        // 实测 8 < 标准下限 10(无报警阈)→ warning。
        var (_, status) = GeoDataQueries.ComputeAcceptanceStatus(null, null, standardMin: 10, standardMax: 20, null, 8);
        Assert.Equal("warning", status);
    }

    [Fact]
    public void Deviation_over_15pct_is_warning()
    {
        // 标准范围内但偏差 20% > 15% → warning。
        var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(null, null, standardMin: 0, standardMax: 1000,
            templateValue: 100, measuredValue: 120);
        Assert.Equal(20.0, dev!.Value, 6);
        Assert.Equal("warning", status);
    }

    [Fact]
    public void Within_all_ranges_is_pass()
    {
        // 实测 105: 报警 80/120 内、标准 90/110 内、偏差 5% < 15% → pass。
        var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(alarmLow: 80, alarmHigh: 120,
            standardMin: 90, standardMax: 110, templateValue: 100, measuredValue: 105);
        Assert.Equal(5.0, dev!.Value, 6);
        Assert.Equal("pass", status);
    }

    [Fact]
    public void Alarm_takes_precedence_over_standard()
    {
        // 实测 200 同时超标准上限 180 与报警上限 150 → fail(报警优先于 warning)。
        var (_, status) = GeoDataQueries.ComputeAcceptanceStatus(alarmLow: null, alarmHigh: 150,
            standardMin: null, standardMax: 180, templateValue: null, measuredValue: 200);
        Assert.Equal("fail", status);
    }

    [Fact]
    public void GetBenchDesignBaseline_reads_seeded_project_params()
    {
        using var db = TestDb.Open();
        var bd = GeoDataQueries.GetBenchDesignBaseline(db.Connection);
        // V007/V026 种子: bench_height/bench_slope_angle/safety_platform_width 的 standard_default 齐备 → FromDb。
        Assert.True(bd.FromDb, "采场台阶设计基准(H/α/W)应齐备(V007+V026 种子)");
        Assert.NotNull(bd.BenchHeightM);
        Assert.NotNull(bd.SlopeAngleDeg);
        Assert.NotNull(bd.SafetyPlatformWidthM);
        // 合理量级(台阶高 5~20m·坡面角 40~80°·安全平台宽 3~30m)—— 真实设计参数非硬编码占位。
        Assert.InRange(bd.BenchHeightM!.Value, 5, 20);
        Assert.InRange(bd.SlopeAngleDeg!.Value, 40, 80);
        Assert.InRange(bd.SafetyPlatformWidthM!.Value, 3, 30);
    }

    [Fact]
    public void GetParameterNorm_unknown_code_returns_null()
    {
        using var db = TestDb.Open();
        Assert.Null(GeoDataQueries.GetParameterNorm(db.Connection, "no_such_param_xyz"));
    }

    [Fact]
    public void GetParameterNorm_seeded_code_roundtrips_and_default_passes()
    {
        using var db = TestDb.Open();
        // 取种子库任一参数定义 code, 验其范围可取回, 且标准默认值判为 pass(默认值应落标准范围内)。
        string? code;
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT code FROM parameter_definition WHERE is_active = 1 AND standard_default IS NOT NULL LIMIT 1";
            code = cmd.ExecuteScalar() as string;
        }
        Assert.NotNull(code);   // 种子库应有已标定参数定义
        var norm = GeoDataQueries.GetParameterNorm(db.Connection, code!);
        Assert.NotNull(norm);
        Assert.Equal(code, norm!.Code);
        // 标准默认值喂 ComputeStatus(模板=默认)→ 偏差 0、落标准范围内 → pass。
        var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(
            norm.AlarmLow, norm.AlarmHigh, norm.StandardMin, norm.StandardMax, norm.StandardDefault, norm.StandardDefault);
        Assert.Equal("pass", status);
        if (norm.StandardDefault.HasValue) Assert.Equal(0.0, dev!.Value, 6);
    }
}
