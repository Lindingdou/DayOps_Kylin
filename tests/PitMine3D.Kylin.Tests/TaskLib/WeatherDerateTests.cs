// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/WeatherDerateTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 天气降效 + 编制锚点的判据。
///
/// <para><b>这组判据要挡的是"加了字段但没接上"这一类**空过**</b>：
/// <c>ExploderConfig.WeatherDeratePct</c> 加进去很容易，装箱那一步忘了乘就是白加，
/// 而界面照样显示得有模有样。所以每一条都成对写：**开闸要变、关闸必须真的变回去**
/// （见 [[incline-shape-guards]] 的 F0：关掉规则闸必须真的红）。</para>
///
/// <para><b>口径</b>：降效落在**能力**上不落在时窗上。班产降了、每班装的量就少，
/// 当日装不完的部分照常报「当日欠产」——这正是设计文档要的"全盘降效回摊"。
/// 若哪天有人改成扣时窗，W3 会红：时窗没动，任务的起止时刻就不该变。</para>
/// </summary>
public class WeatherDerateTests
{
    /// <summary>一个最小可装箱的盘子：三班 × 一个采装面，能力 100 m³/h，日目标 2400 m³（= 恰好装满一天）。</summary>
    private static ExploderConfig Plate(double deratePct)
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-10 周一",
            IdPrefix = "T0810",
            EnforceMassBalance = false,   // 本组只判降效，不牵采排守恒
            WeatherDeratePct = deratePct,
            Shifts =
            {
                new ShiftWindow("早班", 0, 8),
                new ShiftWindow("中班", 8, 16),
                new ShiftWindow("夜班", 16, 24),
            },
        };
        cfg.Faces.Add(new FaceInput
        {
            Zone = "主采面·东",
            Process = ProcessType.Load,
            Material = "煤",
            DayTargetM3 = 2400,
            Group = new EquipmentGroup { MainEquipment = "WK-01", GroupCapacityM3PerH = 100 },
        });
        return cfg;
    }

    private static double Planned(ExploderResult r)
        => r.Tasks.Where(t => t.Process == ProcessType.Load).Sum(t => t.TargetVolumeM3);

    // ── W1：降效真的降产（开闸）+ 不降效时一分不少（关闸）────────────────────
    [Fact]
    public void W1_降效降产而不降效原样()
    {
        double full = Planned(TaskExploder.Explode(Plate(0)));
        double derated = Planned(TaskExploder.Explode(Plate(30)));

        // 关闸：不降效时当日目标应当排得下（2400 = 100 × 24h，扣掉交接班损失后略欠，故用 ≥95%）
        Assert.True(full >= 2400 * 0.95, $"不降效时本该基本排满，实际只排了 {full:0}");

        // 开闸：降 30% 必须真的少排，且少的量与系数同量级（不是少了个零头）
        Assert.True(derated < full * 0.85, $"降效 30% 后计划量 {derated:0} 相对 {full:0} 没有明显下降 —— 降效多半没接进装箱");
    }

    // ── W2：降效导致的缺口必须报「当日欠产」，不许闷声少排 ────────────────────
    [Fact]
    public void W2_降效缺口报当日欠产()
    {
        var r = TaskExploder.Explode(Plate(30));

        Assert.Contains(r.Violations, v => v.Code == ViolationCodes.WeatherDerate);
        Assert.Contains(r.Violations, v => v.Code == ViolationCodes.DayShortfall);

        // 反例组：不降效就不该有这条降效校核（否则它是恒真的，等于没判）
        var clean = TaskExploder.Explode(Plate(0));
        Assert.DoesNotContain(clean.Violations, v => v.Code == ViolationCodes.WeatherDerate);
    }

    // ── W3：降的是能力不是时窗 —— 任务起止时刻不受降效影响 ────────────────────
    [Fact]
    public void W3_时窗不动只有量在动()
    {
        var full = TaskExploder.Explode(Plate(0));
        var derated = TaskExploder.Explode(Plate(30));

        var a = full.Tasks.First(t => t.Process == ProcessType.Load && t.Shift == "早班");
        var b = derated.Tasks.First(t => t.Process == ProcessType.Load && t.Shift == "早班");

        Assert.Equal(a.StartHour, b.StartHour);                 // 起点是时窗给的，与降效无关
        Assert.True(b.TargetVolumeM3 < a.TargetVolumeM3, "同一班降效后应当少装");
    }

    // ── W4：系数边界 —— 上限截到 95%，负值当 0，全停产不靠"降效 100%"表达 ──────
    [Theory]
    [InlineData(0, 1.00)]
    [InlineData(25, 0.75)]
    [InlineData(95, 0.05)]
    [InlineData(120, 0.05)]   // 截断：再大也是 95%
    [InlineData(-10, 1.00)]   // 负降效没有意义，按不降效
    public void W4_能力系数的边界(double pct, double factor)
        => Assert.Equal(factor, new ExploderConfig { WeatherDeratePct = pct }.WeatherFactor, 3);

    // ── W5：空锚点绝不覆盖引擎缺省；有锚点必须真盖上去（成对判，否则这条会空过）──
    [Fact]
    public void W5_锚点空则不动_非空则生效()
    {
        try
        {
            // ① 空锚点：一个字段都不许动，也不许占一段来源文案
            var cfg = new ExploderConfig { Blend = new BlendStandard() };
            double defaultAsh = cfg.Blend.MaxAshPct;
            CompileOverrides.SetForTest(new CompileAnchors());

            Assert.Equal("", CompileOverrides.ApplyTo(cfg));
            Assert.Equal(defaultAsh, cfg.Blend.MaxAshPct, 3);
            Assert.Equal(0, cfg.WeatherDeratePct, 3);

            // ② 有锚点：盖上去、并说出来（否则 ① 那条恒真，等于没判）
            var cfg2 = new ExploderConfig { Blend = new BlendStandard() };
            CompileOverrides.SetForTest(new CompileAnchors { MaxAshPct = 9.9, WeatherDeratePct = 20 });

            string label = CompileOverrides.ApplyTo(cfg2);

            Assert.Equal(9.9, cfg2.Blend.MaxAshPct, 3);
            Assert.Equal(20, cfg2.WeatherDeratePct, 3);
            Assert.Equal(0.8, cfg2.WeatherFactor, 3);
            Assert.False(string.IsNullOrWhiteSpace(label), "锚点生效了就必须写进来源文案");

            // ③ 没锚的那两项保持引擎缺省 —— 半份锚点不许把没填的顶成 0
            Assert.Equal(new BlendStandard().MinCalorificMJkg, cfg2.Blend.MinCalorificMJkg, 3);
            Assert.Equal(new BlendStandard().MaxSulfurPct, cfg2.Blend.MaxSulfurPct, 3);
        }
        finally { CompileOverrides.SetForTest(null); }
    }

    // ── W6：班次码 ↔ 中文名往返；认不出的班制原样保留，不猜 ────────────────────
    [Theory]
    [InlineData("A", "早班")]
    [InlineData("B", "中班")]
    [InlineData("C", "夜班")]
    [InlineData("四班", "四班")]
    public void W6_班次码往返(string code, string name)
    {
        Assert.Equal(name, WorkCalendar.ShiftName(code));
        Assert.Equal(code, WorkCalendar.ShiftCode(name));
    }

    // ── W7：台账未接通时，作业日必须如实说"不知道"，不许现编一个数 ──────────────
    [Fact]
    public void W7_日历不可用时不编数()
    {
        var info = WorkCalendar.MonthWorkdays(new System.DateTime(2026, 8, 10));

        // 单测进程里没有数据库连接：要么如实返回 FromLedger=false，
        // 要么真读到了（开发机上挂着库）——两种都合法，不合法的是"没读到却给个数"
        if (!info.FromLedger)
        {
            Assert.Equal(0, info.Workdays);
            Assert.False(string.IsNullOrWhiteSpace(info.Label), "读不到也必须给出原因文案");
        }
        else
        {
            Assert.True(info.Workdays > 0);
        }
        Assert.Equal("2026-08", info.MonthLabel);
    }
}
