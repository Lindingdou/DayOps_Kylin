using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 车次展开（§三五八）。任务说的是"这个班采 2450 m³"，司机要的是"我第 3 趟 09:12 装车"。
///
/// 三条头号判据：
///   ① <b>错峰项 (i−1)·τ_L 不能省</b> —— n 台车同时压到铲下就得排队；省掉它，
///      展开出来的车次数与匹配系数 MF = n·τ_L/T_c 的物理含义就对不上。
///   ② <b>车次数取 时间法 与 量法 的小者</b>，且一趟算数的条件是<b>能把料卸掉</b>。
///      派车指令是承诺，半趟不是一车料；备采量只够 7 趟就不能签发 8 趟。
///   ③ <b>载重是吨</b>（三口径间唯一的守恒量），实方/松方由它按物料折 ——
///      本类不出现任何密度常量，一律经 <see cref="MaterialCatalog"/>。
/// </summary>
public class DispatchExpandTests
{
    private const string Day = "2026-09-11";

    private static ShiftTask T(double vol = 3000, string mat = "rock", double takt = 3, double tc = 24,
                               double payload = 100, double km = 2.5, double start = 0, double end = 8,
                               string equip = "E1", params string[] trucks)
    {
        var t = new ShiftTask
        {
            Id = "D-" + equip, Process = ProcessType.Load, Shift = "早班", WorkZone = "北一采",
            TargetVolumeM3 = vol, Material = mat,
            DestinationId = "D1", DestinationName = "北排土场", EquivHaulKm = km,
            StartHour = start, EndHour = end, PlannedHours = end - start,
            Group = new EquipmentGroup
            {
                MainEquipment = equip, RecommendedTrucks = 3,
                LoadTaktMin = takt, CycleTimeMin = tc, MatchFactor = 1, TruckPayloadT = payload,
            },
        };
        foreach (var x in (trucks.Length > 0 ? trucks : new[] { "T1", "T2", "T3" })) t.Group.Trucks.Add(x);
        return t;
    }

    private static DispatchPlan P(params ShiftTask[] ts) => DispatchExpand.Expand(ts, Day, "早班");

    // ── 错峰进场 ────────────────────────────────────────────
    [Fact]
    public void 错峰_第i台车晚一个装车节拍进场()
    {
        // ★ 省掉 (i−1)·τ_L，n 台车就成了同时压到铲下
        var p = P(T(vol: 1e6, takt: 3, tc: 24, trucks: new[] { "T1", "T2", "T3" }));
        var firstTrips = p.Orders.Where(o => o.TripNo == 1).OrderBy(o => o.PlannedLoadHour).ToList();
        Assert.Equal(3, firstTrips.Count);
        Assert.Equal(0.0, firstTrips[0].PlannedLoadHour, 4);
        Assert.Equal(3 / 60.0, firstTrips[1].PlannedLoadHour, 4);
        Assert.Equal(6 / 60.0, firstTrips[2].PlannedLoadHour, 4);
    }

    [Fact]
    public void 错峰_同一台车相邻两趟差一个循环时间()
    {
        var p = P(T(vol: 1e6, takt: 3, tc: 24, trucks: new[] { "T1" }));
        var mine = p.Orders.Where(o => o.TruckId == "T1").OrderBy(o => o.TripNo).ToList();
        Assert.True(mine.Count >= 2);
        Assert.Equal(24 / 60.0, mine[1].PlannedLoadHour - mine[0].PlannedLoadHour, 4);
    }

    [Fact]
    public void 时刻_卸车时刻是装完加重车行驶()
    {
        var o = P(T(vol: 1e6, takt: 3, tc: 24, trucks: new[] { "T1" })).Orders.First();
        // loaded ≈ (T_c − τ_L)/2 = 10.5min ⇒ 卸车 = 装车 + (3 + 10.5)/60
        Assert.Equal((3 + 10.5) / 60.0, o.PlannedDumpHour - o.PlannedLoadHour, 4);
    }

