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
/// 任务下达：稳定键 · 下达闸门 · 单据流水（§三五五）。
///
/// 三条头号判据（都不会报错，只会让人对着一份看着正常的单据算错账）：
///   ① <b>稳定键必须跨进程稳定</b>，且不能受任务 Id 漂移影响 —— 拿 Id 当主键，
///      昨天下达的任务今天就显示成"待下达"，而它<b>不报错</b>。
///   ② <b>缺去向不得下达</b> —— 任务书写不出"这车拉到哪"，现场只能自己找地方倒。
///   ③ <b>撤回留痕不抹痕</b> —— 抹掉下达时刻，"谁在几点下达过"就查不到了。
/// </summary>
public class DispatchTests
{
    private const string Day = "2026-09-11";

    private static ShiftTask T(string zone = "北一采", string equip = "E1", ProcessType p = ProcessType.Load,
                               string shift = "早班", double vol = 3000, string dest = "北排土场",
                               string id = "D0911-E1-早", double haulKm = 2.0)
        => new()
        {
            Id = id, Process = p, Shift = shift, WorkZone = zone, TargetVolumeM3 = vol,
            Group = new EquipmentGroup { MainEquipment = equip, RecommendedTrucks = 3, Trucks = { "T1", "T2", "T3" } },
            DestinationName = dest, DestinationId = dest.Length > 0 ? "D1" : "",
            EquivHaulKm = haulKm, StartHour = 0, EndHour = 8, PlannedHours = 8,
        };

    private static ExploderResult R(params ShiftTask[] ts)
    {
        var r = new ExploderResult();
        r.Tasks.AddRange(ts);
        return r;
    }

    // ── 稳定键 ──────────────────────────────────────────────
    [Fact]
    public void 稳定键_跨进程稳定不是GetHashCode()
    {
        // ★ .NET Core 起 string.GetHashCode 按进程随机加盐 —— 落盘场景下那是致命的
        string a = TaskKey.Compose(Day, "早班", "E1", "Load", "北一采");
        string b = TaskKey.Compose(Day, "早班", "E1", "Load", "北一采");
        Assert.Equal(a, b);
        Assert.StartsWith("TK-2026-09-11-早-", a);
        Assert.Equal(12, a.Split('-').Last().Length);
    }

    [Fact]
    public void 稳定键_不受任务号漂移影响()
    {
        // 重排会把前缀改成 D0911R-…，Id 跟着变；键不该跟着变
        var a = T(id: "D0911-E1-早");
        var b = T(id: "D0911R-E1-早");
        Assert.Equal(TaskKey.Of(a, Day), TaskKey.Of(b, Day));
    }

    [Fact]
    public void 稳定键_五元组任一维不同就不同()
    {
        string baseKey = TaskKey.Compose(Day, "早班", "E1", "Load", "北一采");
        Assert.NotEqual(baseKey, TaskKey.Compose("2026-09-12", "早班", "E1", "Load", "北一采"));
        Assert.NotEqual(baseKey, TaskKey.Compose(Day, "中班", "E1", "Load", "北一采"));
        Assert.NotEqual(baseKey, TaskKey.Compose(Day, "早班", "E2", "Load", "北一采"));
        Assert.NotEqual(baseKey, TaskKey.Compose(Day, "早班", "E1", "Dump", "北一采"));
        Assert.NotEqual(baseKey, TaskKey.Compose(Day, "早班", "E1", "Load", "北二采"));
    }

    [Fact]
    public void 稳定键_设备号大小写混录也对得上()
        => Assert.Equal(TaskKey.Compose(Day, "早班", "e1", "Load", "北一采"),
                        TaskKey.Compose(Day, "早班", " E1 ", "Load", "北一采"));

    [Fact]
    public void 稳定键_运输笔带物料码分得开混采面的两趟()
    {
        // ★ 煤去破碎站、岩去排土场，五样全同 ⇒ 不带物料码就是同一个键：
        //   单据后写盖先写、实绩把煤的量记到岩上、"已下达 N 条"少一条 —— 而每条看着都正常
        var coal = T(p: ProcessType.Haul); coal.Material = "coal";
        var rock = T(p: ProcessType.Haul); rock.Material = "rock";
        Assert.NotEqual(TaskKey.Of(coal, Day), TaskKey.Of(rock, Day));
    }

    [Fact]
    public void 稳定键_非运输笔不带物料码()
    {
        var a = T(); a.Material = "coal";
        var b = T(); b.Material = "rock";
        Assert.Equal(TaskKey.Of(a, Day), TaskKey.Of(b, Day));
    }

    [Theory]
    [InlineData("2026-09-11 周五", "2026-09-11")]
    [InlineData("", "nodate")]
    [InlineData("2026/09/11", "2026-09-11")]
    public void 稳定键_日期标签去掉非法字符(string raw, string want)
        => Assert.Equal(want, TaskKey.DateTag(raw));

    [Fact]
    public void 稳定键_没班次时标全()
        => Assert.Equal("全", TaskKey.ShiftTag(""));

