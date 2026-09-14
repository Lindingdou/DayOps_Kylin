using System;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Transport;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 延拓触发设置（§三四〇）：三段参数收编成一处持久默认 + 映射到演化引擎选项 + 校验。
///
/// 两条最要紧的：**默认值必须与 <see cref="RoadEvolutionOptions"/> 现默认逐项相等**（零回归），
/// 以及**移位阈值必须比匹配容差窄**（填反了永远判不出「移位」）。
/// </summary>
public class ExtendTriggerSettingsTests
{
    // ── 零回归 ──────────────────────────────────────────────
    [Fact]
    public void 零回归_默认值与演化引擎现默认逐项相等()
    {
        // 不动设置时行为必须与"写死 new RoadEvolutionOptions()"完全一致
        var o = new ExtendTriggerSettings().ToEvolutionOptions();
        var d = new RoadEvolutionOptions();
        Assert.Equal(d.SampleStepM, o.SampleStepM, 9);
        Assert.Equal(d.MatchToleranceM, o.MatchToleranceM, 9);
        Assert.Equal(d.MatchAngleDeg, o.MatchAngleDeg, 9);
        Assert.Equal(d.NewCoverFrac, o.NewCoverFrac, 9);
        Assert.Equal(d.GoneCoverFrac, o.GoneCoverFrac, 9);
        Assert.Equal(d.GrowMinLenM, o.GrowMinLenM, 9);
        Assert.Equal(d.ShiftThresholdM, o.ShiftThresholdM, 9);
        Assert.Null(o.AdvanceDirXY);
        Assert.Null(d.AdvanceDirXY);
    }

    [Fact]
    public void 零回归_默认配置校验放行()
        => Assert.Empty(new ExtendTriggerSettings().Validate());

