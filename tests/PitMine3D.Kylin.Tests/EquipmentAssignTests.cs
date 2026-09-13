// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/EquipmentAssignTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
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
/// E 组 · 设备指派（<see cref="EquipmentAssigner"/>）。
///
/// <para>这一组判的是<b>"这个月到底干不干得完"这个问题还问不问得出来</b>。
/// 设备指派的失败方式几乎全是静默的：台效大一个量级 → 什么都派得完、一条欠产都不报；
/// 回退链某一级从不命中 → 排班表和"接了实测台效"长得一模一样；
/// 欠产被摊平 → 总账是平的，而现场干不完。三种都不抛异常。</para>
///
/// <para><b>全部是构造性真值，不碰数据库、不碰界面。</b></para>
///
/// <para>⚠ 这里判的是<b>计划</b>指派。任何一条都不代表实绩，也不许回写实测台账。</para>
/// </summary>
public sealed class EquipmentAssignTests
{
    private readonly ITestOutputHelper _out;
    public EquipmentAssignTests(ITestOutputHelper o) => _out = o;

    private const int Y = 2026, Mo = 5;

    // ── 合成算例的零件 ──────────────────────────────────────────

    private static UnitAssignment U(string id, double m3, int seq, UnitKind k = UnitKind.Rock)
        => new() { UnitId = id, Kind = k, Seq = seq, InSituM3 = m3 };

    private static Machine Mach(string id, MachineKind k, string model)
        => new() { MachineId = id, Kind = k, Model = model, Dispatchable = true };

    private static RateRecord Rate(string machineId, string model, MachineKind k, double perDay,
                                   bool measured = true, int y = Y, int mo = Mo)
        => new()
        {
            MachineId = machineId, Model = model, Kind = k,
            Year = y, Month = mo, M3PerDay = perDay, Measured = measured,
            RecordKey = machineId.Length > 0 ? $"测试台效[{machineId},{y},{mo:00}]" : $"测试型号缺省[{model}]",
        };

    /// <summary>一台铲 + n 台车的标准算例。</summary>
    private static EquipmentAssignInput Basic(IEnumerable<UnitAssignment> units, int workdays,
                                              double shovelRate, int truckN, double truckRate)
    {
        var inp = new EquipmentAssignInput { Units = units.ToList(), Year = Y, Month = Mo, WorkdayCount = workdays };
        inp.Machines.Add(Mach("S1", MachineKind.Shovel, "SH"));
        inp.Rates.Add(Rate("S1", "SH", MachineKind.Shovel, shovelRate));
        for (int i = 1; i <= truckN; i++)
        {
            inp.Machines.Add(Mach("T" + i, MachineKind.Truck, "TK"));
            inp.Rates.Add(Rate("T" + i, "TK", MachineKind.Truck, truckRate));
        }
        return inp;
    }

    /// <summary>把 Validate() 的结论打出来，并断言自洽。</summary>
    private void AssertSelfConsistent(EquipmentAssignResult r)
    {
        var bad = r.Validate();
        foreach (var b in bad) _out.WriteLine("BAD: " + b);
        Assert.Empty(bad);
    }

    // ══════════════════════════════════════════════════════════════
    //  E1 量守恒 + 不冲突（正常算例）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E1_量守恒且不冲突()
    {
        var inp = Basic(new[] { U("A", 30000, 1), U("B", 40000, 2), U("C", 50000, 3) },
                        workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());

        Assert.True(r.Success, r.Error);
        AssertSelfConsistent(r);

        // Σ挖装 = Σ需求（全部派得出去）
        Assert.Equal(120000, r.DemandM3, 3);
        Assert.Equal(120000, r.Excavation.Sum(a => a.AssignedM3), 3);
        Assert.True(r.Feasible);
        Assert.Empty(r.Shortfalls);

        // 运输笔不进采出量的账：承运量单独等于挖装量，但不重复计入 AssignedM3
        Assert.Equal(120000, r.Haulage.Sum(a => a.AssignedM3), 3);
        Assert.Equal(120000, r.AssignedM3, 3);
    }

    // ══════════════════════════════════════════════════════════════
    //  E2 设备不够 → 报欠产，不摊平
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E2_设备不够报欠产而不是摊平()
    {
        // 一台铲 10 个工日最多 100000，却要 300000
        var inp = Basic(new[] { U("A", 100000, 1), U("B", 100000, 2), U("C", 100000, 3) },
                        workdays: 10, shovelRate: 10000, truckN: 3, truckRate: 5000);
        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());

        AssertSelfConsistent(r);
        Assert.Equal(300000, r.DemandM3, 3);
        Assert.Equal(100000, r.AssignedM3, 3);
        Assert.Equal(200000, r.ShortM3, 3);
        Assert.False(r.Feasible);

        // 每一条欠产都说得出原因
        Assert.All(r.Shortfalls.Where(s => s.ShortM3 > 1e-6), s => Assert.NotEqual("", s.Reason));

        // 没有把欠的量摊到别的设备上：任何一笔都没超过它自己的工日 × 台效
        Assert.All(r.Excavation, a => Assert.True(a.AssignedM3 <= a.Days * a.RateM3PerDay + 1e-6,
            $"{a.MachineId}@{a.UnitId} 派了 {a.AssignedM3} 超过 {a.Days}×{a.RateM3PerDay}"));