    // ── 下达闸门 ────────────────────────────────────────────
    [Fact]
    public void 闸门_都齐了就放行()
    {
        var chk = DispatchEngine.ValidateForIssue(R(T()));
        Assert.True(chk.CanIssue);
        Assert.Equal(1, chk.TaskCount);
        Assert.Contains("校验通过", chk.Summary);
    }

    [Fact]
    public void 闸门_缺去向不得下达且说清后果()
    {
        var chk = DispatchEngine.ValidateForIssue(R(T(dest: "")));
        Assert.False(chk.CanIssue);
        var b = Assert.Single(chk.Blocks);
        Assert.Equal(DispatchEngine.CodeNoDestination, b.Code);
        Assert.Contains("这车拉到哪", b.Message);
    }

    [Fact]
    public void 闸门_缺主设备不得下达()
    {
        var chk = DispatchEngine.ValidateForIssue(R(T(equip: "")));
        Assert.False(chk.CanIssue);
        Assert.Contains(chk.Blocks, b => b.Code == DispatchEngine.CodeNoEquipment);
    }

    [Fact]
    public void 闸门_爆破笔不判主设备()
    {
        // ★ 按已定口径爆破不指人（爆破队台账根本没有）。不豁免的话，
        //   本班只要有一炮，整盘一条都下达不了，理由还是"未指定主设备"
        var chk = DispatchEngine.ValidateForIssue(R(T(p: ProcessType.Blast, equip: "", dest: "", vol: 0)));
        Assert.True(chk.CanIssue);
    }

    [Fact]
    public void 闸门_没量的采装笔不判去向()
    {
        var chk = DispatchEngine.ValidateForIssue(R(T(dest: "", vol: 0)));
        Assert.True(chk.CanIssue);
    }

