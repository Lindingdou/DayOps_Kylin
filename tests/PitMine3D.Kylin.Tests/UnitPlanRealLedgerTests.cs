// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/UnitPlanRealLedgerTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <b>真基表冒烟</b>（照 <c>RealProfileScheduleTests</c> 那条自报路线做）——
/// 拿现场那份采掘单元台账基表跑一遍「给定采煤量 + 排弃量 → 本月计划」，
/// <b>把演示会看到的那些数打出来</b>，并判不变量。
///
/// <para><b>为什么要有这一条</b>：合成算例判的是<b>方向与自洽</b>，判不了
/// 「这份真数据上排不排得出东西」。演示前最该知道的恰恰是后者 ——
/// 基表里排土位置库容是不是空的、前沿有没有岩可剥、煤够不够凑一个月，
/// 这些在合成算例上<b>全都是绿的</b>。</para>
///
/// <para><b>找不到基表就跳过，并说清缺什么怎么补</b> —— 不假绿也不误红。
/// 找文件按环境变量 → 桌面默认目录；<b>不</b>从运行目录往上翻
/// （判据常用 <c>/p:OutputPath</c> 指到临时目录，往上翻会掉进别的地方）。</para>
/// </summary>
public class UnitPlanRealLedgerTests
{
    private readonly ITestOutputHelper _out;
    public UnitPlanRealLedgerTests(ITestOutputHelper o) => _out = o;

