// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ZoneToShiftChainTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Zoning;

using PitMine3D.Kylin.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「工序作业区入库 → 盘子起面 → 逐日逐班」整链判据（ZS 组），<b>在真库副本上写真表</b>。
///
/// <para><b>为什么必须写表来验</b>：日常生产组织的作业面（<c>FaceInput</c>）不来自
/// 「确定开采程序」那套 <c>WorkingFace</c> —— 它来自 <b>process_zone 台账</b>。
/// 两套同名不同物，只验前者会得出"面已经有了"的错结论
/// （实测：确定开采程序侧 18 个面，而盘子里 0 个）。</para>
///
/// <para><b>只动副本</b>：夹具把真库拷到临时目录再 Initialize，
/// 这里的 Apply 写的是副本。真库一个字节都不碰。</para>
/// </summary>
[Collection("RealDb")]
public class ZoneToShiftChainTests
{
    private readonly ITestOutputHelper _out;
    private readonly RealDbFixture _db;
    public ZoneToShiftChainTests(RealDbFixture db, ITestOutputHelper o) { _db = db; _out = o; }

    private const string LedgerRoot =
        @"C:\Users\0doudou\Desktop\DayOps\PitMine3D\bin\Debug\Data\采掘单元台账";
    private const string Period = "2026-08";

    /// <summary>
    /// ZS1 整链：生成工序区 → 入库 → 盘子起面 → 分解成逐日逐班。
    ///
    /// <para><b>逐环打印 + 逐环断言</b>。断在哪一环，那一环就是缺口 ——
    /// 此前这条链只能靠在界面上点四个按钮、再去各自的状态区里翻。</para>
    /// </summary>
    [Fact]
    public void ZS1_入库工序区之后盘子起面并切出逐日逐班()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP: 真库不可用"); return; }

        // ── ① 生成工序作业区候选 ──────────────────────────────────
        var plan = ProcessZonePlanner.Plan(Period, ledgerRoot: LedgerRoot);
        _out.WriteLine($"① 生成：{plan.Header}");
        foreach (var n in plan.Notes.Take(8)) _out.WriteLine("   " + n);
        if (!plan.Ok) { _out.WriteLine("SKIP: 生成不出候选（上游缺期次或真轨）"); return; }

        var byProc = plan.Proposals.GroupBy(p => p.Process).ToList();
        _out.WriteLine($"   候选 {plan.Proposals.Count} 块："
                     + string.Join(" / ", byProc.Select(g => $"{g.Key} {g.Count()}块")));

        // ── ② 入库（写副本）──────────────────────────────────────
        //  每个分组只入最大的那一块 —— 同名多块都入库会让每块各吃下该面全部量（Z6）
        var chosen = plan.Proposals
            .GroupBy(p => (p.Process, p.GroupKey))
            .Select(g => g.OrderByDescending(x => x.AreaM2).First())
            .ToList();

        string msg = ProcessZoneStore.Apply(Period, chosen, out int ins, out int upd, out var fails);
        _out.WriteLine($"② 入库：{msg}（新增 {ins} · 更新 {upd} · 失败 {fails.Count}）");
        foreach (var f in fails.Take(8)) _out.WriteLine("   ◆ " + f);

        Assert.True(ins + upd > 0, "一块都没入库：" + msg);

        // ── ③ 盘子起面 ────────────────────────────────────────────
        // 入库之后先直接问派生器：它到底看到了什么
        int stored = 0;
        try { stored = ProcessZoneStore.LoadPeriod(Period).Count; } catch (Exception ex) { _out.WriteLine("   LoadPeriod 抛：" + ex.Message); }
        _out.WriteLine($"   台账里本期工序区 {stored} 块　工作日期 {ProjectScope.WorkDate:yyyy-MM-dd}");

        var direct = ProcessZoneFaceSource.Derive(Period, ledgerRoot: LedgerRoot);
        _out.WriteLine($"   直接派生：{(direct?.Count ?? -1)} 个面　来源说明「{ProcessZoneFaceSource.LastSourceLabel}」");
        foreach (var n in ProcessZoneFaceSource.LastNotes.Take(8)) _out.WriteLine("     " + n);