    // ── 两条上界取小 ────────────────────────────────────────
    [Fact]
    public void 取小_量法封顶时按目标量砍最晚的几趟()
    {
        // ★ 备采量只够 7 趟就不能签发 8 趟
        var p = P(T(vol: 100, mat: "rock", payload: 100, trucks: new[] { "T1", "T2", "T3" }));
        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText("rock"));
        int nVol = (int)Math.Ceiling(100 * spec.InSituDensityTPerM3 / 100.0 - 1e-6);
        Assert.Equal(nVol, p.TripCount);
        Assert.Contains("按目标量", string.Join("|", p.Notes));
    }

    [Fact]
    public void 取小_时窗不够一个循环时一趟也不排()
    {
        var p = P(T(takt: 3, tc: 24, start: 0, end: 0.1));   // 6 分钟
        Assert.Empty(p.Orders);
        Assert.Contains("不足一个循环", string.Join("|", p.Notes));
        Assert.Single(p.Skips);
    }

    [Fact]
    public void 取小_一趟算数的条件是能把料卸掉()
    {
        // 窗口 14min：装 3 + 重车 10.5 = 13.5 ≤ 14 ⇒ 每车恰好 1 趟
        var p = P(T(vol: 1e6, takt: 3, tc: 24, start: 0, end: 14 / 60.0, trucks: new[] { "T1" }));
        Assert.Single(p.Orders);
        // 窗口 13min ⇒ 卸不掉，不算一趟
        var p2 = P(T(vol: 1e6, takt: 3, tc: 24, start: 0, end: 13 / 60.0, trucks: new[] { "T1" }));
        Assert.Empty(p2.Orders);
    }

    [Fact]
    public void 取小_每车重新编趟号从一起()
    {
        var p = P(T(vol: 1e6, trucks: new[] { "T1", "T2" }));
        foreach (var g in p.Orders.GroupBy(o => o.TruckId))
            Assert.Equal(Enumerable.Range(1, g.Count()), g.OrderBy(o => o.PlannedLoadHour).Select(o => o.TripNo));
    }

    // ── 三口径 ──────────────────────────────────────────────
    [Fact]
    public void 口径_实方与松方都由吨按物料折()
    {
        // ★ 本类不出现任何密度常量
        var p = P(T(vol: 1e6, mat: "coal", payload: 90, trucks: new[] { "T1" }));
        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText("coal"));
        var o = p.Orders.First();
        Assert.Equal(90, o.PayloadT, 2);
        Assert.Equal(90 / spec.InSituDensityTPerM3, o.PayloadInSituM3, 1);
        Assert.Equal(spec.ToLooseM3(90 / spec.InSituDensityTPerM3), o.PayloadLooseM3, 1);
    }

    [Fact]
    public void 口径_松方一定大于实方()
    {
        var o = P(T(vol: 1e6, trucks: new[] { "T1" })).Orders.First();
        Assert.True(o.PayloadLooseM3 > o.PayloadInSituM3, "卡车拉的是爆破后的松散料");
    }

    [Fact]
    public void 口径_运输功是载重乘运距加权平均运距由它反推()
    {
        var p = P(T(vol: 1e6, payload: 100, km: 3, trucks: new[] { "T1", "T2" }));
        Assert.Equal(p.TotalPayloadT * 3, p.TotalWorkTKm, 3);
        Assert.Equal(3, p.AvgHaulKm!.Value, 6);
        Assert.Contains("运输功", p.Caption);
    }

    [Fact]
    public void 口径_没有车次时加权运距是判不了而不是零()
    {
        var p = P(T(start: 0, end: 0.05));
        Assert.Null(p.AvgHaulKm);                       // 判不了，不是 0
        Assert.Equal("本班没有排出车次。", p.Caption);   // 一趟都没有时抬头直说，不摆一排 0
    }

    // ── 解不出来时说清为什么 ────────────────────────────────
    [Fact]
    public void 解不出_没配车时点名并给建议车数()
    {
        var t = T();
        t.Group.Trucks.Clear();
        var p = P(t);
        Assert.Empty(p.Orders);
        Assert.Contains("尚未配车", string.Join("|", p.Notes));
        Assert.Equal(3, p.Skips.Single().RecommendedTrucks);
    }

    [Fact]
    public void 解不出_编组没有周期分解时不按经验值编一个节拍()
    {
        // ★ 编出来的时刻表看着精确，司机照着跑必然对不上
        var t = T();
        t.Group.LoadTaktMin = 0; t.Group.CycleTimeMin = 0;
        var p = P(t);
        Assert.Empty(p.Orders);
        Assert.Contains("不按经验值编一个节拍", string.Join("|", p.Notes));
    }

    [Fact]
    public void 解不出_台账没载重时按目标吨量反推且不再另设量法上界()
    {
        var t = T(vol: 1e6, payload: 0, trucks: new[] { "T1" });
        var p = P(t);
        Assert.NotEmpty(p.Orders);
        Assert.Contains("按目标吨量反推", string.Join("|", p.Notes));
    }

    [Fact]
    public void 解不出_本班无采装任务时说清楚()
    {
        var p = DispatchExpand.Expand(Array.Empty<ShiftTask>(), Day, "早班");
        Assert.Empty(p.Orders);
        Assert.Contains("无采装任务", string.Join("|", p.Notes));
    }

    // ── 筛选与司机 ──────────────────────────────────────────
    [Fact]
    public void 筛选_只展开采装笔与本班()
    {
        var load = T(equip: "E1");
        var dump = T(equip: "E2"); dump.Process = ProcessType.Dump;
        var other = T(equip: "E3"); other.Shift = "中班";
        var p = P(load, dump, other);
        Assert.All(p.Orders, o => Assert.Equal("E1", o.ShovelId));
    }

    [Fact]
    public void 司机_没派工时留空不编名字()
        => Assert.All(P(T(vol: 1e6, trucks: new[] { "T1" })).Orders, o => Assert.Equal("", o.Driver));

    [Fact]
    public void 司机_派了工就按车号对上()
    {
        var p = DispatchExpand.Expand(new[] { T(vol: 1e6, trucks: new[] { "T1" }) }, Day, "早班",
                                      (shift, truck) => truck == "T1" ? "老张" : "");
        Assert.All(p.Orders, o => Assert.Equal("老张", o.Driver));
    }

    // ── 指令号与稳定键 ──────────────────────────────────────
    [Fact]
    public void 指令号_带日期班次车号与趟号()
    {
        var o = P(T(vol: 1e6, trucks: new[] { "T1" })).Orders.First();
        Assert.Equal("DO-2026-09-11-早-T1-001", o.OrderId);
    }

    [Fact]
    public void 稳定键_每趟都带着任务的稳定键供实绩对账()
    {
        var t = T(vol: 1e6, trucks: new[] { "T1" });
        var o = P(t).Orders.First();
        Assert.Equal(TaskKey.Of(t, Day), o.StableKey);
    }

    // ── 防呆上界 ────────────────────────────────────────────
    [Fact]
    public void 防呆_单台车一个班不超过上限()
    {
        var p = P(T(vol: 1e9, takt: 0.1, tc: 0.2, payload: 0.1, start: 0, end: 24, trucks: new[] { "T1" }));
        Assert.True(p.Orders.Count(o => o.TruckId == "T1") <= DispatchExpand.MaxTripsPerTruck);
    }

    // ── 导出 ────────────────────────────────────────────────
    [Fact]
    public void 导出_表头含三口径与运输功()
    {
        string csv = DispatchExpand.ToCsv(P(T(vol: 1e6, trucks: new[] { "T1" })));
        Assert.Contains("载重t,实方m3,松方m3,运距km,运输功t·km", csv);
        Assert.StartsWith("# 车次", csv);
    }

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(9.25, "09:15")]
    [InlineData(24.5, "00:30+1")]
    public void 时刻_跨日回绕并加标记(double hh, string want)
        => Assert.Equal(want, DispatchExpand.Hm(hh));
}
