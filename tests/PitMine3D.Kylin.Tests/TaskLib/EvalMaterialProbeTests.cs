// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/EvalMaterialProbeTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 探针：当日盘子里的采装面到底带什么物料、有没有煤质目标。
///
/// <para><b>⚠ 它对「台账在不在」极其敏感</b>：作业面派生走
/// <c>MonthlyUnitLedgerStore.DefaultRoot</c>，而那个根是跟着
/// <c>AppContext.BaseDirectory</c> 走的。判据输出目录一换（<c>-p:OutputPath</c> 到 scratch），
/// <c>Data\采掘单元台账</c> 就不在旁边了 —— 台账读不到 ⇒ 队首行为 null ⇒
/// <b>物料一律退成硬岩</b>、备采退成工序区自带的体积列。
/// 于是"煤面被判成了岩面"这个结论会凭空出现，而它是台架自己造的。
/// 换输出目录跑时要把台账目录 junction 到输出目录旁边（见 [[offline-real-db-harness]]）。</para>
/// </summary>
[Collection("RealDb")]
public class EvalMaterialProbeTests
{
    private readonly ITestOutputHelper _out;
    private readonly RealDbFixture _db;
    public EvalMaterialProbeTests(RealDbFixture db, ITestOutputHelper o) { _db = db; _out = o; }

    [Fact]
    public void MP0_采装面的物料构成()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP：真库不可用"); return; }

        var cfg = ProductionPlanContext.Config();
        _out.WriteLine($"作业面来源 = {ProductionPlanContext.FaceOrigin} · {ProductionPlanContext.FaceSourceLabel}");
        _out.WriteLine($"\n═══ 盘子里的面 {cfg.Faces.Count} 个 ═══");
        foreach (var f in cfg.Faces.Take(40))
            _out.WriteLine($"  {f.Zone,-28} {f.Process.Label(),-4} 物料={f.MaterialCode,-10}({f.Material,-6})"
                         + $" 日目标={f.DayTargetM3,10:0} 备采={f.AvailableReserveM3,12:0} 分项={f.Splits?.Count ?? 0}");

        var day = SampleTaskBoard.Day().Where(t => t.Process == ProcessType.Load).ToList();
        _out.WriteLine($"\n═══ 采装任务 {day.Count} 条 ═══");
        foreach (var t in day.Take(40))
            _out.WriteLine($"  {t.WorkZone,-28} 班={t.Shift,-4} 计划={t.TargetVolumeM3,8:0}"
                         + $" 物料码={t.MaterialCode,-10} 混合={t.ResolvedMix.Caption}"
                         + $" 矿比={t.ResolvedMix.OreFraction:0.00} 采出={t.OreVolumeM3:0} 剥离={t.WasteVolumeM3:0}");

        _out.WriteLine($"\n合计：计划 {day.Sum(t => t.TargetVolumeM3) / 1e4:0.00} 万m³"
                     + $" · 采出 {day.Sum(t => t.OreVolumeM3) / 1e4:0.00} 万m³"
                     + $" · 剥离 {day.Sum(t => t.WasteVolumeM3) / 1e4:0.00} 万m³");
        _out.WriteLine($"有煤质目标的面：{day.Count(t => t.QualityTarget != null)}/{day.Count}");

        _out.WriteLine("");
        _out.WriteLine("═══ 出煤的采装任务（OreVolumeM3 > 0）═══");
        var ore = day.Where(t => t.OreVolumeM3 > 1e-6).ToList();
        if (ore.Count == 0) _out.WriteLine("  一条都没有 —— 全天没有出煤的采装任务");
        foreach (var t in ore)
            _out.WriteLine($"  {t.WorkZone,-24} 班={t.Shift,-4} 计划={t.TargetVolumeM3,8:0} 采出={t.OreVolumeM3,8:0} 物料={t.MaterialCode}");
        foreach (var g in day.GroupBy(t => t.Shift))
            _out.WriteLine($"  班「{g.Key}」：{g.Count()} 条 · 出煤 {g.Count(t => t.OreVolumeM3 > 1e-6)} 条 · 采出 {g.Sum(t => t.OreVolumeM3):0} m3");

        foreach (var n in ProductionPlanContext.AssemblyNotes.Take(25)) _out.WriteLine("  · " + n);
    }
}