        ProductionPlanContext.Invalidate();
        var cfg = ProductionPlanContext.Config();
        var load = (cfg.Faces ?? new List<FaceInput>())
                   .Where(f => f != null && f.Process == ProcessType.Load).ToList();
        _out.WriteLine($"③ 盘子：作业面 {cfg.Faces?.Count ?? 0} 个（采装 {load.Count} 个）"
                     + $"　面来源 {ProductionPlanContext.FaceOrigin}"
                     + $"　真实面 {ProductionPlanContext.FacesAreReal}");
        foreach (var f in load.Take(8))
            _out.WriteLine($"   {f.Zone}　主设备「{f.Group?.MainEquipment}」"
                         + $"　编组班产 {f.Group?.GroupCapacityM3PerH:0.##} m³/h");

        // 面必须是**真实来源**，不许是样例 —— 样例面下游每个数都自洽，而它们说的是别的矿
        Assert.NotEmpty(load);
        Assert.True(ProductionPlanContext.FacesAreReal,
            $"面来源是 {ProductionPlanContext.FaceOrigin} —— 不是真实来源");

        // ── ④ 逐日逐班 ────────────────────────────────────────────
        var a = ShiftPlanAssembler.Build(Period, cfg, LedgerRoot);
        _out.WriteLine($"④ 分解：{a.Headline}");
        foreach (var n in a.Notes.Take(12)) _out.WriteLine("   " + n);

        var rows = a.Plan?.Rows ?? new List<ShiftTaskRow>();
        if (rows.Count == 0)
        {
            // 走到这里还切不出来，缺的一定是【可派设备】或【作业日】—— 面已经证明有了
            Assert.True(a.Headline.Contains("可派设备") || a.Headline.Contains("作业日"),
                "面有了却切不出来，而结论没说是缺设备还是缺作业日：" + a.Headline);
            _out.WriteLine("→ 面已就位，缺口在【可派设备 / 作业日】，结论说清了（判据通过）");
            return;
        }

        var byDate = rows.GroupBy(r => r.Date).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        _out.WriteLine($"   覆盖 {byDate.Count} 个作业日 · {rows.Count} 条班级作业");
        foreach (var g in byDate.Take(5))
            _out.WriteLine($"   {g.Key}　{g.Count()} 条　"
                         + string.Join(" / ", g.GroupBy(r => r.Shift)
                             .Select(x => $"{(x.Key.Length > 0 ? x.Key : "(无班)")}{x.Count()}条"))
                         + $"　量 {g.Sum(r => r.VolumeM3) / 1e4:0.##} 万m³"
                         + $"　切出地 {g.Count(r => r.ZoneAreaM2 > 0)} 块");

        // 切出来之后的三条硬约束
        // ★ 爆破笔不在这一条里（2026-08-20）：**爆破没有自己的方量口径** ——
        //   它爆的就是穿孔那一笔的控制方量，再记一遍就成了两份账（M5：四个量口径分开列）。
        //   所以爆破行的量恒为 0、执行人恒为空（爆破队台账还没有，不编一个队号）。
        //   这一条判的是"有量的工序不许排出 0 量的行"，把爆破排除掉才是它原本的意思。
        Assert.All(rows.Where(r => r.Process != ProcessType.Blast),
                   r => Assert.True(r.VolumeM3 > 0, "有一条量不为正：" + r.Caption));

        var clash = rows.Where(r => !string.IsNullOrWhiteSpace(r.MachineId))
                        .GroupBy(r => (r.Date, r.Shift, r.MachineId))
                        .Where(g => g.Select(x => x.FaceName).Distinct(StringComparer.Ordinal).Count() > 1)
                        .ToList();
        foreach (var c in clash.Take(5))
            _out.WriteLine($"   ◆ {c.Key.Date} {c.Key.Shift}班 {c.Key.MachineId} 被排到 "
                         + string.Join("、", c.Select(x => x.FaceName).Distinct()));
        Assert.Empty(clash);

        var dupIds = a.Tasks.Select(t => t.Id).GroupBy(x => x, StringComparer.Ordinal)
                            .Where(g => g.Count() > 1).ToList();
        Assert.Empty(dupIds);
    }
}