    [Fact]
    public void 闸门_Error级校核阻止Warn级只提醒()
    {
        var r = R(T());
        r.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Error, Code = "设备双占", TaskId = "D0911-E1-早", Message = "撞了" });
        r.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Warn, Code = "运力不足", TaskId = "D0911-E1-早", Message = "车少" });
        var chk = DispatchEngine.ValidateForIssue(r);
        Assert.False(chk.CanIssue);
        Assert.Single(chk.Blocks);
        Assert.Single(chk.Warnings);
    }

    [Fact]
    public void 闸门_别的班的校核不拦本班()
    {
        var r = R(T(shift: "早班", id: "A"), T(shift: "中班", id: "B", equip: "E2"));
        r.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Error, Code = "设备双占", TaskId = "B", Message = "中班的事" });
        Assert.True(DispatchEngine.ValidateForIssue(r, "早班").CanIssue);
        Assert.False(DispatchEngine.ValidateForIssue(r, "中班").CanIssue);
    }

    [Fact]
    public void 闸门_按任务号缩小范围()
    {
        var r = R(T(id: "A"), T(id: "B", equip: "", dest: ""));
        Assert.True(DispatchEngine.ValidateForIssue(r, null, new[] { "A" }).CanIssue);
        Assert.False(DispatchEngine.ValidateForIssue(r, null, new[] { "B" }).CanIssue);
    }

    [Fact]
    public void 闸门_空闲笔不参与()
    {
        var chk = DispatchEngine.ValidateForIssue(R(T(p: ProcessType.Idle, equip: "", dest: "")));
        Assert.False(chk.CanIssue);                     // 一条可下达的都没有
        Assert.Contains("无可下达任务", chk.Blocks[0].Message);
    }

    [Fact]
    public void 闸门_没配车与没运距只提醒不拦()
    {
        var t = T(haulKm: 0);
        t.Group.Trucks.Clear();
        var chk = DispatchEngine.ValidateForIssue(R(t));
        Assert.True(chk.CanIssue);
        Assert.Equal(2, chk.Warnings.Count);
        Assert.Contains(chk.Warnings, w => w.Code == DispatchEngine.CodeTruckShortage);
        Assert.Contains(chk.Warnings, w => w.Code == DispatchEngine.CodeHaulMissing);
    }

    [Fact]
    public void 闸门_没有计划时说清楚是没计划()
    {
        var chk = DispatchEngine.ValidateForIssue(null);
        Assert.False(chk.CanIssue);
        Assert.Equal(DispatchEngine.CodeNoPlan, chk.Blocks[0].Code);
    }

    // ── 实例与撤回 ──────────────────────────────────────────
    [Fact]
    public void 实例_下达之后是已下达()
    {
        var inst = TaskInstance.From(T(), Day);
        inst.IssuedBy = "老王"; inst.IssuedAt = new DateTime(2026, 9, 11, 7, 30, 0);
        Assert.True(inst.IsIssued);
        Assert.True(inst.WasIssued);
        Assert.Contains("已下达 v1", inst.IssueCaption);
    }

    [Fact]
    public void 实例_撤回留痕不抹痕()
    {
        // ★ 抹掉 IssuedBy/IssuedAt，"谁在几点下达过这条任务"就此消失
        var inst = TaskInstance.From(T(), Day);
        inst.IssuedBy = "老王"; inst.IssuedAt = new DateTime(2026, 9, 11, 7, 30, 0);
        inst.AckedBy = "三班"; inst.AckedAt = DateTime.Now;
        inst.Withdraw("老李", new DateTime(2026, 9, 11, 9, 0, 0));

        Assert.False(inst.IsIssued);
        Assert.True(inst.WasIssued);                    // 追溯问的是这个
        Assert.Equal("老王", inst.IssuedBy);
        Assert.NotNull(inst.IssuedAt);
        Assert.Equal("", inst.AckedBy);                 // 撤回 ⇒ 班组确认作废
        Assert.Null(inst.AckedAt);
        Assert.Contains("撤回", inst.IssueCaption);
    }

    [Fact]
    public void 实例_快照带走下达那一刻的量与去向()
    {
        var t = T(vol: 3000, dest: "北排土场");
        var inst = TaskInstance.From(t, Day);
        t.TargetVolumeM3 = 9999;                        // 计划后来改了
        Assert.Equal(3000, inst.Snapshot!.TargetVolumeM3, 6);
        Assert.Equal("北排土场", inst.Snapshot.DestinationName);
    }

    // ── 落盘 ────────────────────────────────────────────────
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pm_disp_" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); DispatchStore.DirOverride = Path; }
        public void Dispose()
        {
            DispatchStore.DirOverride = null;
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    [Fact]
    public void 落盘_实例按稳定键往返()
    {
        using var _ = new TempDir();
        var inst = TaskInstance.From(T(), Day);
        inst.IssuedBy = "老王"; inst.IssuedAt = DateTime.Now;
        Assert.Equal("", DispatchStore.SaveInstances(new[] { inst }));

        var back = DispatchStore.LoadInstances();
        Assert.Single(back);
        Assert.True(back[inst.StableKey].IsIssued);
        Assert.Equal("老王", back[inst.StableKey].IssuedBy);
    }

    [Fact]
    public void 落盘_同一稳定键只留一条()
    {
        using var _ = new TempDir();
        var a = TaskInstance.From(T(), Day);
        var b = TaskInstance.From(T(id: "D0911R-E1-早"), Day);   // Id 变了，键没变
        DispatchStore.SaveInstances(new[] { a, b });
        Assert.Single(DispatchStore.LoadInstances());
    }

    [Fact]
    public void 落盘_回执只追加不覆盖()
    {
        // ★ 覆盖等于把之前的流水抹了 —— 单据流水只增不改
        using var _ = new TempDir();
        var inst = TaskInstance.From(T(), Day);
        DispatchStore.AppendReceipts(new[] { DispatchReceipt.For(inst, ReceiptKind.Issue, "老王", "下达") });
        DispatchStore.AppendReceipts(new[] { DispatchReceipt.For(inst, ReceiptKind.Withdraw, "老李", "撤回") });

        var all = DispatchStore.ReceiptsOf(inst.StableKey);
        Assert.Equal(2, all.Count);
        Assert.Equal(ReceiptKind.Issue, all[0].Kind);
        Assert.Equal(ReceiptKind.Withdraw, all[1].Kind);
    }

    [Fact]
    public void 落盘_没有文件时读出空表且不报错()
    {
        using var _ = new TempDir();
        Assert.Empty(DispatchStore.LoadInstances());
        Assert.Equal("", DispatchStore.LastError);
        Assert.Empty(DispatchStore.LoadReceipts());
        Assert.Equal("", DispatchStore.LastError);
    }

    [Fact]
    public void 落盘_文件坏了要给原因不能静默当成没有单据()
    {
        // ★ 静默当成"没有单据"，人会以为今天一条都没下达，然后重下一遍
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "task_instances.json"), "{ 这不是 JSON");
        Assert.Empty(DispatchStore.LoadInstances());
        Assert.Contains("读不出", DispatchStore.LastError);
    }

    [Fact]
    public void 落盘_回执读坏时拒绝写以免顶掉那一段()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "dispatch_receipts.json"), "坏文件");
        var inst = TaskInstance.From(T(), Day);
        string err = DispatchStore.AppendReceipts(new[] { DispatchReceipt.For(inst, ReceiptKind.Issue, "老王", "下达") });
        Assert.Contains("本次不写", err);
    }

    [Fact]
    public void 落盘_空回执列表不动文件()
    {
        using var _ = new TempDir();
        Assert.Equal("", DispatchStore.AppendReceipts(Array.Empty<DispatchReceipt>()));
        Assert.Empty(DispatchStore.LoadReceipts());
    }

    [Fact]
    public void 回执_文案带得出谁在几点做了什么()
    {
        var inst = TaskInstance.From(T(), Day);
        var r = DispatchReceipt.For(inst, ReceiptKind.Issue, "老王", "北一采 采装");
        r.At = new DateTime(2026, 9, 11, 7, 30, 0);
        Assert.Contains("09-11 07:30", r.Caption);
        Assert.Contains("下达", r.Caption);
        Assert.Contains("老王", r.Caption);
    }
}
