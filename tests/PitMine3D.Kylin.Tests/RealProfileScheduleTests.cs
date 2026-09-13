// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/RealProfileScheduleTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using System.Collections.Generic;
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
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// G39 · <b>把逐月采剥接续钉到现场地质上</b>（F5：真用例）。
///
/// <para>G 组其余判据全跑在<b>合成地质</b>上，理由写在验收册里：那一组量的是构造性的东西，
/// 真值能手算，红了一定是算法错。但合成地质有个共同特点 —— <b>干净</b>：
/// 层数少、产状规则、标高格连续、桶分布均匀。现场地质三样都不是。</para>
///
/// <para>这条判据自己去找现场剖面（环境变量 → <c>Tests\data\incline_real.case</c> → <c>%TEMP%</c>），
/// <b>找到就在真地质上跑整条链并判不变量；找不到 / 那份用例里没有岩剖面，就把缺什么、
/// 怎么补说清楚然后跳过</b> —— 不假绿，也不因为环境缺东西就红。</para>
///
/// <para><b>只有地质是真的</b>：排土位置是合成的（现场排土场要另走「排土条带」），
/// 所以这里不判运输功/内排率的绝对值，只判<b>几何与守恒</b>那几条在真地质上照样成立。</para>
/// </summary>
public sealed class RealProfileScheduleTests
{
    private readonly ITestOutputHelper _out;
    public RealProfileScheduleTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// 找现场用例：环境变量 → <c>Tests\data\incline_real.case</c> → <c>%TEMP%</c> 里那份。
    ///
    /// <para><b>⚠ 往上翻要从【本文件的编译期路径】起，不能从 <c>AppContext.BaseDirectory</c> 起</b>：
    /// 判据的输出目录常被 <c>/p:OutputPath</c> 指到仓库外，那时按运行目录往上翻**永远找不到仓库里那份**，
    /// 会一路掉到 <c>%TEMP%</c> 的旧快照上 —— 于是"仓库里明明刷了新用例"和"判据读的是上周的"同时成立。
    /// 第一版就是这么读到 8 月 5 日那份旧的（S7 上踩过同一个坑）。</para>
    /// </summary>
    private static string? FindRealCase([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        string? env = Environment.GetEnvironmentVariable("PITMINE_INCLINE_CASE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        foreach (string anchor in new[] { Path.GetDirectoryName(here) ?? "", AppContext.BaseDirectory })
        {
            if (anchor.Length == 0) continue;
            var dir = new DirectoryInfo(anchor);
            for (int up = 0; up < 8 && dir != null; up++, dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "Tests", "data", "incline_real.case");
                if (File.Exists(p)) return p;
            }
        }
        string tmp = Path.Combine(Path.GetTempPath(), "pitmine_benchdump", "REAL_latest.case");
        return File.Exists(tmp) ? tmp : null;
    }

    [Fact]
    public void G39_RealGeology_RunsTheWholeChain_OrSaysExactlyWhatIsMissing()
    {
        string? path = FindRealCase();
        if (path == null)
        {
            _out.WriteLine("◆ 找不到现场用例 —— 跳过（不是失败）。");
            _out.WriteLine(@"  界面跑一次「生成采区台阶面」，把 %TEMP%\pitmine_benchdump\REAL_latest.case");
            _out.WriteLine(@"  拷到 Tests\data\incline_real.case 即可。");
            return;
        }
        _out.WriteLine($"现场用例：{path}");
        _out.WriteLine($"  {new FileInfo(path).Length / 1048576.0:0.0} MB · {new FileInfo(path).LastWriteTime:yyyy-MM-dd HH:mm}");

        var c = InclineCaseFile.TryLoad(path, out string err);
        Assert.True(c != null, "现场用例读不出来：" + err);

        // 三种状态，前两种是【正当的跳过】不是失败 —— 用例是界面落的，界面还没跑过就没有。
        if (c!.Profile == null)
        {
            _out.WriteLine("◆ 这份用例里**一个剖面都没有** —— 跳过（不是失败）。");
            _out.WriteLine($"  时间 {new FileInfo(path).LastWriteTime:yyyy-MM-dd HH:mm}，说明它是"
                         + "【剖面随用例落盘】这个改动**之前**落的（那时 `InclineCase.Profile` 不写）。");
            _out.WriteLine("  怎么补：界面上重跑一次「生成采区台阶面」（**台阶高填非 0**），");
            _out.WriteLine(@"  再把 %TEMP%\pitmine_benchdump\REAL_latest.case 拷到 Tests\data\incline_real.case。");
            _out.WriteLine("  ⚠ **脱 GUI 补不了**：仓里那份是 `InclineRealCaseRebuildTests` 从"
                         + " JSON+TIN 拼回来的，而剖面要扫**块体模型**才有，"
                         + "块体模型盘上没有持久化（无 .pmbm 之类，代码里也没有存/读块体的路径）——"
                         + "所以只有界面那一条路。");
            return;
        }

        var rock = c.Profile.Rock;
        if (rock == null)
        {
            // 煤剖面有、岩剖面没有：生成台阶面时台阶高给了 0 ⇒ BuildProfile 不装岩。
            _out.WriteLine("◆ 这份用例只有**煤剖面、没有岩剖面** —— 跳过（不是失败）。");
            _out.WriteLine($"  煤 {c.Profile.SeamCount} 层 · 总量 {c.Profile.TotalMaxWt:N1}万t · "
                         + $"时间 {new FileInfo(path).LastWriteTime:yyyy-MM-dd HH:mm}");
            _out.WriteLine("  原因：生成台阶面时**台阶高给了 0**（岩剖面按台阶高分标高格，给 0 就不装）。");
            _out.WriteLine("  重跑时把台阶高填成实际值，这条判据就会自动开始判真地质。");
            return;
        }

        // ── 真地质的规模与形态（合成夹具撞不到的那些）──
        _out.WriteLine($"\n岩剖面：{rock.SeamCount} 层 · 台阶高 {rock.BenchHeight:0.##}m · "
                     + $"标高格 {rock.LevelMin}..{rock.LevelMax}（{rock.LevelMax - rock.LevelMin + 1} 级）");
        _out.WriteLine($"  非零桶 {rock.Bins.Count:N0} · 岩 {rock.TotalRockM3 / 1e4:N1}万m³ · "
                     + $"煤 {rock.TotalCoalWt():N1}万t · 未归类 cell {rock.UnclassifiedCells:N0}");
        _out.WriteLine($"  α={rock.Provenance?.AlphaDeg ?? 0:0.###}° · Δ={rock.SliceWidth:0.##}m");
        Assert.True(rock.Success, "现场岩剖面本身是失败的：" + rock.Error);
        Assert.True(rock.TotalRockM3 > 0, "真地质里岩量是 0");
        Assert.True(rock.TotalCoalWt() > 0, "真地质里煤量是 0");

        double alpha = rock.Provenance?.AlphaDeg ?? 0;
        Assert.True(alpha > 0 && alpha < 90, $"指纹里的 α = {alpha} 不可用");

        // ── 排产：月煤量按可采总量的 1/24 定，跑 12 个月（不到半个储量，留足余量）──
        int T = 12;
        double perMonth = Math.Max(1, rock.TotalCoalWt() / 24.0);
        var sched = MonthlyMineScheduler.Solve(new MonthlyScheduleInput
        {
            Rock = rock, AlphaDeg = alpha, ZDatum = 0,
            CoalTargetWt = Enumerable.Repeat(perMonth, T).ToArray(),
            LookaheadMonths = 3, RecoveryTotalWt = perMonth * 3,
            StartInSteadyState = true, Pace = StripPace.Level,
        });
        _out.WriteLine($"\n排产：月煤 {perMonth:N2}万t × {T} 月");
        foreach (var n in sched.Notes) _out.WriteLine("  " + n);
        foreach (var ck in sched.Checks) _out.WriteLine("  " + ck);
        Assert.True(sched.Success, "真地质排不出来：" + sched.Error);

        // ── 不变量：与合成地质上判的是同一批，只是数据换成真的 ──
        // ① 月煤量对得上目标
        for (int t = 0; t < T; t++)
            Assert.True(Math.Abs(sched.Months[t].CoalWt - perMonth) <= perMonth * 0.01 + 1e-6,
                $"第{t + 1}月采出 {sched.Months[t].CoalWt:0.000} ≠ 目标 {perMonth:0.000}");
        // ② 位置单调不后退（C1）
        for (int t = 1; t < T; t++)
            for (int k = 0; k < sched.Levels.Length; k++)
                Assert.True(sched.Months[t].BenchX[k] >= sched.Months[t - 1].BenchX[k] - 1e-6,
                    $"第{t + 1}月格{sched.Levels[k]}后退了");
        // ③ 台阶超前（C2）：上面的格不落在下面的格后方
        double worstLead = double.PositiveInfinity;
        foreach (var m in sched.Months)
            for (int k = 1; k < sched.Levels.Length; k++)
                worstLead = Math.Min(worstLead, m.BenchX[k] - m.BenchX[k - 1]);
        _out.WriteLine($"  最小相邻超前 {worstLead:0.0}m（≥0 即没倒挂）");
        Assert.True(worstLead >= -1e-6, $"台阶倒挂：最小相邻超前 {worstLead:0.0}m");
        // ④ 三量递推恒等式
        for (int t = 1; t < T; t++)
        {
            var a = sched.Months[t - 1]; var b = sched.Months[t];
            double lhs = b.PreparedWt, rhs = a.PreparedWt + b.NewlyExposedWt - b.CoalWt;
            Assert.True(Math.Abs(lhs - rhs) <= Math.Max(1e-6, Math.Abs(lhs) * 0.005),
                $"第{t + 1}月三量递推不成立：备采 {lhs:0.000} ≠ {rhs:0.000}");
            Assert.True(b.NewlyExposedWt >= -1e-6, $"第{t + 1}月新露为负 {b.NewlyExposedWt:0.000}");
        }
        _out.WriteLine("  三量递推 ✓ · C1 不后退 ✓ · C2 台阶超前 ✓");

        // ── 配对 + 契约（排土位置是合成的，只判守恒与自洽，不判绝对值）──
        var slots = new List<DumpSlot>();
        double need = sched.TotalRockM3;
        for (int lv = 0; lv < 8; lv++)
            for (int b = 0; b < 6; b++)
                slots.Add(new DumpSlot
                {
                    DumpName = "外排土场(合成)", Level = lv, Order = b,
                    CapacityM3 = need / 40.0 + 1, IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.0,
                });
        var mats = new GapMaterial[GapCode.Count(rock.SeamCount)];
        for (int g = 0; g < mats.Length; g++)
            mats[g] = new GapMaterial { Name = $"层间{g}", Code = "rock", Density = 2.5, Kr = 1.15 };

        var dump = DumpAllocator.Allocate(sched, new DumpAllocationInput { Slots = slots, Materials = mats });
        Assert.True(dump.Success, "真地质配对失败：" + dump.Error);
        var ex = MinePlanExport.Build(sched, rock, dump);
        var bad = ex.Validate();
        foreach (var x in bad) _out.WriteLine("  ✗ " + x);
        Assert.Empty(bad);

        // 契约要写得出去（±∞/NaN 会让 ToJson 当场抛）
        string json = ex.ToJson();
        Assert.True(json.Length > 0);
        _out.WriteLine($"\n契约自洽 ✓ · JSON {json.Length / 1024.0:N0} KB · "
                     + $"{ex.Months.Count} 月 / {ex.Flows.Count} 笔流 / {ex.Benches.Count} 条台阶");
        _out.WriteLine($"总煤 {ex.TotalCoalWanT:N1}万t · 总岩 {ex.TotalStripWanM3:N1}万m³ · 剥采比 {ex.OverallRatio:0.00}");
    }
}
