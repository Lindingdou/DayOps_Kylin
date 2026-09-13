// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/ChainDataReadinessTests.cs（逐行对应；仅命名空间适配）
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
/// <b>W 组 · 各条链的"接了但有没有数"</b>（参数排查台账）。
///
/// <para>这一组不判对错，判<b>说不说得出来</b>。理由：本链上有一整类问题长得一模一样 ——
/// <b>「接上了」和「接上了但一条数都没有」算出来的结果完全相同</b>：
/// 路网接了但一笔没命中、装卸点台账接了但空表、设备台账接了但没台效……
/// 界面上都只表现为"东西少一点"，不报错。</para>
///
/// <para>所以这一组把每条链的**数据就绪度**打出来并钉住两件事：
/// ① 取数<b>不抛异常</b>（空库、没注册服务、字段缺失都算常态）；
/// ② 取不到时<b>说得出是哪一条缺</b>（`Notes` 不许空）。
/// 演示前跑一遍，就知道哪几条是"数据没录"、哪几条是真坏了。</para>
/// </summary>
public class ChainDataReadinessTests
{
    private readonly ITestOutputHelper _out;
    public ChainDataReadinessTests(ITestOutputHelper o) => _out = o;

    [Fact(DisplayName = "W1 出矿点（装卸点台账）：取得到就报点数，取不到要说清缺什么")]
    public void W1_CoalSinkReadiness()
    {
        // 造两个煤单元当"采场"—— 台账为空时适配器要退到采场质心，这一步需要它们
        var units = new List<MineUnit>
        {
            new() { UnitId = "煤2-B1-P1", Kind = UnitKind.Coal, Cx = 621000, Cy = 4381000, Cz = 1290, InSituM3 = 5e4 },
            new() { UnitId = "煤2-B1-P2", Kind = UnitKind.Coal, Cx = 621300, Cy = 4381100, Cz = 1288, InSituM3 = 5e4 },
        };

        CoalSinkResolution res;
        try { res = CoalSinkAdapter.Resolve(units, monthlyOperatingHours: 0); }
        catch (Exception ex) { Assert.Fail($"取出矿点抛了 {ex.GetType().Name}：{ex.Message} —— 空库/没注册服务都该是常态，不该抛"); return; }

        _out.WriteLine(res.Describe());
        Assert.True(res.Notes.Count > 0, "一条 Note 都没有 —— 取数结论必须说得出来源与降级");

        bool real = res.UsesRealPoints;
        _out.WriteLine(real
            ? $"✔ 装卸点台账有真出矿点 {res.Sinks.Count} 个 ⇒ 煤卸点会自动填、三维里煤运输线画得出来。"
            : "◆ 装卸点台账**没有可用的真出矿点** ⇒ 排产侧退到【采场质心】兜底（运输功是近似），"
              + "三维里**煤流一条都不画**。这是【数据没录】不是代码问题：\n"
              + "　补法：在「出矿点录入」里录破碎站/原煤仓/储煤场并拾取坐标；或在「采掘单元清单 → 指煤卸点…」图上指一次。");

        // 降级也必须给得出点（否则排产侧连兜底都没有）—— 但那时 UsesRealPoints 必须是 false，不许冒充
        Assert.True(res.Sinks.Count > 0 || res.Notes.Any(n => n.Contains("煤")), "既没有出矿点、也没说为什么");
    }

    [Fact(DisplayName = "W2 设备台账：报出在籍/可派/有实测台效三个数，缺了要说是哪一层缺")]
    public void W2_FleetReadiness()
    {
        FleetResolution fleet;
        try { fleet = EquipmentFleetProvider.Load(2026, 8, workdays: 25); }
        catch (Exception ex) { Assert.Fail($"取设备维抛了 {ex.GetType().Name}：{ex.Message} —— 空库该是常态，不该抛"); return; }

        _out.WriteLine(fleet.Summary());
        foreach (var n in fleet.Notes.Take(6)) _out.WriteLine("　" + n);

        Assert.True(fleet.Notes.Count > 0, "取设备维一条说明都没有 —— 「库没接」和「库里没数」分不出来");

        if (!fleet.DatabaseReady)
        {
            _out.WriteLine("◆ 设备台账**没接上**（判据进程里没有注册 GeoDataBase 服务是正常的）—— "
                         + "这一条只在软件里跑才有意义；此处只判它不抛、且说得出。");
            return;
        }

        _out.WriteLine($"在籍 {fleet.OnRoll} · 可派 {fleet.Dispatchable} · 有实测台效 {fleet.WithMeasuredRate}");
        // 三层逐级递减是定义上的：可派 ⊆ 在籍、有台效 ⊆ 可派。破了说明取数口径错了
        Assert.True(fleet.Dispatchable <= fleet.OnRoll, "可派台数多于在籍 —— 取数口径错了");
        Assert.True(fleet.WithMeasuredRate <= fleet.Dispatchable, "有实测台效的台数多于可派 —— 取数口径错了");

        if (fleet.Dispatchable == 0)
            _out.WriteLine("◆ 一台可派设备都没有 ⇒ 指派会全欠产，三维里一台设备都不会出现。");
        else if (fleet.WithMeasuredRate == 0)
            _out.WriteLine("◆ 有设备但**没有一台有实测台效** ⇒ 台效会退到下一级来源（缺省/中位数），"
                         + "班表算得出来但那不是这个矿的真台效 —— 报告里会注明。");
    }
}