    // ── 方位角映射 ──────────────────────────────────────────
    [Fact]
    public void 方向_不启用时不给引擎方向先验()
    {
        var s = new ExtendTriggerSettings { UseAdvanceDir = false, AdvanceAzimuthDeg = 90 };
        Assert.Null(s.ToEvolutionOptions().AdvanceDirXY);   // 填了角度但没勾选 ⇒ 不生效
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0)]      // 北 = +Y
    [InlineData(90.0, 1.0, 0.0)]     // 东 = +X（顺时针）
    [InlineData(180.0, 0.0, -1.0)]   // 南
    [InlineData(270.0, -1.0, 0.0)]   // 西
    public void 方向_方位角按北零顺时针映射成单位向量(double az, double ex, double ey)
    {
        var s = new ExtendTriggerSettings { UseAdvanceDir = true, AdvanceAzimuthDeg = az };
        var d = s.ToEvolutionOptions().AdvanceDirXY;
        Assert.NotNull(d);
        Assert.Equal(ex, d!.Value.X, 9);
        Assert.Equal(ey, d.Value.Y, 9);
    }

    [Fact]
    public void 方向_映射出来的是单位向量()
    {
        var s = new ExtendTriggerSettings { UseAdvanceDir = true, AdvanceAzimuthDeg = 37 };
        var d = s.ToEvolutionOptions().AdvanceDirXY!.Value;
        Assert.Equal(1.0, Math.Sqrt(d.X * d.X + d.Y * d.Y), 9);
    }

    [Fact]
    public void 映射_B段阈值直填同名属性()
    {
        var s = new ExtendTriggerSettings
        {
            SampleStepM = 1.5, MatchToleranceM = 12, MatchAngleDeg = 20,
            NewCoverFrac = 0.5, GoneCoverFrac = 0.6, GrowMinLenM = 25, ShiftThresholdM = 4,
        };
        var o = s.ToEvolutionOptions();
        Assert.Equal(1.5, o.SampleStepM, 9);
        Assert.Equal(12, o.MatchToleranceM, 9);
        Assert.Equal(20, o.MatchAngleDeg, 9);
        Assert.Equal(0.5, o.NewCoverFrac, 9);
        Assert.Equal(0.6, o.GoneCoverFrac, 9);
        Assert.Equal(25, o.GrowMinLenM, 9);
        Assert.Equal(4, o.ShiftThresholdM, 9);
    }

    // ── 校验 ────────────────────────────────────────────────
    [Fact]
    public void 校验_移位阈值必须比匹配容差窄()
    {
        // 容差答"是不是同一条路"(宽)，移位阈值答"变没变"(窄)；填反了容差内的推进永远不报
        var s = new ExtendTriggerSettings { MatchToleranceM = 8, ShiftThresholdM = 8 };
        Assert.Contains(s.Validate(), e => e.Contains("永远判不出"));

        s.ShiftThresholdM = 9;
        Assert.Contains(s.Validate(), e => e.Contains("永远判不出"));

        s.ShiftThresholdM = 7.9;
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void 校验_移位阈值为零时报的是零而不是宽窄()
    {
        // 两条判据不能同时报, 否则用户先改宽窄改半天也去不掉那条
        var s = new ExtendTriggerSettings { ShiftThresholdM = 0 };
        var e = s.Validate();
        Assert.Contains(e, x => x.Contains("移位阈值需大于 0"));
        Assert.DoesNotContain(e, x => x.Contains("永远判不出"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void 校验_推进步距要大于零(double v)
        => Assert.Contains(new ExtendTriggerSettings { AdvanceStepM = v }.Validate(),
                           e => e.Contains("推进步距阈值需大于 0"));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void 校验_时间步至少一期(int v)
        => Assert.Contains(new ExtendTriggerSettings { TimeStepPeriods = v }.Validate(),
                           e => e.Contains("时间步阈值"));

    [Theory]
    [InlineData(-1.0)]
    [InlineData(91.0)]
    [InlineData(double.NaN)]
    public void 校验_走向夹角要在零到九十(double v)
        => Assert.Contains(new ExtendTriggerSettings { MatchAngleDeg = v }.Validate(),
                           e => e.Contains("走向夹角"));

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void 校验_覆盖比要在开区间零到一(double v)
    {
        Assert.Contains(new ExtendTriggerSettings { NewCoverFrac = v }.Validate(), e => e.Contains("新建覆盖比"));
        Assert.Contains(new ExtendTriggerSettings { GoneCoverFrac = v }.Validate(), e => e.Contains("废除覆盖比"));
    }

    [Fact]
    public void 校验_采样步长与最小段长要大于零()
    {
        Assert.Contains(new ExtendTriggerSettings { SampleStepM = 0 }.Validate(), e => e.Contains("采样步长"));
        Assert.Contains(new ExtendTriggerSettings { GrowMinLenM = 0 }.Validate(), e => e.Contains("最小段长"));
        Assert.Contains(new ExtendTriggerSettings { MatchToleranceM = 0 }.Validate(), e => e.Contains("匹配容差"));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(360.0)]
    [InlineData(400.0)]
    public void 校验_启用方向时方位角要在零到三百六十(double az)
        => Assert.Contains(new ExtendTriggerSettings { UseAdvanceDir = true, AdvanceAzimuthDeg = az }.Validate(),
                           e => e.Contains("方位角"));

    [Fact]
    public void 校验_没启用方向就不查方位角()
    {
        // 没勾选时那个框是禁用的, 里头留着什么值都不该拦确认
        var s = new ExtendTriggerSettings { UseAdvanceDir = false, AdvanceAzimuthDeg = 999 };
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void 校验_多条错一起报()
    {
        var s = new ExtendTriggerSettings { AdvanceStepM = 0, SampleStepM = 0, NewCoverFrac = 2 };
        Assert.True(s.Validate().Count >= 3);
    }

    // ── 摘要文案 ────────────────────────────────────────────
    [Fact]
    public void 文案_两种触发模式各自的说法()
    {
        Assert.Contains("推进步距 50m", new ExtendTriggerSettings().Caption);
        Assert.Contains("时间步 每3期",
            new ExtendTriggerSettings { TriggerMode = ExtendTriggerSettings.Mode.TimeStep, TimeStepPeriods = 3 }.Caption);
        Assert.Contains("推进方向 关", new ExtendTriggerSettings().Caption);
        Assert.Contains("推进方向 开", new ExtendTriggerSettings { UseAdvanceDir = true }.Caption);
    }

    // ── 持久化 ──────────────────────────────────────────────
    [Fact]
    public void 持久化_存读往返()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_ext_" + Guid.NewGuid().ToString("N"));
        using var st = new PitMine3D.Kylin.UserSettings(dir);

        var s = new ExtendTriggerSettings
        {
            TriggerMode = ExtendTriggerSettings.Mode.TimeStep, TimeStepPeriods = 4,
            AutoReclaimTemporary = true, MatchToleranceM = 12, ShiftThresholdM = 5,
            UseAdvanceDir = true, AdvanceAzimuthDeg = 135,
        };
        Assert.True(s.TrySave(st, out string? err), err);

        var back = ExtendTriggerSettings.Load(st);
        Assert.Equal(ExtendTriggerSettings.Mode.TimeStep, back.TriggerMode);
        Assert.Equal(4, back.TimeStepPeriods);
        Assert.True(back.AutoReclaimTemporary);
        Assert.Equal(12, back.MatchToleranceM, 9);
        Assert.Equal(5, back.ShiftThresholdM, 9);
        Assert.True(back.UseAdvanceDir);
        Assert.Equal(135, back.AdvanceAzimuthDeg, 9);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void 持久化_没存过时读回默认值()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_ext_" + Guid.NewGuid().ToString("N"));
        using var st = new PitMine3D.Kylin.UserSettings(dir);
        var s = ExtendTriggerSettings.Load(st);
        Assert.Equal(50.0, s.AdvanceStepM, 9);
        Assert.Equal(ExtendTriggerSettings.Mode.AdvanceStep, s.TriggerMode);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void 持久化_键名与原版一致()
        => Assert.Equal("road.extend.trigger", ExtendTriggerSettings.SettingsKey);

    // ── 阈值真的进得了引擎 ──────────────────────────────────
    [Fact]
    public void 联动_收紧匹配容差会改变演化判定结果()
    {
        // 只验"设置真的传到引擎里"：同一对输入（整条平移 6 m），
        // 容差 8 m ⇒ 认成同一条路(移位)；容差 2 m ⇒ 认成两条路(旧的废除 + 新的延拓)。
        var prev = new[] { Line("L1", (0, 0), (100, 0)) };
        var curr = new[] { Line("L1", (0, 6), (100, 6)) };

        var loose = new ExtendTriggerSettings { MatchToleranceM = 8, ShiftThresholdM = 3 };
        var tight = new ExtendTriggerSettings { MatchToleranceM = 2, ShiftThresholdM = 1 };

        string a = Summary(RoadEvolutionAnalyzer.Analyze(prev, curr, loose.ToEvolutionOptions()));
        string b = Summary(RoadEvolutionAnalyzer.Analyze(prev, curr, tight.ToEvolutionOptions()));
        Assert.NotEqual(a, b);
        Assert.Contains("Shift", a);       // 宽容差：同一条路横移了
        Assert.Contains("Abolish", b);     // 窄容差：旧的整条废除
    }

    private static string Summary(RoadEvolutionResult r)
        => string.Join(",", r.Routes.Select(x => x.Class.ToString()).OrderBy(x => x, StringComparer.Ordinal));

    private static EvoLine Line(string id, params (double x, double y)[] pts)
        => new() { Id = id, Centerline = pts.Select(p => new Pt3(p.x, p.y, 0.0)).ToList() };
}
