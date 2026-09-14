using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 生产任务书的口径（§三五六）。
///
/// 三条头号判据（都不会报错，只会让人对着一份看着正常的单据算错账）：
///   ① <b>三本方量账不合并</b> —— 穿孔记控制方量、采装记原位实方、排土记排弃占容。
///      原版这里曾写成「Σ(采装 + 排土) 的 TargetVolumeM3」，而排弃占容方也写在那个字段里，
///      于是抬头那个"万m³实方"<b>不对应任何真实量</b>。
///   ② <b>缺卸点不得下达</b>，且要单列点名 —— 单据的命门就是「从哪采 → 拉到哪」。
///   ③ <b>本层只出单据不签发</b>：下达状态从单据流水回显。两处各判一次，
///      "任务书上已签发"与"任务下达里待下达"就能同时为真。
/// </summary>
public class TaskOrderModelTests
{
    private const string Day = "2026-09-11";

    private static ShiftTask T(ProcessType p = ProcessType.Load, string zone = "北一采", string equip = "E1",
                               double vol = 3000, string dest = "北排土场", string mat = "rock",
                               string shift = "早班", double start = 0, double end = 8, double km = 2.5,
                               string id = "D-1")
        => new()
        {
            Id = id, Process = p, Shift = shift, WorkZone = zone, TargetVolumeM3 = vol, Material = mat,
            BenchElevationM = 1195,
            Group = new EquipmentGroup { MainEquipment = equip, RecommendedTrucks = 3, Trucks = { "T1", "T2", "T3" } },
            DestinationName = dest, DestinationId = dest.Length > 0 ? "D1" : "",
            EquivHaulKm = km, StartHour = start, EndHour = end, PlannedHours = end - start,
        };

    private static OrderDoc Doc(params ShiftTask[] ts)
        => TaskOrderModel.Build(ts, Day, "早班", 7.5);

    // ── 三本账不合并 ────────────────────────────────────────
    [Fact]
    public void 分账_采装与排土不加在一起()
    {
        // ★ 加起来的那个数不对应任何真实量
        var d = Doc(T(ProcessType.Load, vol: 4000), T(ProcessType.Dump, zone: "北排", vol: 6000, equip: "E2", id: "D-2"));
        Assert.Contains("采装 1 项 · 0.40 万m³实方", d.MetricsText);
        Assert.Contains("排土 1 项 · 排弃占容 0.60 万m³", d.MetricsText);
        Assert.DoesNotContain("1.00 万m³实方", d.MetricsText);
    }

