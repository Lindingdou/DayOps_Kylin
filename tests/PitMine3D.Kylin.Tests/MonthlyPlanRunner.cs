// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/MonthlyPlanRunner.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Tests.Synth;
using BlockModel = PitMine3D.Kylin.Tests.Synth.BlockModel;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <b>月度计划离线排产器</b>。
///
/// <para><b>它在做什么</b>：采矿模型与排土模型给出的是<b>潜在位置</b>（哪儿有煤、哪儿有岩、
/// 哪儿能排），月度计划要从这批候选里定<b>这个月采哪些、排到哪些位置、怎么排最优</b>。
/// 候选全在采掘单元基表里（煤 / 岩 / 排土位置三类），所以这一步<b>不需要界面</b>。</para>
///
/// <para><b>写盘要显式开</b>：设环境变量 <c>PITMINE_PERIOD=2026-08</c> 才落盘，
/// 否则只算 + 只报，不碰你桌面上的台账 —— 判据跑全套时不该改用户的数据。</para>
/// </summary>
public sealed class MonthlyPlanRunner
{
    private readonly ITestOutputHelper _out;
    public MonthlyPlanRunner(ITestOutputHelper o) => _out = o;

    private static string LedgerRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "采掘单元台账");

    // 目标沿用现场给的两个数
    private const double CoalTargetT = 1_248_596;
    private const double StripTargetM3 = 3_685_618;

    /// <summary>一套方案的成绩。维度与 `UnitSchemeCompareWindow` 那 6 个对齐。</summary>
    private sealed class Scheme
    {
        public DumpOrder Dump; public FacePriority Face; public StrikeAdvance Strike; public PairingStrategy Pair;
        public UnitPlanResult R = null!;
        public double CoalDevPct, StripRatio, WorkWan, DumpHaulKm, InternalPct, SlotCount;
        public double Score;
        public bool Usable;
        public string Caption => $"{T(Dump)} × {T(Face)} × {T(Strike)} × {T(Pair)}";

        // 文案在 PlanLib.Views 那侧（UnitSchemeText），本工程引不到 —— 这里只作显示，不作口径
        private static string T(DumpOrder d) => d switch
        { DumpOrder.PanelThenStep => "逐幅到底", DumpOrder.NearestToSource => "就近优先",
          DumpOrder.MostRoom => "摊平", _ => "逐带推进" };
        private static string T(FacePriority f) => f switch
        { FacePriority.LowestRatioFirst => "剥采比低优先", FacePriority.NearestFirst => "近路优先", _ => "长带优先" };
        private static string T(StrikeAdvance s) => s switch
        { StrikeAdvance.BothEnds => "两头对采", StrikeAdvance.FromMiddle => "中间开切", _ => "单向" };
        private static string T(PairingStrategy p) => p switch
        { PairingStrategy.InternalFirst => "内排优先", PairingStrategy.LevelCapacity => "库容均衡", _ => "运输功最小" };
    }

    [Fact]
    public void 排出本月计划()
    {
        string basePath = Path.Combine(LedgerRoot, MonthlyUnitLedgerStore.BaseFileName);
        if (!File.Exists(basePath)) { _out.WriteLine("◆ 找不到基表：" + basePath); return; }

        // ── 候选：三类都从基表来 ────────────────────────────────────────────
        string text = File.ReadAllText(basePath);
        Assert.True(MiningUnitLedger.TryRead(text, out var rows, out var issues),
                    "基表读不出来：" + string.Join("；", issues.Take(3)));
        foreach (var i in issues.Take(5)) _out.WriteLine("基表：" + i);

        var notes = new List<string>();
        var (units, slots) = MineUnitAdapter.FromLedger(rows, notes);
        foreach (var n in notes) _out.WriteLine(n);
        Assert.True(units.Count > 0, "没有采掘单元");

        // 出矿点：`CoalSinkAdapter` 在 PlanLib，本工程引不到；它的两级兜底是
        // 「装卸点台账 → 采场质心」，而台账要连库。这里直接走**采场质心**那一档并<b>明说</b> ——
        // 它在煤单元正中间，煤流的运距是个近似，不能拿它画运输线（与适配器的告诫同一句）。
        double sx = units.Where(u => u.IsCoal).Average(u => u.Cx);
        double sy = units.Where(u => u.IsCoal).Average(u => u.Cy);
        double sz = units.Where(u => u.IsCoal).Average(u => u.Cz);
        var sinks = new List<CoalSink>
        { new() { Name = "采场质心（降级）", Code = "CR-DEFAULT", Cx = sx, Cy = sy, Cz = sz } };
        _out.WriteLine($"出矿点：◆ 降级用【采场质心】({sx:0}, {sy:0}, {sz:0}) —— "
                     + "煤流运距是近似，不能拿它画运输线。要真点请在界面「指煤卸点」。");

        _out.WriteLine("");
        _out.WriteLine($"候选：煤 {units.Count(u => u.IsCoal)} · 岩 {units.Count(u => !u.IsCoal)} · 排土位置 {slots.Count}"
                     + $" · 库容合计 {slots.Sum(s => s.CapacityM3) / 1e4:N1} 万m³占容");
        _out.WriteLine($"目标：采出 {CoalTargetT / 1e4:N2} 万t · 排弃 {StripTargetM3 / 1e4:N1} 万m³实方");
        _out.WriteLine("");

        UnitPlanInput Make(DumpOrder d, FacePriority f, StrikeAdvance s, PairingStrategy p)
        {
            // ★ 库容有状态：每套方案必须拿一份【干净副本】，否则第 2 套开始就在吃第 1 套排剩下的
            //   —— 那是把 N 套算成了一条链，越靠后越"排不下"，而且看上去像方案本身差（U6）。
            var (u2, s2) = MineUnitAdapter.FromLedger(rows, new List<string>());
            return new UnitPlanInput
            {
                Units = u2, Slots = s2, CoalSinks = sinks,
                Materials = new[] { new GapMaterial { Name = "岩", Code = "rock", Density = 2.5, Kr = 1.15 } },
                CoalTargetT = CoalTargetT,
                StripTargetM3 = StripTargetM3,
                CoalDensity = MiningUnitLedger.DefaultCoalDensity,
                DumpOrder = d, FacePriority = f, StrikeAdvance = s, Strategy = p,
                Month = 8,
            };
        }

        // ── 先跑一套量耗时，再决定扫多宽 ────────────────────────────────────
        var sw = Stopwatch.StartNew();
        var probe = UnitPlanEngine.Solve(Make(DumpOrder.StepThenPanel, FacePriority.LongestFirst,
                                              StrikeAdvance.OneWay, PairingStrategy.MinHaul));
        sw.Stop();
        Assert.True(probe.Success, "排产失败：" + probe.Error);
        _out.WriteLine($"单套耗时 {sw.ElapsedMilliseconds} ms");

        var dumps = (DumpOrder[])Enum.GetValues(typeof(DumpOrder));
        var faces = (FacePriority[])Enum.GetValues(typeof(FacePriority));
        var strikes = (StrikeAdvance[])Enum.GetValues(typeof(StrikeAdvance));
        // 只有一个排土场时「配对策略」这条轴是塌的（三种必然挑同一个位置）——
        // 塌了就别摆进比选表，否则用户以为自己在更多选项里挑。
        int dumpNames = slots.Select(s => s.DumpName).Distinct(StringComparer.Ordinal).Count();
        var pairs = dumpNames > 1
            ? (PairingStrategy[])Enum.GetValues(typeof(PairingStrategy))
            : new[] { PairingStrategy.MinHaul };
        if (dumpNames <= 1) _out.WriteLine($"· 只有 {dumpNames} 个排土场 ⇒「配对策略」这条轴塌了，只留 1 个取值。");

        long est = (long)dumps.Length * faces.Length * strikes.Length * pairs.Length * Math.Max(1, sw.ElapsedMilliseconds);
        _out.WriteLine($"· 派生 {dumps.Length}×{faces.Length}×{strikes.Length}×{pairs.Length} "
                     + $"= {dumps.Length * faces.Length * strikes.Length * pairs.Length} 套，预计 {est / 1000.0:0.#} 秒");
        _out.WriteLine("");

        var list = new List<Scheme>();
        foreach (var d in dumps)
            foreach (var f in faces)
                foreach (var s in strikes)
                    foreach (var p in pairs)
                    {
                        var r = UnitPlanEngine.Solve(Make(d, f, s, p));
                        if (!r.Success) continue;
                        var flows = r.Assignments.SelectMany(a => a.Flows).Where(x => !x.IsCoalSink).ToList();
                        double dumpM3 = flows.Sum(x => x.InSituM3);
                        list.Add(new Scheme
                        {
                            Dump = d, Face = f, Strike = s, Pair = p, R = r,
                            CoalDevPct = CoalTargetT > 1e-9 ? Math.Abs(r.CoalT - CoalTargetT) / CoalTargetT * 100 : 0,
                            StripRatio = r.StripRatio,
                            WorkWan = r.TransportWorkTKm / 1e4,
                            DumpHaulKm = dumpM3 > 1e-9 ? flows.Sum(x => x.InSituM3 * x.HaulKm) / dumpM3 : 0,
                            InternalPct = r.InternalRatePct,
                            SlotCount = r.DumpSlotCount,
                            Usable = r.Validate().Count == 0,
                        });
                    }
        Assert.True(list.Count > 0, "一套方案都没排出来");
        Score(list);

        var best = list.Where(x => x.Usable).OrderByDescending(x => x.Score).FirstOrDefault()
                ?? list.OrderByDescending(x => x.Score).First();

        _out.WriteLine(" 名次 | 方案                                     | 分  | 采出万t | 剥离万m³ | 剥采比 | 运输功万t·km | 排不下万m³ | 位置数 | 自洽");
        _out.WriteLine("------|------------------------------------------|-----|---------|----------|--------|--------------|------------|--------|-----");
        int rank = 1;
        foreach (var x in list.OrderByDescending(v => v.Usable).ThenByDescending(v => v.Score).Take(12))
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,5} | {1,-40} | {2,3:0} | {3,7:N1} | {4,8:N1} | {5,6:0.00} | {6,12:N1} | {7,10:N1} | {8,6} | {9}",
                rank++, x.Caption, x.Score, x.R.CoalT / 1e4, x.R.StripM3 / 1e4, x.R.StripRatio,
                x.WorkWan, x.R.UnplacedM3 / 1e4, x.R.DumpSlotCount, x.Usable ? "✔" : "◆"));

        _out.WriteLine("");
        _out.WriteLine("★ 选中：" + best.Caption + $"（{best.Score:0} 分）");
        _out.WriteLine(best.R.Report().TrimEnd());

        // ── 剥采 / 剥排 对账（E1 / E2）─────────────────────────────────────
        var bad = new List<string>();
        double coalM3 = 0, rockM3 = 0, placed = 0, cap = 0;
        var byId = best.R.Assignments.ToDictionary(a => a.UnitId, a => a, StringComparer.Ordinal);
        var uById = units.ToDictionary(u => u.UnitId, u => u, StringComparer.Ordinal);
        foreach (var a in best.R.Assignments)
        {
            if (!uById.TryGetValue(a.UnitId, out var u)) { bad.Add($"排产吐出的 {a.UnitId} 不在候选里"); continue; }
            if (u.IsCoal) coalM3 += a.InSituM3; else rockM3 += a.InSituM3;
            foreach (var f in a.Flows)
            {
                if (u.IsCoal != f.IsCoalSink) bad.Add($"{a.UnitId} 出了一笔类型不符的流 —— 剥采串账");
                if (f.IsCoalSink) continue;
                placed += f.InSituM3; cap += f.DumpM3;
                if (Math.Abs(f.DumpM3 - f.InSituM3 * f.Kr) > 1e-6 * Math.Max(1, f.DumpM3))
                    bad.Add($"{a.UnitId}→{f.DestinationCode} 占容 ≠ 实方×Kr");
            }
        }
        Rel("E1 采出", coalM3 * MiningUnitLedger.DefaultCoalDensity, best.R.CoalT, bad);
        Rel("E1 剥离", rockM3, best.R.StripM3, bad);
        Rel("E2 排得下+排不下=剥离", placed + best.R.UnplacedM3, best.R.StripM3, bad);
        _out.WriteLine("");
        _out.WriteLine($"E1 采出 {coalM3 * MiningUnitLedger.DefaultCoalDensity / 1e4:N2} 万t · 剥离 {rockM3 / 1e4:N1} 万m³");
        _out.WriteLine($"E2 排得下 {placed / 1e4:N1} + 排不下 {best.R.UnplacedM3 / 1e4:N1} = {(placed + best.R.UnplacedM3) / 1e4:N1} 万m³ · 占容 {cap / 1e4:N1} 万m³");
        foreach (var b in bad.Take(8)) _out.WriteLine("   ◆ " + b);
        Assert.Empty(bad);

        // ── 落盘（要显式开）────────────────────────────────────────────────
        string period = Environment.GetEnvironmentVariable("PITMINE_PERIOD") ?? "";
        if (period.Length == 0)
        { _out.WriteLine("\n（只算不写。要落盘：设 PITMINE_PERIOD=2026-08 再跑）"); return; }

        int wrote = WriteBack(rows, best.R, period, out int dumpRows);
        var outRows = rows.Where(r => string.Equals(r.Period, period, StringComparison.Ordinal)).ToList();
        // ToCsv 自己会加「采掘单元台账 · 」前缀，这里只给后半截，否则标题会重复一遍
        string csv = MiningUnitLedger.ToCsv(outRows, $"期次 {period}");
        string path = Path.Combine(LedgerRoot, period + ".csv");
        File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true));
        _out.WriteLine($"\n已落盘 {path}");
        _out.WriteLine($"  采场单元 {wrote} 行 + 排土位置 {dumpRows} 行 = {outRows.Count} 行（MU9 三类齐全）");
    }

    /// <summary>把排产结果写回台账行 —— 与 `MiningUnitPlanWindow` 的写回逻辑同构。</summary>
    private static int WriteBack(List<MiningUnitLedger.Row> rows, UnitPlanResult r, string period, out int dumpRows)
    {
        // ★ 先把【本期次】的旧标记清掉再写。
        //   基表是长期表，行上带着历次排产留下的 Period —— 不清的话，上一轮排中、
        //   这一轮没排中的行会**照旧带着 2026-08 被写出去**：实测 129 个单元的计划
        //   落出来 225 行，多出来的 96 行是上一轮的残留，而每一项校核都是 ✓。
        //   只清同名期次，别的月份不动。
        foreach (var r0 in rows)
            if (string.Equals(r0.Period, period, StringComparison.Ordinal)) r0.Period = "";

        var byId = rows.ToDictionary(x => x.UnitId, x => x, StringComparer.Ordinal);
        int wrote = 0;
        var usedDest = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in r.Assignments)
        {
            if (!byId.TryGetValue(a.UnitId, out var row)) continue;
            row.Seq = a.Seq;
            row.Period = period;
            row.Status = a.DoneAfter >= 1 - 1e-6 ? "已采" : "在采";
            row.Done = Math.Min(1, a.DoneAfter);
            row.Flows.Clear();
            foreach (var f in a.Flows)
            {
                row.Flows.Add(new MiningUnitLedger.Flow
                {
                    Destination = f.DestinationCode, InSituM3 = f.InSituM3,
                    HaulKm = f.HaulKm, MaterialCode = f.MaterialCode,
                });
                if (!f.IsCoalSink) usedDest.Add(f.DestinationCode);
            }
            wrote++;
        }
        // MU9：本月真正用到的【排土位置】也要进期次 —— 否则岩流指向的位置不在表里，
        //      这份期次台账自己解释不了自己。
        //
        // ⚠ 不能拿 UnitId 去匹配去向码 —— 它们是【两套字符串】：
        //      台账行 UnitId  = 内排土场1-L2-P03-S01   （人读的，L 级是台账极性：L1=最上一级）
        //      流的去向码     = 内排土场1-L3-1003200   （引擎的 SlotCode，L 级已翻成自下而上）
        //   第一版按 UnitId 匹配，结果**一个都没匹配上、排土 0 行**，而校核全过。
        //   正确做法是用共享件把码重建一遍（`DumpSlotCode` 就是为收口这件事存在的）。
        int maxLv = DumpSlotCode.MaxLevelOf(rows);
        dumpRows = 0;
        foreach (var row in rows)
        {
            if (row.Kind != LedgerKind.Dump) continue;
            DumpSlotCode.TryLevelFromSeam(row.Seam, out int lv);
            string code = DumpSlotCode.Of(row.Region, Math.Max(0, maxLv - lv), row.Band, row.Panel);
            if (!usedDest.Contains(code)) continue;
            row.Period = period;
            if (row.Status.Length == 0) row.Status = "在用";
            dumpRows++;
        }
        return wrote;
    }

    private static void Score(List<Scheme> list)
    {
        (Func<Scheme, double> Get, bool Lower, double W)[] dims =
        {
            (x => x.CoalDevPct, true, 1.0), (x => x.StripRatio, true, 0.6),
            (x => x.WorkWan, true, 1.0),    (x => x.DumpHaulKm, true, 0.6),
            (x => x.InternalPct, false, 0.8), (x => x.SlotCount, true, 0.0),
        };
        double wsum = dims.Sum(d => d.W); if (wsum <= 1e-9) wsum = 1;
        foreach (var x in list)
        {
            double v = 0;
            foreach (var d in dims)
            {
                double lo = list.Min(y => d.Get(y)), hi = list.Max(y => d.Get(y));
                double n = hi - lo < 1e-12 ? 1.0 : (d.Get(x) - lo) / (hi - lo);
                v += d.W * (d.Lower ? 1 - n : n);      // 方向感知归一
            }
            x.Score = 100.0 * v / wsum;
        }
    }

    private static void Rel(string what, double a, double b, List<string> bad)
    {
        double m = Math.Max(Math.Abs(a), Math.Abs(b));
        if (m < 1e-9) return;
        if (Math.Abs(a - b) / m > 1e-6) bad.Add($"{what} 不闭合：{a:0.###} vs {b:0.###}");
    }
}
