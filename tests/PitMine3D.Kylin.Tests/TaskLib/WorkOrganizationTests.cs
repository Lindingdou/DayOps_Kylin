// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/WorkOrganizationTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 作业组织策略（BR-P4）与检修档期进时窗的判据。
///
/// <para><b>策略这组要挡的第一件事是"偷偷改总量"</b>：三种策略都只是把同样的日总量
/// 摊在不同的面上，谁也无权改月计划定的日采出。O1 对三种策略各求一次总量，
/// 三个数必须相等——这条一红，说明重分配里有量被吞了或凭空多出来。</para>
///
/// <para><b>第二件是"拿 0 当上限"</b>：备采储量刚加上，多数面还没录（=0）。
/// 若把 0 当成"这个面只能装 0"，集中/展开两种策略会把全矿的活挤到唯一录过备采的那个面上。
/// O4 专判这一条：三个面都没录备采时，策略照常工作。</para>
/// </summary>
public class WorkOrganizationTests
{
    /// <summary>三个采装面：产能 200 / 100 / 50，日目标各 1000（合计 3000）。</summary>
    private static ExploderConfig Plate(WorkOrganization org, double reserveEach = 0)
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-11 周二",
            IdPrefix = "T0811",
            EnforceMassBalance = false,
            Organization = org,
            Shifts =
            {
                new ShiftWindow("早班", 0, 8),
                new ShiftWindow("中班", 8, 16),
                new ShiftWindow("夜班", 16, 24),
            },
        };
        double[] caps = { 200, 100, 50 };
        for (int i = 0; i < caps.Length; i++)
            cfg.Faces.Add(new FaceInput
            {
                Zone = $"面{i + 1}",
                Process = ProcessType.Load,
                Material = "煤",
                DayTargetM3 = 1000,
                AvailableReserveM3 = reserveEach,
                Group = new EquipmentGroup { MainEquipment = $"WK-0{i + 1}", GroupCapacityM3PerH = caps[i] },
            });
        return cfg;
    }

    private static double Total(ExploderConfig cfg)
        => cfg.Faces.Where(f => f.Process == ProcessType.Load).Sum(f => f.DayTargetM3);

    // ── O1：三种策略总量必须一致（策略只重分配，不改总量）────────────────────
    [Fact]
    public void O1_三种策略总量守恒()
    {
        double bal = Run(WorkOrganization.Balanced);
        double conc = Run(WorkOrganization.Concentrated);
        double multi = Run(WorkOrganization.MultiFace);

        Assert.Equal(3000, bal, 0);
        Assert.Equal(bal, conc, 0);
        Assert.Equal(bal, multi, 0);

        static double Run(WorkOrganization org)
        {
            var cfg = Plate(org);
            TaskExploder.Explode(cfg);
            return Total(cfg);
        }
    }

    // ── O2：集中强采把量堆到大产能面上；均衡型必须原样不动（成对判）──────────
    [Fact]
    public void O2_集中强采堆大面而均衡不动()
    {
        var bal = Plate(WorkOrganization.Balanced);
        TaskExploder.Explode(bal);
        Assert.All(bal.Faces, f => Assert.Equal(1000, f.DayTargetM3, 0));   // 关闸：一个字节都不许动

        var conc = Plate(WorkOrganization.Concentrated);
        TaskExploder.Explode(conc);
        var byZone = conc.Faces.ToDictionary(f => f.Zone, f => f.DayTargetM3);

        // 面1 产能最大(200×20h=4000 上限)，3000 全灌得下 → 只开一个面
        Assert.Equal(3000, byZone["面1"], 0);
        Assert.Equal(0, byZone["面2"], 0);
        Assert.Equal(0, byZone["面3"], 0);
    }

    // ── O3：多面展开让每个面都在干，且按产能排序单调 ──────────────────────
    [Fact]
    public void O3_多面展开人人有活()
    {
        var cfg = Plate(WorkOrganization.MultiFace);
        TaskExploder.Explode(cfg);
        var t = cfg.Faces.ToDictionary(f => f.Zone, f => f.DayTargetM3);

        Assert.All(t.Values, v => Assert.True(v > 0, "多面展开不该有面分不到量"));
        Assert.True(t["面1"] > t["面2"], "产能大的面应分到更多");
        Assert.True(t["面2"] > t["面3"], "产能大的面应分到更多");
    }

    // ── O4：备采未录（0）不当上限——否则活会全挤到录过的那个面 ────────────────
    [Fact]
    public void O4_备采未录不当上限()
    {
        var cfg = Plate(WorkOrganization.Concentrated, reserveEach: 0);
        TaskExploder.Explode(cfg);

        Assert.Equal(3000, Total(cfg), 0);
        Assert.True(cfg.Faces.Any(f => f.DayTargetM3 > 0), "备采未录时策略仍应正常分配，不能全被 0 上限卡死");
    }

    // ── O5：备采**录了**就真的是上限，灌不下的余量如实报出、不塞回任何面 ──────
    [Fact]
    public void O5_备采是上限且余量如实报()
    {
        var cfg = Plate(WorkOrganization.Concentrated, reserveEach: 400);   // 三面各 400，合计 1200 < 3000
        var res = TaskExploder.Explode(cfg);

        Assert.All(cfg.Faces, f => Assert.True(f.DayTargetM3 <= 400 + 1, $"{f.Zone} 超了备采上限"));
        Assert.Equal(1200, Total(cfg), 0);

        var warn = res.Violations.Where(v => v.Code == ViolationCodes.WorkOrg
                                          && v.Severity == ViolationSeverity.Warn).ToArray();
        Assert.Single(warn);
        Assert.Contains("灌不下", warn[0].Message);
    }

    // ── O6：策略排在配煤之前 —— 配煤能推翻策略，反过来不行 ────────────────────
    //  判法：给两个面不同灰分并设一个必然超标的配煤上限；集中强采会把量全堆到高灰面，
    //  若顺序反了（配煤先跑），最终综合灰分必然超标且不会有「配煤调整」。
    [Fact]
    public void O6_配煤能推翻策略()
    {
        var cfg = Plate(WorkOrganization.Concentrated);
        cfg.Blend = new BlendStandard { MaxAshPct = 12.0 };
        cfg.EffHoursPerDay = 20;
        // 面1（产能最大，集中强采的首选）是高灰面
        cfg.Faces[0].Quality = new CoalQuality { AshPct = 18, CalorificMJkg = 20, SulfurPct = 0.5, MoisturePct = 8 };
        cfg.Faces[1].Quality = new CoalQuality { AshPct = 8, CalorificMJkg = 24, SulfurPct = 0.4, MoisturePct = 8 };
        cfg.Faces[2].Quality = new CoalQuality { AshPct = 8, CalorificMJkg = 24, SulfurPct = 0.4, MoisturePct = 8 };

        var res = TaskExploder.Explode(cfg);

        // 策略先跑（把量堆到面1）→ 配煤后跑（把量从高灰的面1 移走）：面1 不该独占全部
        double f1 = cfg.Faces[0].DayTargetM3;
        Assert.True(f1 < 3000, $"配煤应当把量从高灰面移走，实际面1 仍是 {f1:0}");
        Assert.Contains(res.Violations, v => v.Code == ViolationCodes.WorkOrg);
        Assert.Contains(res.Violations, v => v.Code == ViolationCodes.BlendAdjusted
                                          || v.Code == ViolationCodes.BlendFailed);
    }

    // ── O7：检修档期真的从有效时窗里扣掉（班首检修 ⇒ 该班作业从检修结束起算）────
    [Fact]
    public void O7_检修档期扣时窗()
    {
        var cfg = Plate(WorkOrganization.Balanced);
        cfg.Faces.RemoveRange(1, 2);                       // 只留一个面，判起来干净
        cfg.Faces[0].DayTargetM3 = 100_000;                // 目标足够大，每班都会顶满时窗

        var before = TaskExploder.Explode(cfg).Tasks
            .First(t => t.Process == ProcessType.Load && t.Shift == "早班");

        var cfg2 = Plate(WorkOrganization.Balanced);
        cfg2.Faces.RemoveRange(1, 2);
        cfg2.Faces[0].DayTargetM3 = 100_000;
        cfg2.Maintenance.Add(new MaintenanceWindow { EquipId = "WK-01", Start = 0, End = 4, Label = "定修" });

        var after = TaskExploder.Explode(cfg2).Tasks
            .First(t => t.Process == ProcessType.Load && t.Shift == "早班");

        Assert.Equal(0, before.StartHour, 2);              // 关闸：没有档期时从 0 点起
        Assert.Equal(4, after.StartHour, 2);               // 开闸：检修到 4 点，作业从 4 点起
        Assert.True(after.TargetVolumeM3 < before.TargetVolumeM3, "扣掉 4 小时后本班应当少装");
    }
}