    [Fact]
    public void 分账_采装给实方与吨双口径()
    {
        var d = Doc(T(ProcessType.Load, vol: 1000, mat: "rock"));
        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText("rock"));
        Assert.Contains("m³", d.Rows[0].Plan);
        Assert.Contains($"{1000 * spec.InSituDensityTPerM3:N0} t", d.Rows[0].Plan);
        Assert.Equal("原位实方", d.Rows[0].Basis);
    }

    [Fact]
    public void 分账_排土那一笔口径名是排弃占容()
        => Assert.Equal("排弃占容", Doc(T(ProcessType.Dump)).Rows[0].Basis);

    [Fact]
    public void 分账_穿孔那一笔记的是孔数与延米不是方量()
    {
        var t = T(ProcessType.Drill, vol: 0, dest: "");
        t.DrillHoles = 120; t.DrillMeters = 1800;
        var d = Doc(t);
        Assert.Equal("控制方量", d.Rows[0].Basis);
        Assert.Contains("120 孔", d.Rows[0].Plan);
        Assert.Contains("1,800 m", d.Rows[0].Plan);
        Assert.Contains("控制方量 —", d.MetricsText);      // 没录控制方量就写「—」，不写 0
    }

    [Fact]
    public void 分账_没有运输笔时运输功如实写没有()
    {
        // ★ 不拿采装笔的量反推 —— 原版正是从反推改成逐笔取的
        var d = Doc(T(ProcessType.Load));
        Assert.DoesNotContain("运输功", d.MetricsText);
        Assert.DoesNotContain("加权运距", d.MetricsText);
    }

    [Fact]
    public void 分账_有运输笔时才给运输功与加权运距()
    {
        var d = Doc(T(ProcessType.Haul, vol: 5000, km: 3));
        Assert.Contains("运输功", d.MetricsText);
        Assert.Contains("加权运距 3.00 km", d.MetricsText);
    }

    [Fact]
    public void 分账_爆破笔不记方量()
    {
        var d = Doc(T(ProcessType.Blast, vol: 0, dest: "", equip: ""));
        Assert.Equal("—", d.Rows[0].Plan);
        Assert.Contains("爆破 1 炮", d.MetricsText);
    }

    [Fact]
    public void 分账_本班无任务时说清楚()
        => Assert.Equal("本班无任务。", Doc().MetricsText);

    // ── 从哪采 → 拉到哪 ─────────────────────────────────────
    [Fact]
    public void 卸点_缺卸点要单列点名且标红()
    {
        var d = Doc(T(dest: ""));
        Assert.True(d.Rows[0].NoDestination);
        Assert.Equal("未指定卸点", d.Rows[0].Destination);
        Assert.True(d.HasMissingSinks);
        Assert.Contains("未指定卸点", d.MissingSinks[0]);
    }

    [Fact]
    public void 卸点_穿孔笔不要卸点()
    {
        var d = Doc(T(ProcessType.Drill, dest: "", vol: 0));
        Assert.False(d.Rows[0].NoDestination);
        Assert.Equal("—", d.Rows[0].Destination);
        Assert.False(d.HasMissingSinks);
    }

    [Fact]
    public void 卸点_没量的采装笔不判缺卸点()
    {
        Assert.False(Doc(T(dest: "", vol: 0)).HasMissingSinks);
    }

    [Fact]
    public void 卸点_判据与下达闸门是同一条()
    {
        // 两处各写一遍的话，任务书标红不许签发、下达那侧照样放行 —— 而放行那侧才真落盘
        var t = T(dest: "");
        Assert.Equal(TaskOrderModel.MissingDestination(t),
                     !DispatchEngine.ValidateForIssue(new ExploderResult { Tasks = { t } }).CanIssue);
    }

    [Fact]
    public void 去向分布_按采装笔实方算并写明按什么算的()
    {
        var d = Doc(T(vol: 4000, dest: "北排土场"), T(vol: 6000, dest: "南排土场", equip: "E2", id: "D-2"));
        Assert.Contains("去向分布（按采装笔实方计）", d.SummaryText);
        Assert.Contains("南排土场 0.6万m³实方", d.SummaryText);   // 量大的排前面
    }

    // ── 只出单据不签发 ──────────────────────────────────────
    [Fact]
    public void 签发_下达状态从单据流水回显()
    {
        var t = T();
        string key = TaskKey.Of(t, Day);
        var d = TaskOrderModel.Build(new[] { t }, Day, "早班", 7.5,
                                     k => k == key ? "✓ 已下达 v2（老王·09-11 07:30）" : "待下达");
        Assert.StartsWith("✓ 已下达 v2", d.Rows[0].Issue);
        Assert.Equal(key, d.Rows[0].Key);
    }

    [Fact]
    public void 签发_没给回显函数时一律待下达()
        => Assert.Equal("待下达", Doc(T()).Rows[0].Issue);

    // ── 行内容 ──────────────────────────────────────────────
    [Fact]
    public void 行_编组带配车数作业地点带台阶()
    {
        var r = Doc(T()).Rows[0];
        Assert.Equal("E1（配 3 车）", r.Group);
        Assert.Equal("北一采（+1195）", r.Place);
        Assert.Equal("00:00–08:00 / 8 h", r.Span);
    }

    [Fact]
    public void 行_作业人员没派工就写破折号不编名字()
        => Assert.Equal("—", Doc(T()).Rows[0].Crew);

    [Fact]
    public void 行_物料编码翻成中文并带上原文()
        => Assert.Contains("（rock）", Doc(T(mat: "rock")).Rows[0].Material);

    [Fact]
    public void 行_没运距写破折号不写零()
        => Assert.Equal("—", Doc(T(km: 0)).Rows[0].Haul);

    [Fact]
    public void 行_按开始时刻排序并从一编号()
    {
        var d = Doc(T(start: 8, end: 16, id: "B"), T(start: 0, end: 8, id: "A"));
        Assert.Equal(new[] { "A", "B" }, d.Rows.Select(r => r.TaskId));
        Assert.Equal(new[] { 1, 2 }, d.Rows.Select(r => r.No));
    }

    [Fact]
    public void 行_只出本班的空闲笔不进单据()
    {
        var d = Doc(T(shift: "早班", id: "A"), T(shift: "中班", id: "B"),
                    T(ProcessType.Idle, shift: "早班", id: "C"));
        Assert.Single(d.Rows);
        Assert.Equal("A", d.Rows[0].TaskId);
    }

    [Fact]
    public void 抬头_带日期班次与编制时刻()
    {
        var d = Doc(T());
        Assert.Contains("2026-09-11", d.MetaText);
        Assert.Contains("早班", d.MetaText);
        Assert.Contains("07:30", d.MetaText);
    }

    [Fact]
    public void 结论句_点明三本账不合并()
        => Assert.Contains("任何一处把它们加在一起得到的都不是真实量", Doc(T()).SummaryText);

    // ── 量的换算 ────────────────────────────────────────────
    [Fact]
    public void 换算_排土量落到排弃占容不落到原位实方()
    {
        var q = TaskOrderModel.ToQuantity(T(ProcessType.Dump, vol: 6000));
        Assert.Equal(6000, q.DumpVolumeM3, 6);
        Assert.Equal(0, q.TargetVolumeM3, 9);
    }

    [Fact]
    public void 换算_采装吨按物料原位密度折()
    {
        var q = TaskOrderModel.ToQuantity(T(ProcessType.Load, vol: 1000, mat: "coal"));
        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText("coal"));
        Assert.Equal(1000 * spec.InSituDensityTPerM3, q.TargetTonnageT, 6);
    }

    [Fact]
    public void 换算_空任务不抛()
        => Assert.Equal(ProcessType.Drill, TaskOrderModel.ToQuantity(null!).Process);

    // ── 导出 ────────────────────────────────────────────────
    [Fact]
    public void 导出_表头与两行抬头都在且逗号被转义()
    {
        var d = Doc(T(zone: "北一采,东"));
        string csv = TaskOrderModel.ToCsv(d);
        Assert.Contains("序号,编组,作业地点,卸载地点", csv);
        Assert.Contains("\"北一采,东（+1195）\"", csv);
        Assert.StartsWith("# ", csv);
    }
}
