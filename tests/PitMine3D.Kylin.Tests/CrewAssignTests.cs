using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 班组派工（§三五七）。原实现里人员一律显示"（待派）"、保存是个空方法 ——
/// 等于这个窗口<b>从来没有派过工</b>。
///
/// 三条头号判据（都不会报错，只会让人以为派工是对的）：
///   ① <b>姓名不是主键</b>：花名册里两个"张建国"时，随手 <c>First()</c> 会静默挑一个去校持证 ——
///      挑错了界面上一点异常也看不出来。同名的一律不认，当场点名要求用工号区分。
///   ② <b>持证校核是真校核</b>：不在花名册 / 证件类别对不上 / 工种不对 / 今天休班，都要直说，
///      而不是一律打勾。
///   ③ <b>爆破按口径不指人</b>，自动派工也不许给它塞一个；<b>人手不足要点名</b>，不静默留空。
/// </summary>
public class CrewAssignTests
{
    private const string Day = "2026-09-11";

    private static CrewMember M(string name, string job = CrewAssignModel.JobOperator,
                                string cert = "电铲", bool duty = true, string id = "")
        => new() { PersonId = id.Length > 0 ? id : "P" + name, Name = name, Job = job, CertFor = cert, OnDuty = duty };

    private static ShiftTask T(string equip = "E1", ProcessType p = ProcessType.Load,
                               string shift = "早班", params string[] trucks)
    {
        var t = new ShiftTask
        {
            Id = "D-" + equip, Process = p, Shift = shift, WorkZone = "北一采", TargetVolumeM3 = 3000,
            Group = new EquipmentGroup { MainEquipment = equip },
        };
        foreach (var tr in trucks) t.Group.Trucks.Add(tr);
        return t;
    }