    /// <summary>基表在哪：环境变量优先，其次桌面默认目录（窗口默认落的就是这里）。</summary>
    private static string? FindBase()
    {
        string? env = Environment.GetEnvironmentVariable("PITMINE_UNIT_LEDGER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (File.Exists(env)) return env;
            string cand = Path.Combine(env, "基表_采掘单元.csv");
            if (File.Exists(cand)) return cand;
        }
        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                   "采掘单元台账", "基表_采掘单元.csv");
        return File.Exists(desk) ? desk : null;
    }

    [Fact]
    public void 真基表_给定采煤量与排弃量能排出本月计划()
    {
        string? path = FindBase();
        if (path == null)
        {
            _out.WriteLine("跳过：没找到基表。补法 —— 在「采掘单元清单」窗口点【从采矿模型取】+【从排土条带取】，"
                         + "再点【保存基表】；或把 PITMINE_UNIT_LEDGER 指到基表_采掘单元.csv 所在目录。");
            return;
        }
        _out.WriteLine($"基表：{path}（{new FileInfo(path).Length / 1024.0:0.0} KB，"
                     + $"{File.GetLastWriteTime(path):MM-dd HH:mm}）");

        var store = new MonthlyUnitLedgerStore(Path.GetDirectoryName(path));
        Assert.True(store.TryLoadBase(out var rows, out var issues), "基表读不出来：" + string.Join("；", issues));
        foreach (var s in issues) _out.WriteLine("　◆ " + s);

        var notes = new List<string>();
        var (units, slots) = MineUnitAdapter.FromLedger(rows, notes);
        foreach (var n in notes) _out.WriteLine("　" + n);

        double coalAvailT = units.Where(u => u.IsCoal).Sum(u => u.RemainM3) * MiningUnitLedger.DefaultCoalDensity;
        double rockAvailM3 = units.Where(u => !u.IsCoal).Sum(u => u.RemainM3);
        double capM3 = slots.Sum(s => s.CapacityM3);
        _out.WriteLine($"可采煤 {coalAvailT / 1e4:0.0}万t · 可剥岩 {rockAvailM3 / 1e4:0.0}万m³ · "
                     + $"排土位置 {slots.Count} 个 / 库容 {capM3 / 1e4:0.0}万m³占容");

        // 演示口径：月煤量取"可采煤的 1/12"、排弃量按剥采比 3 折过来 —— 两个数都是【给定】的，
        // 引擎不许拿其中一个去改另一个（U13.3），下面逐条判。
        double coalT = Math.Max(1e4, coalAvailT / 12.0);
        double stripM3 = coalT * 3.0;

        // ⚠ 出矿点必须放在【采场附近】，不能图省事放原点：现场坐标是百万级，
        //   放原点会把每笔运距变成几百公里，运输功虚高三个数量级 —— 那是台架编出来的数，
        //   不是引擎的结论。这里照 CoalSinkAdapter 没有装卸点台账时的降级口径来：采场质心。
        double sx = units.Average(u => u.Cx), sy = units.Average(u => u.Cy), sz = units.Average(u => u.Cz);
        _out.WriteLine($"出矿点按【采场质心】兜底：({sx:0}, {sy:0}, {sz:0})"
                     + " —— 与界面上没指煤卸点时的降级口径一致，不是真破碎站位置。");

        var inp = new UnitPlanInput
        {
            Units = units,
            Slots = slots,
            CoalSinks = new List<CoalSink> { new() { Name = "破碎站(采场质心兜底)", Code = "CR-DEMO", Cx = sx, Cy = sy, Cz = sz } },
            Materials = new[] { new GapMaterial { Name = "岩", Code = "rock", Density = 2.5, Kr = 1.15 } },
            CoalTargetT = coalT,
            StripTargetM3 = stripM3,
            CoalDensity = MiningUnitLedger.DefaultCoalDensity,
            Month = 1,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = UnitPlanEngine.Solve(inp);
        sw.Stop();
        _out.WriteLine($"\n=== 演示会看到的（耗时 {sw.ElapsedMilliseconds} ms）===\n" + r.Report());
        // 运输功是个大数，光看它看不出合不合理 —— 折成平均运距才有量纲感（几公里 = 对，几百公里 = 台架摆错了）
        double tons = r.Assignments.Sum(a => a.TonnageT);
        if (tons > 1e-9)
            _out.WriteLine($"平均运距 {r.TransportWorkTKm / tons:0.00} km（吨量加权，{tons / 1e4:0.0}万t）");

        Assert.True(r.Success, "真基表上求解失败：" + r.Error);

        // ① 五类自洽 + 通用判据（真数据上同样要过，不因为"是真数据"就放宽）
        var g = inp.Graph ?? UnitGraphBuilder.Build(units, null);
        var bad = UnitPlanCriteria.All(r, inp, g, slots);
        Assert.True(bad.Count == 0, "真基表上判据不过：\n　" + string.Join("\n　", bad));
        var self = r.Validate();
        Assert.True(self.Count == 0, "真基表上引擎自检不过：\n　" + string.Join("\n　", self));

        // ② U13：给定的两个量都不许被对方改写
        Assert.Equal(stripM3, r.StripTargetM3, 3);
        Assert.True(r.Assignments.Count > 0, "一个单元都没排出来 —— 演示时会是一张空表");

        // ③ 演示前最该知道的三件事，红了也要说得出是哪一件（不 Assert，如实回显）
        if (r.ShortfallT > 1e-6) _out.WriteLine($"◆ 演示注意：煤欠 {r.ShortfallT / 1e4:0.0}万t —— 该推进作业面");
        if (r.ShortStripM3 > 1e-6) _out.WriteLine($"◆ 演示注意：剥不够 {r.ShortStripM3 / 1e4:0.0}万m³ —— 前沿无岩可剥");
        if (r.UnplacedM3 > 1e-6) _out.WriteLine($"◆ 演示注意：排不下 {r.UnplacedM3 / 1e4:0.0}万m³ —— 库容不够");
    }

    /// <summary>
    /// <b>内排上限这个参数是不是活的</b>（参数排查台账 §二.1）。
    ///
    /// <para><b>"接上了"不等于"有用"</b>：`InternalCumCapM3` 此前引擎在读、界面从没填过 ⇒
    /// 内排一直无约束（真基表实测内排率 100.0%）。接进界面之后必须证明它<b>真的改变结果</b>，
    /// 否则下一个人看到内排率还是 100%，会以为是数据问题而不是闸没生效。</para>
    ///
    /// <para>本仓库在这条上踩过一次：回填比压到 2% 内排率纹丝不动 ——
    /// 因为卡在了剥离能力那一侧（"能剥多少"），而不是配对侧（"剥出来往哪儿放"）。
    /// 所以判的是<b>单调性</b>：上限越紧，内排率不增。</para>
    /// </summary>
    [Fact]
    public void 真基表_内排上限这道闸必须真的咬人()
    {
        string? path = FindBase();
        if (path == null) { _out.WriteLine("跳过：没找到基表（补法见上）。"); return; }

        var store = new MonthlyUnitLedgerStore(Path.GetDirectoryName(path));
        Assert.True(store.TryLoadBase(out var rows, out _), "基表读不出来");
        var (units, slots) = MineUnitAdapter.FromLedger(rows, new List<string>());
        double sx = units.Average(u => u.Cx), sy = units.Average(u => u.Cy), sz = units.Average(u => u.Cz);
        var g = UnitGraphBuilder.Build(units, null);

        double coalT = 20e4, stripM3 = 60e4;
        UnitPlanResult Run(double cap)
        {
            var inp = new UnitPlanInput
            {
                Units = units, Slots = slots, Graph = g,
                CoalSinks = new List<CoalSink> { new() { Name = "破碎站", Code = "CR", Cx = sx, Cy = sy, Cz = sz } },
                Materials = new[] { new GapMaterial { Name = "岩", Code = "rock", Density = 2.5, Kr = 1.15 } },
                CoalTargetT = coalT, StripTargetM3 = stripM3,
                CoalDensity = MiningUnitLedger.DefaultCoalDensity,
                InternalCumCapM3 = cap, Month = 1,
            };
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, r.Error);
            return r;
        }

        // ⚠ 判据要按【台账里有没有外排去向】分情形 —— 这一条我第一版写窄了，当场被真数据打脸：
        //   全是内排的台账上，收紧上限之后量**无处可去**，只会变成「排不下」，内排率纹丝不动
        //   （那正是 G15c「允许去向是硬的，满了要报排不下，不许悄悄改投」的正确行为）。
        //   拿"内排率必须降"去判，会把对的实现判成错的。
        bool hasExternal = slots.Any(s => !s.IsInternal);
        _out.WriteLine($"台账里外排位置 {slots.Count(s => !s.IsInternal)} 个 / 内排 {slots.Count(s => s.IsInternal)} 个"
                     + $" ⇒ 按{(hasExternal ? "【有外排：量应改投外排，内排率下降】" : "【全内排：量无处可去，排不下上升】")}判");
        _out.WriteLine("内排上限万m³ │ 内排率% │ 内排占容万m³ │ 排不下万m³");

        double prevPct = double.MaxValue, prevInner = double.MaxValue, prevUnplaced = -1;
        int moved = 0;
        foreach (double capWan in new[] { 0.0, 200.0, 100.0, 40.0, 10.0 })   // 0 = 不卡，然后越卡越紧
        {
            var r = Run(capWan * 1e4);
            double pct = r.InternalRatePct;
            double inner = r.Rock.SelectMany(a => a.Flows).Where(f => f.IsInternalDump).Sum(f => f.DumpM3);
            _out.WriteLine($"{(capWan <= 0 ? "不卡" : capWan.ToString("0")),12} │ {pct,7:0.0} │ {inner / 1e4,12:0.0} │ {r.UnplacedM3 / 1e4,10:0.0}");

            if (capWan > 0)
            {
                // 无论哪种情形，【内排实际占容】都必须单调不增 —— 这才是这道闸直接管的量
                Assert.True(inner <= prevInner + 1e-6,
                    $"上限收紧到 {capWan}万m³，内排占容反而多了：{prevInner / 1e4:0.0} → {inner / 1e4:0.0} 万m³");
                if (hasExternal)
                    Assert.True(pct <= prevPct + 1e-6, $"有外排可投，上限收紧内排率却升了：{prevPct:0.0}% → {pct:0.0}%");
                else
                    Assert.True(r.UnplacedM3 >= prevUnplaced - 1e-6,
                        $"全内排的台账上，上限收紧「排不下」反而少了：{prevUnplaced / 1e4:0.0} → {r.UnplacedM3 / 1e4:0.0} 万m³");
                if (Math.Abs(inner - prevInner) > 1e-6 || Math.Abs(r.UnplacedM3 - prevUnplaced) > 1e-6) moved++;
            }
            prevPct = pct; prevInner = inner; prevUnplaced = r.UnplacedM3;
        }

        // ★ 关键：这道闸必须**真的咬人** —— 各档答案全一样就是没生效（本仓库踩过：卡错了地方，
        //   只卡剥离能力管的是"能剥多少"，不管"剥出来往哪儿放"）。
        Assert.True(moved > 0,
            "内排上限从不卡收到 10万m³，内排占容与排不下**一个都没动** —— 这道闸没有生效。");
    }

    /// <summary>
    /// <b>演示用的量级表</b>：煤量从小到大扫一遍，看必剥闭包在哪个量级上开始咬人。
    ///
    /// <para><b>为什么要有它</b>：真数据上小煤量时闭包恒为 0（前沿那批煤本来就露着），
    /// 于是「剥离必须先剥压覆」这一档在演示里<b>一次都不会触发</b> ——
    /// 看上去像是这条规则没做。得先知道它从哪儿开始起作用，演示才挑得对量级。</para>
    ///
    /// <para>判的是<b>单调性</b>不是具体数字（换一版基表数字全变、方向不变）：
    /// 煤量↑ ⇒ 必剥闭包不减。</para>
    /// </summary>
    [Fact]
    public void 真基表_煤量扫描_必剥闭包从哪个量级开始咬人()
    {
        string? path = FindBase();
        if (path == null) { _out.WriteLine("跳过：没找到基表（补法见上一条）。"); return; }

        var store = new MonthlyUnitLedgerStore(Path.GetDirectoryName(path));
        Assert.True(store.TryLoadBase(out var rows, out _), "基表读不出来");
        var (units, slots) = MineUnitAdapter.FromLedger(rows, new List<string>());
        double sx = units.Average(u => u.Cx), sy = units.Average(u => u.Cy), sz = units.Average(u => u.Cz);
        var g = UnitGraphBuilder.Build(units, null);        // 图建一次给所有档共用（快，也保证同源）

        _out.WriteLine("月煤量万t │ 必剥万m³ │ 剥离万m³ │ 单元数 │ 煤欠万t │ 剥不够万m³");
        double prevMand = -1;
        foreach (double wanT in new[] { 20.0, 40.0, 60.0, 80.0, 120.0, 200.0 })
        {
            var inp = new UnitPlanInput
            {
                Units = units, Slots = slots, Graph = g,
                CoalSinks = new List<CoalSink> { new() { Name = "破碎站", Code = "CR", Cx = sx, Cy = sy, Cz = sz } },
                Materials = new[] { new GapMaterial { Name = "岩", Code = "rock", Density = 2.5, Kr = 1.15 } },
                CoalTargetT = wanT * 1e4,
                StripTargetM3 = wanT * 1e4 * 3.0,           // 排弃量跟着按剥采比 3 给（演示常用口径）
                CoalDensity = MiningUnitLedger.DefaultCoalDensity,
                Month = 1,
            };
            var r = UnitPlanEngine.Solve(inp);
            Assert.True(r.Success, $"{wanT}万t 那一档求解失败：{r.Error}");
            _out.WriteLine($"{wanT,9:0} │ {r.MandatoryStripM3 / 1e4,8:0.0} │ {r.StripM3 / 1e4,8:0.0} │ "
                         + $"{r.Assignments.Count,6} │ {r.ShortfallT / 1e4,7:0.0} │ {r.ShortStripM3 / 1e4,10:0.0}");

            // 单调性：煤量↑ ⇒ 必剥闭包不减（换地质数字全变、方向不变）
            Assert.True(r.MandatoryStripM3 >= prevMand - 1e-6,
                $"煤量涨到 {wanT}万t 必剥闭包反而降了：{prevMand / 1e4:0.0} → {r.MandatoryStripM3 / 1e4:0.0} 万m³");
            prevMand = r.MandatoryStripM3;
        }
    }
}