        // 也没有偷偷加班：没有任何设备的占用超过本月工日
        foreach (var kv in r.BusyDaysByMachine())
            Assert.True(kv.Value <= inp.WorkdayCount, $"{kv.Key} 占了 {kv.Value} 个工日，本月只有 {inp.WorkdayCount} 个");
    }

    // ══════════════════════════════════════════════════════════════
    //  E3 台效 0/负：不设下限 → 丢弃并回退，不当 0 用
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E3_非正台效被丢弃并回退到下一级()
    {
        var inp = Basic(new[] { U("A", 20000, 1) }, workdays: 20, shovelRate: 1, truckN: 3, truckRate: 5000);
        inp.Rates.RemoveAll(x => x.MachineId == "S1");
        inp.Rates.Add(Rate("S1", "SH", MachineKind.Shovel, 0));          // 实测是 0
        inp.Rates.Add(Rate("", "SH", MachineKind.Shovel, 8000, measured: false));   // 型号字典缺省
        inp.MinRateM3PerDay = 0;                                          // 不设下限 ⇒ 丢弃

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        var exc = r.Excavation.First();
        Assert.Equal(RateSource.ModelDefault, exc.RateSource);            // 退到了字典缺省
        Assert.False(exc.IsMeasuredRate);                                 // 而且知道它不是实测
        Assert.Equal(8000, exc.RateM3PerDay, 6);
        Assert.Contains("丢弃", string.Join("｜", r.Notes));
    }

    // ══════════════════════════════════════════════════════════════
    //  E4 台效负 + 设了下限 → 夹回下限，并在溯源键里留痕
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E4_设了下限则夹回并留条()
    {
        var inp = Basic(new[] { U("A", 20000, 1) }, workdays: 20, shovelRate: 1, truckN: 3, truckRate: 5000);
        inp.Rates.RemoveAll(x => x.MachineId == "S1");
        inp.Rates.Add(Rate("S1", "SH", MachineKind.Shovel, -500));
        inp.MinRateM3PerDay = 2000;

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        var exc = r.Excavation.First();
        Assert.Equal(2000, exc.RateM3PerDay, 6);
        Assert.Contains("夹回下限", exc.RateRecord);                       // 追得到它被夹过
        Assert.Contains("夹回下限", string.Join("｜", r.Notes));
    }

    // ══════════════════════════════════════════════════════════════
    //  E5 解不出台效 = 不可派，不是台效为 0
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E5_解不出台效的设备不可派而不是按零排()
    {
        var inp = Basic(new[] { U("A", 20000, 1) }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        inp.Machines.Add(Mach("S9", MachineKind.Shovel, "没这个型号"));    // 一条台效都没有

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        // S9 一笔都不该出现 —— 按 0 台效排会让它看起来在岗却永远干不出量
        Assert.DoesNotContain(r.Assignments, a => a.MachineId == "S9");
        Assert.Contains("解不出台效", string.Join("｜", r.Notes));
    }

    // ══════════════════════════════════════════════════════════════
    //  E6 一台空闲卡车都没有 ⇒ 开不了工（不允许无车开采）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E6_没有卡车就全部欠产而不是无车开采()
    {
        var inp = Basic(new[] { U("A", 50000, 1) }, workdays: 20, shovelRate: 10000, truckN: 0, truckRate: 0);
        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.Equal(0, r.AssignedM3, 6);
        Assert.Equal(50000, r.ShortM3, 3);
        Assert.Contains("卡车", r.Shortfalls.Single().Reason);

        // 一台车都没有 ⇒ 本月【每一个】工日都开不了工。
        // 只判 >0 是不够的：早退出的实现只报 1 天，数照样对不上而判据全绿。
        Assert.Equal(inp.WorkdayCount, r.HaulBlockedDays);
    }

    // ══════════════════════════════════════════════════════════════
    //  E7 转场占工日 —— 近/远两个对照组（近的不该占）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E7_转场按距离占工日_近的不占()
    {
        static EquipmentAssignInput Mk(double dx)
        {
            var inp = Basic(new[] { U("A", 10000, 1), U("B", 10000, 2) },
                            workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
            inp.Sites.Add(new UnitSite { UnitId = "A", Cx = 0, Cy = 0, Cz = 0 });
            inp.Sites.Add(new UnitSite { UnitId = "B", Cx = dx, Cy = 0, Cz = 0 });
            inp.FreeRelocationM = 300;
            inp.RelocationDaysTracked = 2;
            return inp;
        }

        var far = EquipmentAssigner.Assign(Mk(5000));
        var near = EquipmentAssigner.Assign(Mk(100));
        _out.WriteLine("远：" + far.Report());
        _out.WriteLine("近：" + near.Report());
        AssertSelfConsistent(far);
        AssertSelfConsistent(near);

        Assert.Equal(2, far.RelocationDays);       // 远 ⇒ 占 2 个工日
        Assert.Equal(0, near.RelocationDays);      // 近 ⇒ 不占

        // 转场笔不出量，但确实占着设备（否则"同一天不能在两处"就漏掉了转场日）
        Assert.All(far.Relocations, a => Assert.Equal(0, a.AssignedM3, 9));
        Assert.All(far.Relocations, a => Assert.True(a.Days > 0));
    }

    // ══════════════════════════════════════════════════════════════
    //  E8 台效来源逐笔可追，且回退链每一级都真能命中
    //     （"接了实测台效"和"接了但一条都没命中"排出来一模一样 —— 必须分得出）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E8_台效来源可追且型号级回退真的命中()
    {
        var inp = new EquipmentAssignInput { Year = Y, Month = Mo, WorkdayCount = 25, MaxLoadersPerUnit = 1 };
        inp.Units.Add(U("A", 20000, 1));
        inp.Units.Add(U("B", 20000, 2));
        inp.Units.Add(U("C", 20000, 3));

        // S1：自己有本月实测
        inp.Machines.Add(Mach("S1", MachineKind.Shovel, "SH"));
        inp.Rates.Add(Rate("S1", "SH", MachineKind.Shovel, 10000));
        // S2：同型号，自己没有任何记录 ⇒ 只能靠 S1 的记录走型号级
        inp.Machines.Add(Mach("S2", MachineKind.Shovel, "SH"));
        // S3：另一个型号，只有字典缺省
        inp.Machines.Add(Mach("S3", MachineKind.Shovel, "XH"));
        inp.Rates.Add(Rate("", "XH", MachineKind.Shovel, 6000, measured: false));

        for (int i = 1; i <= 9; i++)
        {
            inp.Machines.Add(Mach("T" + i, MachineKind.Truck, "TK"));
            inp.Rates.Add(Rate("T" + i, "TK", MachineKind.Truck, 5000));
        }

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        // 每一笔挖装都追得到用的是哪条台效
        Assert.All(r.Excavation, a =>
        {
            Assert.NotEqual(RateSource.Unresolved, a.RateSource);
            Assert.NotEqual("", a.RateRecord);
            Assert.True(a.RateM3PerDay > 0);
        });

        var used = r.Excavation.GroupBy(a => a.MachineId).ToDictionary(g => g.Key, g => g.First());
        Assert.Equal(RateSource.MachineMonth, used["S1"].RateSource);
        Assert.True(used["S1"].IsMeasuredRate);

        // ★ 型号级回退必须真的命中 —— S2 拿的是 S1 那条实测派生出来的型号中位数
        Assert.Equal(RateSource.ModelMonth, used["S2"].RateSource);
        Assert.True(used["S2"].IsMeasuredRate);
        Assert.Equal(10000, used["S2"].RateM3PerDay, 6);
        Assert.Contains("S2", used["S2"].RateRecord);      // 溯源键点名说了它自己没有记录

        // 字典缺省要能和实测分开
        Assert.Equal(RateSource.ModelDefault, used["S3"].RateSource);
        Assert.False(used["S3"].IsMeasuredRate);

        Assert.Equal(2, r.MeasuredRateHits);
        Assert.Equal(3, r.RateTotal);
    }

    // ══════════════════════════════════════════════════════════════
    //  E9 量级体检不是死路 —— 现场那个量级必须报，正常量级必须不报
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E9_台效量级体检能证伪()
    {
        // 现场 capacity_monthly 的量级：电铲月产 ~635 万 m³ ÷ 25 ≈ 25 万 m³/台·日
        var wild = Basic(new[] { U("A", 1e6, 1) }, workdays: 25, shovelRate: 250000, truckN: 6, truckRate: 50000);
        var sane = Basic(new[] { U("A", 1e6, 1) }, workdays: 25, shovelRate: 12000, truckN: 6, truckRate: 5000);

        var rw = EquipmentAssigner.Assign(wild);
        var rs = EquipmentAssigner.Assign(sane);
        _out.WriteLine("离谱量级：" + rw.RateScaleAudit);

        Assert.NotEqual("", rw.RateScaleAudit);   // 会报
        Assert.Equal("", rs.RateScaleAudit);      // 不会瞎报
    }

    // ══════════════════════════════════════════════════════════════
    //  E10 判据本身能证伪 —— 手工造坏结果，Validate() 必须红
    //      （只判"成功"的判据会空过；这一条防的就是那个）
    // ══════════════════════════════════════════════════════════════
    private static EquipmentAssignResult Clean()
    {
        var r = new EquipmentAssignResult
        { Success = true, WorkdayCount = 10, ShiftsPerDay = 3, DemandM3 = 1000, AssignedM3 = 1000 };
        r.Assignments.Add(new MachineAssignment
        {
            UnitId = "A", MachineId = "M1", Model = "SH", MachineKind = MachineKind.Shovel,
            Role = MachineRole.Excavate, StartDay = 1, EndDay = 1, Shifts = 3,
            AssignedM3 = 1000, RateM3PerDay = 1000, RateSource = RateSource.MachineMonth,
            RateRecord = "k", SpareCapM3 = 0,
        });
        r.Assignments.Add(new MachineAssignment
        {
            UnitId = "A", MachineId = "T1", Model = "TK", MachineKind = MachineKind.Truck,
            Role = MachineRole.Haul, ServesMachineId = "M1", StartDay = 1, EndDay = 1, Shifts = 3,
            AssignedM3 = 1000, RateM3PerDay = 1000, RateSource = RateSource.MachineMonth, RateRecord = "k",
        });
        return r;
    }

    [Fact]
    public void E10a_干净的结果判据不该瞎报()
    {
        var bad = Clean().Validate();
        foreach (var b in bad) _out.WriteLine("BAD: " + b);
        Assert.Empty(bad);      // 基准必须是干净的，否则下面几条什么也证明不了
    }

    [Fact]
    public void E10b_同一天在两处必须报()
    {
        var r = Clean();
        r.Assignments.Add(new MachineAssignment
        {
            UnitId = "B", MachineId = "M1", Model = "SH", MachineKind = MachineKind.Shovel,
            Role = MachineRole.Excavate, StartDay = 1, EndDay = 1, Shifts = 3,
            AssignedM3 = 0, RateM3PerDay = 1000, RateSource = RateSource.MachineMonth, RateRecord = "k",
        });
        Assert.Contains(r.Validate(), s => s.Contains("设备冲突"));
    }

    [Fact]
    public void E10c_量不守恒必须报()
    {
        var r = Clean();
        r.DemandM3 = 2000;                       // 已派 1000、欠产 0，对不上
        Assert.Contains(r.Validate(), s => s.Contains("量不守恒"));
    }

    [Fact]
    public void E10d_欠产说不出原因必须报()
    {
        var r = Clean();
        r.DemandM3 = 1500;
        r.Shortfalls.Add(new UnitShortfall { UnitId = "B", DemandM3 = 500, AssignedM3 = 0, Reason = "" });
        Assert.Contains(r.Validate(), s => s.Contains("说不出原因"));
    }

    [Fact]
    public void E10e_派的量超过台效必须报()
    {
        // 这一条曾经永远红不了：SpareCapM3 被 Math.Max(0,·) 夹过，负值到不了判据手里
        var r = Clean();
        r.DemandM3 = r.AssignedM3 = 3000;
        var exc = r.Assignments[0];
        exc.AssignedM3 = 3000;
        exc.SpareCapM3 = 1 * 1000 - 3000;        // 1 个工日 × 1000 台效，却派了 3000
        r.Assignments[1].AssignedM3 = 3000;
        Assert.Contains(r.Validate(), s => s.Contains("超过了"));
    }

    [Fact]
    public void E10f_运输笔绑了不存在的铲必须报()
    {
        var r = Clean();
        r.Assignments[1].ServesMachineId = "根本没这台";
        Assert.Contains(r.Validate(), s => s.Contains("不在挖装清单里"));
    }

    [Fact]
    public void E10g_承运量对不上挖装量必须报()
    {
        var r = Clean();
        r.Assignments[1].AssignedM3 = 400;       // 挖了 1000 只拉走 400
        Assert.Contains(r.Validate(), s => s.Contains("承运量"));
    }

    // ══════════════════════════════════════════════════════════════
    //  E11 密集算例：独立重建逐日占用（不走 Validate 的那份实现）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E11_密集算例下没有一台设备同一天在两处()
    {
        var inp = new EquipmentAssignInput { Year = Y, Month = Mo, WorkdayCount = 25 };
        for (int i = 1; i <= 60; i++)
            inp.Units.Add(U("U" + i, 15000 + (i % 7) * 3000, i, i % 5 == 0 ? UnitKind.Coal : UnitKind.Rock));
        for (int i = 1; i <= 5; i++)
        {
            inp.Machines.Add(Mach("S" + i, MachineKind.Shovel, "SH"));
            inp.Rates.Add(Rate("S" + i, "SH", MachineKind.Shovel, 9000 + i * 500));
        }
        for (int i = 1; i <= 16; i++)
        {
            inp.Machines.Add(Mach("T" + i, MachineKind.Truck, "TK"));
            inp.Rates.Add(Rate("T" + i, "TK", MachineKind.Truck, 4000));
        }

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        // 独立扫一遍：设备 × 日 只能落一个去处（转场日也算占着）
        var seen = new Dictionary<(string, int), string>();
        foreach (var a in r.Assignments)
            for (int d = a.StartDay; d <= a.EndDay; d++)
            {
                string tag = a.Role + "@" + a.UnitId;
                if (seen.TryGetValue((a.MachineId, d), out var had))
                    Assert.True(had == tag, $"{a.MachineId} 第 {d} 日同时在 {had} 和 {tag}");
                seen[(a.MachineId, d)] = tag;
            }

        // 量守恒（独立于 Validate 再算一遍）
        Assert.Equal(r.DemandM3, r.Excavation.Sum(a => a.AssignedM3) + r.Shortfalls.Sum(s => s.ShortM3), 3);
        // 设备摆不下这么多单元，欠产必须报得出来（报不出来说明能力上限是假的）
        Assert.True(r.ShortM3 > 0, "60 个单元 5 台铲 25 个工日却一条欠产都没有 —— 能力上限没起作用");
    }

    // ══════════════════════════════════════════════════════════════
    //  E12 编组：recommended_truck_count 是对「一对(铲型,车型)」说的
    // ══════════════════════════════════════════════════════════════
    private static EquipmentAssignInput Pairing(int nA, int nB, int nC)
    {
        var inp = new EquipmentAssignInput { Year = Y, Month = Mo, WorkdayCount = 5 };
        inp.Units.Add(U("A", 10000, 1));
        inp.Machines.Add(Mach("S1", MachineKind.Shovel, "SH"));
        inp.Rates.Add(Rate("S1", "SH", MachineKind.Shovel, 10000));

        void Add(string model, int n)
        {
            for (int i = 1; i <= n; i++)
            {
                inp.Machines.Add(Mach($"{model}-{i}", MachineKind.Truck, model));
                inp.Rates.Add(Rate($"{model}-{i}", model, MachineKind.Truck, 6000));
            }
        }
        Add("TA", nA); Add("TB", nB); Add("TC", nC);

        // TC 故意不写进编组规则 —— 它对这台铲是不可用的
        inp.Pairings.Add(new FleetPairing { LoaderModel = "SH", TruckModel = "TA", TruckCount = 3, EfficiencyScore = 90 });
        inp.Pairings.Add(new FleetPairing { LoaderModel = "SH", TruckModel = "TB", TruckCount = 4, EfficiencyScore = 50 });
        return inp;
    }

    [Fact]
    public void E12a_优先按单一车型成组且用那个车型自己的推荐台数()
    {
        var r = EquipmentAssigner.Assign(Pairing(nA: 5, nB: 5, nC: 5));
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        var crew = r.Haulage.Where(a => a.StartDay == 1).ToList();
        Assert.Equal(3, crew.Count);                                  // TA 的规则说 3 台，不是 max(3,4)
        Assert.All(crew, a => Assert.Equal("TA", a.Model));           // 效率评分高的那个车型
        Assert.Equal(0, r.MixedModelCrewDays);
        Assert.True(r.SingleModelCrewDays > 0);
    }

    [Fact]
    public void E12b_首选车型不够就换另一个车型_仍是单一车型()
    {
        var r = EquipmentAssigner.Assign(Pairing(nA: 2, nB: 5, nC: 5));   // TA 只有 2 台，凑不够 3
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        var crew = r.Haulage.Where(a => a.StartDay == 1).ToList();
        Assert.Equal(4, crew.Count);                                  // 改用 TB，按 TB 自己的 4 台
        Assert.All(crew, a => Assert.Equal("TB", a.Model));
        Assert.Equal(0, r.MixedModelCrewDays);
    }

    [Fact]
    public void E12c_哪个车型都凑不够才混编_而且如实报出来()
    {
        var r = EquipmentAssigner.Assign(Pairing(nA: 2, nB: 2, nC: 9));   // 两种都不够
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.True(r.MixedModelCrewDays > 0, "混编了却没报");
        Assert.Contains("混编", string.Join("｜", r.Notes));

        // TC 不在编组规则里 —— 一天都不该被派给这台铲
        Assert.DoesNotContain(r.Haulage, a => a.Model == "TC");
    }

    [Fact]
    public void E12d_编组规则是硬约束_没有可编组的车就开不了工()
    {
        var inp = Pairing(nA: 0, nB: 0, nC: 9);                          // 只剩规则外的 TC
        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.Equal(0, r.AssignedM3, 6);
        Assert.Empty(r.Haulage);
        Assert.Equal(inp.WorkdayCount, r.HaulBlockedDays);                // 每一个工日都开不了工
    }

    // ══════════════════════════════════════════════════════════════
    //  E13 退化输入不许静默
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E13_退化输入要么报错要么留条_不静默()
    {
        // 工日为 0
        var z = EquipmentAssigner.Assign(Basic(new[] { U("A", 1000, 1) }, 0, 10000, 3, 5000));
        Assert.False(z.Success);
        Assert.NotEqual("", z.Error);
        Assert.NotEmpty(z.Validate());

        // 没有单元
        var none = EquipmentAssigner.Assign(Basic(Array.Empty<UnitAssignment>(), 20, 10000, 3, 5000));
        Assert.False(none.Success);
        Assert.NotEqual("", none.Error);

        // 一台可派挖装设备都没有
        var noLoader = Basic(new[] { U("A", 1000, 1) }, 20, 10000, 3, 5000);
        noLoader.Machines.RemoveAll(m => m.Kind == MachineKind.Shovel);
        var nl = EquipmentAssigner.Assign(noLoader);
        Assert.False(nl.Success);
        Assert.NotEqual("", nl.Error);

        // 每日班次非正 ⇒ 夹回 3 并留条
        var badShift = Basic(new[] { U("A", 10000, 1) }, 20, 10000, 3, 5000);
        badShift.ShiftsPerDay = 0;
        var bs = EquipmentAssigner.Assign(badShift);
        Assert.Equal(3, bs.ShiftsPerDay, 6);
        Assert.Contains("每日班次", string.Join("｜", bs.Notes));

        // 负的单元量不许变成负指派
        var neg = Basic(new[] { U("A", -5000, 1) }, 20, 10000, 3, 5000);
        var rn = EquipmentAssigner.Assign(neg);
        AssertSelfConsistent(rn);
        Assert.All(rn.Assignments, a => Assert.True(a.AssignedM3 >= 0));
    }

    // ══════════════════════════════════════════════════════════════
    //  E14 运力卡住时按车走，不按铲走（且报出来）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void E14_卡车拉不动时产量按运力算并报出来()
    {
        // 铲 20000/日，配 3 台车共 6000/日 ⇒ 每天只能出 6000
        var inp = Basic(new[] { U("A", 60000, 1) }, workdays: 10, shovelRate: 20000, truckN: 3, truckRate: 2000);
        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.True(r.HaulLimitedDays > 0, "运力明显不够却没报被卡住的工日");
        Assert.Equal(60000, r.AssignedM3, 3);                  // 10 天 × 6000 刚好
        Assert.All(r.Excavation, a => Assert.True(a.HaulLimited));
        // 铲的能力没被用满，余能是正的（而且没有被夹成 0 掩盖掉）
        Assert.True(r.Excavation.Sum(a => a.SpareCapM3) > 0);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ED 组 · 穿孔（钻机）与排土（推土机）
    //
    //  这一组要抓的失败方式，和上面那些同源、但更隐蔽：
    //    · 开关打开了，而穿爆超前一天都没推迟 → 排班表和"没接"长得一模一样；
    //    · 排土的量被加进采出量 → 总账仍然是平的，只是那个数不再是任何真实的量；
    //    · 需爆破的岩单元没穿爆却照样开挖 → 每一项校核都是 ✓。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>给单元挂一笔去向流（Kr=1.2 ⇒ 占容 = 实方×1.2）。</summary>
    private static UnitAssignment WithFlow(UnitAssignment u, string dest, double kr = 1.2)
    {
        u.Flows.Add(new UnitFlow
        {
            DestinationCode = dest, DestinationName = dest + "·内排", IsInternalDump = true,
            InSituM3 = u.InSituM3, DumpM3 = u.InSituM3 * kr, TonnageT = u.InSituM3 * 2.4, HaulKm = 2.5,
        });
        return u;
    }

    private static void AddDrills(EquipmentAssignInput inp, int n, double perDay)
    {
        for (int i = 1; i <= n; i++)
        {
            inp.Machines.Add(Mach("D" + i, MachineKind.Drill, "DR"));
            inp.Rates.Add(Rate("D" + i, "DR", MachineKind.Drill, perDay));
        }
    }

    private static void AddDozers(EquipmentAssignInput inp, int n, double perDay)
    {
        for (int i = 1; i <= n; i++)
        {
            inp.Machines.Add(Mach("Z" + i, MachineKind.Dozer, "DZ"));
            inp.Rates.Add(Rate("Z" + i, "DZ", MachineKind.Dozer, perDay));
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  ED0【F0 自检】开关关着 = 加这两个工序之前的行为，一个字节都不差
    //     在册有钻机和推土机也不许被排 —— 否则"默认关"就是假的。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED0_两个开关关着时行为与加工序之前完全一致()
    {
        var inp = Basic(new[] { WithFlow(U("A", 30000, 1), "DP1"), WithFlow(U("B", 40000, 2), "DP1") },
                        workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        AddDrills(inp, 2, 20000);
        AddDozers(inp, 2, 20000);
        // 刻意不动 ScheduleDrilling / ScheduleDozing —— 缺省必须是关

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.False(r.DrillingScheduled);
        Assert.False(r.DozingScheduled);
        Assert.Empty(r.Drilling);
        Assert.Empty(r.Dumping);
        Assert.Equal(0, r.DrillDemandM3);
        Assert.Equal(0, r.DozeDemandM3);
        Assert.Equal(0, r.DrillShortM3);
        Assert.Equal(0, r.DozeShortM3);

        // 采装侧与不带钻机/推土机时逐字相同
        Assert.Equal(70000, r.DemandM3, 3);
        Assert.Equal(70000, r.AssignedM3, 3);
        Assert.True(r.Feasible);
        Assert.Empty(r.Shortfalls);

        // 在册的钻机/推土机被计入"未排"，不是被悄悄漏掉
        Assert.Contains(r.Notes, n => n.Contains("钻机") && n.Contains("关着"));
        Assert.Contains(r.Notes, n => n.Contains("推土机") && n.Contains("关着"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED1【F0 自检】打开穿孔，挖装的开工日【真的】被推后
    //     A/B 对照：同一算例开关一开一关，采装的起始日必须不同。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED1_穿爆超前真的把挖装推后了()
    {
        static EquipmentAssignInput Mk(bool drill)
        {
            var inp = Basic(new[] { U("A", 30000, 1) }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
            AddDrills(inp, 1, 15000);                      // 30000 需 2 个工日穿完
            inp.ScheduleDrilling = drill;
            inp.BlastLeadDays = 3;
            return inp;
        }

        var off = EquipmentAssigner.Assign(Mk(false));
        var on = EquipmentAssigner.Assign(Mk(true));
        _out.WriteLine("关：" + off.Report());
        _out.WriteLine("开：" + on.Report());
        AssertSelfConsistent(off);
        AssertSelfConsistent(on);

        int offStart = off.Excavation.Min(a => a.StartDay);
        int onStart = on.Excavation.Min(a => a.StartDay);
        Assert.Equal(1, offStart);                          // 关着：第 1 日就开挖

        // 穿孔 2 个工日（第 1~2 日）+ 超前 3 工日 ⇒ 最早第 6 日
        int drillEnd = on.Drilling.Max(a => a.EndDay);
        Assert.Equal(2, drillEnd);
        Assert.Equal(drillEnd + 3 + 1, onStart);
        Assert.True(onStart > offStart, "打开穿孔之后挖装一天都没推迟 —— 超前约束等于没加");
        Assert.True(on.UnitsDelayedByBlastLead > 0);

        // 穿孔的量是控制方量，与采出量各记各的账
        Assert.Equal(30000, on.DrillDemandM3, 3);
        Assert.Equal(30000, on.DrilledM3, 3);
        Assert.Equal(30000, on.AssignedM3, 3);              // 采出量没有被穿孔量污染
    }

    // ══════════════════════════════════════════════════════════════
    //  ED2【F0 自检】人为破坏超前关系，判据⑥必须变红
    //     判据自己也要能证伪 —— 一条永远不红的判据等于没有。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED2_挖装抢在穿爆之前判据必须报红()
    {
        var inp = Basic(new[] { U("A", 30000, 1) }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        AddDrills(inp, 1, 15000);
        inp.ScheduleDrilling = true;
        inp.BlastLeadDays = 3;

        var r = EquipmentAssigner.Assign(inp);
        AssertSelfConsistent(r);                            // 先确认本来是绿的

        // 把挖装那一笔硬拽到穿孔还没完的第 1 日
        var exc = r.Excavation.First();
        int span = exc.EndDay - exc.StartDay;
        exc.StartDay = 1; exc.EndDay = 1 + span;

        var bad = r.Validate();
        foreach (var b in bad) _out.WriteLine("BAD: " + b);
        Assert.Contains(bad, b => b.Contains("穿爆超前没挡住"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED3 没有钻机 ⇒ 需爆破的单元整个采不了，且原因写的是穿爆不是"设备不够"
    //     指错原因比不给原因更糟：会有人去加铲，而缺的是钻机。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED3_没钻机时欠产原因指向穿爆而不是挖装设备()
    {
        var inp = Basic(new[] { U("A", 30000, 1) }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        inp.ScheduleDrilling = true;                        // 打开了，但一台钻机都没加

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.Equal(0, r.AssignedM3, 3);                   // 一方都挖不出来
        Assert.Equal(30000, r.ShortM3, 3);
        Assert.False(r.Feasible);
        Assert.True(r.UnitsBlockedByDrilling > 0);

        var s = r.ExcavationShortfalls.Single();
        Assert.Contains("穿爆", s.Reason);
        Assert.DoesNotContain("没有空闲的挖装设备", s.Reason);
        Assert.Contains(r.Notes, n => n.Contains("一台可派的钻机都没有"));

        // 穿孔侧也如实记了一笔欠产，两本账各自平
        Assert.Equal(30000, r.DrillDemandM3, 3);
        Assert.Equal(30000, r.DrillShortM3, 3);
    }

    // ══════════════════════════════════════════════════════════════
    //  ED4 免爆单元 / 煤单元不穿孔，也不被超前期推后
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED4_免爆单元与煤单元不穿孔也不被推后()
    {
        // ⚠ 这里必须走 A/B 对照，不能直接断言"免爆单元第 1 日开挖"：
        //   一台铲要挨个单元干，还要转场，本来就排不到第 1 日。
        //   拿"第 1 日开挖"当判据的话，它测的是排班的串行化，不是超前约束 ——
        //   而那正是"判据判到了别的东西"这一类空过。
        // ⚠ 三个单元各配一台铲：一台铲要挨个干、还要转场的话，ROCK 本来就排到第 5 日，
        //   超前期算出的第 4 日<b>根本卡不住它</b> —— 那时 A/B 两边的开工日相同，
        //   而这并不说明约束失效。要测约束，先得让它成为约束。
        static EquipmentAssignInput Mk(bool drill)
        {
            var inp = new EquipmentAssignInput
            {
                Units = new List<UnitAssignment>
                {
                    U("COAL", 20000, 1, UnitKind.Coal),  // 煤：一律不穿
                    U("SOIL", 20000, 2),                 // 岩，但在免爆清单里
                    U("ROCK", 20000, 3),                 // 岩，要穿
                },
                Year = Y, Month = Mo, WorkdayCount = 20,
            };
            for (int i = 1; i <= 3; i++)
            {
                inp.Machines.Add(Mach("S" + i, MachineKind.Shovel, "SH"));
                inp.Rates.Add(Rate("S" + i, "SH", MachineKind.Shovel, 20000));
            }
            for (int i = 1; i <= 9; i++)
            {
                inp.Machines.Add(Mach("T" + i, MachineKind.Truck, "TK"));
                inp.Rates.Add(Rate("T" + i, "TK", MachineKind.Truck, 10000));
            }
            AddDrills(inp, 1, 20000);
            inp.ScheduleDrilling = drill;
            inp.BlastLeadDays = 2;
            inp.NoBlastUnitIds.Add("SOIL");
            return inp;
        }

        var off = EquipmentAssigner.Assign(Mk(false));
        var on = EquipmentAssigner.Assign(Mk(true));
        _out.WriteLine("关：" + off.Report());
        _out.WriteLine("开：" + on.Report());
        AssertSelfConsistent(off);
        AssertSelfConsistent(on);

        // 只有 ROCK 进穿孔的账
        Assert.Equal(new[] { "ROCK" }, on.Drilling.Select(a => a.UnitId).Distinct().ToList());
        Assert.Equal(20000, on.DrillDemandM3, 3);
        Assert.Equal(1, on.UnitsDelayedByBlastLead);

        static int Start(EquipmentAssignResult r, string u) => r.Excavation.Where(a => a.UnitId == u).Min(a => a.StartDay);

        // 煤与免爆单元：开不开穿孔，开工日一模一样
        Assert.Equal(Start(off, "COAL"), Start(on, "COAL"));
        Assert.Equal(Start(off, "SOIL"), Start(on, "SOIL"));
        // 要穿爆的那个：确实被推后了
        Assert.True(Start(on, "ROCK") > Start(off, "ROCK"),
                    $"ROCK 开工日 关={Start(off, "ROCK")} 开={Start(on, "ROCK")} —— 超前约束没起作用");

        Assert.Equal(60000, on.AssignedM3, 3);
        Assert.True(on.Feasible);
    }

    // ══════════════════════════════════════════════════════════════
    //  ED5 排土量走【排弃占容】口径，且【绝不】进采出量的账
    //     这一条是本组最重要的：混进去之后总账仍然是平的，没有任何东西会报错。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED5_排土量单独结账不混进采出量()
    {
        var inp = Basic(new[] { WithFlow(U("A", 30000, 1), "DP1"), WithFlow(U("B", 30000, 2), "DP1") },
                        workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        AddDozers(inp, 3, 20000);
        inp.ScheduleDozing = true;

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        // 采出量 = 原位实方 60000，一点没变
        Assert.Equal(60000, r.DemandM3, 3);
        Assert.Equal(60000, r.AssignedM3, 3);
        Assert.Equal(60000, r.Excavation.Sum(a => a.AssignedM3), 3);

        // 排土量 = 占容 = 60000 × 1.2 = 72000，走的是另一本账
        Assert.Equal(72000, r.DozeDemandM3, 3);
        Assert.Equal(72000, r.DozedM3, 3);
        Assert.Equal(72000, r.Dumping.Sum(a => a.AssignedM3), 3);
        Assert.True(r.DozersUsed > 0);

        // 排土笔的作业对象是【去向码】不是单元号
        Assert.All(r.Dumping, a => Assert.Equal("DP1", a.UnitId));

        // 口径写进了溯源说明
        Assert.Contains(r.Notes, n => n.Contains("排弃占容"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED6 煤流不排土（DumpM3=0）—— 煤进原煤仓不占排土库容
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED6_煤流不产生排土需求()
    {
        var coal = U("C1", 30000, 1, UnitKind.Coal);
        coal.Flows.Add(new UnitFlow
        {
            DestinationCode = "SILO", DestinationName = "原煤仓", IsCoalSink = true,
            InSituM3 = 30000, DumpM3 = 0, TonnageT = 40000, HaulKm = 1.8,
        });

        var inp = Basic(new[] { coal }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        AddDozers(inp, 2, 20000);
        inp.ScheduleDozing = true;

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.Equal(30000, r.AssignedM3, 3);
        Assert.Equal(0, r.DozeDemandM3);
        Assert.Empty(r.Dumping);
        Assert.Contains(r.Notes, n => n.Contains("没有需要排弃的料"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED7 推土机不够 ⇒ 排土欠产，而采装那本账【不受影响】
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED7_推土机不够只欠排土不动采装的账()
    {
        var inp = Basic(new[] { WithFlow(U("A", 100000, 1), "DP1") },
                        workdays: 10, shovelRate: 10000, truckN: 3, truckRate: 5000);
        AddDozers(inp, 1, 2000);                            // 每天只能推 2000，远不够
        inp.ScheduleDozing = true;
        inp.MaxDozersPerSink = 1;

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        // 采装：10 日 × 10000 = 100000，全部派出
        Assert.Equal(100000, r.DemandM3, 3);
        Assert.Equal(100000, r.AssignedM3, 3);
        Assert.Equal(0, r.ShortM3, 3);
        Assert.Empty(r.ExcavationShortfalls);

        // 排土：需求 120000 占容，只推得动 10 日 × 2000 = 20000
        Assert.Equal(120000, r.DozeDemandM3, 3);
        Assert.Equal(20000, r.DozedM3, 3);
        Assert.Equal(100000, r.DozeShortM3, 3);
        Assert.False(r.Feasible);                           // 排土干不完，这个月就是干不完

        var s = r.DozeShortfalls.Single();
        Assert.Equal("DP1", s.UnitId);
        Assert.Equal("排弃占容", s.BasisText);
        Assert.NotEmpty(s.Reason);
    }

    // ══════════════════════════════════════════════════════════════
    //  ED8【F0 自检】把排土的量加进采出量，判据必须变红
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED8_把排土量混进采出量判据必须报红()
    {
        var inp = Basic(new[] { WithFlow(U("A", 30000, 1), "DP1") },
                        workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        AddDozers(inp, 2, 20000);
        inp.ScheduleDozing = true;

        var r = EquipmentAssigner.Assign(inp);
        AssertSelfConsistent(r);                            // 本来是绿的

        r.AssignedM3 += r.DozedM3;                          // 人为把两个口径加到一起
        var bad = r.Validate();
        foreach (var b in bad) _out.WriteLine("BAD: " + b);
        Assert.Contains(bad, b => b.Contains("汇总对不上"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED9 穿孔 + 排土 一起开，三本账各自守恒、设备互不冲突
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED9_三个工序同时排时三本账各自守恒()
    {
        var inp = Basic(new[]
        {
            WithFlow(U("A", 40000, 1), "DP1"),
            WithFlow(U("B", 40000, 2), "DP2"),
        }, workdays: 25, shovelRate: 10000, truckN: 4, truckRate: 5000);
        AddDrills(inp, 2, 20000);
        AddDozers(inp, 2, 30000);
        inp.ScheduleDrilling = true;
        inp.ScheduleDozing = true;
        inp.BlastLeadDays = 2;

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        _out.WriteLine(EquipmentAssigner.ToCsv(r));
        AssertSelfConsistent(r);

        Assert.Equal(80000, r.DemandM3, 3);                 // 采装·原位实方
        Assert.Equal(80000, r.DrillDemandM3, 3);            // 穿孔·控制方量
        Assert.Equal(96000, r.DozeDemandM3, 3);             // 排土·排弃占容 = 80000×1.2
        Assert.True(r.Feasible);

        // 三种量绝不相等也绝不相加 —— 各自的 Σ逐笔 = 各自的汇总
        Assert.Equal(r.AssignedM3, r.Excavation.Sum(a => a.AssignedM3), 3);
        Assert.Equal(r.DrilledM3, r.Drilling.Sum(a => a.AssignedM3), 3);
        Assert.Equal(r.DozedM3, r.Dumping.Sum(a => a.AssignedM3), 3);

        // CSV 里必须带量口径列，否则下游一 SUM 就得到一个不存在的量
        string csv = EquipmentAssigner.ToCsv(r);
        Assert.Contains("量口径", csv);
        Assert.Contains("控制方量", csv);
        Assert.Contains("排弃占容", csv);
    }

    // ══════════════════════════════════════════════════════════════
    //  ED10 面级型号约束是【硬约束】：钉了就只用那个型号
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED10_面上钉了型号就只挑那个型号()
    {
        var inp = Basic(new[] { U("A", 40000, 1) }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        // 再加一台别的型号的铲，台效更高 —— 不钉的话引擎会优先挑它
        inp.Machines.Add(Mach("S2", MachineKind.Shovel, "BIG"));
        inp.Rates.Add(Rate("S2", "BIG", MachineKind.Shovel, 40000));

        var free = EquipmentAssigner.Assign(inp);
        AssertSelfConsistent(free);
        Assert.Contains(free.Excavation, a => a.Model == "BIG");     // 不钉：高台效那台被挑走

        inp.Pins["A"] = new UnitFacePin { FaceName = "一采区", LoaderModel = "SH" };
        var pinned = EquipmentAssigner.Assign(inp);
        _out.WriteLine(pinned.Report());
        AssertSelfConsistent(pinned);

        Assert.All(pinned.Excavation, a => Assert.Equal("SH", a.Model));
        Assert.DoesNotContain(pinned.Excavation, a => a.Model == "BIG");
        Assert.Contains(pinned.Notes, n => n.Contains("面级型号约束"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED11 钉了一个【一台都没有】的型号 ⇒ 欠产 + 指出根因在配置不在设备数量
    //     静默退回"全矿挑"的话，界面上钉的型号就成了摆设，而报表还显示它生效了。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED11_钉了不存在的型号是欠产而不是静默放行()
    {
        var inp = Basic(new[] { U("A", 40000, 1) }, workdays: 20, shovelRate: 10000, truckN: 3, truckRate: 5000);
        inp.Pins["A"] = new UnitFacePin { FaceName = "一采区", LoaderModel = "库里没有这个型号" };

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.Equal(0, r.AssignedM3, 3);                            // 没有悄悄换一台顶上
        Assert.Equal(40000, r.ShortM3, 3);
        Assert.False(r.Feasible);
        Assert.Contains(r.Notes, n => n.Contains("没有这个型号") && n.Contains("确定开采程序"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED12 面级穿爆超前期覆盖全局值，且【逐单元】生效
    //     硬岩大区爆破 5 天、煤层控制爆破 1 天 —— 一个全局常数表达不了。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED12_面级超前期覆盖全局且逐单元生效()
    {
        var inp = new EquipmentAssignInput
        {
            Units = new List<UnitAssignment> { U("HARD", 20000, 1), U("SOFT", 20000, 2) },
            Year = Y, Month = Mo, WorkdayCount = 25,
            ScheduleDrilling = true,
            BlastLeadDays = 2,                                  // 全局 2 天
        };
        // 两个单元各一台铲，免得排班串行化把超前期盖过去
        for (int i = 1; i <= 2; i++)
        {
            inp.Machines.Add(Mach("S" + i, MachineKind.Shovel, "SH"));
            inp.Rates.Add(Rate("S" + i, "SH", MachineKind.Shovel, 20000));
        }
        for (int i = 1; i <= 6; i++)
        {
            inp.Machines.Add(Mach("T" + i, MachineKind.Truck, "TK"));
            inp.Rates.Add(Rate("T" + i, "TK", MachineKind.Truck, 10000));
        }
        AddDrills(inp, 2, 20000);

        inp.Pins["HARD"] = new UnitFacePin { FaceName = "硬岩面", BlastLeadDays = 6 };   // 面级覆盖
        // SOFT 不填 ⇒ 回退全局 2

        var r = EquipmentAssigner.Assign(inp);
        _out.WriteLine(r.Report());
        AssertSelfConsistent(r);

        Assert.Equal(1, r.LeadOverriddenUnits);
        Assert.Equal(6, r.LeadDaysByUnit["HARD"]);
        Assert.Equal(2, r.LeadDaysByUnit["SOFT"]);

        // 两个单元都第 1 日穿完 ⇒ HARD 最早第 8 日、SOFT 最早第 4 日
        int hardDrill = r.Drilling.Where(a => a.UnitId == "HARD").Max(a => a.EndDay);
        int softDrill = r.Drilling.Where(a => a.UnitId == "SOFT").Max(a => a.EndDay);
        Assert.Equal(hardDrill + 6 + 1, r.Excavation.Where(a => a.UnitId == "HARD").Min(a => a.StartDay));
        Assert.Equal(softDrill + 2 + 1, r.Excavation.Where(a => a.UnitId == "SOFT").Min(a => a.StartDay));
        Assert.Contains(r.Notes, n => n.Contains("面级穿爆超前期"));
    }

    // ══════════════════════════════════════════════════════════════
    //  ED13【F0 自检】判据⑥必须按【单元自己的】超前期判，不是按全局值
    //     照全局值判的话：面上把超前期改长了、挖装抢在中间开工 —— 判据不会红。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void ED13_判据按单元自己的超前期判而不是全局值()
    {
        var inp = Basic(new[] { U("A", 20000, 1) }, workdays: 25, shovelRate: 20000, truckN: 3, truckRate: 10000);
        AddDrills(inp, 1, 20000);
        inp.ScheduleDrilling = true;
        inp.BlastLeadDays = 1;                                        // 全局只有 1 天
        inp.Pins["A"] = new UnitFacePin { FaceName = "硬岩面", BlastLeadDays = 8 };  // 面上要 8 天

        var r = EquipmentAssigner.Assign(inp);
        AssertSelfConsistent(r);                                      // 本来是绿的

        // 把挖装拽到"全局值满足、面级值不满足"的那一天：穿完第 1 日 + 全局 1 天 ⇒ 第 3 日
        var exc = r.Excavation.First();
        int span = exc.EndDay - exc.StartDay;
        exc.StartDay = 3; exc.EndDay = 3 + span;

        var bad = r.Validate();
        foreach (var b in bad) _out.WriteLine("BAD: " + b);
        // 按全局 1 天判的话第 3 日是合法的 —— 只有照面级 8 天判才抓得到
        Assert.Contains(bad, b => b.Contains("穿爆超前没挡住") && b.Contains("间隔 8 工日"));
    }
}