    private static List<CrewRow> Rows(params ShiftTask[] ts)
        => CrewAssignModel.BuildRows(ts, "早班",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["E1"] = "电铲", ["E2"] = "钻机" });

    // ── 姓名不是主键 ────────────────────────────────────────
    [Fact]
    public void 重名_一律不认并点名要求用工号区分()
    {
        // ★ 随手挑一个去校证，挑错了界面上一点异常也看不出来
        var roster = new List<CrewMember> { M("张建国", id: "P1"), M("张建国", cert: "钻机", id: "P2") };
        var rows = Rows(T());
        rows[0].Operator = "张建国";
        CrewAssignModel.Recheck(rows, roster);
        Assert.Contains("有 2 个同名", rows[0].Cert);
        Assert.Contains("工号", rows[0].Cert);
        Assert.Equal("", rows[0].OperatorId);          // 认不出来就不填工号，不猜
    }

    [Fact]
    public void 重名_不重名时把工号存下来给下游对号()
    {
        var rows = Rows(T());
        rows[0].Operator = "李四";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("李四", id: "P9") });
        Assert.Equal("P9", rows[0].OperatorId);
        Assert.Equal("✓ 持证齐全", rows[0].Cert);
    }

    // ── 持证校核是真校核 ────────────────────────────────────
    [Fact]
    public void 校核_不在花名册要直说()
    {
        var rows = Rows(T());
        rows[0].Operator = "查无此人";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("李四") });
        Assert.Contains("不在花名册", rows[0].Cert);
    }

    [Fact]
    public void 校核_证件类别对不上要直说()
    {
        var rows = Rows(T());                           // E1 是电铲
        rows[0].Operator = "钻工";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("钻工", cert: "钻机") });
        Assert.Contains("持钻机证 ≠ 电铲", rows[0].Cert);
    }

    [Fact]
    public void 校核_无证的写成持无证()
    {
        var rows = Rows(T());
        rows[0].Operator = "新人";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("新人", cert: "") });
        Assert.Contains("持无证", rows[0].Cert);
    }

    [Fact]
    public void 校核_设备类别未知时不因此放行()
    {
        // 类别读不到 ⇒ 判"对不上"并说明是类别未知，不当成通过
        var rows = CrewAssignModel.BuildRows(new[] { T() }, "早班", null);
        rows[0].Operator = "李四";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("李四") });
        Assert.Contains("该设备类别未知", rows[0].Cert);
    }

    [Fact]
    public void 校核_工种不对要直说()
    {
        var rows = Rows(T());
        rows[0].Operator = "老司机";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("老司机", job: CrewAssignModel.JobDriver, cert: "矿卡") });
        Assert.Contains("工种为司机", rows[0].Cert);
    }

    [Fact]
    public void 校核_休班的人单列在出勤那一列()
    {
        var rows = Rows(T());
        rows[0].Operator = "李四";
        CrewAssignModel.Recheck(rows, new List<CrewMember> { M("李四", duty: false) });
        Assert.Contains("休班：李四", rows[0].Attend);
        Assert.Equal("✓ 持证齐全", rows[0].Cert);       // 证是齐的，人不在 —— 两件事分开报
    }

    [Fact]
    public void 校核_没派人时写待派不写通过()
    {
        var rows = Rows(T());
        var chk = CrewAssignModel.Recheck(rows, new List<CrewMember>());
        Assert.Contains("操作手待派", rows[0].Cert);
        Assert.Equal(1, chk.IssueCount);
        Assert.Equal(0, chk.OkCount);
    }

    [Fact]
    public void 校核_司机缺几个就报几个()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班", "T1", "T2", "T3"));
        rows[0].Operator = "李四";
        rows[0].Drivers = "王五";
        CrewAssignModel.Recheck(rows, new List<CrewMember>
        { M("李四"), M("王五", job: CrewAssignModel.JobDriver, cert: "矿卡") });
        Assert.Contains("缺 2 名司机", rows[0].Cert);
    }

    [Theory]
    [InlineData("电铲", "电铲", true)]
    [InlineData("液压铲", "电铲", true)]      // 同族称谓互认
    [InlineData("钻机", "电铲", false)]
    [InlineData("", "电铲", false)]
    [InlineData("电铲", "", false)]
    public void 校核_持证与类别相容判定(string cert, string cat, bool want)
        => Assert.Equal(want, CrewAssignModel.SameCert(cert, cat));

    // ── 爆破按口径不指人 ────────────────────────────────────
    [Fact]
    public void 爆破_单出一行说明不派不校不算待处理()
    {
        var rows = Rows(T("E1"), T("", ProcessType.Blast));
        var blast = rows.Single(r => r.Kind == CrewRowKind.Blast);
        Assert.False(blast.NeedsCrew);
        Assert.Contains("不指人", blast.Cert);

        CrewAssignModel.AutoAssign(rows, new List<CrewMember> { M("李四") });
        Assert.Equal("", blast.Operator);              // ★ 自动派工也不许给它塞一个
    }

    [Fact]
    public void 爆破_不落盘()
        => Assert.Null(CrewAssignModel.ToAssignment(
            new CrewRow { Kind = CrewRowKind.Blast, Main = "爆破" }, Day, "早班", "老王"));

    // ── 自动派工 ────────────────────────────────────────────
    [Fact]
    public void 自动派工_优先按设备类别对持证类别()
    {
        var rows = Rows(T("E1"), T("E2"));               // E1 电铲 / E2 钻机
        CrewAssignModel.AutoAssign(rows, new List<CrewMember>
        { M("钻工", cert: "钻机"), M("铲工", cert: "电铲") });
        Assert.Equal("铲工", rows.Single(r => r.Main == "E1").Operator);
        Assert.Equal("钻工", rows.Single(r => r.Main == "E2").Operator);
    }

    [Fact]
    public void 自动派工_对不上证时退到任意在岗同工种()
    {
        var rows = Rows(T("E1"));
        CrewAssignModel.AutoAssign(rows, new List<CrewMember> { M("钻工", cert: "钻机") });
        Assert.Equal("钻工", rows[0].Operator);
        Assert.Contains("持钻机证 ≠ 电铲", rows[0].Cert);   // 派了，但照样报证不对
    }

    [Fact]
    public void 自动派工_避开休班的人()
    {
        var rows = Rows(T("E1"));
        var res = CrewAssignModel.AutoAssign(rows, new List<CrewMember> { M("李四", duty: false) });
        Assert.Equal("", rows[0].Operator);
        Assert.Contains("无可派操作手", string.Join("|", res.Shortfall));
    }

    [Fact]
    public void 自动派工_同一个人不派两台设备()
    {
        var rows = Rows(T("E1"), T("E2"));
        CrewAssignModel.AutoAssign(rows, new List<CrewMember> { M("独苗") });
        var got = rows.Where(r => r.NeedsCrew).Select(r => r.Operator).Where(CrewAssignModel.IsName).ToList();
        Assert.Single(got);
    }

    [Fact]
    public void 自动派工_手工填的名字不被顶掉也不重复派()
    {
        var rows = Rows(T("E1"), T("E2"));
        rows[0].Operator = "老张";
        CrewAssignModel.AutoAssign(rows, new List<CrewMember> { M("老张"), M("小李") });
        Assert.Equal("老张", rows[0].Operator);
        Assert.Equal("小李", rows[1].Operator);
    }

    [Fact]
    public void 自动派工_司机按配车数补齐并优先矿卡证()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班", "T1", "T2"));
        CrewAssignModel.AutoAssign(rows, new List<CrewMember>
        {
            M("辅工", job: CrewAssignModel.JobDriver, cert: "推土机"),
            M("卡司一", job: CrewAssignModel.JobDriver, cert: "矿卡"),
            M("卡司二", job: CrewAssignModel.JobDriver, cert: "矿卡"),
            M("铲工"),
        });
        var names = CrewAssignModel.SplitNames(rows[0].Drivers).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("卡司一", names);
        Assert.Contains("卡司二", names);
    }

    [Fact]
    public void 自动派工_人不够时点名并留占位不静默留空()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班", "T1", "T2"));
        var res = CrewAssignModel.AutoAssign(rows, new List<CrewMember> { M("铲工") });
        Assert.Contains("缺 2 名司机", string.Join("|", res.Shortfall));
        Assert.Contains(CrewAssignModel.Unassigned, rows[0].Drivers);
    }

    [Fact]
    public void 自动派工_花名册为空时一个都派不出且点名()
    {
        var rows = Rows(T("E1"));
        var res = CrewAssignModel.AutoAssign(rows, new List<CrewMember>());
        Assert.Equal(0, res.Filled);
        Assert.NotEmpty(res.Shortfall);
    }

    // ── 车号 ↔ 司机显式配对 ─────────────────────────────────
    [Fact]
    public void 配对_车号与司机按位显式配不是靠下标猜()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班", "T1", "T2"));
        rows[0].Operator = "铲工";
        rows[0].Drivers = "甲、乙";
        CrewAssignModel.Recheck(rows, new List<CrewMember>
        {
            M("铲工"),
            M("甲", job: CrewAssignModel.JobDriver, cert: "矿卡", id: "D1"),
            M("乙", job: CrewAssignModel.JobDriver, cert: "矿卡", id: "D2"),
        });
        var a = CrewAssignModel.ToAssignment(rows[0], Day, "早班", "老王")!;
        Assert.Equal(2, a.TruckDrivers.Count);
        Assert.Equal("T1", a.TruckDrivers[0].TruckId);
        Assert.Equal("甲", a.TruckDrivers[0].Name);
        Assert.Equal("D1", a.TruckDrivers[0].PersonId);
        Assert.Equal("T2", a.TruckDrivers[1].TruckId);
    }

    [Fact]
    public void 配对_占位的待派不进配对()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班", "T1", "T2"));
        rows[0].Drivers = $"甲、{CrewAssignModel.Unassigned}";
        rows[0].DriverIds = new List<string> { "D1", "" };
        var a = CrewAssignModel.ToAssignment(rows[0], Day, "早班", "老王")!;
        Assert.Single(a.TruckDrivers);
    }

    // ── 回填与下游 ──────────────────────────────────────────
    [Fact]
    public void 回填_重开窗口后接着改()
    {
        var rows = Rows(T("E1"));
        var saved = new Dictionary<string, CrewAssignment>(StringComparer.OrdinalIgnoreCase)
        {
            [CrewAssignment.KeyOf(Day, "早班", "E1")] = new()
            { PlanDate = Day, Shift = "早班", MainEquipment = "E1", Operator = "老张", OperatorId = "P1" },
        };
        CrewAssignModel.Apply(rows, saved, Day, "早班");
        Assert.Equal("老张", rows[0].Operator);
    }

    [Fact]
    public void 下游_任务书那一列没派工时是破折号不编名字()
    {
        var empty = new Dictionary<string, CrewAssignment>();
        Assert.Equal("—", CrewAssignModel.CrewTextOf(empty, Day, "早班", "E1"));
        Assert.Equal("—", CrewAssignModel.CrewTextOf(null, Day, "早班", "E1"));
    }

    [Fact]
    public void 下游_派了工就给操作手与司机()
    {
        var saved = new Dictionary<string, CrewAssignment>(StringComparer.OrdinalIgnoreCase)
        {
            [CrewAssignment.KeyOf(Day, "早班", "E1")] = new()
            {
                PlanDate = Day, Shift = "早班", MainEquipment = "E1", Operator = "老张",
                TruckDrivers = { new TruckDriver { TruckId = "T1", Name = "甲" } },
            },
        };
        Assert.Equal("老张 / 甲", CrewAssignModel.CrewTextOf(saved, Day, "早班", "E1"));
    }

    // ── 落盘 ────────────────────────────────────────────────
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pm_crew_" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); CrewStore.DirOverride = Path; }
        public void Dispose()
        {
            CrewStore.DirOverride = null;
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    [Fact]
    public void 落盘_花名册往返()
    {
        using var _ = new TempDir();
        Assert.Equal("", CrewStore.SaveRoster(new[] { M("李四", id: "P9", duty: false) }));
        var back = CrewStore.LoadRoster().Single();
        Assert.Equal("P9", back.PersonId);
        Assert.False(back.OnDuty);
    }

    [Fact]
    public void 落盘_没有花名册文件时是空表不是样例()
    {
        // ★ 不编样例人名：编出来的名字会被当成真人派工、写进单据、发到班组
        using var _ = new TempDir();
        Assert.Empty(CrewStore.LoadRoster());
        Assert.Equal("", CrewStore.LastError);
    }

    [Fact]
    public void 落盘_派工按键覆盖别的班次不受影响()
    {
        using var _ = new TempDir();
        CrewStore.SaveAssignments(new[]
        {
            new CrewAssignment { PlanDate = Day, Shift = "早班", MainEquipment = "E1", Operator = "甲" },
            new CrewAssignment { PlanDate = Day, Shift = "中班", MainEquipment = "E1", Operator = "乙" },
        });
        CrewStore.SaveAssignments(new[]
        { new CrewAssignment { PlanDate = Day, Shift = "早班", MainEquipment = "E1", Operator = "丙" } });

        var all = CrewStore.LoadAssignments();
        Assert.Equal(2, all.Count);
        Assert.Equal("丙", all[CrewAssignment.KeyOf(Day, "早班", "E1")].Operator);
        Assert.Equal("乙", all[CrewAssignment.KeyOf(Day, "中班", "E1")].Operator);
    }

    [Fact]
    public void 落盘_文件坏了要给原因且拒绝写以免顶掉()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "crew_assignments.json"), "坏文件");
        Assert.Empty(CrewStore.LoadAssignments());
        Assert.Contains("读不出", CrewStore.LastError);

        string err = CrewStore.SaveAssignments(new[]
        { new CrewAssignment { PlanDate = Day, Shift = "早班", MainEquipment = "E1" } });
        Assert.Contains("本次不写", err);
    }

    // ── 建行 ────────────────────────────────────────────────
    [Fact]
    public void 建行_一台主设备一行且带上配车()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班", "T1", "T2"),
                        T("E1", ProcessType.Dump, "早班", "T2", "T3"));
        var r = Assert.Single(rows);
        Assert.Equal(3, r.TruckCount);                 // 车号去重
        Assert.Contains("配 3 车", r.Group);
    }

    [Fact]
    public void 建行_只出本班的没有主设备的不出行()
    {
        var rows = Rows(T("E1", ProcessType.Load, "早班"), T("E2", ProcessType.Load, "中班"), T(""));
        Assert.Single(rows);
        Assert.Equal("E1", rows[0].Main);
    }
}
